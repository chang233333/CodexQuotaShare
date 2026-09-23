import { DurableObject } from 'cloudflare:workers';
import { authFromHeaders, AUTH_CLOCK_SKEW_MS, AUTH_NONCE_HISTORY, canonicalAuthMessage, createJoinCode,
  INVITE_TTL_MS, randomSecret, verify } from './auth';
import { requireLocalDevelopment, type Env } from './index';
import { ACTIVITY_UPDATE_INTERVAL_MS, HEARTBEAT_WRITE_INTERVAL_MS, MAX_DEVICES, MAX_MESSAGE_BYTES, ProtocolError,
  errorResponse, identifier, json, keys, parseClientMessage, parseInternalCreate, parseInternalJoin,
  publicSnapshot, readJson, record, snapshotMessage, type AuthEnvelope, type Device, type QuotaObservation,
  type StoredGroup, type UsageLedger } from './protocol';

const STATE_KEY = 'group';
interface Connection { deviceId: string; connectionId: string; requestPath: string; }
const RESET_DROP_THRESHOLD = 5;

function emptyLedger(devices: Device[]): UsageLedger {
  return { deviceUsage: Object.fromEntries(devices.map(device => [device.deviceId, 0])), unattributedUsage: 0, confidence: {} };
}

export class QuotaGroup extends DurableObject<Env> {
  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair('ping', 'pong'));
    ctx.blockConcurrencyWhile(async () => {
      const state = await ctx.storage.get<StoredGroup>(STATE_KEY);
      if (!state || state.schemaVersion !== 2) return;
      let changed = false;
      for (const device of state.devices) {
        const status = this.statusFor(state, device.deviceId);
        if (device.status !== status) { device.status = status; changed = true; }
      }
      if (changed) {
        state.version++; state.updatedAt = Date.now();
        await ctx.storage.put(STATE_KEY, state);
        this.broadcast(state);
      }
    });
  }

  private connection(socket: WebSocket): Connection | null {
    const value = socket.deserializeAttachment() as Partial<Connection> | null;
    return value && typeof value.deviceId === 'string' && typeof value.connectionId === 'string' && typeof value.requestPath === 'string'
      ? value as Connection : null;
  }

  private sockets(deviceId: string): WebSocket[] {
    return this.ctx.getWebSockets(deviceId).filter(socket => socket.readyState === WebSocket.OPEN);
  }

  private async state(): Promise<StoredGroup> {
    const state = await this.ctx.storage.get<StoredGroup>(STATE_KEY);
    if (!state) throw new ProtocolError('GROUP_NOT_FOUND', 404);
    if (state.schemaVersion !== 2) throw new ProtocolError('SCHEMA_UPGRADE_REQUIRED', 503);
    return state;
  }

  private device(state: StoredGroup, deviceId: string): Device {
    const device = state.devices.find(item => item.deviceId === deviceId);
    if (!device) throw new ProtocolError('DEVICE_NOT_FOUND', 403);
    return device;
  }

  private assertOwner(state: StoredGroup, deviceId: string): void {
    if (state.ownerDeviceId !== deviceId) throw new ProtocolError('OWNER_REQUIRED', 403);
  }

  private statusFor(state: StoredGroup, deviceId: string): Device['status'] {
    const device = this.device(state, deviceId);
    const usage = state.usageLedger.deviceUsage[deviceId] ?? 0;
    if (device.limitPercent !== null && device.limitPercent !== undefined && usage >= device.limitPercent) return 'LIMIT_REACHED';
    return this.sockets(deviceId).length ? 'ONLINE' : 'OFFLINE';
  }

  private async change(update: (state: StoredGroup) => boolean): Promise<{ state: StoredGroup; changed: boolean }> {
    return this.ctx.storage.transaction(async transaction => {
      const state = await transaction.get<StoredGroup>(STATE_KEY);
      if (!state) throw new ProtocolError('GROUP_NOT_FOUND', 404);
      if (state.schemaVersion !== 2) throw new ProtocolError('SCHEMA_UPGRADE_REQUIRED', 503);
      const changed = update(state);
      if (changed) {
        state.version++; state.updatedAt = Date.now();
      }
      await transaction.put(STATE_KEY, state); // Persist private sequence/corroboration even without a public version change.
      return { state, changed };
    });
  }

  private send(socket: WebSocket, value: unknown): void {
    try { socket.send(JSON.stringify(value)); }
    catch { try { socket.close(1011, 'SEND_FAILED'); } catch { /* Already closed. */ } }
  }

  private broadcast(state: StoredGroup): void {
    const message = snapshotMessage(state);
    for (const socket of this.ctx.getWebSockets()) if (socket.readyState === WebSocket.OPEN) this.send(socket, message);
  }

  private async authenticateRequest(request: Request, payload: unknown): Promise<{ state: StoredGroup; auth: AuthEnvelope }> {
    let auth: AuthEnvelope;
    try { auth = authFromHeaders(request); }
    catch { throw new ProtocolError('AUTH_REQUIRED', 401); }
    const state = await this.state();
    const secret = state.secrets[auth.deviceId];
    if (!secret) throw new ProtocolError('AUTH_FAILED', 401);
    if (Math.abs(Date.now() - auth.timestamp) > AUTH_CLOCK_SKEW_MS) throw new ProtocolError('STALE_AUTH', 401);
    const url = new URL(request.url);
    const message = canonicalAuthMessage(request.method, url.pathname + url.search, auth, payload);
    if (!await verify(secret, message, auth.signature)) throw new ProtocolError('AUTH_FAILED', 401);
    const updated = await this.commitAuthentication(auth);
    return { state: updated, auth };
  }

  private async authenticateMessage(socket: WebSocket, identity: Connection, message: ReturnType<typeof parseClientMessage>): Promise<StoredGroup> {
    const state = await this.state();
    if (message.auth.deviceId !== identity.deviceId) throw new ProtocolError('AUTH_DEVICE_MISMATCH', 401);
    const secret = state.secrets[message.auth.deviceId];
    if (!secret) throw new ProtocolError('AUTH_FAILED', 401);
    if (Math.abs(Date.now() - message.auth.timestamp) > AUTH_CLOCK_SKEW_MS) throw new ProtocolError('STALE_AUTH', 401);
    const { auth: _auth, ...payload } = message;
    const signatureMessage = canonicalAuthMessage('WS', identity.requestPath, message.auth, payload);
    if (!await verify(secret, signatureMessage, message.auth.signature)) throw new ProtocolError('AUTH_FAILED', 401);
    return this.commitAuthentication(message.auth);
  }

  private async commitAuthentication(auth: AuthEnvelope): Promise<StoredGroup> {
    return this.ctx.storage.transaction(async transaction => {
      const state = await transaction.get<StoredGroup>(STATE_KEY);
      if (!state || state.schemaVersion !== 2) throw new ProtocolError('SCHEMA_UPGRADE_REQUIRED', 503);
      if (!state.secrets[auth.deviceId]) throw new ProtocolError('AUTH_FAILED', 401);
      const nonces = state.usedNonces[auth.deviceId] ?? [];
      if (nonces.includes(auth.nonce)) throw new ProtocolError('REPLAYED_NONCE', 409);
      if (auth.sequence <= (state.authSequences[auth.deviceId] ?? 0)) throw new ProtocolError('REPLAYED_AUTH_SEQUENCE', 409);
      state.authSequences[auth.deviceId] = auth.sequence;
      state.usedNonces[auth.deviceId] = [...nonces, auth.nonce].slice(-AUTH_NONCE_HISTORY);
      await transaction.put(STATE_KEY, state);
      return state;
    });
  }

  private async parseProtectedBody(request: Request): Promise<unknown> {
    return request.method === 'GET' || request.method === 'DELETE' ? null : readJson(request);
  }

  async fetch(request: Request): Promise<Response> {
    try {
      // Only the Worker can reach this object; public routing never exposes /internal/*.
      const url = new URL(request.url);
      if (request.method === 'POST' && ['/internal/create', '/internal/join'].includes(url.pathname)) {
        const raw = await readJson(request);
        const joining = url.pathname === '/internal/join';
        const input = joining ? parseInternalJoin(raw) : parseInternalCreate(raw);
        const joinCode = joining ? (input as ReturnType<typeof parseInternalJoin>).joinCode : undefined;
        const deviceId = crypto.randomUUID();
        const deviceSecret = randomSecret();
        const now = Date.now();
        const device: Device = { deviceId, displayName: input.displayName, role: joining ? 'MEMBER' : 'OWNER',
          status: 'OFFLINE', limitPercent: null, joinedAt: now, lastSeen: null, lastActivity: null };
        const state = await this.ctx.storage.transaction(async transaction => {
          let state = await transaction.get<StoredGroup>(STATE_KEY);
          if (!joining) {
            if (state) throw new ProtocolError('GROUP_EXISTS', 409);
            state = { schemaVersion: 2, groupId: input.groupId, ownerDeviceId: deviceId, version: 1,
              createdAt: now, updatedAt: now, devices: [device], sequences: { [deviceId]: 0 }, activityUpdatedAt: {},
              joinCode: createJoinCode(input.groupId), joinCodeExpiresAt: now + INVITE_TTL_MS,
              secrets: { [deviceId]: deviceSecret }, authSequences: { [deviceId]: 0 }, usedNonces: { [deviceId]: [] },
              quotaSequences: { [deviceId]: 0 }, officialQuota: null, weeklyEpoch: null,
              usageLedger: emptyLedger([device]), lastNotification: null };
          } else {
            if (!state || state.schemaVersion !== 2 || state.groupId !== input.groupId) throw new ProtocolError('GROUP_NOT_FOUND', 404);
            if (state.joinCode !== joinCode) throw new ProtocolError('INVALID_JOIN_CODE', 403);
            if (state.joinCodeExpiresAt < now) throw new ProtocolError('INVITE_EXPIRED', 410);
            if (state.devices.length >= MAX_DEVICES) throw new ProtocolError('GROUP_FULL', 409);
            state.devices.push(device); state.sequences[deviceId] = 0; state.activityUpdatedAt[deviceId] = 0;
            state.secrets[deviceId] = deviceSecret; state.authSequences[deviceId] = 0; state.usedNonces[deviceId] = [];
            state.quotaSequences[deviceId] = 0; state.usageLedger.deviceUsage[deviceId] = 0;
            state.version++; state.updatedAt = now;
          }
          await transaction.put(STATE_KEY, state);
          return state;
        });
        this.broadcast(state);
        return json({ protocolVersion: 1, groupId: state.groupId, deviceId, deviceSecret,
          ...(joining ? {} : { joinCode: state.joinCode, joinCodeExpiresAt: state.joinCodeExpiresAt }),
          snapshot: publicSnapshot(state) }, 201);
      }

      const state = await this.state();
      const expected = '/v1/groups/' + state.groupId;
      if (url.pathname === expected && request.method === 'GET') {
        const { state: authenticated } = await this.authenticateRequest(request, null);
        return json({ protocolVersion: 1, type: 'GROUP_SNAPSHOT', snapshot: publicSnapshot(authenticated) });
      }
      if (url.pathname === expected + '/ws' && request.method === 'GET') {
        if (request.headers.get('Upgrade')?.toLowerCase() !== 'websocket') throw new ProtocolError('WEBSOCKET_REQUIRED', 426);
        const { auth } = await this.authenticateRequest(request, null);
        const deviceId = identifier(url.searchParams.get('deviceId'));
        if (deviceId !== auth.deviceId) throw new ProtocolError('AUTH_DEVICE_MISMATCH', 401);
        this.device(state, deviceId);
        const pair = new WebSocketPair();
        const client = pair[0]; const server = pair[1];
        for (const previous of this.sockets(deviceId)) previous.close(4001, 'REPLACED');
        server.serializeAttachment({ deviceId, connectionId: crypto.randomUUID(), requestPath: url.pathname + url.search } satisfies Connection);
        this.ctx.acceptWebSocket(server, [deviceId]);
        try {
          const { state: updated } = await this.change(current => {
            const device = this.device(current, deviceId);
            device.status = this.statusFor(current, deviceId); device.lastSeen = Date.now(); return true;
          });
          this.broadcast(updated);
        } catch (error) { server.close(1011, 'STORAGE_FAILED'); throw error; }
        return new Response(null, { status: 101, webSocket: client });
      }

      const rotatePath = expected + '/invite/rotate';
      const ownerPath = expected + '/owner';
      const devicePath = new RegExp('^' + expected.replaceAll('/', '\\/') + '\\/devices\\/([a-f0-9-]+)$').exec(url.pathname);
      const protectedBody = await this.parseProtectedBody(request);
      const authenticated = await this.authenticateRequest(request, protectedBody);
      const actor = this.device(authenticated.state, authenticated.auth.deviceId);
      if (url.pathname === rotatePath && request.method === 'POST') {
        const input = record(protectedBody); keys(input, ['protocolVersion']);
        if (input.protocolVersion !== 1) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
        if (actor.role !== 'OWNER') throw new ProtocolError('OWNER_REQUIRED', 403);
        const { state: updated } = await this.change(current => {
          this.assertOwner(current, actor.deviceId);
          current.joinCode = createJoinCode(current.groupId); current.joinCodeExpiresAt = Date.now() + INVITE_TTL_MS; return true;
        });
        this.broadcast(updated);
        return json({ protocolVersion: 1, joinCode: updated.joinCode, joinCodeExpiresAt: updated.joinCodeExpiresAt, snapshot: publicSnapshot(updated) });
      }
      if (url.pathname === ownerPath && request.method === 'POST') {
        const input = record(protectedBody); keys(input, ['protocolVersion', 'deviceId']);
        if (input.protocolVersion !== 1) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
        if (actor.role !== 'OWNER') throw new ProtocolError('OWNER_REQUIRED', 403);
        const targetId = identifier(input.deviceId);
        const { state: updated } = await this.change(current => {
          this.assertOwner(current, actor.deviceId);
          const target = this.device(current, targetId);
          const currentOwner = this.device(current, current.ownerDeviceId);
          currentOwner.role = 'MEMBER'; target.role = 'OWNER'; current.ownerDeviceId = targetId; return true;
        });
        this.broadcast(updated); return json({ protocolVersion: 1, snapshot: publicSnapshot(updated) });
      }
      if (!devicePath) throw new ProtocolError('NOT_FOUND', 404);
      const targetId = identifier(devicePath[1]);
      if (actor.role !== 'OWNER' && !(request.method === 'DELETE' && targetId === actor.deviceId)) throw new ProtocolError('OWNER_REQUIRED', 403);
      if (request.method === 'PATCH') {
        const input = record(protectedBody);
        if (input.protocolVersion !== 1) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
        const actual = Object.keys(input);
        if (actual.length === 2 && actual.includes('protocolVersion') && actual.includes('displayName')) {
          const name = typeof input.displayName === 'string' ? input.displayName.trim() : '';
          if (!name || name.length > 64 || /[\x00-\x1f\x7f]/u.test(name)) throw new ProtocolError('INVALID_DISPLAY_NAME');
          const { state: updated } = await this.change(current => { this.assertOwner(current, actor.deviceId); this.device(current, targetId).displayName = name; return true; });
          this.broadcast(updated); return json({ protocolVersion: 1, snapshot: publicSnapshot(updated) });
        }
        if (actual.length !== 2 || !actual.includes('protocolVersion') || !actual.includes('limitPercent')) throw new ProtocolError('INVALID_FIELDS');
        const limit = input.limitPercent;
        if (limit !== null && (typeof limit !== 'number' || !Number.isSafeInteger(limit) || limit < 1 || limit > 100)) throw new ProtocolError('INVALID_LIMIT');
        const { state: updated } = await this.change(current => {
          const target = this.device(current, targetId);
          target.limitPercent = limit as number | null;
          this.applyLimitStatuses(current);
          return true;
        });
        this.broadcast(updated); return json({ protocolVersion: 1, snapshot: publicSnapshot(updated) });
      }
      if (request.method === 'DELETE') {
        if (protectedBody !== null) throw new ProtocolError('INVALID_FIELDS');
        const { state: updated } = await this.change(current => {
          if (targetId !== actor.deviceId) this.assertOwner(current, actor.deviceId);
          if (targetId === current.ownerDeviceId) throw new ProtocolError('CANNOT_REMOVE_OWNER', 409);
          this.device(current, targetId);
          current.devices = current.devices.filter(device => device.deviceId !== targetId);
          delete current.sequences[targetId]; delete current.activityUpdatedAt[targetId]; delete current.secrets[targetId];
          delete current.authSequences[targetId]; delete current.usedNonces[targetId]; delete current.quotaSequences[targetId];
          current.usageLedger.unattributedUsage += current.usageLedger.deviceUsage[targetId] ?? 0;
          if (current.pendingActivity) delete current.pendingActivity[targetId];
          delete current.usageLedger.deviceUsage[targetId]; delete current.usageLedger.confidence[targetId]; return true;
        });
        for (const socket of this.sockets(targetId)) socket.close(4003, 'REMOVED');
        this.broadcast(updated); return json({ protocolVersion: 1, snapshot: publicSnapshot(updated) });
      }
      throw new ProtocolError('NOT_FOUND', 404);
    } catch (error) { return errorResponse(error); }
  }

  async webSocketMessage(socket: WebSocket, raw: string | ArrayBuffer): Promise<void> {
    try {
      if (typeof raw !== 'string') { socket.close(1003, 'TEXT_REQUIRED'); return; }
      if (new TextEncoder().encode(raw).byteLength > MAX_MESSAGE_BYTES) { socket.close(1009, 'MESSAGE_TOO_LARGE'); return; }
      const identity = this.connection(socket);
      if (!identity || !this.sockets(identity.deviceId).includes(socket)) throw new ProtocolError('CONNECTION_EXPIRED', 403);
      const message = parseClientMessage(raw, Date.now());
      const authenticated = await this.authenticateMessage(socket, identity, message);
      if (message.type === 'REQUEST_SNAPSHOT') { this.send(socket, snapshotMessage(authenticated)); return; }
      const { state, changed } = await this.change(current => {
        if (!this.sockets(identity.deviceId).includes(socket)) throw new ProtocolError('CONNECTION_EXPIRED', 403);
        const device = this.device(current, identity.deviceId);
        if (message.type === 'HEARTBEAT') {
          if (device.lastSeen !== null && Date.now() - device.lastSeen < HEARTBEAT_WRITE_INTERVAL_MS) return false;
          device.lastSeen = Date.now(); return true;
        }
        if (message.type === 'QUOTA_OBSERVATION') {
          if (message.sequence <= current.quotaSequences[identity.deviceId]) throw new ProtocolError('REPLAYED_QUOTA_SEQUENCE', 409);
          current.quotaSequences[identity.deviceId] = message.sequence;
          device.lastSeen = Date.now();
          return this.applyQuotaObservation(current, identity.deviceId, message.observation);
        }
        if (message.sequence <= current.sequences[identity.deviceId]) throw new ProtocolError('REPLAYED_SEQUENCE', 409);
        if (device.lastActivity && message.activity.windowStart < device.lastActivity.windowEnd) throw new ProtocolError('OVERLAPPING_ACTIVITY', 409);
        const now = Date.now(); const lastActivityUpdate = current.activityUpdatedAt[identity.deviceId];
        if (lastActivityUpdate !== undefined && lastActivityUpdate > 0 && now - lastActivityUpdate < ACTIVITY_UPDATE_INTERVAL_MS) throw new ProtocolError('RATE_LIMITED', 429);
        current.activityUpdatedAt[identity.deviceId] = now;
        current.sequences[identity.deviceId] = message.sequence;
        current.pendingActivity ??= {};
        const pending = current.pendingActivity[identity.deviceId];
        current.pendingActivity[identity.deviceId] = {
          tokens: Math.min(1e12, (pending && now - pending.receivedAt < 300_000 ? pending.tokens : 0) + message.activity.tokenDelta),
          receivedAt: now
        };
        device.lastActivity = message.activity; device.lastSeen = Date.now(); return true;
      });
      if (changed) this.broadcast(state);
      else this.send(socket, { protocolVersion: 1, type: 'HEARTBEAT_ACK', version: state.version });
      if (message.type === 'ACTIVITY_UPDATE' || message.type === 'QUOTA_OBSERVATION')
        this.send(socket, { protocolVersion: 1, type: 'REPORT_ACK', kind: message.type, sequence: message.sequence });
    } catch (error) {
      this.send(socket, { protocolVersion: 1, type: 'ERROR', code: error instanceof ProtocolError ? error.code : 'INTERNAL_ERROR' });
    }
  }

  private applyQuotaObservation(state: StoredGroup, deviceId: string, observation: QuotaObservation): boolean {
    const now = Date.now();
    const previous = state.officialQuota;
    if (previous && observation.observedAt !== undefined && observation.observedAt < previous.observedAt) return false;
    if (previous && observation.weeklyResetAt < previous.weeklyResetAt) return false;
    if (!previous) {
      state.officialQuota = { weeklyUsedPercent: observation.weeklyUsedPercent,
        weeklyRemainingPercent: 100 - observation.weeklyUsedPercent,
        weeklyResetAt: observation.weeklyResetAt, planType: observation.planType, observedAt: observation.observedAt ?? now };
      state.weeklyEpoch = { epochId: crypto.randomUUID(), startedAt: now, resetAt: observation.weeklyResetAt,
        lastQuota: observation.weeklyUsedPercent };
      this.applyLimitStatuses(state);
      return true;
    }

    const resetPossible = observation.weeklyResetAt > previous.weeklyResetAt &&
      (observation.weeklyUsedPercent <= previous.weeklyUsedPercent - RESET_DROP_THRESHOLD ||
       (now >= previous.weeklyResetAt && observation.weeklyUsedPercent <= previous.weeklyUsedPercent));
    let resetDetected = false;
    if (resetPossible) {
      let candidate = state.resetCandidate;
      if (!candidate || candidate.resetAt !== observation.weeklyResetAt || Math.abs(candidate.used - observation.weeklyUsedPercent) > 2) {
        candidate = { resetAt: observation.weeklyResetAt, used: observation.weeklyUsedPercent, firstSeen: now, devices: [] };
      }
      if (!candidate.devices.includes(deviceId)) candidate.devices.push(deviceId);
      state.resetCandidate = candidate;
      resetDetected = candidate.devices.length >= 2 || (now >= previous.weeklyResetAt && now - candidate.firstSeen >= 60_000);
      if (!resetDetected) return true; // Persist corroboration without changing the epoch/ledger.
    }
    if (resetDetected) {
      state.officialQuota = { weeklyUsedPercent: observation.weeklyUsedPercent,
        weeklyRemainingPercent: 100 - observation.weeklyUsedPercent,
        weeklyResetAt: observation.weeklyResetAt, planType: observation.planType, observedAt: now };
      state.weeklyEpoch = { epochId: crypto.randomUUID(), startedAt: now, resetAt: observation.weeklyResetAt,
        lastQuota: observation.weeklyUsedPercent };
      state.usageLedger = emptyLedger(state.devices);
      state.pendingActivity = {}; delete state.resetCandidate;
      this.notify(state, { eventId: crypto.randomUUID(), type: 'WEEKLY_RESET', createdAt: now });
      this.applyLimitStatuses(state);
      return true;
    }

    if (observation.weeklyResetAt !== previous.weeklyResetAt || observation.weeklyUsedPercent < previous.weeklyUsedPercent) return false;
    const delta = observation.weeklyUsedPercent - previous.weeklyUsedPercent;
    state.officialQuota = { weeklyUsedPercent: observation.weeklyUsedPercent,
      weeklyRemainingPercent: 100 - observation.weeklyUsedPercent,
      weeklyResetAt: previous.weeklyResetAt, planType: observation.planType, observedAt: now };
    if (state.weeklyEpoch) state.weeklyEpoch.lastQuota = observation.weeklyUsedPercent;
    if (delta <= 0) { this.applyLimitStatuses(state); return true; }

    const pending = state.pendingActivity ?? {};
    const activities = state.devices.filter(device => pending[device.deviceId]?.tokens > 0 && now - pending[device.deviceId].receivedAt < 300_000);
    const totalTokens = activities.reduce((sum, device) => sum + pending[device.deviceId].tokens, 0);
    state.pendingActivity = {};
    if (totalTokens <= 0) {
      state.usageLedger.unattributedUsage += delta;
      this.applyLimitStatuses(state);
      return true;
    }
    const confidence = activities.length === 1 ? 'HIGH' : 'MEDIUM';
    for (const device of activities) {
      const weight = pending[device.deviceId].tokens / totalTokens;
      state.usageLedger.deviceUsage[device.deviceId] = (state.usageLedger.deviceUsage[device.deviceId] ?? 0) + delta * weight;
      state.usageLedger.confidence[device.deviceId] = confidence;
    }
    this.applyLimitStatuses(state);
    return true;
  }

  private notify(state: StoredGroup, notification: NonNullable<StoredGroup['lastNotification']>): void {
    state.lastNotification = notification;
    state.notifications = [...(state.notifications ?? []), notification].slice(-16);
  }

  private applyLimitStatuses(state: StoredGroup): void {
    for (const device of state.devices) {
      const previous = device.status;
      const next = this.statusFor(state, device.deviceId);
      device.status = next;
      if (next === 'LIMIT_REACHED' && previous !== 'LIMIT_REACHED') {
        this.notify(state, { eventId: crypto.randomUUID(), type: 'DEVICE_LIMIT_REACHED', deviceId: device.deviceId, createdAt: Date.now() });
      }
    }
  }

  async webSocketClose(socket: WebSocket, code: number): Promise<void> {
    try { socket.close(code === 1005 || code === 1006 || code === 1015 ? 1000 : code, 'CLOSED'); } catch { /* Already closed. */ }
    await this.disconnect(socket);
  }

  async webSocketError(socket: WebSocket): Promise<void> {
    try { socket.close(1011, 'CONNECTION_ERROR'); } catch { /* Already closed. */ }
    await this.disconnect(socket);
  }

  private async disconnect(socket: WebSocket): Promise<void> {
    const identity = this.connection(socket);
    if (!identity || this.sockets(identity.deviceId).some(other => other !== socket)) return;
    const { state, changed } = await this.change(current => {
      if (this.sockets(identity.deviceId).some(other => other !== socket)) return false;
      const device = current.devices.find(item => item.deviceId === identity.deviceId);
      if (!device || device.status === 'OFFLINE') return false;
      const next = this.statusFor(current, identity.deviceId);
      if (device.status === next) return false;
      device.status = next; return true;
    });
    if (changed) this.broadcast(state);
  }
}

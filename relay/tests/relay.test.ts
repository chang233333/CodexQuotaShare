import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';
import { canonicalAuthMessage, randomNonce, sign } from '../src/auth';
import { createRuntime } from './runtime';
import type { GroupSnapshot } from '../src/protocol';
let SELF: Awaited<ReturnType<typeof createRuntime>>;
beforeAll(async () => { SELF = await createRuntime(); });
afterAll(async () => { await SELF.dispose(); });
const headers = { 'Content-Type': 'application/json', 'X-CQS-Local-Test': '1' };
interface Membership { groupId: string; deviceId: string; deviceSecret: string; joinCode?: string; snapshot: GroupSnapshot; authSequence: number; }
const clients: Client[] = [];
class Client {
  messages: any[] = [];
  socket: WebSocket;
  constructor(socket: WebSocket, private readonly member: Membership, private readonly requestPath: string) {
    this.socket = socket;
    socket.addEventListener('message', event => {
      this.messages.push(event.data === 'pong' ? 'pong' : JSON.parse(event.data as string));
    });
    socket.addEventListener('close', event => {
      if (socket.readyState === WebSocket.OPEN) socket.close(event.code === 1005 ? 1000 : event.code);
    });
    socket.accept(); clients.push(this);
  }
  async send(value: Record<string, unknown>) {
    const auth = await messageAuth(this.member, this.requestPath, value);
    this.socket.send(JSON.stringify({ ...value, auth }));
  }
  async next(predicate: (message: any) => boolean): Promise<any> {
    const deadline = Date.now() + 3000;
    for (;;) {
      const i = this.messages.findIndex(predicate);
      if (i >= 0) return this.messages.splice(i, 1)[0];
      if (Date.now() >= deadline) throw new Error('Expected WebSocket message timed out');
      await new Promise(resolve => setTimeout(resolve, 5));
    }
  }
  async snapshot(version = 0): Promise<GroupSnapshot> {
    return (await this.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.version >= version)).snapshot;
  }
  close() { if (this.socket.readyState === WebSocket.OPEN) this.socket.close(1000, 'TEST_DONE'); }
}
afterEach(async () => {
  for (const client of clients.splice(0)) client.close();
  // Let workerd finish close callbacks; each test uses a new random group.
  await new Promise(resolve => setTimeout(resolve, 30));
});
async function post(route: string, body: unknown) {
  return SELF.fetch('http://localhost' + route, { method: 'POST', headers, body: JSON.stringify(body) });
}
async function requestAuth(member: Membership, method: string, path: string, payload: unknown): Promise<Record<string, string>> {
  const auth = { deviceId: member.deviceId, timestamp: Date.now(), nonce: randomNonce(), sequence: ++member.authSequence, signature: '' };
  const { signature: _signature, ...unsigned } = auth;
  auth.signature = await sign(member.deviceSecret, canonicalAuthMessage(method, path, unsigned, payload));
  return { 'X-CQS-Device-Id': auth.deviceId, 'X-CQS-Timestamp': String(auth.timestamp), 'X-CQS-Nonce': auth.nonce,
    'X-CQS-Sequence': String(auth.sequence), 'X-CQS-Signature': auth.signature };
}
async function messageAuth(member: Membership, path: string, payload: Record<string, unknown>) {
  const auth = { deviceId: member.deviceId, timestamp: Date.now(), nonce: randomNonce(), sequence: ++member.authSequence, signature: '' };
  const { signature: _signature, ...unsigned } = auth;
  auth.signature = await sign(member.deviceSecret, canonicalAuthMessage('WS', path, unsigned, payload));
  return auth;
}
async function create(displayName = 'MAIN-PC'): Promise<Membership> {
  const response = await post('/v1/groups', { protocolVersion: 1, displayName });
  expect(response.status).toBe(201); return { ...(await response.json()), authSequence: 0 };
}
async function join(owner: Membership, displayName = 'LAB-PC'): Promise<Membership> {
  const response = await post('/v1/groups/join', { protocolVersion: 1, joinCode: owner.joinCode, displayName });
  expect(response.status).toBe(201); return { ...(await response.json()), authSequence: 0 };
}
async function connect(member: Membership): Promise<Client> {
  const path = '/v1/groups/' + member.groupId + '/ws?deviceId=' + member.deviceId;
  const auth = await requestAuth(member, 'GET', path, null);
  const response = await SELF.fetch('http://localhost' + path, { headers: { ...headers, ...auth, Upgrade: 'websocket' } });
  expect(response.status).toBe(101); return new Client(response.webSocket!, member, path);
}
async function getState(member: Membership): Promise<GroupSnapshot> {
  const path = '/v1/groups/' + member.groupId;
  const auth = await requestAuth(member, 'GET', path, null);
  const response = await SELF.fetch('http://localhost' + path, { headers: { ...headers, ...auth } });
  expect(response.status).toBe(200); const message: any = await response.json(); return message.snapshot;
}
function activity(sequence = 1, tokenDelta = 100, start = Date.now() - 20_000, end = Date.now()) {
  return { protocolVersion: 1, type: 'ACTIVITY_UPDATE', sequence,
    activity: { windowStart: start, windowEnd: end, tokenDelta, activeSessionCount: 1 } };
}
function quota(sequence: number, weeklyUsedPercent: number, weeklyResetAt: number) {
  return { protocolVersion: 1, type: 'QUOTA_OBSERVATION', sequence,
    observation: { weeklyUsedPercent, weeklyResetAt, planType: 'Plus' } };
}
describe('Worker + SQLite-backed QuotaGroup', () => {
  it('creates owner, joins member, synchronizes two clients', async () => {
    const owner = await create(); const a = await connect(owner); await a.snapshot();
    const member = await join(owner); const b = await connect(member);
    const connected = await b.snapshot(); expect(connected.devices).toHaveLength(2);
    expect(connected.devices.map(d => d.status)).toEqual(['ONLINE', 'ONLINE']);
    await b.send(activity());
    const observed = await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1]?.lastActivity?.tokenDelta === 100);
    const peer = await b.snapshot(observed.snapshot.version);
    expect(peer).toEqual(observed.snapshot);
    expect(peer.devices[1].role).toBe('MEMBER');
    expect(peer).toEqual(await getState(member));
  });
  it('broadcasts to four clients with owner offline and preserves activity on reconnect', async () => {
    const owner = await create(); const members = [owner];
    for (const name of ['LAB-PC', 'LAPTOP', 'OFFICE-PC']) members.push(await join(owner, name));
    const connections: Client[] = [];
    for (const member of members) { const client = await connect(member); await client.snapshot(); connections.push(client); }
    const before = await getState(owner); expect(before.devices.every(d => d.status === 'ONLINE')).toBe(true);
    connections[0].close();
    await connections[1].next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].status === 'OFFLINE');
    await connections[1].send(activity(1, 800));
    const observations = await Promise.all(connections.slice(1).map(c => c.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity?.tokenDelta === 800)));
    expect(observations[1]).toEqual(observations[0]); expect(observations[2]).toEqual(observations[0]);
    connections[1].close();
    await connections[2].next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].status === 'OFFLINE');
    const restored = await connect(members[1]); const snapshot = await restored.snapshot();
    expect(snapshot.devices[1].lastActivity?.tokenDelta).toBe(800);
    expect(snapshot.devices[0].status).toBe('OFFLINE');
    await restored.send(activity(1, 900)); expect((await restored.next(m => m.type === 'ERROR')).code).toBe('REPLAYED_SEQUENCE');
  });
  it('enforces the four-device limit under concurrent joins', async () => {
    const owner = await create();
    const responses = await Promise.all(Array.from({ length: 8 }, (_, index) => post('/v1/groups/join',
      { protocolVersion: 1, joinCode: owner.joinCode, displayName: 'PC-' + index })));
    await Promise.all(responses.map(response => response.text()));
    expect(responses.filter(r => r.status === 201)).toHaveLength(3);
    expect(responses.filter(r => r.status === 409)).toHaveLength(5);
    expect((await getState(owner)).devices).toHaveLength(4);
  });
  it('replaces old device connections without a false offline transition', async () => {
    const member = await create(); const old = await connect(member); await old.snapshot();
    const closed = new Promise<number>(resolve => old.socket.addEventListener('close', event => resolve(event.code), { once: true }));
    const replacement = await connect(member); await replacement.snapshot();
    expect(await closed).toBe(4001);
    expect(old.socket.readyState).not.toBe(WebSocket.OPEN);
    expect((await getState(member)).devices[0].status).toBe('ONLINE');
    await replacement.send(activity());
    expect((await replacement.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity)).snapshot.devices[0].lastActivity.tokenDelta).toBe(100);
  });
  it('persists before broadcast and keeps sequence high-water in storage', async () => {
    const member = await create(); const client = await connect(member); await client.snapshot();
    await client.send(activity(42, 123));
    const message = await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity);
    const inspection = await SELF.inspect(member.groupId);
    expect(inspection.stored.version).toBe(message.snapshot.version);
    expect(inspection.stored.sequences[member.deviceId]).toBe(42);
    expect(inspection.stored.devices[0].lastActivity.tokenDelta).toBe(123);
    expect(Object.keys(inspection.attachments[0]).sort()).toEqual(['connectionId', 'deviceId', 'requestPath']);
  });
  it('rejects duplicates and overlapping intervals without changing state', async () => {
    const member = await create(); const client = await connect(member); await client.snapshot();
    const event = activity(1); await client.send(event);
    await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity);
    const before = await getState(member);
    await client.send(event); expect((await client.next(m => m.type === 'ERROR')).code).toBe('REPLAYED_SEQUENCE');
    await client.send({ ...event, sequence: 2 }); expect((await client.next(m => m.type === 'ERROR')).code).toBe('OVERLAPPING_ACTIVITY');
    expect(await getState(member)).toEqual(before);
    await new Promise(resolve => setTimeout(resolve, 5_050));
    await client.send(activity(2, 20, event.activity.windowEnd, event.activity.windowEnd + 1));
    const next = await client.snapshot(before.version + 1);
    expect(next.devices[0].lastActivity?.tokenDelta).toBe(20);
  });
  it('rate-limits activity reports per device and keeps the limit after restart', async () => {
    const member = await create(); const client = await connect(member); await client.snapshot();
    const first = activity(1, 10); await client.send(first);
    await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity?.tokenDelta === 10);
    await client.send(activity(2, 20, first.activity.windowEnd, first.activity.windowEnd + 1));
    expect((await client.next(m => m.type === 'ERROR')).code).toBe('RATE_LIMITED');
    await SELF.restart();
    const restored = await connect(member); await restored.snapshot();
    await restored.send(activity(2, 20, first.activity.windowEnd, first.activity.windowEnd + 1));
    expect((await restored.next(m => m.type === 'ERROR')).code).toBe('RATE_LIMITED');
    await new Promise(resolve => setTimeout(resolve, 5_050));
    await restored.send(activity(2, 20, first.activity.windowEnd, first.activity.windowEnd + 1));
    expect((await restored.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity?.tokenDelta === 20)).snapshot.devices[0].lastActivity?.tokenDelta).toBe(20);
  });
  it('supports complete same-version snapshot repair and idle keepalive without writes', async () => {
    const member = await create(); const client = await connect(member); const first = await client.snapshot();
    await client.send({ protocolVersion: 1, type: 'REQUEST_SNAPSHOT' }); expect(await client.snapshot()).toEqual(first);
    await client.send({ protocolVersion: 1, type: 'HEARTBEAT' });
    expect((await client.next(m => m.type === 'HEARTBEAT_ACK')).version).toBe(first.version);
    client.socket.send('ping'); expect(await client.next(m => m === 'pong')).toBe('pong');
    expect(await getState(member)).toEqual(first);
  });
  it('attributes quota delta to the only active device with high confidence', async () => {
    const owner = await create(); const member = await join(owner);
    const a = await connect(owner); await a.snapshot(); const b = await connect(member); await b.snapshot();
    const resetAt = Date.now() + 7 * 86_400_000;
    await a.send(quota(1, 40, resetAt)); await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 40);
    await b.send(activity(1, 800, Date.now() - 20_000, Date.now()));
    await b.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity?.tokenDelta === 800);
    await a.send(quota(2, 42, resetAt));
    const snapshot = (await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 42)).snapshot;
    expect(snapshot.usageLedger.deviceUsage[member.deviceId]).toBeCloseTo(2);
    expect(snapshot.usageLedger.unattributedUsage).toBe(0);
    expect(snapshot.usageLedger.confidence[member.deviceId]).toBe('HIGH');
  });
  it('splits quota delta by activity weights and keeps unattributed usage explicit', async () => {
    const owner = await create(); const member = await join(owner);
    const a = await connect(owner); await a.snapshot(); const b = await connect(member); await b.snapshot();
    const resetAt = Date.now() + 7 * 86_400_000;
    await a.send(quota(1, 40, resetAt)); await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 40);
    await a.send(activity(1, 80, Date.now() - 20_000, Date.now()));
    await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity?.tokenDelta === 80);
    await b.send(activity(1, 20, Date.now() - 20_000, Date.now()));
    await b.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity?.tokenDelta === 20);
    await a.send(quota(2, 42, resetAt));
    const weighted = (await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 42)).snapshot;
    expect(weighted.usageLedger.deviceUsage[owner.deviceId]).toBeCloseTo(1.6);
    expect(weighted.usageLedger.deviceUsage[member.deviceId]).toBeCloseTo(0.4);
    expect(weighted.usageLedger.confidence[owner.deviceId]).toBe('MEDIUM');

    const isolated = await create('UNATTRIBUTED'); const c = await connect(isolated); await c.snapshot();
    const isolatedReset = Date.now() + 7 * 86_400_000;
    await c.send(quota(1, 50, isolatedReset)); await c.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 50);
    await c.send(quota(2, 52, isolatedReset));
    const unknown = (await c.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 52)).snapshot;
    expect(unknown.usageLedger.unattributedUsage).toBe(2);
  });
  it('confirms weekly reset only on resetAt change plus a sharp decrease and emits one event', async () => {
    const owner = await create(); const member = await join(owner); const peer = await connect(member); await peer.snapshot();
    const client = await connect(owner); await client.snapshot();
    const firstReset = Date.now() + 2 * 86_400_000; const secondReset = firstReset + 7 * 86_400_000;
    await client.send(quota(1, 83, firstReset));
    const first = (await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 83)).snapshot;
    await client.send(quota(2, 80, secondReset));
    expect((await client.next(m => m.type === 'HEARTBEAT_ACK')).version).toBe(first.version);
    await client.send(quota(3, 1, secondReset));
    const candidate = await client.snapshot(first.version + 1);
    expect(candidate.weeklyEpoch?.epochId).toBe(first.weeklyEpoch?.epochId);
    await peer.send(quota(1, 1, secondReset));
    const reset = (await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 1)).snapshot;
    expect(reset.weeklyEpoch?.resetAt).toBe(secondReset);
    expect(reset.usageLedger.unattributedUsage).toBe(0);
    expect(reset.lastNotification?.type).toBe('WEEKLY_RESET');
    expect(reset.lastNotification?.eventId).not.toBe(first.lastNotification?.eventId);
  });
  it('owner limit reaches a device status and broadcasts a deduplicable notification', async () => {
    const owner = await create(); const client = await connect(owner); await client.snapshot();
    const path = '/v1/groups/' + owner.groupId + '/devices/' + owner.deviceId;
    const body = { protocolVersion: 1, limitPercent: 1 };
    const policy = await SELF.fetch('http://localhost' + path, { method: 'PATCH', headers: { ...headers, ...(await requestAuth(owner, 'PATCH', path, body)) }, body: JSON.stringify(body) });
    expect(policy.status).toBe(200);
    const resetAt = Date.now() + 7 * 86_400_000;
    await client.send(quota(1, 40, resetAt)); await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 40);
    await client.send(activity(1, 100, Date.now() - 20_000, Date.now())); await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity?.tokenDelta === 100);
    await client.send(quota(2, 42, resetAt));
    const reached = (await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.officialQuota?.weeklyUsedPercent === 42)).snapshot;
    expect(reached.devices[0].status).toBe('LIMIT_REACHED');
    expect(reached.devices[0].limitPercent).toBe(1);
    expect(reached.lastNotification?.type).toBe('DEVICE_LIMIT_REACHED');
    expect(reached.lastNotification?.deviceId).toBe(owner.deviceId);
    const unlimited = { protocolVersion: 1, limitPercent: null };
    const clear = await SELF.fetch('http://localhost' + path, { method: 'PATCH', headers: { ...headers, ...(await requestAuth(owner, 'PATCH', path, unlimited)) }, body: JSON.stringify(unlimited) });
    expect(clear.status).toBe(200);
  });
  it('does not broadcast invalid input or return raw private input', async () => {
    const owner = await create(); const client = await connect(owner); await client.snapshot();
    const before = await getState(owner);
    client.socket.send(JSON.stringify({ ...activity(), privateMarker: 'SYNTHETIC_PRIVATE_MARKER' }));
    const error = await client.next(m => m.type === 'ERROR');
    expect(error.code).toBe('INVALID_FIELDS'); expect(JSON.stringify(error)).not.toContain('SYNTHETIC_PRIVATE_MARKER');
    client.socket.send(JSON.stringify({ protocolVersion: 1, type: 'POLICY_UPDATE', estimatedUsagePercent: 0 }));
    expect((await client.next(m => m.type === 'ERROR')).code).toBe('UNKNOWN_MESSAGE_TYPE');
    expect(await getState(owner)).toEqual(before);
  });
  it('isolates groups and rejects unknown members', async () => {
    const first = await create('A'); const second = await create('B');
    const wrongPath = '/v1/groups/' + first.groupId;
    const wrongAuth = await requestAuth(second, 'GET', wrongPath, null);
    const response = await SELF.fetch('http://localhost' + wrongPath,
      { headers: { ...headers, ...wrongAuth } });
    expect(response.status).toBe(401);
    expect((await getState(first)).devices.map(d => d.displayName)).toEqual(['A']);
    expect((await getState(second)).devices.map(d => d.displayName)).toEqual(['B']);
  });
  it('rejects public origins, browser origins and disabled local mode', async () => {
    const blocked = await SELF.fetch('https://public.example/v1/groups', { method: 'POST', headers, body: '{}' });
    expect(blocked.status).toBe(403);
    const browser = await SELF.fetch('http://localhost/v1/groups', { method: 'POST', headers: { ...headers, Origin: 'https://evil.example' }, body: '{}' });
    expect(browser.status).toBe(403);
    const noHeader = await SELF.fetch('http://localhost/v1/groups', { method: 'POST', body: '{}' });
    expect(noHeader.status).toBe(403);
    const disabledRuntime = await createRuntime('false');
    try {
      const disabled = await disabledRuntime.fetch('http://localhost/v1/groups', { method: 'POST', headers, body: '{}' });
      expect(disabled.status).toBe(403);
    } finally { await disabledRuntime.dispose(); }
  });
  it('rejects oversized HTTP bodies and binary/oversized WebSocket frames', async () => {
    const response = await SELF.fetch('http://localhost/v1/groups', { method: 'POST', headers, body: 'x'.repeat(16_385) });
    expect(response.status).toBe(413);
    const owner = await create();
    for (const payload of [new Uint8Array([1, 2, 3]).buffer, 'x'.repeat(16_385)]) {
      const client = await connect(owner); await client.snapshot();
      const closed = new Promise<number>(resolve => client.socket.addEventListener('close', event => resolve(event.code), { once: true }));
      client.socket.send(payload); expect(await closed).toBe(typeof payload === 'string' ? 1009 : 1003);
    }
  });
  it('recovers hibernating connections after actual workerd eviction', async () => {
    const owner = await create(); const member = await join(owner);
    const a = await connect(owner); await a.snapshot();
    const b = await connect(member); await b.snapshot();
    const firstEvent = activity(1, 80); await b.send(firstEvent);
    const before = (await b.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity)).snapshot;
    const previous = await SELF.inspect(owner.groupId);
    // workerd evicts inactive DO instances after 10 seconds. No timers exist inside the DO.
    await new Promise(resolve => setTimeout(resolve, 12_000));
    await b.send({ protocolVersion: 1, type: 'REQUEST_SNAPSHOT' });
    expect(await b.snapshot(before.version)).toEqual(before);
    const restored = await SELF.inspect(owner.groupId);
    expect(restored.instanceId).not.toBe(previous.instanceId);
    expect(restored.attachments).toHaveLength(2);
    await b.send(firstEvent); expect((await b.next(m => m.type === 'ERROR')).code).toBe('REPLAYED_SEQUENCE');
    await b.send(activity(2, 20, firstEvent.activity.windowEnd, Date.now()));
    const updated = await a.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity?.tokenDelta === 20);
    expect(updated.snapshot.version).toBe(before.version + 1);
    expect(updated.snapshot.devices.every((d: any) => d.status === 'ONLINE')).toBe(true);
  });
  it('restores group and sequence from SQLite after a full runtime restart', async () => {
    const owner = await create(); const member = await join(owner);
    const b = await connect(member); await b.snapshot();
    const event = activity(17, 456); await b.send(event);
    const before = (await b.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity)).snapshot;
    await SELF.restart();
    const restored = await getState(member);
    expect(restored.groupId).toBe(owner.groupId);
    expect(restored.ownerDeviceId).toBe(owner.deviceId);
    expect(restored.devices).toHaveLength(2);
    expect(restored.devices[1].lastActivity).toEqual(event.activity);
    expect(restored.devices.every(d => d.status === 'OFFLINE')).toBe(true);
    expect(restored.version).toBeGreaterThanOrEqual(before.version);
    const reconnected = await connect(member); await reconnected.snapshot();
    await reconnected.send(event); expect((await reconnected.next(m => m.type === 'ERROR')).code).toBe('REPLAYED_SEQUENCE');
    await reconnected.send(activity(18, 5, event.activity.windowEnd, Date.now()));
    expect((await reconnected.next(m => m.type === 'ERROR')).code).toBe('RATE_LIMITED');
    await new Promise(resolve => setTimeout(resolve, 5_050));
    await reconnected.send(activity(18, 5, event.activity.windowEnd, Date.now()));
    const updated = await reconnected.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[1].lastActivity?.tokenDelta === 5);
    expect(updated.snapshot.devices[0].status).toBe('OFFLINE');
  });

  it('requires HMAC credentials and rejects replayed authentication', async () => {
    const owner = await create(); const path = '/v1/groups/' + owner.groupId;
    const missing = await SELF.fetch('http://localhost' + path, { headers });
    expect(missing.status).toBe(401);
    const validHeaders = await requestAuth(owner, 'GET', path, null);
    const valid = await SELF.fetch('http://localhost' + path, { headers: { ...headers, ...validHeaders } });
    expect(valid.status).toBe(200);
    const replay = await SELF.fetch('http://localhost' + path, { headers: { ...headers, ...validHeaders } });
    expect(replay.status).toBe(409);
    const tamperedHeaders = await requestAuth(owner, 'GET', path, null);
    tamperedHeaders['X-CQS-Signature'] = (tamperedHeaders['X-CQS-Signature'].startsWith('A') ? 'B' : 'A') + tamperedHeaders['X-CQS-Signature'].slice(1);
    const tampered = await SELF.fetch('http://localhost' + path, { headers: { ...headers, ...tamperedHeaders } });
    expect(tampered.status).toBe(401);
  });

  it('authenticates WebSocket messages independently of the handshake', async () => {
    const owner = await create(); const client = await connect(owner); await client.snapshot();
    const payload = activity(1, 7);
    const invalidAuth = await messageAuth(owner, '/v1/groups/' + owner.groupId + '/ws?deviceId=' + owner.deviceId, payload);
    invalidAuth.signature = (invalidAuth.signature.startsWith('A') ? 'B' : 'A') + invalidAuth.signature.slice(1);
    client.socket.send(JSON.stringify({ ...payload, auth: invalidAuth }));
    expect((await client.next(m => m.type === 'ERROR')).code).toBe('AUTH_FAILED');
    await client.send(payload);
    expect((await client.next(m => m.type === 'GROUP_SNAPSHOT' && m.snapshot.devices[0].lastActivity?.tokenDelta === 7)).snapshot.devices[0].lastActivity?.tokenDelta).toBe(7);
  });

  it('rotates expiring invites and enforces owner-only mutations', async () => {
    const owner = await create(); const originalCode = owner.joinCode!; const member = await join(owner);
    const rotatePath = '/v1/groups/' + owner.groupId + '/invite/rotate'; const rotateBody = { protocolVersion: 1 };
    const memberRotate = await SELF.fetch('http://localhost' + rotatePath, { method: 'POST', headers: { ...headers, ...(await requestAuth(member, 'POST', rotatePath, rotateBody)) }, body: JSON.stringify(rotateBody) });
    expect(memberRotate.status).toBe(403);
    const rotatedHeaders = await requestAuth(owner, 'POST', rotatePath, rotateBody);
    const rotated = await SELF.fetch('http://localhost' + rotatePath, { method: 'POST', headers: { ...headers, ...rotatedHeaders }, body: JSON.stringify(rotateBody) });
    expect(rotated.status).toBe(200); const rotatedBody: any = await rotated.json();
    expect(rotatedBody.joinCode).not.toBe(originalCode);
    const oldJoin = await post('/v1/groups/join', { protocolVersion: 1, joinCode: originalCode, displayName: 'OLD' });
    expect(oldJoin.status).toBe(403);
    const newMember = await join({ ...owner, joinCode: rotatedBody.joinCode, authSequence: owner.authSequence });
    expect(newMember.snapshot.devices).toHaveLength(3);
  });

  it('allows owner rename/removal but denies member mutation', async () => {
    const owner = await create(); const member = await join(owner);
    const devicePath = '/v1/groups/' + owner.groupId + '/devices/' + member.deviceId;
    const renameBody = { protocolVersion: 1, displayName: 'RENAMED' };
    const memberRename = await SELF.fetch('http://localhost' + devicePath, { method: 'PATCH', headers: { ...headers, ...(await requestAuth(member, 'PATCH', devicePath, renameBody)) }, body: JSON.stringify(renameBody) });
    expect(memberRename.status).toBe(403);
    const ownerRename = await SELF.fetch('http://localhost' + devicePath, { method: 'PATCH', headers: { ...headers, ...(await requestAuth(owner, 'PATCH', devicePath, renameBody)) }, body: JSON.stringify(renameBody) });
    expect(ownerRename.status).toBe(200);
    expect((await getState(owner)).devices.find(d => d.deviceId === member.deviceId)?.displayName).toBe('RENAMED');
    const removed = await SELF.fetch('http://localhost' + devicePath, { method: 'DELETE', headers: { ...headers, ...(await requestAuth(owner, 'DELETE', devicePath, null)) } });
    expect(removed.status).toBe(200);
    expect((await getState(owner)).devices).toHaveLength(1);
    const removedAuth = await requestAuth(member, 'GET', '/v1/groups/' + owner.groupId, null);
    const removedRead = await SELF.fetch('http://localhost/v1/groups/' + owner.groupId, { headers: { ...headers, ...removedAuth } });
    expect(removedRead.status).toBe(401);
  });

  it('transfers owner role only through an owner-authorized request', async () => {
    const owner = await create(); const member = await join(owner);
    const path = '/v1/groups/' + owner.groupId + '/owner'; const body = { protocolVersion: 1, deviceId: member.deviceId };
    const denied = await SELF.fetch('http://localhost' + path, { method: 'POST', headers: { ...headers, ...(await requestAuth(member, 'POST', path, body)) }, body: JSON.stringify(body) });
    expect(denied.status).toBe(403);
    const transferred = await SELF.fetch('http://localhost' + path, { method: 'POST', headers: { ...headers, ...(await requestAuth(owner, 'POST', path, body)) }, body: JSON.stringify(body) });
    expect(transferred.status).toBe(200);
    const snapshot = await getState(member);
    expect(snapshot.ownerDeviceId).toBe(member.deviceId);
    expect(snapshot.devices.find(d => d.deviceId === owner.deviceId)?.role).toBe('MEMBER');
    expect(snapshot.devices.find(d => d.deviceId === member.deviceId)?.role).toBe('OWNER');
    const rotatePath = '/v1/groups/' + owner.groupId + '/invite/rotate'; const rotateBody = { protocolVersion: 1 };
    const newOwnerRotate = await SELF.fetch('http://localhost' + rotatePath, { method: 'POST', headers: { ...headers, ...(await requestAuth(member, 'POST', rotatePath, rotateBody)) }, body: JSON.stringify(rotateBody) });
    expect(newOwnerRotate.status).toBe(200);
  });

  it('retains activity across unchanged observations and zero reports', async () => {
    const owner = await create(); const member = await join(owner);
    const a = await connect(owner); await a.snapshot(); const b = await connect(member); await b.snapshot();
    const resetAt = Date.now() + 3 * 86_400_000;
    await a.send(quota(1, 40, resetAt)); await a.next(m => m.snapshot?.officialQuota?.weeklyUsedPercent === 40);
    const first = activity(1, 80); await a.send(first);
    await a.next(m => m.snapshot?.devices[0].lastActivity?.tokenDelta === 80);
    await b.send(activity(1, 20)); await b.next(m => m.snapshot?.devices[1].lastActivity?.tokenDelta === 20);
    await b.send(quota(1, 40, resetAt)); await b.next(m => m.snapshot?.officialQuota?.weeklyUsedPercent === 40);
    await new Promise(resolve => setTimeout(resolve, 5050));
    await a.send(activity(2, 0, first.activity.windowEnd, Date.now()));
    await a.next(m => m.snapshot?.devices[0].lastActivity?.tokenDelta === 0);
    await a.send(quota(2, 42, resetAt));
    const result = (await a.next(m => m.snapshot?.officialQuota?.weeklyUsedPercent === 42)).snapshot;
    expect(result.usageLedger.deviceUsage[owner.deviceId]).toBeCloseTo(1.6);
    expect(result.usageLedger.deviceUsage[member.deviceId]).toBeCloseTo(.4);
    await a.send(quota(3, 43, resetAt));
    expect((await a.next(m => m.snapshot?.officialQuota?.weeklyUsedPercent === 43)).snapshot.usageLedger.unattributedUsage).toBe(1);
  });

  it('serves production HTTPS pairing and rate limits anonymous creation', async () => {
    const production = await createRuntime('false');
    try {
      const responses = [];
      for (let i = 0; i < 11; i++) responses.push(await production.fetch('https://relay.example/v1/groups', {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'CF-Connecting-IP': '192.0.2.1' },
        body: JSON.stringify({ protocolVersion: 1, displayName: 'PC' })
      }));
      expect(responses[0].status).toBe(201);
      expect(responses[10].status).toBe(429);
      const group: any = await responses[0].json();
      expect(group.snapshot.devices).toHaveLength(1);
      const member: Membership = { ...group, authSequence: 0 };
      const path = '/v1/groups/' + group.groupId;
      const read = await production.fetch('https://relay.example' + path, { headers: await requestAuth(member, 'GET', path, null) });
      expect(read.status).toBe(200);
    } finally { await production.dispose(); }
  });

  it('member can leave but cannot remove another member', async () => {
    const owner = await create(); const member = await join(owner);
    const ownPath = '/v1/groups/' + owner.groupId + '/devices/' + member.deviceId;
    const deniedPath = '/v1/groups/' + owner.groupId + '/devices/' + owner.deviceId;
    expect((await SELF.fetch('http://localhost' + deniedPath, { method: 'DELETE', headers: { ...headers, ...await requestAuth(member, 'DELETE', deniedPath, null) } })).status).toBe(403);
    expect((await SELF.fetch('http://localhost' + ownPath, { method: 'DELETE', headers: { ...headers, ...await requestAuth(member, 'DELETE', ownPath, null) } })).status).toBe(200);
    expect((await getState(owner)).devices).toHaveLength(1);
  });

  it('resets a low-use epoch with one online member only after a second server-timed confirmation', async () => {
    const owner = await create(); const member = await join(owner); const b = await connect(member); await b.snapshot();
    const firstReset = Date.now() - 1000;
    await b.send(quota(1, 2, firstReset));
    const first = (await b.next(m => m.snapshot?.officialQuota?.weeklyUsedPercent === 2)).snapshot;
    await b.send(quota(2, 1, firstReset + 7 * 86_400_000));
    const pending = await b.snapshot(first.version + 1);
    expect(pending.weeklyEpoch.epochId).toBe(first.weeklyEpoch.epochId);
    await SELF.fetch('http://localhost/__test/age-reset/' + owner.groupId);
    await b.send(quota(3, 1, firstReset + 7 * 86_400_000));
    const reset = (await b.next(m => m.snapshot?.lastNotification?.type === 'WEEKLY_RESET')).snapshot;
    expect(reset.weeklyEpoch.epochId).not.toBe(first.weeklyEpoch.epochId);
    expect(reset.usageLedger.deviceUsage[member.deviceId]).toBe(0);
  });

});

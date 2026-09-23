/** Local protocol. Production deployment remains disabled until the full client flow is verified. */
export const PROTOCOL_VERSION = 1;
export const MAX_DEVICES = 4;
export const MAX_MESSAGE_BYTES = 16 * 1024;
export const HEARTBEAT_WRITE_INTERVAL_MS = 30_000;
export const ACTIVITY_UPDATE_INTERVAL_MS = 5_000;
export interface AuthEnvelope {
  deviceId: string;
  timestamp: number;
  nonce: string;
  sequence: number;
  signature: string;
}
export interface ActivityReport {
  windowStart: number;
  windowEnd: number;
  tokenDelta: number;
  activeSessionCount: number;
}
export interface QuotaObservation {
  observedAt?: number;
  weeklyUsedPercent: number;
  weeklyResetAt: number;
  planType: string | null;
}
export interface OfficialQuota {
  weeklyUsedPercent: number;
  weeklyRemainingPercent: number;
  weeklyResetAt: number;
  planType: string | null;
  observedAt: number;
}
export interface WeeklyEpoch {
  epochId: string;
  startedAt: number;
  resetAt: number;
  lastQuota: number;
}
export interface UsageLedger {
  deviceUsage: Record<string, number>;
  unattributedUsage: number;
  confidence: Record<string, 'HIGH' | 'MEDIUM' | 'UNATTRIBUTED'>;
}
export type NotificationType = 'WEEKLY_RESET' | 'DEVICE_LIMIT_REACHED';
export interface RelayNotification {
  eventId: string;
  type: NotificationType;
  createdAt: number;
  deviceId?: string;
}
export interface Device {
  deviceId: string;
  displayName: string;
  role: 'OWNER' | 'MEMBER';
  status: 'ONLINE' | 'OFFLINE' | 'LIMIT_REACHED';
  limitPercent: number | null;
  joinedAt: number;
  lastSeen: number | null;
  lastActivity: ActivityReport | null;
}
export interface GroupSnapshot {
  groupId: string;
  ownerDeviceId: string;
  version: number;
  createdAt: number;
  updatedAt: number;
  devices: Device[];
  officialQuota: OfficialQuota | null;
  weeklyEpoch: WeeklyEpoch | null;
  usageLedger: UsageLedger;
  lastNotification: RelayNotification | null;
  notifications?: RelayNotification[];
}
export interface StoredGroup extends GroupSnapshot {
  pendingActivity?: Record<string, { tokens: number; receivedAt: number }>;
  resetCandidate?: { resetAt: number; used: number; firstSeen: number; devices: string[] };
  notifications?: RelayNotification[];
  schemaVersion: 2;
  sequences: Record<string, number>;
  /** Server receive times are private state and are never projected to clients. */
  activityUpdatedAt: Record<string, number>;
  joinCode: string;
  joinCodeExpiresAt: number;
  secrets: Record<string, string>;
  authSequences: Record<string, number>;
  usedNonces: Record<string, string[]>;
  quotaSequences: Record<string, number>;
  officialQuota: OfficialQuota | null;
  weeklyEpoch: WeeklyEpoch | null;
  usageLedger: UsageLedger;
  lastNotification: RelayNotification | null;
}
export type ClientMessage =
  | { protocolVersion: 1; type: 'REQUEST_SNAPSHOT'; auth: AuthEnvelope }
  | { protocolVersion: 1; type: 'HEARTBEAT'; auth: AuthEnvelope }
  | { protocolVersion: 1; type: 'ACTIVITY_UPDATE'; sequence: number; activity: ActivityReport; auth: AuthEnvelope }
  | { protocolVersion: 1; type: 'QUOTA_OBSERVATION'; sequence: number; observation: QuotaObservation; auth: AuthEnvelope };
export class ProtocolError extends Error {
  constructor(public readonly code: string, public readonly status = 400) { super(code); }
}
export function record(value: unknown): Record<string, unknown> {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ProtocolError('INVALID_PAYLOAD');
  return value as Record<string, unknown>;
}
export function keys(value: Record<string, unknown>, expected: string[]): void {
  const actual = Object.keys(value);
  if (actual.length !== expected.length || actual.some(key => !expected.includes(key))) throw new ProtocolError('INVALID_FIELDS');
}
function integer(value: unknown, min: number, max: number): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= min && value <= max;
}
export function parseJson(text: string): unknown {
  if (new TextEncoder().encode(text).byteLength > MAX_MESSAGE_BYTES) throw new ProtocolError('MESSAGE_TOO_LARGE', 413);
  try { return JSON.parse(text); } catch { throw new ProtocolError('INVALID_JSON'); }
}
export function displayName(value: unknown): string {
  if (typeof value !== 'string' || value.trim().length < 1 || value.length > 64 || /[\x00-\x1f\x7f]/u.test(value)) throw new ProtocolError('INVALID_DISPLAY_NAME');
  return value.trim();
}
export function identifier(value: unknown): string {
  if (typeof value !== 'string' || !/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/.test(value)) throw new ProtocolError('INVALID_ID');
  return value;
}
export function parseMembership(value: unknown, joining: boolean): { displayName: string; joinCode?: string } {
  const input = record(value);
  keys(input, joining ? ['protocolVersion', 'displayName', 'joinCode'] : ['protocolVersion', 'displayName']);
  if (input.protocolVersion !== PROTOCOL_VERSION) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
  return { displayName: displayName(input.displayName), ...(joining ? { joinCode: input.joinCode as string } : {}) };
}
export function parseInternalCreate(value: unknown): { displayName: string; groupId: string } {
  const input = record(value);
  keys(input, ['protocolVersion', 'displayName', 'groupId']);
  if (input.protocolVersion !== PROTOCOL_VERSION) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
  return { displayName: displayName(input.displayName), groupId: identifier(input.groupId) };
}
export function parseInternalJoin(value: unknown): { displayName: string; groupId: string; joinCode: string } {
  const input = record(value);
  keys(input, ['protocolVersion', 'displayName', 'groupId', 'joinCode']);
  if (input.protocolVersion !== PROTOCOL_VERSION) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
  const groupId = identifier(input.groupId);
  if (typeof input.joinCode !== 'string' || !input.joinCode.startsWith(groupId + '.')) throw new ProtocolError('INVALID_JOIN_CODE');
  return { displayName: displayName(input.displayName), groupId, joinCode: input.joinCode };
}
export function parseClientMessage(text: string, now: number): ClientMessage {
  const input = record(parseJson(text));
  if (input.protocolVersion !== PROTOCOL_VERSION) throw new ProtocolError('UNSUPPORTED_PROTOCOL');
  if (input.type === 'HEARTBEAT' || input.type === 'REQUEST_SNAPSHOT') {
    keys(input, ['protocolVersion', 'type', 'auth']);
    const auth = parseAuthRecord(input.auth);
    return { protocolVersion: 1, type: input.type, auth };
  }
  if (input.type === 'QUOTA_OBSERVATION') {
    keys(input, ['protocolVersion', 'type', 'sequence', 'observation', 'auth']);
    const auth = parseAuthRecord(input.auth);
    if (!integer(input.sequence, 1, Number.MAX_SAFE_INTEGER)) throw new ProtocolError('INVALID_SEQUENCE');
    const observation = record(input.observation);
    keys(observation, ['weeklyUsedPercent', 'weeklyResetAt', 'planType', ...(Object.hasOwn(observation, 'observedAt') ? ['observedAt'] : [])]);
    if (observation.observedAt !== undefined && !integer(observation.observedAt, now - 300_000, now + 60_000)) throw new ProtocolError('STALE_OBSERVATION');
    if (typeof observation.weeklyUsedPercent !== 'number' || !Number.isFinite(observation.weeklyUsedPercent) || observation.weeklyUsedPercent < 0 || observation.weeklyUsedPercent > 100 ||
        !integer(observation.weeklyResetAt, Math.max(0, now - 7 * 86_400_000), now + 14 * 86_400_000) ||
        (observation.planType !== null && (typeof observation.planType !== 'string' || observation.planType.length > 32 || /[\x00-\x1f\x7f]/u.test(observation.planType))))
      throw new ProtocolError('INVALID_QUOTA_OBSERVATION');
    return { protocolVersion: 1, type: 'QUOTA_OBSERVATION', sequence: input.sequence,
      observation: { ...(observation.observedAt !== undefined ? { observedAt: observation.observedAt as number } : {}), weeklyUsedPercent: observation.weeklyUsedPercent, weeklyResetAt: observation.weeklyResetAt, planType: observation.planType }, auth };
  }
  if (input.type !== 'ACTIVITY_UPDATE') throw new ProtocolError('UNKNOWN_MESSAGE_TYPE');
  keys(input, ['protocolVersion', 'type', 'sequence', 'activity', 'auth']);
  const auth = parseAuthRecord(input.auth);
  if (!integer(input.sequence, 1, Number.MAX_SAFE_INTEGER)) throw new ProtocolError('INVALID_SEQUENCE');
  const a = record(input.activity);
  keys(a, ['windowStart', 'windowEnd', 'tokenDelta', 'activeSessionCount']);
  if (!integer(a.windowStart, Math.max(0, now - 7 * 86_400_000), now + 60_000) ||
      !integer(a.windowEnd, a.windowStart, Math.min(a.windowStart + 86_400_000, now + 60_000)) ||
      !integer(a.tokenDelta, 0, 1_000_000_000_000) || !integer(a.activeSessionCount, 0, 10_000)) throw new ProtocolError('INVALID_ACTIVITY');
  // Construct explicitly: local session state and arbitrary extra fields never cross the boundary.
  return { protocolVersion: 1, type: 'ACTIVITY_UPDATE', sequence: input.sequence,
    activity: { windowStart: a.windowStart, windowEnd: a.windowEnd, tokenDelta: a.tokenDelta, activeSessionCount: a.activeSessionCount }, auth };
}
function parseAuthRecord(value: unknown): AuthEnvelope {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new ProtocolError('INVALID_AUTH', 401);
  const input = value as Record<string, unknown>;
  keys(input, ['deviceId', 'timestamp', 'nonce', 'sequence', 'signature']);
  if (typeof input.deviceId !== 'string' || !/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/.test(input.deviceId) ||
      !integer(input.timestamp, 0, Number.MAX_SAFE_INTEGER) || typeof input.nonce !== 'string' || !/^[A-Za-z0-9_-]{16,128}$/.test(input.nonce) ||
      !integer(input.sequence, 1, Number.MAX_SAFE_INTEGER) || typeof input.signature !== 'string' || !/^[A-Za-z0-9_-]{16,128}$/.test(input.signature)) {
    throw new ProtocolError('INVALID_AUTH', 401);
  }
  return { deviceId: input.deviceId, timestamp: input.timestamp, nonce: input.nonce, sequence: input.sequence, signature: input.signature };
}
export function publicSnapshot(state: StoredGroup): GroupSnapshot {
  return { groupId: state.groupId, ownerDeviceId: state.ownerDeviceId, version: state.version,
    createdAt: state.createdAt, updatedAt: state.updatedAt,
    devices: state.devices.map(d => ({ deviceId: d.deviceId, displayName: d.displayName, role: d.role,
      status: d.status, limitPercent: d.limitPercent, joinedAt: d.joinedAt, lastSeen: d.lastSeen,
      lastActivity: d.lastActivity === null ? null : { windowStart: d.lastActivity.windowStart,
        windowEnd: d.lastActivity.windowEnd, tokenDelta: d.lastActivity.tokenDelta, activeSessionCount: d.lastActivity.activeSessionCount } })),
    officialQuota: state.officialQuota,
    weeklyEpoch: state.weeklyEpoch,
    usageLedger: state.usageLedger,
    lastNotification: state.lastNotification, notifications: state.notifications ?? [] };
}
export function snapshotMessage(state: StoredGroup) {
  return { protocolVersion: PROTOCOL_VERSION, type: 'GROUP_SNAPSHOT' as const, snapshot: publicSnapshot(state) };
}
export function json(value: unknown, status = 200): Response {
  return Response.json(value, { status, headers: { 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' } });
}
export function errorResponse(error: unknown): Response {
  return json({ protocolVersion: 1, type: 'ERROR', code: error instanceof ProtocolError ? error.code : 'INTERNAL_ERROR' }, error instanceof ProtocolError ? error.status : 500);
}
export async function readJson(request: Request): Promise<unknown> {
  if (request.headers.get('Content-Type')?.split(';')[0].trim().toLowerCase() !== 'application/json') throw new ProtocolError('JSON_REQUIRED', 415);
  if (!request.body) throw new ProtocolError('INVALID_JSON');
  const reader = request.body.getReader();
  const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: false });
  let text = ''; let bytes = 0;
  try {
    for (;;) {
      const part = await reader.read(); if (part.done) break;
      bytes += part.value.byteLength;
      if (bytes > MAX_MESSAGE_BYTES) { await reader.cancel(); throw new ProtocolError('MESSAGE_TOO_LARGE', 413); }
      try { text += decoder.decode(part.value, { stream: true }); } catch { throw new ProtocolError('INVALID_JSON'); }
    }
    try { text += decoder.decode(); } catch { throw new ProtocolError('INVALID_JSON'); }
  } finally { reader.releaseLock(); }
  return parseJson(text);
}

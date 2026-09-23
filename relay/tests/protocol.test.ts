import { describe, expect, it } from 'vitest';
import { parseClientMessage, parseMembership, publicSnapshot, readJson, type StoredGroup } from '../src/protocol';
const now = 1_789_800_000_000;
const report = { protocolVersion: 1, type: 'ACTIVITY_UPDATE', sequence: 1,
  activity: { windowStart: now - 20_000, windowEnd: now, tokenDelta: 100, activeSessionCount: 1 },
  auth: { deviceId: '11111111-1111-4111-8111-111111111111', timestamp: now, nonce: 'A'.repeat(22), sequence: 1, signature: 'B'.repeat(43) } };
const quota = { protocolVersion: 1, type: 'QUOTA_OBSERVATION', sequence: 1,
  observation: { weeklyUsedPercent: 42.5, weeklyResetAt: now + 7 * 86_400_000, planType: 'Plus' },
  auth: report.auth };
describe('protocol whitelist', () => {
  it('accepts only the numeric activity projection', () => {
    expect(parseClientMessage(JSON.stringify(report), now)).toEqual(report);
  });
  it('accepts a quota observation projection', () => {
    expect(parseClientMessage(JSON.stringify(quota), now)).toEqual(quota);
  });
  it.each(['prompt', 'response', 'cookie', 'source', 'path', 'sessionId', 'estimatedUsagePercent', 'limitPercent']) (
    'rejects extra top-level %s', field => {
      expect(() => parseClientMessage(JSON.stringify({ ...report, [field]: 'synthetic-only' }), now)).toThrow('INVALID_FIELDS');
    });
  it.each(['prompt', 'filePath', 'PendingBase64', 'sessionId', 'accessToken'])(
    'rejects extra nested %s', field => {
      expect(() => parseClientMessage(JSON.stringify({ ...report, activity: { ...report.activity, [field]: 'synthetic-only' } }), now)).toThrow('INVALID_FIELDS');
    });
  it.each([-1, 1.5, null, '1', Number.MAX_SAFE_INTEGER + 1])('rejects invalid counters %s', value => {
    expect(() => parseClientMessage(JSON.stringify({ ...report, activity: { ...report.activity, tokenDelta: value } }), now)).toThrow('INVALID_ACTIVITY');
  });
  it('rejects invalid time intervals and sequence', () => {
    for (const activity of [ { ...report.activity, windowStart: now + 1 },
      { ...report.activity, windowEnd: now + 60_001 }, { ...report.activity, windowStart: now - 8 * 86_400_000 } ]) {
      expect(() => parseClientMessage(JSON.stringify({ ...report, activity }), now)).toThrow('INVALID_ACTIVITY');
    }
    expect(() => parseClientMessage(JSON.stringify({ ...report, sequence: 0 }), now)).toThrow('INVALID_SEQUENCE');
  });
  it('rejects unknown versions, business types, malformed and oversized messages', () => {
    expect(() => parseClientMessage('{', now)).toThrow('INVALID_JSON');
    expect(() => parseClientMessage('[]', now)).toThrow('INVALID_PAYLOAD');
    expect(() => parseClientMessage(JSON.stringify({ protocolVersion: 2, type: 'HEARTBEAT' }), now)).toThrow('UNSUPPORTED_PROTOCOL');
    expect(() => parseClientMessage(JSON.stringify({ protocolVersion: 1, type: 'POLICY_UPDATE' }), now)).toThrow('UNKNOWN_MESSAGE_TYPE');
    expect(() => parseClientMessage(' '.repeat(16_385), now)).toThrow('MESSAGE_TOO_LARGE');
  });
  it.each(['prompt', 'accessToken'])('rejects extra quota fields %s', field => {
    expect(() => parseClientMessage(JSON.stringify({ ...quota, observation: { ...quota.observation, [field]: 'synthetic-only' } }), now)).toThrow('INVALID_FIELDS');
  });
  it('rejects invalid quota observations', () => {
    for (const observation of [
      { ...quota.observation, weeklyUsedPercent: -1 },
      { ...quota.observation, weeklyUsedPercent: 101 },
      { ...quota.observation, weeklyResetAt: now - 8 * 86_400_000 },
      { ...quota.observation, planType: '\nprivate' }
    ]) expect(() => parseClientMessage(JSON.stringify({ ...quota, observation }), now)).toThrow('INVALID_QUOTA_OBSERVATION');
  });
  it('rejects extra join fields and invalid display names', () => {
    expect(() => parseMembership({ protocolVersion: 1, displayName: 'PC', owner: true }, false)).toThrow('INVALID_FIELDS');
    for (const displayName of ['', ' '.repeat(4), 'x'.repeat(65), 'PC\nname']) {
      expect(() => parseMembership({ protocolVersion: 1, displayName }, false)).toThrow('INVALID_DISPLAY_NAME');
    }
  });
  it('does not serialize internal storage or arbitrary attached fields', () => {
    const state = { groupId: 'group', ownerDeviceId: 'owner', version: 1, createdAt: now, updatedAt: now,
      schemaVersion: 2, sequences: { owner: 42 }, activityUpdatedAt: { owner: now }, joinCode: 'synthetic', joinCodeExpiresAt: now,
      secrets: { owner: 'synthetic' }, authSequences: { owner: 1 }, usedNonces: { owner: [] }, auth: 'synthetic', devices: [{ deviceId: 'owner', displayName: 'PC',
        role: 'OWNER', status: 'ONLINE', joinedAt: now, lastSeen: now,
        lastActivity: { ...report.activity, prompt: 'synthetic' }, deviceSecret: 'synthetic' }] } as unknown as StoredGroup;
    const output = JSON.stringify(publicSnapshot(state));
    for (const prohibited of ['sequences', 'schemaVersion', 'activityUpdatedAt', 'joinCode', 'secrets', 'authSequences', 'usedNonces', 'auth', 'prompt', 'deviceSecret', 'synthetic']) expect(output).not.toContain(prohibited);
  });
  it('limits streaming request bytes and rejects invalid UTF-8', async () => {
    const huge = new Request('http://localhost', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: ' '.repeat(16_385) });
    await expect(readJson(huge)).rejects.toThrow('MESSAGE_TOO_LARGE');
    const invalid = new Request('http://localhost', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: new Uint8Array([0xff]) });
    await expect(readJson(invalid)).rejects.toThrow('INVALID_JSON');
  });
});

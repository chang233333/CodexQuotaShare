import type { AuthEnvelope } from './protocol';

const DEVICE_ID_PATTERN = /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/;
const TOKEN_PATTERN = /^[A-Za-z0-9_-]{16,128}$/;
export const AUTH_CLOCK_SKEW_MS = 5 * 60_000;
export const AUTH_NONCE_HISTORY = 64;
export const INVITE_TTL_MS = 15 * 60_000;

function bytesToBase64Url(bytes: Uint8Array): string {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/u, '');
}

function base64UrlToBytes(value: string): Uint8Array {
  if (!TOKEN_PATTERN.test(value)) throw new Error('INVALID_TOKEN');
  const padded = value.replaceAll('-', '+').replaceAll('_', '/') + '='.repeat((4 - value.length % 4) % 4);
  const binary = atob(padded);
  return Uint8Array.from(binary, character => character.charCodeAt(0));
}

export function randomSecret(): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return bytesToBase64Url(bytes);
}

export function randomNonce(): string {
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return bytesToBase64Url(bytes);
}

export function createJoinCode(groupId: string): string {
  return `${groupId}.${randomSecret().slice(0, 22)}`;
}

export function groupIdFromJoinCode(value: unknown): string {
  if (typeof value !== 'string') throw new Error('INVALID_JOIN_CODE');
  const [groupId, token, ...extra] = value.split('.');
  if (extra.length || !DEVICE_ID_PATTERN.test(groupId) || !token || !TOKEN_PATTERN.test(token)) throw new Error('INVALID_JOIN_CODE');
  return groupId;
}

export function stableStringify(value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(stableStringify).join(',')}]`;
  const record = value as Record<string, unknown>;
  return `{${Object.keys(record).sort().map(key => `${JSON.stringify(key)}:${stableStringify(record[key])}`).join(',')}}`;
}

export function canonicalAuthMessage(method: string, path: string, auth: Omit<AuthEnvelope, 'signature'>, payload: unknown): string {
  return [method.toUpperCase(), path, auth.timestamp, auth.nonce, auth.sequence, stableStringify(payload)].join('\n');
}

export async function sign(secret: string, message: string): Promise<string> {
  const key = await crypto.subtle.importKey('raw', base64UrlToBytes(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return bytesToBase64Url(new Uint8Array(await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(message))));
}

export async function verify(secret: string, message: string, signature: string): Promise<boolean> {
  try {
    const key = await crypto.subtle.importKey('raw', base64UrlToBytes(secret), { name: 'HMAC', hash: 'SHA-256' }, false, ['verify']);
    return await crypto.subtle.verify('HMAC', key, base64UrlToBytes(signature), new TextEncoder().encode(message));
  } catch { return false; }
}

function validDeviceId(value: unknown): value is string {
  return typeof value === 'string' && DEVICE_ID_PATTERN.test(value);
}

export function parseAuth(value: unknown): AuthEnvelope {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) throw new Error('INVALID_AUTH');
  const input = value as Record<string, unknown>;
  const expected = ['deviceId', 'timestamp', 'nonce', 'sequence', 'signature'];
  const actual = Object.keys(input);
  if (actual.length !== expected.length || actual.some(key => !expected.includes(key))) throw new Error('INVALID_AUTH');
  const timestamp = input.timestamp;
  const sequence = input.sequence;
  if (!validDeviceId(input.deviceId) || typeof timestamp !== 'number' || !Number.isSafeInteger(timestamp) ||
      typeof input.nonce !== 'string' || !TOKEN_PATTERN.test(input.nonce) ||
      typeof sequence !== 'number' || !Number.isSafeInteger(sequence) || sequence < 1 ||
      typeof input.signature !== 'string' || !TOKEN_PATTERN.test(input.signature)) throw new Error('INVALID_AUTH');
  return { deviceId: input.deviceId, timestamp, nonce: input.nonce,
    sequence, signature: input.signature };
}

export function authFromHeaders(request: Request): AuthEnvelope {
  return parseAuth({
    deviceId: request.headers.get('X-CQS-Device-Id'),
    timestamp: Number(request.headers.get('X-CQS-Timestamp')),
    nonce: request.headers.get('X-CQS-Nonce'),
    sequence: Number(request.headers.get('X-CQS-Sequence')),
    signature: request.headers.get('X-CQS-Signature')
  });
}

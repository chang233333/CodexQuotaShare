import { errorResponse, identifier, json, parseMembership, ProtocolError, readJson } from './protocol';
import { groupIdFromJoinCode } from './auth';
export { QuotaGroup } from './quota-group';
export interface Env { QUOTA_GROUPS: DurableObjectNamespace; LOCAL_DEV: string; PAIRING_LIMITER?: RateLimit; }
export function requireLocalDevelopment(request: Request, env: Pick<Env, 'LOCAL_DEV'>): void {
  const host = new URL(request.url).hostname;
  if (env.LOCAL_DEV !== 'true') {
    if (new URL(request.url).protocol !== 'https:') throw new ProtocolError('HTTPS_REQUIRED', 403);
    if (request.headers.has('Origin')) throw new ProtocolError('BROWSER_ORIGIN_DENIED', 403);
    return;
  }
  if (!['localhost', '127.0.0.1', '[::1]'].includes(host)) throw new ProtocolError('LOCAL_TEST_ONLY', 403);
  // P3 has no authentication. Reject browser origins and require an explicit test header.
  if (request.headers.has('Origin') || request.headers.get('X-CQS-Local-Test') !== '1') throw new ProtocolError('LOCAL_TEST_ONLY', 403);
}
export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      requireLocalDevelopment(request, env);
      const url = new URL(request.url);
      if (url.search && url.pathname !== '/v1/groups/join' && !url.pathname.endsWith('/ws')) throw new ProtocolError('INVALID_QUERY');
      if (request.method === 'POST' && (url.pathname === '/v1/groups' || url.pathname === '/v1/groups/join')) {
        if (url.search) throw new ProtocolError('INVALID_QUERY');
        if (env.LOCAL_DEV !== 'true') {
          if (!env.PAIRING_LIMITER) throw new ProtocolError('RATE_LIMITER_REQUIRED', 503);
          const result = await env.PAIRING_LIMITER.limit({ key: request.headers.get('CF-Connecting-IP') ?? 'unknown' });
          if (!result.success) throw new ProtocolError('RATE_LIMITED', 429);
        }
        const joining = url.pathname.endsWith('/join');
        const input = parseMembership(await readJson(request), joining);
        let groupId: string;
        if (joining) {
          try { groupId = groupIdFromJoinCode(input.joinCode); }
          catch { throw new ProtocolError('INVALID_JOIN_CODE', 400); }
        } else groupId = crypto.randomUUID();
        const stub = env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(groupId));
        return stub.fetch(new Request('http://localhost/internal/' + (joining ? 'join' : 'create'), {
          method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CQS-Local-Test': '1' },
          body: JSON.stringify({ protocolVersion: 1, groupId, displayName: input.displayName, ...(joining ? { joinCode: input.joinCode } : {}) }) }));
      }
      const match = /^\/v1\/groups\/([^/]+)(\/ws)?$/.exec(url.pathname);
      if (request.method === 'POST' && /^\/v1\/groups\/[^/]+\/invite\/rotate$/.test(url.pathname)) {
        const groupId = identifier(url.pathname.split('/')[3]);
        return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(groupId)).fetch(request);
      }
      if (request.method === 'POST' && /^\/v1\/groups\/[^/]+\/owner$/.test(url.pathname)) {
        const groupId = identifier(url.pathname.split('/')[3]);
        return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(groupId)).fetch(request);
      }
      if (request.method === 'PATCH' && /^\/v1\/groups\/[^/]+\/devices\/[^/]+$/.test(url.pathname)) {
        const parts = url.pathname.split('/');
        identifier(parts[3]); identifier(parts[5]);
        return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(parts[3])).fetch(request);
      }
      if (request.method === 'DELETE' && /^\/v1\/groups\/[^/]+\/devices\/[^/]+$/.test(url.pathname)) {
        const parts = url.pathname.split('/');
        identifier(parts[3]); identifier(parts[5]);
        return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(parts[3])).fetch(request);
      }
      if (request.method !== 'GET' || !match) throw new ProtocolError('NOT_FOUND', 404);
      const groupId = identifier(match[1]);
      if (match[2]) {
        if (url.searchParams.size !== 1 || !url.searchParams.has('deviceId')) throw new ProtocolError('INVALID_QUERY');
        identifier(url.searchParams.get('deviceId'));
      }
      return env.QUOTA_GROUPS.get(env.QUOTA_GROUPS.idFromName(groupId)).fetch(request);
    } catch (error) { return errorResponse(error); }
  }
} satisfies ExportedHandler<Env>;

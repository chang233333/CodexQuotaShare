import { build } from 'esbuild';
import { Miniflare } from 'miniflare';
import { mkdtemp, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
export async function createRuntime(localDev = 'true') {
  const directory = await mkdtemp(join(tmpdir(), 'cqs-relay-'));
  const built = await build({ entryPoints: ['tests/fixtures/instrumented-worker.ts'], bundle: true, write: false,
    format: 'esm', platform: 'neutral', external: ['cloudflare:workers'], target: 'es2022' });
  const options = { modules: true, script: built.outputFiles[0].text, compatibilityDate: '2026-03-10',
    bindings: { LOCAL_DEV: localDev }, ratelimits: { PAIRING_LIMITER: { simple: { limit: 10, period: 60 as const } } }, durableObjects: { QUOTA_GROUPS: { className: 'QuotaGroup', useSQLite: true, unsafePreventEviction: false } },
    durableObjectsPersist: join(directory, 'do') };
  let instance = new Miniflare(options);
  return {
    fetch: (input: string, init?: RequestInit) => instance.dispatchFetch(input, init as any),
    async restart() { await instance.dispose(); instance = new Miniflare(options); },
    async inspect(groupId: string) { return (await instance.dispatchFetch('http://localhost/__test/inspect/' + groupId)).json() as Promise<any>; },
    async dispose() { await instance.dispose(); await rm(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 }); }
  };
}

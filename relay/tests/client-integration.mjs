import { build } from 'esbuild';
import { Miniflare } from 'miniflare';
import { spawn } from 'node:child_process';
import { openSync, closeSync, writeFileSync } from 'node:fs';
import { promisify } from 'node:util';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
const [dotnet, assembly, directory] = process.argv.slice(2);
if (!directory) throw new Error('Usage: node tests/client-integration.mjs DOTNET ASSEMBLY OUTPUT');
await mkdir(directory, { recursive: true });
process.on('exit', code => writeFileSync(resolve(directory, 'node-exit.json'), JSON.stringify({ code })));
process.on('uncaughtException', error => { writeFileSync(resolve(directory, 'node-error.txt'), error.stack ?? String(error)); process.exitCode = 1; });
const built = await build({ entryPoints: ['src/index.ts'], bundle: true, write: false, format: 'esm', platform: 'neutral', external: ['cloudflare:workers'] });
const runtime = new Miniflare({ modules: true, script: built.outputFiles[0].text, compatibilityDate: '2026-03-10',
  bindings: { LOCAL_DEV: 'true' }, durableObjects: { QUOTA_GROUPS: { className: 'QuotaGroup', useSQLite: true } },
  durableObjectsPersist: resolve(directory, 'do') });
try {
  const url = await runtime.ready;
  const fd = openSync(resolve(directory, 'client.log'), 'w');
  const child = spawn(dotnet, [assembly, '--relay-integration', url.href, resolve(directory, 'clients')], { stdio: ['ignore', fd, fd], windowsHide: true });
  closeSync(fd);
  const timeout = setTimeout(() => child.kill(), 120000);
  const code = await new Promise((resolve, reject) => { child.on('exit', resolve); child.on('error', reject); });
  clearTimeout(timeout);
  await writeFile(resolve(directory, 'result.json'), JSON.stringify({ code }));
  console.log(await readFile(resolve(directory, 'client.log'), 'utf8'));
  if (code !== 0) process.exitCode = 1;
} catch (error) { console.error(error.message); process.stdout.write(error.stdout ?? ''); process.stderr.write(error.stderr ?? ''); process.exitCode = 1; }
finally { await runtime.dispose(); await writeFile(resolve(directory, 'disposed.json'), JSON.stringify({ disposed: true })); }

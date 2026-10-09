import { spawnSync } from 'node:child_process';
import { resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..');
for (const args of [['--check', 'website/script.js'], ['--check', 'worker/index.js'], ['--check', 'worker/cloudflare.js'], ['--test', 'tests/cloudflare-worker.test.mjs']]) {
  const result = spawnSync(process.execPath, args, { cwd: root, stdio: 'inherit' });
  if (result.status !== 0) process.exit(result.status || 1);
}

import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { resolve, join } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const wrangler = join(root, 'node_modules/wrangler/bin/wrangler.js');
const configPath = join(root, 'cloudflare/wrangler.json');
function run(script, args = []) {
  const result = spawnSync(process.execPath, [script, ...args], {
    cwd: root,
    env: { ...process.env, WRANGLER_SEND_METRICS: 'false' },
    stdio: 'inherit',
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Command failed (${result.status}): ${script}`);
}
try {
  run(join(root, 'tests/run-site-checks.mjs'));
  run(join(root, 'scripts/build-veyra-site.mjs'));
  const release = JSON.parse(readFileSync(join(root, 'dist/client/downloads/release.json'), 'utf8'));
  run(join(root, 'scripts/publish-github-release.mjs'));
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  const url = `${config.vars.GITHUB_RELEASE_BASE}/v${release.version}/${release.filename}`;
  const response = await fetch(url, { method: 'HEAD' });
  if (!response.ok || Number(response.headers.get('content-length')) !== release.size) {
    throw new Error('Das vollständige Windows-Paket ist auf GitHub noch nicht erreichbar. Die Website wird nicht veröffentlicht.');
  }
  run(wrangler, ['d1', 'execute', 'veyra-analytics', '--remote', '--file', join(root, 'drizzle/0000_download_events.sql'), '--config', configPath, '--yes']);
  run(wrangler, ['deploy', '--config', configPath]);
} catch (error) {
  console.error(`Veröffentlichung fehlgeschlagen: ${error.message}`);
  process.exitCode = 1;
}

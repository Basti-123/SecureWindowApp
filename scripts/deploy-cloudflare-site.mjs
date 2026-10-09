import { spawnSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
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
  const version = readFileSync(join(root, 'SecureWindowApp.csproj'), 'utf8').match(/<Version>([^<]+)<\/Version>/)[1];
  const published = await fetch(`https://api.github.com/repos/Basti-123/SecureWindowApp/releases/tags/v${version}`, { headers: { accept: 'application/vnd.github+json' } });
  if (!published.ok) throw new Error(`Das geprüfte Windows-Release v${version} fehlt noch auf GitHub. Zuerst den Windows-Build-Workflow ausführen.`);
  const data = await published.json();
  const filename = `Veyra-${version}-win-x64-portable.zip`;
  const asset = data.assets.find(item => item.name === filename && item.state === 'uploaded');
  if (!asset?.digest?.startsWith('sha256:')) throw new Error('Das vollständige geprüfte Windows-Paket fehlt auf GitHub.');
  const release = { version, filename, size: asset.size, sha256: asset.digest.slice('sha256:'.length) };
  const temporary = mkdtempSync(join(tmpdir(), 'veyra-release-'));
  try {
    const metadataPath = join(temporary, 'release.json');
    writeFileSync(metadataPath, JSON.stringify(release));
    run(join(root, 'scripts/build-veyra-site.mjs'), ['--cloudflare', '--release-metadata', metadataPath]);
  } finally { rmSync(temporary, { recursive: true, force: true }); }
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  const url = `${config.vars.GITHUB_RELEASE_BASE}/v${release.version}/${release.filename}`;
  const response = await fetch(url, { method: 'HEAD' });
  if (!response.ok || (response.headers.has('content-length') && Number(response.headers.get('content-length')) !== release.size)) {
    throw new Error('Das vollständige Windows-Paket ist auf GitHub noch nicht erreichbar. Die Website wird nicht veröffentlicht.');
  }
  run(wrangler, ['d1', 'execute', 'veyra-analytics', '--remote', '--file', join(root, 'drizzle/0000_download_events.sql'), '--config', configPath, '--yes']);
  run(wrangler, ['deploy', '--config', configPath]);
} catch (error) {
  console.error(`Veröffentlichung fehlgeschlagen: ${error.message}`);
  process.exitCode = 1;
}

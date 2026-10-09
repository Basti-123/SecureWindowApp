import { spawnSync } from 'node:child_process';
import { createReadStream, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const root = resolve(import.meta.dirname, '..');
const repository = 'Basti-123/SecureWindowApp';
const releaseInput = JSON.parse(readFileSync(join(root, 'dist/client/downloads/release.json'), 'utf8'));

function githubToken() {
  if (process.env.GH_TOKEN || process.env.GITHUB_TOKEN) return process.env.GH_TOKEN || process.env.GITHUB_TOKEN;
  const credential = spawnSync('git', ['credential', 'fill'], {
    cwd: root,
    input: `protocol=https\nhost=github.com\npath=${repository}.git\n\n`,
    encoding: 'utf8',
    env: { ...process.env, GIT_TERMINAL_PROMPT: '0' },
  });
  return credential.stdout?.split('\n').find(line => line.startsWith('password='))?.slice('password='.length);
}
const token = githubToken();
if (!token) throw new Error('GitHub-Anmeldung fehlt. Für die Veröffentlichung Git anmelden oder GITHUB_TOKEN setzen.');
const headers = { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28' };
async function api(path, options = {}, allow404 = false) {
  const response = await fetch(`https://api.github.com/repos/${repository}${path}`, {
    ...options,
    headers: { ...headers, ...(options.body ? { 'content-type': 'application/json' } : {}), ...options.headers },
  });
  if ((response.status === 404 || (response.status === 422 && path.startsWith('/commits/'))) && allow404) return null;
  if (!response.ok) throw new Error(`GitHub-Anfrage fehlgeschlagen: HTTP ${response.status} (${path})`);
  return response.status === 204 ? null : response.json();
}

const tag = `v${releaseInput.version}`;
let release = await api(`/releases/tags/${tag}`, {}, true);
if (!release) {
  const repositoryInfo = await api('');
  if (repositoryInfo.private) throw new Error('Die Downloads benötigen ein öffentliches Release-Repository. Das private Repository wird nicht öffentlich geschaltet.');
  const revision = spawnSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).stdout.trim();
  if (!await api(`/commits/${revision}`, {}, true)) throw new Error('Der Quellcode des neuen Releases muss zuerst zu GitHub gepusht werden.');
  release = await api('/releases', {
    method: 'POST',
    body: JSON.stringify({
      tag_name: tag,
      target_commitish: revision,
      name: `Veyra ${releaseInput.version} – Windows portable`,
      draft: true,
      body: `Portables Paket für Windows 10/11 (x64). ZIP vollständig entpacken und Veyra.exe öffnen. WebView2 ist enthalten.\n\nSHA-256: ${releaseInput.sha256}`,
    }),
  });
}

async function uploadAsset(filename, contentType, checksum, size) {
  const assets = await api(`/releases/${release.id}/assets`);
  const existing = assets.find(asset => asset.name === filename);
  if (existing?.size === size && (existing.digest === `sha256:${checksum}` || existing.label === checksum)) {
    console.log(`GitHub-Download unverändert: ${filename}`);
    return;
  }
  // Upload first, then replace the public filename. A failed large upload does
  // not delete the last complete downloadable package.
  const stagingName = `${filename}.upload-${checksum.slice(0, 12)}`;
  const previousStaging = assets.find(asset => asset.name === stagingName);
  if (previousStaging) await api(`/releases/assets/${previousStaging.id}`, { method: 'DELETE' });
  const url = new URL(release.upload_url.split('{')[0]);
  if (url.hostname !== 'uploads.github.com') throw new Error('Unexpected GitHub upload destination.');
  url.searchParams.set('name', stagingName);
  url.searchParams.set('label', checksum);
  console.log(`GitHub-Upload: ${filename} (${size} Bytes)`);
  const uploaded = await fetch(url, {
    method: 'POST',
    headers: { ...headers, 'content-type': contentType, 'content-length': String(size) },
    body: createReadStream(join(root, filename)),
    duplex: 'half',
  });
  if (!uploaded.ok) throw new Error(`GitHub-Upload fehlgeschlagen: HTTP ${uploaded.status}`);
  const asset = await uploaded.json();
  if (asset.size !== size || (asset.digest && asset.digest !== `sha256:${checksum}`)) throw new Error('Uploaded asset does not match the release input.');
  if (existing) await api(`/releases/assets/${existing.id}`, { method: 'DELETE' });
  await api(`/releases/assets/${asset.id}`, { method: 'PATCH', body: JSON.stringify({ name: filename, label: checksum }) });
}

await uploadAsset(releaseInput.filename, 'application/zip', releaseInput.sha256, releaseInput.size);
const checksumFile = `${releaseInput.filename}.sha256`;
const checksumContent = readFileSync(join(root, 'dist/client/downloads', checksumFile));
// Keep the checksum source identical to the website's generated checksum.
const { createHash } = await import('node:crypto');
const { writeFileSync } = await import('node:fs');
writeFileSync(join(root, checksumFile), checksumContent);
await uploadAsset(checksumFile, 'text/plain', createHash('sha256').update(checksumContent).digest('hex'), checksumContent.length);
if (release.draft) release = await api(`/releases/${release.id}`, { method: 'PATCH', body: JSON.stringify({ draft: false }) });
console.log(`Windows-Download veröffentlicht: ${release.html_url}`);

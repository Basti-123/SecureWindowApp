import { spawn, spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
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
if (!release) release = (await api('/releases?per_page=100')).find(item => item.tag_name === tag);
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
  const curlConfiguration = [
    `url = ${JSON.stringify(url.toString())}`,
    'request = "POST"',
    `upload-file = ${JSON.stringify(join(root, filename))}`,
    `header = ${JSON.stringify(`Authorization: Bearer ${token}`)}`,
    `header = ${JSON.stringify('Accept: application/vnd.github+json')}`,
    `header = ${JSON.stringify(`Content-Type: ${contentType}`)}`,
    `header = ${JSON.stringify('X-GitHub-Api-Version: 2022-11-28')}`,
  ].join('\n') + '\n';
  // Credentials go through stdin, never command arguments or files. Curl's
  // transfer meter makes slow large uploads observable and bounds their runtime.
  const uploaded = await new Promise((resolveUpload, rejectUpload) => {
    const child = spawn('curl', ['--config', '-', '--http1.1', '--fail-with-body', '--show-error', '--connect-timeout', '30', '--max-time', '1800'], {
      cwd: root,
      stdio: ['pipe', 'pipe', 'inherit'],
    });
    let output = '';
    child.stdout.setEncoding('utf8');
    child.stdout.on('data', chunk => { output += chunk; });
    child.on('error', rejectUpload);
    child.on('close', code => {
      if (code !== 0) return rejectUpload(new Error(`GitHub-Upload fehlgeschlagen (curl ${code}).`));
      try { resolveUpload(JSON.parse(output)); } catch { rejectUpload(new Error('Invalid GitHub upload response.')); }
    });
    child.stdin.end(curlConfiguration);
  });
  const asset = uploaded;
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
release = await api(`/releases/${release.id}`, { method: 'PATCH', body: JSON.stringify({ draft: false, body: `Portables Paket für Windows 10/11 (x64). ZIP vollständig entpacken und Veyra.exe öffnen. WebView2 ist enthalten.\n\nSHA-256: ${releaseInput.sha256}` }) });
console.log(`Windows-Download veröffentlicht: ${release.html_url}`);

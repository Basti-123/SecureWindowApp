import { cpSync, mkdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve } from 'node:path';

const root = resolve(process.cwd());
const remoteMetadataPath = process.argv[process.argv.indexOf('--release-metadata') + 1];
const cloudflareBuild = process.argv.includes('--cloudflare');
const dist = resolve(root, cloudflareBuild ? 'dist/cloudflare' : 'dist');
const client = resolve(dist, 'client');
const server = resolve(dist, 'server');
const version = readFileSync(resolve(root, 'SecureWindowApp.csproj'), 'utf8').match(/<Version>([^<]+)<\/Version>/)[1];
const filename = `Veyra-${version}-win-x64-portable.zip`;
const archive = resolve(root, filename);
// Validate the release input before replacing the last site build.
const remote = cloudflareBuild && process.argv.includes('--release-metadata') ? JSON.parse(readFileSync(remoteMetadataPath, 'utf8')) : null;
if (remote && (remote.version !== version || remote.filename !== filename || !/^[a-f0-9]{64}$/.test(remote.sha256) || !Number.isSafeInteger(remote.size) || remote.size <= 0)) throw new Error('Invalid published release metadata.');
const size = remote ? remote.size : statSync(archive).size;
const sha256 = remote ? remote.sha256 : createHash('sha256').update(readFileSync(archive)).digest('hex');

rmSync(dist, { recursive: true, force: true });
mkdirSync(client, { recursive: true });
mkdirSync(server, { recursive: true });
cpSync(resolve(root, 'website'), client, { recursive: true });
mkdirSync(resolve(client, 'downloads'), { recursive: true });
if (!cloudflareBuild) cpSync(archive, resolve(client, 'downloads', filename));
writeFileSync(resolve(client, 'downloads', `${filename}.sha256`), `${sha256}  ${filename}\n`);
writeFileSync(resolve(client, 'downloads', 'release.json'), JSON.stringify({ version, filename, size, sha256 }, null, 2) + '\n');
const sizeLabel = new Intl.NumberFormat('de-DE', { maximumFractionDigits: 1 }).format(size / (1024 * 1024)) + ' MiB';
for (const file of ['index.html', 'versions.html']) {
  const target = resolve(client, file);
  writeFileSync(target, readFileSync(target, 'utf8').replace(/<span data-download-size>[^<]*<\/span>/g, `<span data-download-size>${sizeLabel}</span>`));
}
cpSync(resolve(root, 'worker', 'index.js'), resolve(server, 'index.js'));

writeFileSync(resolve(server, 'wrangler.json'), JSON.stringify({
  main: './index.js',
  compatibility_date: '2026-05-15',
  assets: { directory: '../client', binding: 'ASSETS' },
  d1_databases: [{
    binding: 'DB',
    database_name: 'veyra-analytics',
    database_id: '00000000-0000-4000-8000-000000000000',
  }],
}, null, 2) + '\n');

console.log(`Built Veyra Worker site in ${dist}`);

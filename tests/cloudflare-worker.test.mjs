import { test } from 'node:test';
import assert from 'node:assert/strict';
import worker from '../worker/cloudflare.js';

const filename = 'Veyra-1.4.1-win-x64-portable.zip';
function environment() {
  return {
    ASSETS: { fetch: () => new Response('website') },
    GITHUB_RELEASE_BASE: 'https://github.com/Basti-123/SecureWindowApp/releases/download',
  };
}
test('Download redirects to the matching public GitHub release', async () => {
  const response = await worker.fetch(new Request(`https://veyra.example/downloads/${filename}`), environment());
  assert.equal(response.status, 302);
  assert.equal(response.headers.get('location'), `https://github.com/Basti-123/SecureWindowApp/releases/download/v1.4.1/${filename}`);
  assert.equal(response.headers.get('cache-control'), 'no-store');
});
test('Future release versions use their own tag and filename', async () => {
  const response = await worker.fetch(new Request('https://veyra.example/downloads/Veyra-2.0.0-win-x64-portable.zip'), environment());
  assert.equal(response.headers.get('location'), 'https://github.com/Basti-123/SecureWindowApp/releases/download/v2.0.0/Veyra-2.0.0-win-x64-portable.zip');
});
test('Missing download backend fails explicitly', async () => {
  assert.equal((await worker.fetch(new Request(`https://veyra.example/downloads/${filename}`), {})).status, 503);
});
test('Download redirects accept only GET and HEAD', async () => {
  for (const method of ['GET', 'HEAD']) assert.equal((await worker.fetch(new Request(`https://veyra.example/downloads/${filename}`, { method }), environment())).status, 302);
  assert.equal((await worker.fetch(new Request(`https://veyra.example/downloads/${filename}`, { method: 'POST' }), environment())).status, 405);
});
test('Static website and checksum files keep using the assets binding', async () => {
  for (const path of ['/', `/downloads/${filename}.sha256`]) assert.equal(await (await worker.fetch(new Request(`https://veyra.example${path}`), environment())).text(), 'website');
});
test('Statistics are unavailable publicly when no password is configured', async () => {
  for (const path of ['/analytics', '/api/downloads/summary']) assert.equal((await worker.fetch(new Request(`https://veyra.example${path}`), environment())).status, 404);
});
test('Statistics reject missing or incorrect credentials', async () => {
  const env = { ...environment(), ANALYTICS_PASSWORD: 'test-password' };
  for (const authorization of ['', `Basic ${btoa('veyra:wrong')}`]) assert.equal((await worker.fetch(new Request('https://veyra.example/analytics', { headers: { authorization } }), env)).status, 401);
});
test('Authenticated dashboard remains available', async () => {
  const env = { ...environment(), ANALYTICS_PASSWORD: 'test-password' };
  const response = await worker.fetch(new Request('https://veyra.example/analytics', { headers: { authorization: `Basic ${btoa('veyra:test-password')}` } }), env);
  assert.equal(response.status, 200);
  const html = await response.text();
  assert.match(html, /Download-Statistik/);
  assert.doesNotMatch(html, /rows\.innerHTML/);
});

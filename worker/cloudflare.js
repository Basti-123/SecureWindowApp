import site from './index.js';

async function authorized(request, env) {
  // Statistics stay private on workers.dev. No password means disabled.
  if (!env.ANALYTICS_PASSWORD) return false;
  const value = request.headers.get('authorization') || '';
  if (!value.startsWith('Basic ')) return false;
  try {
    const provided = atob(value.slice(6));
    const expected = `veyra:${env.ANALYTICS_PASSWORD}`;
    const digest = (text) => crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
    const [a, b] = await Promise.all([digest(provided), digest(expected)]);
    const left = new Uint8Array(a);
    const right = new Uint8Array(b);
    let difference = 0;
    for (let i = 0; i < left.length; i++) difference |= left[i] ^ right[i];
    return difference === 0;
  } catch { return false; }
}

export default {
  async fetch(request, env) {
    const path = new URL(request.url).pathname;
    if (path === '/analytics' || path === '/api/downloads/summary') {
      if (!env.ANALYTICS_PASSWORD) return new Response('Not found', { status: 404 });
      if (!await authorized(request, env)) return new Response('Authentication required', {
        status: 401,
        headers: { 'www-authenticate': 'Basic realm="Veyra statistics", charset="UTF-8"', 'cache-control': 'no-store' },
      });
    }
    const match = /^\/downloads\/(Veyra-(\d+\.\d+\.\d+)-win-x64-portable\.zip)$/.exec(path);
    if (match) {
      if (!['GET', 'HEAD'].includes(request.method)) return new Response('Method not allowed', { status: 405, headers: { allow: 'GET, HEAD' } });
      const base = env.GITHUB_RELEASE_BASE;
      if (!base || !base.startsWith('https://github.com/')) return new Response('Download unavailable', { status: 503 });
      return new Response(null, {
        status: 302,
        headers: {
          location: `${base}/v${match[2]}/${match[1]}`,
          'cache-control': 'no-store',
          'x-content-type-options': 'nosniff',
        },
      });
    }
    return site.fetch(request, env);
  },
};

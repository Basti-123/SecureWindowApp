const jsonHeaders = {
  'content-type': 'application/json; charset=utf-8',
  'cache-control': 'no-store',
};

const dashboard = `<!doctype html>
<html lang="de">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Veyra · Download-Statistik</title>
  <style>
    :root { color-scheme: light; font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; background:#f4f5f8; color:#191a22; }
    * { box-sizing:border-box; }
    body { margin:0; min-height:100vh; background:linear-gradient(145deg,#f8f9fc 0%,#eef0f7 100%); }
    main { width:min(1080px,calc(100% - 32px)); margin:0 auto; padding:42px 0 64px; }
    a { color:#3156dc; text-decoration:none; }
    .topline { display:flex; align-items:center; justify-content:space-between; gap:20px; margin-bottom:38px; }
    .brand { display:flex; align-items:center; gap:10px; color:#191a22; font-weight:800; letter-spacing:.11em; text-transform:uppercase; }
    .brand-mark { width:30px; height:30px; border-radius:9px; background:linear-gradient(135deg,#4cdbec,#3851dc 55%,#7d4be9); box-shadow:0 7px 20px #5365dc3b; }
    .back { padding:10px 14px; border:1px solid #d9dce7; background:#fff; border-radius:10px; font-size:14px; }
    h1 { margin:0 0 10px; font-size:clamp(32px,5vw,58px); line-height:1.02; letter-spacing:-.045em; }
    .intro { max-width:650px; color:#626779; font-size:17px; line-height:1.6; margin:0 0 32px; }
    .cards { display:grid; grid-template-columns:repeat(3,minmax(0,1fr)); gap:14px; margin-bottom:28px; }
    .card { padding:22px; background:#fff; border:1px solid #e1e3eb; border-radius:18px; box-shadow:0 16px 40px #2d3c6c0c; }
    .label { color:#7a7e8d; font-size:13px; text-transform:uppercase; letter-spacing:.08em; }
    .value { display:block; margin-top:12px; font-size:35px; font-weight:780; letter-spacing:-.04em; }
    .panel { padding:22px; background:#fff; border:1px solid #e1e3eb; border-radius:18px; box-shadow:0 16px 40px #2d3c6c0c; }
    .panel h2 { margin:0 0 16px; font-size:20px; letter-spacing:-.02em; }
    .events { width:100%; border-collapse:collapse; }
    .events th,.events td { padding:12px 8px; border-bottom:1px solid #eceef3; text-align:left; font-size:14px; }
    .events th { color:#7a7e8d; font-size:12px; text-transform:uppercase; letter-spacing:.06em; }
    .empty { color:#7a7e8d; padding:18px 0 4px; }
    .error { color:#a42b47; background:#fff0f3; border-color:#f4c5d0; }
    @media (max-width:700px) { main { padding-top:24px; } .topline { margin-bottom:26px; } .cards { grid-template-columns:1fr; } .events th:nth-child(3),.events td:nth-child(3) { display:none; } }
  </style>
</head>
<body>
  <main>
    <div class="topline"><a class="brand" href="/"><span class="brand-mark" aria-hidden="true"></span><span>Veyra</span></a><a class="back" href="/">Zur Website</a></div>
    <h1>Download-Statistik</h1>
    <p class="intro">Hier siehst du, wie viele Download-Starts Veyra verzeichnet und zu welchen Uhrzeiten sie ausgelöst wurden. Die Zeiten werden für dich in Europe/Berlin angezeigt.</p>
    <section class="cards" aria-label="Zusammenfassung">
      <div class="card"><span class="label">Alle Downloads</span><strong class="value" id="total">–</strong></div>
      <div class="card"><span class="label">Heute</span><strong class="value" id="today">–</strong></div>
      <div class="card"><span class="label">Aktuelle Version</span><strong class="value" id="version">1.4.1</strong></div>
    </section>
    <section class="panel"><h2>Letzte Download-Starts</h2><div id="state" class="empty">Statistik wird geladen …</div><table class="events" hidden><thead><tr><th>Uhrzeit</th><th>Version</th><th>Plattform</th></tr></thead><tbody id="rows"></tbody></table></section>
  </main>
  <script>
    const formatter = new Intl.DateTimeFormat('de-DE', { dateStyle:'medium', timeStyle:'medium', timeZone:'Europe/Berlin' });
    const state = document.querySelector('#state');
    const table = document.querySelector('.events');
    const rows = document.querySelector('#rows');
    fetch('/api/downloads/summary', { headers: { accept:'application/json' } })
      .then((response) => { if (!response.ok) throw new Error('unavailable'); return response.json(); })
      .then((data) => {
        document.querySelector('#total').textContent = data.total.toLocaleString('de-DE');
        document.querySelector('#today').textContent = data.today.toLocaleString('de-DE');
        if (!data.latest.length) { state.textContent = 'Noch keine Download-Starts vorhanden.'; return; }
        state.hidden = true; table.hidden = false;
        rows.replaceChildren(...data.latest.map((event) => {
          const row = document.createElement('tr');
          for (const value of [formatter.format(new Date(event.downloaded_at + 'Z')), event.version, event.platform]) {
            const cell = document.createElement('td');
            cell.textContent = value;
            row.append(cell);
          }
          return row;
        }));
      })
      .catch(() => { state.textContent = 'Die Statistik ist momentan nicht verfügbar.'; state.classList.add('error'); });
  </script>
</body>
</html>`;

function response(body, status = 200, headers = {}) {
  return new Response(body, { status, headers });
}

function validText(value, fallback) {
  const text = typeof value === 'string' ? value.trim() : '';
  return text.slice(0, 40) || fallback;
}

async function recordDownload(request, env) {
  if (!env.DB) return response(JSON.stringify({ ok: false, error: 'analytics_unavailable' }), 503, jsonHeaders);
  let body = {};
  try { body = await request.json(); } catch { /* keep safe defaults */ }
  const version = validText(body.version, '1.4.1');
  const platform = validText(body.platform, 'Windows');
  await env.DB.prepare('INSERT INTO download_events (app_version, platform) VALUES (?, ?)').bind(version, platform).run();
  return response(JSON.stringify({ ok: true }), 201, jsonHeaders);
}

async function downloadSummary(env) {
  if (!env.DB) return response(JSON.stringify({ error: 'analytics_unavailable' }), 503, jsonHeaders);
  const [total, today, latest] = await Promise.all([
    env.DB.prepare('SELECT COUNT(*) AS count FROM download_events').first(),
    env.DB.prepare("SELECT COUNT(*) AS count FROM download_events WHERE downloaded_at >= datetime('now', 'start of day')").first(),
    env.DB.prepare('SELECT app_version AS version, platform, downloaded_at FROM download_events ORDER BY id DESC LIMIT 100').all(),
  ]);
  return response(JSON.stringify({
    total: Number(total?.count || 0),
    today: Number(today?.count || 0),
    latest: latest?.results || [],
  }), 200, jsonHeaders);
}

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname === '/analytics') return response(dashboard, 200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
    if (url.pathname === '/api/downloads' && request.method === 'POST') return recordDownload(request, env);
    if (url.pathname === '/api/downloads/summary' && request.method === 'GET') return downloadSummary(env);
    if (env.ASSETS?.fetch) return env.ASSETS.fetch(request);
    return response('Not found', 404, { 'content-type': 'text/plain; charset=utf-8' });
  },
};

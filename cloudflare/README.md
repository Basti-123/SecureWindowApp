# Veyra auf Cloudflare Workers

Die Website läuft unter **https://veyra-website.veyraapp.workers.dev** als
Worker `veyra-website` im Konto **Veyra Browser**.
`cloudflare/wrangler.json` enthält die Konto- und Ressourcen-Zuordnung;
Zugangsdaten gehören nicht in diese Datei.

## Lokale Änderungen automatisch veröffentlichen

Für Codex ist das Verhalten in `AGENTS.md` festgelegt: Nach einer fertigen,
geprüften Änderung an Website oder Download wird `npm run site:deploy`
ausgeführt. Ein ständig laufender Hintergrundprozess ist dafür nicht nötig.
Einzelne Speichervorgänge während einer noch unfertigen Änderung lösen keine
Veröffentlichung aus. Ein ausdrücklicher Wunsch nach einer lokalen Vorschau
wird berücksichtigt.

Einmal installieren und bei Bedarf bei Cloudflare anmelden:

```sh
npm ci
npx wrangler login
```

Manuell denselben Ablauf starten:

```sh
npm run site:deploy
```

Der Ablauf prüft das Worker-Verhalten, liest Größe und Prüfsumme des bereits
unter Windows geprüften GitHub Releases und baut die Cloudflare-Website unter
`dist/cloudflare/client`. Anschließend wird ausschließlich die kleine Website
hochgeladen. Reine Website-Änderungen benötigen weder einen Windows-Neubau
noch einen ZIP-Upload. Die separate lokale Vorschau unter `dist/client` und
die dort enthaltene vollständige ZIP bleiben erhalten.

Änderungen an der App werden zuerst zu GitHub gepusht. Der Windows-Workflow
baut die App, führt die nativen Tests aus und veröffentlicht das portable ZIP
im Job `publish-download`. Danach kann die Website auf die neue Version zeigen.

## Automatische Veröffentlichung bei GitHub-Pushes

Der vorhandene Workflow `.github/workflows/build.yml` baut und prüft die
Windows-App bei Pushes auf `main` und `codex/veyra-workers-site`.
Danach veröffentlicht `publish-download` das geprüfte Windows-Paket.
Der zusätzliche Job `deploy-website` kann auch die Website zu Cloudflare laden.

Für diesen Job benötigt das GitHub-Repository einmalig das Actions-Secret
`CLOUDFLARE_API_TOKEN` sowie die Repository-Variable
`CLOUDFLARE_AUTO_DEPLOY=true`. Solange diese zusätzliche GitHub-Automatik
nicht aktiviert ist, bleibt der bestehende Windows-Build unabhängig davon
funktionsfähig; Codex veröffentlicht lokale Website-Änderungen weiterhin direkt.
Verwende einen auf das Konto **Veyra Browser**
beschränkten Token mit **Workers Scripts: Edit** und **D1: Edit**.
Account-Read/Workers-Subdomain-Read kann für
Wranglers Kontoprüfung erforderlich sein. Tokens niemals als normalen
Repository-Code speichern. Die lokale OAuth-Anmeldung ist unabhängig von
diesem GitHub-Secret und wird nicht zu GitHub übertragen.

## Große portable Downloads

Workers-Assets erlauben einzelne Dateien bis 25 MiB. Die vollständige ZIP
liegt deshalb als öffentliches GitHub-Release-Asset im vorhandenen Repository
`Basti-123/SecureWindowApp`. Die Website bleibt auf workers.dev; der Worker
leitet den Download auf das zum Versionsnamen passende GitHub Release weiter.
Cloudflare R2 oder ein zusätzlicher kostenpflichtiger Speicher sind nicht nötig.

Bei Website-Änderungen wird das bereits veröffentlichte Windows-Paket verwendet.
Neue ZIPs werden beim GitHub-Runner zunächst unter einem temporären Namen
vollständig hochgeladen und erst danach ersetzt. Neue Releases werden erst nach
dem Upload veröffentlicht. Vor dem Worker-Deploy wird geprüft, dass das öffentliche
vollständige Paket erreichbar ist. Ein sehr langsamer lokaler Upload ist nicht nötig.

Für die lokale Website-Veröffentlichung reicht die Cloudflare-Anmeldung;
die Release-Metadaten sind öffentlich lesbar. GitHub Actions verwendet für
Windows-Releases den eigenen `GITHUB_TOKEN` mit `contents: write`.
`website/.assetsignore` verhindert den Upload der ZIP als Worker-Asset.

## Download-Statistik

Die D1-Datenbank wird über `DB` gebunden. Das öffentliche Download-Ereignis
speichert weiterhin Version, Plattform und Zeitpunkt. Auf workers.dev sind
`/analytics` und `/api/downloads/summary` standardmäßig deaktiviert.

Optional kann der Betreiber das Dashboard mit einem Passwort aktivieren:

```sh
npx wrangler secret put ANALYTICS_PASSWORD --config cloudflare/wrangler.json
```

Das Passwort wird interaktiv eingegeben und nur als Cloudflare-Secret
gespeichert. Benutzername für die Browser-Anmeldung: `veyra`.

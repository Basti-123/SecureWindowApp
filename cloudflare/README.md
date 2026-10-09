# Veyra auf Cloudflare Workers

Die Website wird als Worker `veyra-website` im Konto **Veyra Browser**
veröffentlicht. Die von Wrangler ausgegebene workers.dev-Adresse ist die
öffentliche Website-Adresse. `cloudflare/wrangler.json` enthält die Konto-
und Ressourcen-Zuordnung; Zugangsdaten gehören nicht in diese Datei.

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

Der Ablauf prüft das Worker-Verhalten, baut `dist/` neu, veröffentlicht ein
geändertes Windows-Paket als GitHub Release und lädt anschließend die Website
zu Cloudflare hoch. Die zum
aktuellen `<Version>`-Eintrag in `SecureWindowApp.csproj` gehörende portable
ZIP-Datei muss im Projektordner liegen. Änderungen an der App benötigen
zuerst einen neuen portablen App-Build; reine Website-Änderungen verwenden
das vorhandene Paket weiter.

## Automatische Veröffentlichung bei GitHub-Pushes

Der vorhandene Workflow `.github/workflows/build.yml` baut und prüft die
Windows-App bei Pushes auf `main`. Danach veröffentlicht der Job
`deploy-website` Website und geprüftes Download-Paket.

Für diesen Job benötigt das GitHub-Repository einmalig das Actions-Secret
`CLOUDFLARE_API_TOKEN`. Verwende einen auf das Konto **Veyra Browser**
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

Bei reinen Website-Änderungen wird das bereits vorhandene Download-Paket anhand
seiner Prüfsumme wiederverwendet. Geänderte ZIPs werden zunächst unter einem
temporären Namen vollständig hochgeladen und erst danach ersetzt. Neue Releases
werden erst nach dem Upload veröffentlicht. Vor dem Worker-Deploy wird geprüft,
dass das öffentliche vollständige Paket erreichbar ist.

Die lokale GitHub-Anmeldung wird über Gits Credential Helper verwendet.
GitHub Actions verwendet den eigenen `GITHUB_TOKEN` mit `contents: write`.
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

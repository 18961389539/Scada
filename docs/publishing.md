# Unified Server and Web Publishing

The frontend and backend are deployed as one application. The server hosts
the compiled Vue application and API on the same port. Starting the server
also makes the frontend available; no Node.js, Vite or separate frontend
process is needed on the deployment machine.

## Build the Deployment Folder

The build machine requires the .NET 10 SDK and Node.js with npm. Run from
the repository root:

```powershell
dotnet publish src/Scada.Server/Scada.Server.csproj -c Release -o publish/Scada.Server
```

The publish target runs `npm ci` when `web/node_modules` is absent, then
`npm run build` and copies all built files into the server's `wwwroot` directory.
For reproducible local releases, run `npm ci` explicitly in `web` first; an
existing dependency directory is not proof that it matches the lockfile. A frontend
installation, type-check or build failure fails the server publication.

```text
publish/Scada.Server/
  Scada.Server.exe
  Scada.Server.dll
  Scada.Server.deps.json
  Scada.Server.runtimeconfig.json
  appsettings.json
  ... backend dependencies ...
  wwwroot/
    index.html
    assets/
    ... public frontend assets ...
```

Deploy the entire output folder, not just the executable. Prefer a fresh
output directory per release, rather than publishing over a running instance
or a directory containing site data. Stop any preview running directly from
`bin/Release` before rebuilding that same configuration.

For a Windows x64 deployment that includes the .NET runtime:

```powershell
dotnet publish src/Scada.Server/Scada.Server.csproj -c Release -r win-x64 --self-contained true -o publish/Scada.Server-win-x64
```

The default framework-dependent output requires the ASP.NET Core 10 runtime
on the deployment machine. Both output types include the frontend.

`Scada.Server.exe` is built as a Windows GUI-subsystem executable (`WinExe`) with
an application icon. It is meant to be launched interactively or as a service,
not as a console program: nothing is printed to the terminal that starts it,
startup errors included. Logs, the notification-area icon and the startup
failure dialog are described in
[Desktop interaction](#desktop-interaction-tray-icon-startup-failures-and-logs).

## Start the Combined Application

Start the server using the existing site configuration or service installer.
For example, from the published directory:

```powershell
.\Scada.Server.exe --contentRoot="$PWD" --SiteSettings="D:\ScadaData\appsettings.Site.json"
```

An explicit `--SiteSettings` selects the site's existing configuration. Without
it, Production first checks the executable directory and the data directory;
if neither contains `appsettings.Site.json`, it atomically creates one with a
random JWT signing key and data paths. Portable data defaults to `data` beside
the executable, with a platform data-directory fallback if that location
cannot be created. Development instead uses a temporary signing key.

Automatic configuration is a portable-start convenience, not site acceptance.
For a commissioned installation, explicitly select persistent data directories,
the listening address and protected site configuration; retain the signing key
across upgrades. The Windows service installer also generates initial site
configuration. Never commit signing keys or put them in frontend files.
Service installation requires an administrator terminal and is a separate step.

The bundled `wwwroot` is resolved relative to the server executable, not the
shell's working directory. Existing explicit `--webroot` and
`ASPNETCORE_WEBROOT` overrides are still honored for development and tests.

Open the backend's address in a browser, for example
`http://localhost:5080/traceability`. Refreshing a frontend route uses the
existing SPA fallback. API requests and SignalR use the same host and port.
Starting the server serves the frontend. When the executable is started
interactively (double-click or a terminal in Production), it opens the default
browser at the listening address after the server is ready. Windows service
mode and Development/automation runs do not open a browser; set
`Scada:OpenBrowserOnStart` (or `SCADA_NO_BROWSER=1`) to control it explicitly.

For an isolated local preview only, the development environment can generate
a temporary signing key:

```powershell
.\Scada.Server.exe --environment=Development --contentRoot="$PWD" --Kestrel:Endpoints:Http:Url=http://127.0.0.1:5080
```

The executable is a `WinExe`, so this preview prints nothing to the terminal
either. Start the DLL through the .NET host when console output is needed:

```powershell
dotnet .\Scada.Server.dll --environment=Development --contentRoot="$PWD" --Kestrel:Endpoints:Http:Url=http://127.0.0.1:5080
```

Do not use the development preview as a production deployment. Set explicit
data directories and production secrets before connecting field equipment.

## Optional Loopback Collection via Gateway.Host

In-process collection remains the default. To keep the Server from opening field connections and route all enabled devices through a same-machine `Gateway.Host`, set `Scada:LocalCollectionEnabled` to `false`, assign every enabled device a `GatewayId`, and enable `Gateway:AllowLoopbackHttp` on the Server plus `GatewayAgent:AllowLoopbackHttp` on the agent. See [local collection](local-collection.md). This is a limited, supported optional mode — it is not plugin process isolation, and `reconnect_device` over MCP still does not issue gateway engineering commands. Do not start a real control link to verify the mode.

## Desktop Interaction: Tray Icon, Startup Failures and Logs

Interactive Windows starts keep a notification-area icon. Double-click it to
open the web UI; right-click for **打开界面**, **打开日志目录** and **退出**. Exit
stops the application the same way Ctrl+C would. The icon is not created for
Windows service runs, Development-environment runs, non-Windows hosts, or when
`SCADA_NO_TRAY=1` is set.

Because the process has no console, the tray icon and the log directory are the
primary operating surfaces:

- Logs are Serilog rolling files named `log-*.txt`. They go to `<data>/logs` for a
  portable site configuration, or to `<Scada:LogRoot>` when that option is set;
  with an empty `Scada:LogRoot` the fallback is `logs` beside the executable. The
  tray menu opens that directory and the startup failure dialog prints it.
- A startup exception is logged as Critical and shown in a message box naming
  the log directory and common causes: port already in use, invalid site
  settings, unwritable data directory. It also suggests `--urls` for a quick
  port change. Service-mode starts log only; no dialog can appear, so rely on
  service recovery policy.
- Browser auto-launch and the tray icon share the same interactive conditions:
  `SCADA_NO_BROWSER=1` suppresses only the browser and `SCADA_NO_TRAY=1` only the
  icon, while `Scada:OpenBrowserOnStart` overrides the Development default.

## Installing the Web Client (PWA)

The published frontend registers a service worker with automatic updates and
serves `manifest.webmanifest` (name `SCADA 监控系统`, short name `SCADA`,
`start_url` `/screens`, standalone display, 192/512/maskable PNG icons).
Browsers that accept the site as installable offer **Install app**; the result
opens in its own window without browser chrome.

- Precaching covers built JS, CSS, HTML, SVG, PNG, WebManifest and WOFF2 files up
  to 4 MiB each, so a previously loaded shell can still open without a network.
- `/api/`, `/hubs/`, `/health` and `/metrics` are excluded from the service worker
  navigation fallback and always reach the server. Live values, history queries
  and alarms still require connectivity.
- The installed window is a convenience for workstations and tablets. It is not a
  kiosk shell and does not replace `/kiosk/screens/:id`, autostart policy or
  domain login restrictions.

## Windows Desktop Client

`Scada.Desktop` is an optional WinForms + WebView2 client for operator PCs and
unattended displays. It connects to an existing server; the server is not bundled.

Publish it on a build machine with the .NET 10 SDK:

```powershell
dotnet publish src/Scada.Desktop/Scada.Desktop.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -p:PublishTrimmed=false -o publish/Scada.Desktop-win-x64
```

Deploy the whole output folder. The target needs Windows 10 1903+ or Windows 11 x64
and the Microsoft Edge WebView2 Evergreen Runtime. The client detects a missing
runtime and offers the official download page. Do not enable single-file publishing
or trimming: the WebView2 native loader and WinForms reflection are not compatible
with the default publish pipeline.

The first start opens a Chinese setup wizard: server address, dedicated account,
published screen, published layout or workstation, monitor and auto-start options.
The account password is stored only as Windows DPAPI (CurrentUser) ciphertext in
`%LOCALAPPDATA%\Scada\Desktop\settings.json`; tokens are never written to disk or
command lines. Use a dedicated read-only account for unattended screens. A workstation
with *Allow control* disabled also rejects control requests server-side.

Runtime files:

```text
%LOCALAPPDATA%\Scada\Desktop\settings.json
%LOCALAPPDATA%\Scada\Desktop\settings.json.bak
%LOCALAPPDATA%\Scada\Desktop\logs\desktop-YYYYMMDD.log
%LOCALAPPDATA%\Scada\Desktop\WebView2\
```

The supervisor keeps a tray icon with **打开画面**, **重新加载画面**, **设置…**,
**打开日志目录** and **退出**. It restarts a crashed screen process with 2/5/15/30/60
second backoff and pauses after repeated crashes. The hidden exit shortcut is
`Ctrl+Alt+Shift+Q`; Alt+F4 is ignored. Auto-start uses the current user's Run key and
does not require administrator rights.

Exit codes: `0` normal exit, `1` crash (restarted), `2` configuration or authentication
failure (no automatic retry, to avoid account lockout), `3` missing WebView2 Runtime.

Limitations: one display and one window per client; console `window.open` multi-monitor
popups remain blocked in this release; self-signed certificates are not bypassed, so
install the internal CA in the Windows trusted root store. Auto-start begins after a
Windows user logs in; Windows automatic logon and Assigned Access are outside this
document. The client depends on the web client's `scada_token`, `scada_user`,
`scada_token_expires`, `scada_workstation_id` and `scada_workstation` localStorage keys;
upgrade it together with the web frontend.

### Centralized Deployment and Operations

The client supports a machine-wide non-secret configuration and a per-user secret:

```text
%ProgramData%\Scada\Desktop\client.json      server, target, display, downloads, locked fields
%LOCALAPPDATA%\Scada\Desktop\settings.json   account, DPAPI password, ClientId
%ProgramData%\Scada\Desktop\install.json     installer scope and managed autostart
```

Machine values override user values on every load. Fields listed in `locked` are disabled
in the setup wizard; the installer locks all fields it provides by default. A machine
configuration cannot contain a password: DPAPI CurrentUser ciphertext is not decryptable
by another Windows account.

Provision a machine or a user without the wizard:

```powershell
Scada.Desktop.exe --provision --config client.json --scope machine
Get-Content .\password.txt | Scada.Desktop.exe --provision --config client.json --scope user --password-stdin
```

`--scope machine` requires an administrator and rejects passwords. `--scope user` writes
the DPAPI secret; the password is accepted only through standard input, never as a
command-line argument. User scope targets the profile of the account that runs the
command, so an elevated prompt would configure the administrator instead of the operator;
that case is refused unless `--allow-elevated-user` is passed explicitly.

Build, install, upgrade, roll back and uninstall with the scripts under
`tools/deployment/desktop`:

```powershell
pwsh -File tools/deployment/desktop/Build-DesktopPackage.ps1 -OutputPath D:\pkg\desktop -Version 1.1.0
pwsh -File tools/deployment/desktop/Install-DesktopClient.ps1 -PackagePath D:\pkg\desktop -Scope CurrentUser
pwsh -File tools/deployment/desktop/Upgrade-DesktopClient.ps1 -PackagePath D:\pkg\desktop-next -Version 1.2.0
pwsh -File tools/deployment/desktop/Rollback-DesktopClient.ps1 -Version 1.1.0
pwsh -File tools/deployment/desktop/Uninstall-DesktopClient.ps1 -Scope CurrentUser
```

The installer keeps versioned folders with a `current` junction and registers a logon
scheduled task (`ScadaDesktop`) with restart-on-failure and `IgnoreNew`. It removes the
per-user Run key so the task is the only autostart path. This is intentionally not a
Windows service: services run in Session 0 and cannot host the interactive WebView2 UI.

Clients send a heartbeat every `Scada:DesktopClient:HeartbeatSeconds` seconds (default 30,
range 10–600) to `/api/desktop/heartbeat`. Users with System permission see online state,
machine, account, target, version, WebView2 version, the active version policy and
blocked/outdated counts in the **桌面客户端** panel on the Ops page. The registry is in
memory and per node; it is repopulated within one heartbeat interval after a server
restart. Heartbeats are not written to operation logs; an online → offline transition is
logged once by the server-side monitor.

The desktop client calls the server API with `UseProxy = false`: a system proxy on an
enterprise image would otherwise intercept requests to a loopback/LAN server while the
WebView2 page still loads, producing "browser works, client does not". Terminating TLS in
a reverse proxy requires enabling `Scada:ReverseProxy` on the server; see
[configuration](configuration.md#tls-与反向代理) — otherwise every terminal shares the
proxy address and the per-terminal rate limit collapses into one bucket.

Set `Scada:DesktopClient` in the site settings to enforce versions:

```json
{
  "Scada": {
    "DesktopClient": {
      "MinimumVersion": "1.1.0",
      "LatestVersion": "1.2.0",
      "DownloadUrl": "https://scada.example.com/downloads/ScadaDesktop-latest.zip",
      "HeartbeatSeconds": 30,
      "OnlineThresholdSeconds": 90
    }
  }
}
```

A client below `MinimumVersion` blocks before loading the screen and shows the download
link. Unknown versions fail open. `LatestVersion` shows a clickable update notice in the
client; without `DownloadUrl` it tells the operator to contact an administrator. This
section is read through `IOptionsMonitor`, so editing the site settings file applies
within one heartbeat without restarting the server. Silent auto-update is not
implemented; deploy a new package and switch `current` with the upgrade script.

Downloads stay disabled by default. When enabled in the wizard or machine configuration,
WebView2 saves exports to the configured directory with sanitized file names and shows a
non-blocking completion notice; otherwise the download is canceled and logged.

The client follows HA role changes: if the configured node is standby and reports a
`primaryUrl`, or a request returns `503` with `X-Scada-Primary`, it reconnects, logs in
again and reloads the target. It does not automatically switch back. Prefer a stable
VIP/hostname in production; origin-scoped preferences such as monitor mappings are lost
across a switch.

See [high availability](high-availability.md) for Data Protection key-ring and OPC PKI sharing, alarm and control takeover, runtime-lag health, and history spool handling. The key ring is synchronized only over the authenticated `Ha:SharedKey` peer channel (`/api/ha/peer/secrets`); prefer HTTPS, because the shared key authenticates the peer but does not encrypt HTTP. Do not upload keys on an unauthenticated channel. There is no online re-encryption product and no external KMS.

## Site Identity, Login and First Setup

Merge the following into the existing site settings; keep its signing key,
listening address and data-directory settings:

```json
{
  "Scada": {
    "SiteName": "Assembly Hall SCADA",
    "DemoMode": false
  }
}
```

`SiteName` defaults to `SCADA`, must contain 1–120 characters after trimming,
and is displayed on login and in the application header. The anonymous
`GET /api/auth/site-info` endpoint returns only the site name and explicit demo
flag, not secrets, paths or operational configuration.

Production login starts with empty credentials and does not display the demo
administrator hint. Assign site accounts and immediately change the initial
administrator password. Only `Scada:DemoMode=true` enables the labelled demo
login convenience; `SeedDemoData` does not implicitly enable it.

After login, operators/viewers default to process screens; engineers and
administrators default to overview. Explicit, local deep links are preserved.
The overview setup guide is available to configuration users and expands when
their visible device catalog is empty. Follow import (optional), devices, tags,
manual live-data verification, screen publication and backup/recovery steps.
Saved configuration is not proof of connectivity or successful site acceptance.
Area configuration users are directed to a system administrator for backups.

Navigation is grouped into monitoring, production/quality, engineering and
administration. Daily downtime entry, cause classification, confirmation and
count corrections are at `/production/events` with area-scoped Control
permission. `/config/oee` retains engineering dictionaries and settings.
Downtime lists are date/line/confirmation-filtered, with a visible 2,000-record
limit; narrow the filters when that limit is reached.

`/reports` is ad hoc daily/monthly **tag statistics**. `/report-center` is
**business reports**, separated into generated archives and templates/generation.
Devices and tags default to essential columns; enable engineering columns for
IDs and detailed parameters. Secondary actions are under More, and deletion
still requires confirmation naming the affected object.

Upgrade frontend and backend together. Traceability migrations preserve old
records as unevaluated; back up the configuration database before deployment.

## Backup Scheduling

Full backup scheduling uses Quartz.NET 4.6.2, hosted inside the server. No
separate scheduler process or Quartz database is required. The scheduler
starts 10 seconds after application startup and checks once per minute.

`Scada:FullBackupIntervalHours` retains its existing meaning: the minimum
time since the latest local full backup, in hours, with a default of 168.
Zero or a negative value disables automatic backups, not manual backups.
Manual backups also reset the interval used by subsequent automatic checks.

Only the active node runs automatic backups. Each execution checks the
current node role and latest backup again after acquiring the shared backup
lock. Quartz also prevents overlapping scheduled executions and skips missed
checks rather than replaying them. Shutdown signals cancellation to running
scheduled work and waits for it to finish stopping.

Backup creation, optional restore drills, offsite copies, retention, run
records and health warnings continue to use the existing backup pipeline.

Quartz uses an in-memory job store. On restart, the trigger is recreated and
the backup files determine whether a backup is due. There is no persistent
trigger history or Quartz cluster lock; multi-node deployments still depend
on the application's active/standby role management.

## Encrypted Backups

A full backup bundle contains the configuration database (including every user
password hash and plant-level operations settings such as the factory calendar),
the runtime database, the history files, unpurged report archives
under `ReportsRoot`, the OPC PKI and the ASP.NET Data Protection key ring under
`keys/` (including `report-signing.pfx`). The site settings file (`appsettings.Site.json`,
JWT signing key, `Ha:SharedKey`, listen addresses) is a deployment-plane item: it is
retained on restore and is not part of the configuration snapshot. See [configuration-planes.md](configuration-planes.md). Report run paths are stored relative to
`ReportsRoot` and rewritten on restore; SHA-256 mismatches or missing archive
files fail verification and restore. Old files are not re-signed, and there is
no online one-click restore HTTP API. Whoever can read the bundle can
forge authentication cookies and decrypt device passwords, so the archive must
be treated as a credential, not as a report.

Set `Scada:BackupPassword` to store new bundles as `full_*.zip.enc`:

- Container: `SCADABK1` header, PBKDF2-SHA256 (310,000 iterations, 16-byte salt),
  AES-256-CBC body, HMAC-SHA256 trailer over header plus ciphertext.
- Verification decrypts to a temporary ZIP only after the HMAC matches, so a
  wrong password or a corrupted bundle never yields partial plaintext.
- Leave the setting empty to keep writing plain `full_*.zip` bundles. Existing
  plain bundles stay verifiable and restorable after a password is configured.
- `Scada.BackupTool` accepts `--password`, or `SCADA_BACKUP_PASSWORD` so the
  password does not land in shell history. A plain bundle needs no password.

Rotating the password is an offline, site-side procedure (`Set-BackupPassword.ps1`).
New bundles use `Scada:BackupPassword`. Old encrypted bundles can still be opened with
`Scada:BackupPasswordPrevious` after a rotation, or they can be rewritten beside the
original with `Scada.BackupTool reencrypt`. Plain historical `full_*.zip` packs are not
rewritten and must be recreated through the normal backup pipeline. Record it with the same care as the JWT
signing key, and store offsite copies on media that is not shared with the site.

`Scada.BackupTool reencrypt` rewrites one encrypted bundle beside the original,
so existing retention and offsite copies do not have to be rebuilt by hand:

```powershell
.\Scada.BackupTool.exe reencrypt --backup 'D:\ScadaBackups\full_example.zip.enc' `
  --new-password '<new password>' --prompt-password
.\Scada.BackupTool.exe verify --backup 'D:\ScadaBackups\full_example.zip.reenc.zip.enc' --prompt-password
```

- The source bundle is never modified. The tool decrypts to a temporary file,
  writes a sibling `full_*.zip.enc`, decrypts the new bundle again with the new
  password and compares the plaintext byte-for-byte. Any mismatch removes the
  new bundle and leaves the original untouched.
- The target must stay in the same directory as the source, because retention,
  backup listings and offsite copies only scan the backup root. Plain bundles
  are rejected; rebuild them through the normal backup pipeline after setting
  `Scada:BackupPassword`.
- `--prompt-new-password` hides the new password and asks for it twice, so a
  typo cannot produce a bundle nobody can decrypt. Delete the old bundle
  manually only after `verify` and a restore drill succeed.
- There is no in-service key rotation or KMS integration: the password still
  lives in site configuration (or the environment). Treat rotation as a
  maintenance-window action, not a background job.

### Quarterly Recovery Drills

Run these before connecting field equipment and once per quarter. CI proves the code path; these drills prove the site's data, credentials and operator procedure still work.

Prerequisites: a stopped or isolated copy of the site, a verified encrypted bundle, and a maintenance window. Never restore over a running instance.

1. **Full restore**
   - `Scada.BackupTool verify --backup <bundle> --prompt-password` → expect a successful verification.
   - `Scada.BackupTool restore --backup <bundle> --target <empty dir> --prompt-password`.
   - Point a copy of the server at the restored directory, start it, log in, open a screen, and confirm device/tag/alarm counts match the manifest.
   - Record the bundle name, restore duration, manifest counts and operator.
2. **HA failover**
   - With both nodes healthy, stop the active node; the standby must take over after the lease expires and keep writing history.
   - Issue one control command and one alarm acknowledgement; each must appear once in the audit log, not twice.
   - Restart the former active node and confirm it rejoins as standby without duplicating rows.
3. **Migration failure rollback**
   - Take a fresh backup before upgrading.
   - On the copy, force the upgrade to fail (read-only data directory or a deliberately missing table).
   - Confirm the process exits non-zero and writes the reason to `startup.log`; then restore the pre-upgrade bundle and compare device/tag/alarm counts with the backup manifest.

Pass criteria: every step completes inside the window, counts match, and no control command is duplicated. Keep the drill report with the backup register. These drills do not replace CI and are not part of the merge gate.

## Release Gates and Package Verification

CI blocks on backend tests, frontend type/build and gzip bundle budgets,
internationalization/access checks, and three repeated core browser smoke runs.
The larger simulator/UI suite remains observational while its existing scenarios
are stabilized; it does not replace the required smoke gate. Configure branch
protection to require these jobs; workflow code cannot change repository settings.

The package job runs on pull requests as well as main. It starts the actual
published executable from an unrelated working directory, checks login and SPA
assets, persists configuration across restart, verifies a full backup, performs
a restore drill, and boots from an offline-restored database. All data is in a
new temporary directory; the checker must never point at a running site.

```powershell
dotnet build tools\Scada.BackupTool -c Release
node tools\deployment\test-server-package.mjs --package D:\Packages\Scada-new
node tools\deployment\package-manifest.mjs D:\Packages\Scada-new
```

To validate a real binary upgrade and recovery, supply the approved previous
package as `--previous-package D:\Packages\Scada-old`. The checker seeds the old
version, upgrades using the new binary, then restores the pre-upgrade backup and
starts the old binary. Without that argument, it explicitly reports same-version
restart/restore only. Existing migration tests separately exercise old database
schemas; neither test substitutes for a release-specific prior binary.

`release-manifest.json` records the commit, dirty-worktree flag, CI build number,
creation time and SHA-256 of every package file except the manifest itself.
CI also emits a SHA-256 sidecar for the ZIP. These detect accidental corruption;
they are not a digital signature or a replacement for a trusted release channel.

## History Capacity Regression

Run this before a release that changes history storage, query limits, or the
retention default. It is a portable regression check, not a CI merge gate and
not a site SLA. Parameters, failure budgets, and the separate 24-hour site
soak are in [history capacity](history-capacity.md#可重复容量基线).

```powershell
dotnet test tests\Scada.Engine.Tests -c Release --filter Category=Capacity --logger "console;verbosity=detailed"
```

Open-box installs stay on SQLite. The product default for raw history is 6
months (`HistoryRetentionMonths`). A small-site overlay for warn threshold,
spool, and query concurrency is
`docs/examples/appsettings.History.Small.json`; merge it into the site
settings file. It is not loaded automatically and is not part of the
published package. Do not treat it as a Timescale or PostgreSQL setup.

## Browser Regression Isolation and Frontend Budgets

Every E2E spec imports `test` from `web/tests/fixtures.ts`. Each test (including
retries/repeats) starts its own server, temporary data directories and OPC
simulator, and stops them before the next test. Keep one worker because the
legacy simulator scenarios use fixed local ports. `PW_BASE_URL` is rejected by
this isolated suite to avoid accidentally mutating a real site. Package checks
use the separate runner above. Use `PW_CHANNEL=chromium` after installing the
Playwright browser; Edge remains the default for local runs.
Occupied test ports fail setup rather than reusing another service. On a shared
development machine, do not rebuild while tests run; `PW_SERVER_DIR`,
`PW_OPC_DIR` and `PW_WEB_ROOT` can select immutable copies of the built server,
OPC simulator and frontend.

```powershell
Set-Location web
npm ci
npm run test:e2e:build
npm run test:smoke -- --repeat-each=3
```

Element Plus templates and programmatic services import components/styles on
demand, with `ElConfigProvider` retaining Chinese/English component localization.
`npm run build` enforces initial static-import closure budgets (JS and CSS,
gzip): login 350 KiB, overview 500 KiB. ECharts and Monaco must not occur in either
initial closure. Lazy chunks are still precached by the existing PWA for offline
use; the budget describes initial route dependencies, not all background traffic.
Use cold-cache network throttling on actual operator terminals to measure UX;
bundle sizes and local timings are not WAN performance promises.
The Chromium/Edge navigation regression also disables cache and tests login at
150 ms latency and 200,000 bytes/second download, requiring editable login fields
within 20 seconds and no eager chart/editor requests. This is a reproducible
browser-emulation check, not a substitute for testing the site's real network.


### Backup encryption: first setup and plaintext migration (2026-11-16)

Production startup now warns when `Scada:BackupPassword` is empty. Local plaintext backups remain
available for compatibility, but **Directory and S3 offsite targets refuse plaintext before any
remote call or remote retention deletion**. A failed offsite target is recorded in backup runs
and surfaced through the existing health warning. Renaming `.zip` to `.enc` does not bypass the check.

For Windows first setup, in a stopped-server maintenance window run the repository tool:

```powershell
.\tools\deployment\Set-BackupPassword.ps1 -SiteSettingsPath 'D:\ScadaData\appsettings.Site.json'
```

It prompts twice without echo, accepts at least 12 characters, preserves unrelated settings and
copies the existing file ACL onto the temporary file before writing. Replacement is atomic. Use
the server-generated JSON file, coordinate with other configuration editors, and restrict its ACL
to the service account and trusted administrators: like `Jwt:Key`, this value is a credential.
When a password is already set, the same script rotates it in a stopped-server window: it verifies
the current password, writes `Scada:BackupPasswordPrevious` for restore only, and writes the new
`Scada:BackupPassword`. It lists recent `full_*.zip.enc` packs so they can be reencrypted with
`Scada.BackupTool reencrypt --prompt-password --prompt-new-password` (never pass the password as an
argument). Plain historical zip files still need a new backup after restart. Restart the server and
run a restore drill. It never changes running settings; environment/command-line overrides still win.
No HTTP password-change endpoint is exposed: this repository has no general authenticated
secret-bearing site-file writer, and adding one casually would expand the credential attack surface. Keep a separate, access-controlled
recovery copy of the password, NOT solely inside the backup it unlocks. Do not log it or pass it
in command-line arguments.

After restart create a **new** `.zip.enc`, run the server restore drill, then independently verify
with the offline tool's hidden input (or `SCADA_BACKUP_PASSWORD` supplied through approved secret
management):

```powershell
.\Scada.BackupTool.exe verify --backup 'D:\ScadaBackups\full_example.zip.enc' --prompt-password
```

A console prompt is refused when input is redirected. Older encrypted bundles still need their
original password. Setup is not rotation or key escrow. Scheduled bundles and offsite transfer
use the same configured password. No one-time conversion or deletion of old plaintext bundles is performed; existing backup retention policies still apply to subsequent runs. Inventory
local/share/S3/vendor copies, create and verify replacement encrypted bundles, obtain retention
approval, then remove the old copies in a separate controlled operation. Never remove the last
known-good restore point merely to clear a warning. This change performs no migration/deletion
of existing site data or backups.

## Offline Restore Runbook

The published self-contained Windows package includes `tools/Scada.BackupTool.exe` (added by `tools/deployment/Publish-BackupTool.ps1` after `dotnet publish`). Use it together with the service restore scripts; do **not** expose a one-click restore HTTP API.

- Windows: `tools/deployment/server/Restore-ScadaService.ps1`
- Linux systemd: `tools/deployment/server/restore.sh`

The runbook is: stop the service, restore into an empty staging directory, verify the archive, swap the data directory (keeping `appsettings.Site.json` and logs), start and health-check, and on failure swap back.

Two backup kinds are not interchangeable:

- **Full backup packages** produced by BackupTool / `/api/maintenance/backups` (`full_*.zip` or `.zip.enc`) contain the configuration database (including the factory calendar), runtime database, history shards, unpurged report archives, OPC PKI and keys. The site settings file is not packed and must be kept on the target machine. This is what the restore scripts accept. Restore rewrites report paths to the new `ReportsRoot` and refuses the pack when a packed report is missing or its SHA-256 does not match; `PurgedTs` is never set as a substitute for a missing file.
- **Configuration copies** left by the daily maintenance task (`scada_*.db`) are SQLite copies of the configuration database only. They are not a complete site restore and must not be fed to the full restore runbook as if they were.

The restore scripts write only to an empty staging directory and never overwrite a running instance in place.

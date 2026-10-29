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

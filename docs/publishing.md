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

The publish target automatically runs `npm ci` and `npm run build` in `web`,
then copies all built files into the server's `wwwroot` directory. A frontend
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

## Start the Combined Application

Start the server using the existing site configuration or service installer.
For example, from the published directory:

```powershell
.\Scada.Server.exe --contentRoot="$PWD" --SiteSettings="D:\ScadaData\appsettings.Site.json"
```

The site configuration must supply a production JWT signing key and the
desired listening address and data directories. The existing Windows service
installer generates an initial site configuration; do not commit signing
keys or put them into frontend files. Service installation requires an
administrator terminal and is a separate deployment step.

The bundled `wwwroot` is resolved relative to the server executable, not the
shell's working directory. Existing explicit `--webroot` and
`ASPNETCORE_WEBROOT` overrides are still honored for development and tests.

Open the backend's address in a browser, for example
`http://localhost:5080/traceability`. Refreshing a frontend route uses the
existing SPA fallback. API requests and SignalR use the same host and port.
Starting the server serves the frontend; it does not launch a browser window.

For an isolated local preview only, the development environment can generate
a temporary signing key:

```powershell
.\Scada.Server.exe --environment=Development --contentRoot="$PWD" --Kestrel:Endpoints:Http:Url=http://127.0.0.1:5080
```

Do not use the development preview as a production deployment. Set explicit
data directories and production secrets before connecting field equipment.

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

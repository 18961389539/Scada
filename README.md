# SCADA

Current application: .NET 10 server, collection gateway and Vue 3 web client.
The server publishes the frontend and API together on one port.

## Project map

- [Scada.sln](Scada.sln): active backend projects and tests.
- [src](src): Core/Data/Collector/Engine/Server, Gateway/Host and ScriptWorker.
- [web](web): active Vue application, build checks and Playwright tests.
- [tests](tests): backend unit/integration and capacity regression tests.
- [tools/deployment](tools/deployment): server/gateway installation, upgrade and release checks.
- [ScadaComm](ScadaComm), [ScadaCommon](ScadaCommon), [ScadaServer](ScadaServer),
  [ScadaWeb](ScadaWeb), [ScadaReport](ScadaReport): legacy/reference tree, not
  included in the active solution. Do not confuse these with `src` and `web`.

## Build and validate

Requires .NET 10 SDK and Node.js 22+ with npm. From the repository root:

```powershell
dotnet build Scada.sln -c Release
dotnet test Scada.sln -c Release --no-build
Set-Location web
npm ci
npm run build
npm run i18n:check
npm run check:access
npm run test:e2e:build
npm run test:smoke -- --repeat-each=3
```

Local browser tests use Edge by default. To use bundled Chromium, run
`npx playwright install chromium` and set `$env:PW_CHANNEL = 'chromium'`.
Tests create isolated temporary databases and must not connect to a production site.
Never rebuild/publish over a running instance or an output folder with site data.

## Deployment and acceptance

- [Unified publishing and release verification](docs/publishing.md)
- [History storage, query budgets and capacity baseline](docs/history-capacity.md)
- [Software licensing and activation](docs/licensing.md)
- [C# advanced scripts](docs/csharp-scripts.md)
- [Customer demo](docs/customer-demo.md)
- [Embedded read-only MCP integration](docs/mcp.md)
- [Screen designer](docs/screen-designer.md)
- [Layout lifecycle](docs/layout-lifecycle.md)
- [Traceability](docs/traceability.md)

The published Windows server is a GUI-subsystem executable with an application
icon and a tray menu (open UI, open log directory, exit). It writes no console
output; logs go to the data directory's `logs` folder. Tray controls, the startup
failure dialog and PWA installation are covered in
[unified publishing](docs/publishing.md).

CI checks are not a site commissioning certificate. Verify real device connectivity,
hardware capacity, backups and recovery, target browsers and role permissions before
connecting field equipment.

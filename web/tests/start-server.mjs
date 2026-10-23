import { spawn } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join, sep } from 'node:path'
const root = mkdtempSync(join(tmpdir(), 'scada-e2e-'))
const server = resolve('../src/Scada.Server')
const child = spawn('dotnet', [join(server, 'bin/Release/net10.0/Scada.Server.dll'),
  '--Kestrel:Endpoints:Http:Url=http://127.0.0.1:18581', `--Scada:ConfigDbPath=${join(root, 'config.db')}`,
  `--Scada:HistoryRoot=${join(root, 'history')}`, `--Scada:BackupRoot=${join(root, 'backups')}`,
  '--Scada:SeedDemoData=false', `--webroot=${resolve('dist')}`],
{ cwd: server, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: 'inherit', windowsHide: true })
const stop = () => child.kill()
process.on('SIGINT', stop); process.on('SIGTERM', stop); process.on('exit', stop)
child.on('exit', code => {
  // Only this test's freshly-created temporary directory may be removed.
  if (resolve(root).startsWith(resolve(tmpdir()) + sep) && root.split(sep).at(-1).startsWith('scada-e2e-')) rmSync(root, { recursive: true, force: true })
  process.exit(code ?? 0)
})

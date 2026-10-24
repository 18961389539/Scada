import { spawn } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { resolve, join, sep } from 'node:path'
const root = mkdtempSync(join(tmpdir(), 'scada-e2e-'))
const server = resolve('../src/Scada.Server')
let child, stopping = false
const simulator = spawn('dotnet', [resolve('../tools/Scada.OpcSimulator/bin/Release/net10.0/Scada.OpcSimulator.dll'), '--endpoint=opc.tcp://localhost:18583/ScadaSimulator', `--pki=${join(root, 'simulator-pki')}`, '--accept-test-clients=true'], { windowsHide: true, stdio: ['ignore', 'pipe', 'inherit'] })
let simulatorLog = ''
simulator.stdout.on('data', data => {
  simulatorLog = (simulatorLog + String(data)).slice(-8192)
  if (!child && simulatorLog.includes('OPC UA server listening at')) startServer()
})
function startServer() {
child = spawn('dotnet', [join(server, 'bin/Release/net10.0/Scada.Server.dll'),
  '--Kestrel:Endpoints:Http:Url=http://127.0.0.1:18581', `--Scada:ConfigDbPath=${join(root, 'config.db')}`,
  `--Scada:HistoryRoot=${join(root, 'history')}`, `--Scada:BackupRoot=${join(root, 'backups')}`, `--Scada:ReportsRoot=${join(root, 'reports')}`,
  '--Scada:SeedDemoData=false', '--Gateway:AllowLoopbackHttp=true', '--Gateway:CommandTimeoutMs=3000', `--webroot=${resolve('dist')}`],
{ cwd: server, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: 'inherit', windowsHide: true })
child.on('exit', (code, signal) => {
  if (!stopping) process.stderr.write(`[scada-e2e] center exited at ${new Date().toISOString()}: code=${code}, signal=${signal}\n`)
  const finish = () => { cleanup(); process.exit(code ?? 0) }
  if (simulator.exitCode !== null || simulator.signalCode !== null) finish()
  else { simulator.once('exit', finish); simulator.kill() }
})
}
const stop = () => { stopping = true; child?.kill(); simulator.kill() }
process.on('SIGINT', stop); process.on('SIGTERM', stop); process.on('exit', stop)
simulator.on('exit', (code, signal) => {
  if (!stopping) process.stderr.write(`[scada-e2e] simulator exited at ${new Date().toISOString()}: code=${code}, signal=${signal}\n`)
  if (!child) { process.stderr.write(simulatorLog); cleanup(); process.exit(code || 1) }
})
function cleanup() {
  // Only this test's freshly-created temporary directory may be removed.
  if (resolve(root).startsWith(resolve(tmpdir()) + sep) && root.split(sep).at(-1).startsWith('scada-e2e-')) rmSync(root, { recursive: true, force: true })
}

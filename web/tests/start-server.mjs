import { spawn } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { resolve, join } from 'node:path'

const root = mkdtempSync(join(tmpdir(), 'scada-e2e-'))
const server = resolve('../src/Scada.Server')
const binaryRoot = resolve(process.env.PW_SERVER_DIR ?? join(server, 'bin/Release/net10.0'))
const simulatorRoot = resolve(process.env.PW_OPC_DIR ?? '../tools/Scada.OpcSimulator/bin/Release/net10.0')
const webroot = resolve(process.env.PW_WEB_ROOT ?? 'dist')
const children = []
let stopping = false
let failure
let notifyStop
const stopped = new Promise(done => { notifyStop = done })
function stop() { stopping = true; notifyStop() }
process.on('message', message => { if (message === 'stop') stop() })
process.on('disconnect', stop)
process.on('SIGINT', stop)
process.on('SIGTERM', stop)

async function assertFree(port) {
  const probe = createServer()
  await new Promise((done, reject) => {
    probe.once('error', reject)
    probe.listen({ port, host: '::', exclusive: true }, done)
  })
  await new Promise((done, reject) => probe.close(error => error ? reject(error) : done()))
}

function launch(args, options) {
  const child = spawn('dotnet', args, { windowsHide: true, ...options })
  const closed = new Promise(done => child.once('close', done))
  children.push({ child, closed })
  child.once('error', error => { failure = error; stop() })
  child.once('exit', (code, signal) => {
    if (!stopping) {
      failure = new Error(`Test process exited unexpectedly: ${args[0]}, code=${code}, signal=${signal}`)
      stop()
    }
  })
  return child
}

try {
  // Never send test requests to an unrelated service already using these ports.
  await assertFree(18581)
  await assertFree(18583)
  let simulatorReady = false
  let simulatorLog = ''
  const simulator = launch([
    join(simulatorRoot, 'Scada.OpcSimulator.dll'),
    '--endpoint=opc.tcp://localhost:18583/ScadaSimulator',
    `--pki=${join(root, 'simulator-pki')}`, '--accept-test-clients=true'
  ], { stdio: ['ignore', 'pipe', 'inherit'] })
  simulator.stdout.on('data', data => {
    simulatorLog = (simulatorLog + String(data)).slice(-8192)
    simulatorReady = simulatorLog.includes('OPC UA server listening at')
  })
  while (!stopping && !simulatorReady) await new Promise(done => setTimeout(done, 100))
  if (!stopping) {
    launch([
      join(binaryRoot, 'Scada.Server.dll'),
      `--Scada:DataRoot=${root}`, `--Scada:LogRoot=${join(root, 'logs')}`,
      `--Scada:DiagnosticsRoot=${join(root, 'diagnostics')}`,
      '--Kestrel:Endpoints:Http:Url=http://127.0.0.1:18581',
      `--Scada:ConfigDbPath=${join(root, 'config.db')}`,
      `--Scada:HistoryRoot=${join(root, 'history')}`, `--Scada:BackupRoot=${join(root, 'backups')}`,
      `--Scada:ReportsRoot=${join(root, 'reports')}`, '--Scada:SeedDemoData=false',
      '--Gateway:AllowLoopbackHttp=true', '--Gateway:CommandTimeoutMs=3000',
      '--Scada:CSharpScripts:Enabled=true', `--webroot=${webroot}`
    ], { cwd: server, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', SCADA_NO_BROWSER: '1', SCADA_NO_TRAY: '1' }, stdio: 'inherit' })
    while (!stopping) {
      try {
        const response = await fetch('http://127.0.0.1:18581/health', { signal: AbortSignal.timeout(1000) })
        if (response.ok) { process.send?.('ready'); break }
      } catch (error) {
        if (error.name !== 'TimeoutError' && error.cause?.code !== 'ECONNREFUSED') throw error
      }
      await new Promise(done => setTimeout(done, 200))
    }
    await stopped
  }
  if (failure) throw failure
} catch (error) {
  console.error(error)
  process.exitCode = 1
} finally {
  stopping = true
  for (const { child, closed } of children.reverse()) {
    if (child.exitCode === null && child.signalCode === null) child.kill()
    await closed
  }
  rmSync(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 500 })
  if (process.connected) process.disconnect()
}

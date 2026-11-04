import { execFileSync, spawn } from 'node:child_process'
import { existsSync, mkdtempSync, rmSync } from 'node:fs'
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
  // 启动参数由 fixtures 按用例内容决定：只有访问 OPC 端点的用例才需要模拟器。
  const needsOpc = process.argv.includes('--opc')
  const serverDll = join(binaryRoot, 'Scada.Server.dll')
  if (!existsSync(serverDll)) throw new Error(`Missing ${serverDll}. Run: npm run test:e2e:build`)
  if (!existsSync(join(webroot, 'index.html'))) throw new Error(`Missing web build at ${webroot}. Run: npm run test:e2e:build`)
  // Never send test requests to an unrelated service already using these ports.
  await assertFree(18581)
  if (needsOpc) await assertFree(18583)
  let simulatorLog = ''
  if (needsOpc) {
    let simulatorReady = false
    const simulator = launch([
      join(simulatorRoot, 'Scada.OpcSimulator.dll'),
      '--endpoint=opc.tcp://localhost:18583/ScadaSimulator',
      `--pki=${join(root, 'simulator-pki')}`, '--accept-test-clients=true'
    ], { stdio: ['ignore', 'pipe', 'pipe'] })
    const collect = data => {
      simulatorLog = (simulatorLog + String(data)).slice(-8192)
      simulatorReady = simulatorLog.includes('OPC UA server listening at')
    }
    simulator.stdout.on('data', collect)
    simulator.stderr.on('data', collect)
    while (!stopping && !simulatorReady && !failure) await new Promise(done => setTimeout(done, 100))
    if (!stopping && !simulatorReady) throw new Error(`OPC simulator did not start:\n${simulatorLog}`)
  }
  if (!stopping) {
    launch([
      serverDll,
      `--Scada:DataRoot=${root}`, `--Scada:LogRoot=${join(root, 'logs')}`,
      `--Scada:DiagnosticsRoot=${join(root, 'diagnostics')}`,
      '--Kestrel:Endpoints:Http:Url=http://127.0.0.1:18581',
      `--Scada:ConfigDbPath=${join(root, 'config.db')}`,
      `--Scada:HistoryRoot=${join(root, 'history')}`, `--Scada:BackupRoot=${join(root, 'backups')}`,
      `--Scada:ReportsRoot=${join(root, 'reports')}`, '--Scada:SeedDemoData=false',
      // E2E 用 admin/admin123 直接建数据；强制改密会挡住所有用例的准备工作（策略本身由 PasswordChangeTests 覆盖）。
      '--Scada:RequirePasswordChangeOnFirstLogin=false',
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
  if (failure) {
    if (simulatorLog) failure = new Error(`${failure.message}\n--- OPC simulator log ---\n${simulatorLog}`)
    throw failure
  }
} catch (error) {
  console.error(error)
  process.exitCode = 1
} finally {
  stopping = true
  for (const { child, closed } of children.reverse()) {
    if (child.exitCode === null && child.signalCode === null) {
      child.kill()
      // dotnet 偶尔会在退出阶段卡住；超时后升级为整棵进程树强杀，避免锁住下一次构建。
      const timer = setTimeout(() => {
        if (child.exitCode === null && child.signalCode === null && process.platform === 'win32' && child.pid) {
          try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' }) } catch { /* 已退出 */ }
        }
      }, 5000)
      await closed
      clearTimeout(timer)
      continue
    }
    await closed
  }
  rmSync(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 500 })
  if (process.connected) process.disconnect()
}

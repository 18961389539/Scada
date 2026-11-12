#!/usr/bin/env node
// 契约漂移门禁：拉起一个临时服务端，拉取 /openapi/v1.json，重新生成 contract.ts 并与仓库版本比对。
// 服务端产物需先构建：npm run test:e2e:build（CI 里由前一步完成）。
// 用法：node scripts/check-api-contract.mjs [--update]
import { spawn, execFileSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs'
import { createServer } from 'node:net'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import openapiTS, { astToString } from 'openapi-typescript'

const update = process.argv.includes('--update')
const serverDll = resolve('../src/Scada.Server/bin/Release/net10.0/Scada.Server.dll')
const contractPath = resolve('src/api/contract.ts')
if (!existsSync(serverDll)) {
  console.error(`[api-contract] 缺少 ${serverDll}；先运行 npm run test:e2e:build`)
  process.exit(1)
}

const freePort = () => new Promise((done, reject) => {
  const probe = createServer()
  probe.once('error', reject)
  probe.listen({ port: 0, host: '127.0.0.1', exclusive: true }, () => {
    const { port } = probe.address()
    probe.close(error => error ? reject(error) : done(port))
  })
})

const port = await freePort()
const root = mkdtempSync(join(tmpdir(), 'scada-contract-'))
const child = spawn('dotnet', [serverDll,
  `--urls`, `http://127.0.0.1:${port}`,
  `--Scada:DataRoot=${root}`,
  `--Scada:ConfigDbPath=${join(root, 'config.db')}`,
  `--Scada:RuntimeDbPath=${join(root, 'runtime.db')}`,
  `--Scada:HistoryRoot=${join(root, 'history')}`,
  `--Scada:BackupRoot=${join(root, 'backups')}`,
  `--Scada:LogRoot=${join(root, 'logs')}`,
  '--Scada:SeedDemoData=false',
  '--Scada:RequirePasswordChangeOnFirstLogin=false'
], {
  windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
  env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development', SCADA_NO_TRAY: '1', SCADA_NO_BROWSER: '1' }
})

let log = ''
const collect = data => { log = (log + String(data)).slice(-16384) }
child.stdout.on('data', collect)
child.stderr.on('data', collect)

const stop = () => new Promise(done => {
  if (child.exitCode !== null || child.signalCode !== null) return done()
  const timer = setTimeout(() => {
    if (process.platform === 'win32' && child.pid) {
      try { execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' }) } catch { }
    }
    done()
  }, 5000)
  child.once('exit', () => { clearTimeout(timer); done() })
  child.kill()
})

try {
  const deadline = Date.now() + 60000
  let healthy = false
  while (Date.now() < deadline && !healthy) {
    if (child.exitCode !== null) break
    try {
      const response = await fetch(`http://127.0.0.1:${port}/health`, { signal: AbortSignal.timeout(1000) })
      healthy = response.ok
    } catch { }
    if (!healthy) await new Promise(done => setTimeout(done, 200))
  }
  if (!healthy) throw new Error(`临时服务端未就绪：\n${log}`)

  const response = await fetch(`http://127.0.0.1:${port}/openapi/v1.json`)
  if (!response.ok) throw new Error(`GET /openapi/v1.json 返回 ${response.status}`)
  const schema = await response.json()

  // 响应契约覆盖棘轮：历史端点先不强制改造，但新增端点必须声明响应类型，
  // 已声明的响应也不能悄悄退回 anonymous。否则 types.ts 会继续与后端字段各写各的。
  const operations = Object.values(schema.paths ?? {})
    .flatMap(path => Object.values(path))
    .filter(operation => operation && typeof operation === 'object' && operation.responses)
  const withResponse = operations.filter(operation =>
    Object.values(operation.responses).some(item => item?.content && Object.keys(item.content).length > 0)).length
  const coveragePath = resolve('contract-coverage.json')
  const baseline = existsSync(coveragePath)
    ? JSON.parse(readFileSync(coveragePath, 'utf8'))
    : { operations: 0, withResponse: 0 }

  const generated = `// 由 scripts/generate-api-types.mjs 从 /openapi/v1.json 生成，请勿手改。\n${astToString(await openapiTS(schema))}`

  if (update) {
    writeFileSync(contractPath, generated, 'utf8')
    writeFileSync(coveragePath, `${JSON.stringify({ operations: operations.length, withResponse }, null, 2)}\n`, 'utf8')
    console.log(`[api-contract] 已更新 ${contractPath}；响应契约覆盖 ${withResponse}/${operations.length}`)
  } else {
    const committed = existsSync(contractPath) ? readFileSync(contractPath, 'utf8') : ''
    if (committed !== generated) {
      console.error('[api-contract] src/api/contract.ts 与当前服务端契约不一致。')
      console.error('  确认是有意的接口变更后，运行：npm run api:types:update（需要已构建的服务端）')
      process.exitCode = 1
    } else {
      console.log('[api-contract] 契约一致')
    }

    const required = baseline.withResponse + Math.max(0, operations.length - baseline.operations)
    if (withResponse < required) {
      console.error(`[api-contract] 响应契约覆盖不足：${withResponse}/${operations.length}，要求至少 ${required}。`)
      console.error('  新增端点请用 TypedResults<T> 或 .Produces<T>() 声明响应类型；有意移除声明时同步更新 contract-coverage.json。')
      process.exitCode = 1
    } else {
      console.log(`[api-contract] 响应契约覆盖 ${withResponse}/${operations.length}（新增端点须声明响应类型）`)
    }
  }
} catch (error) {
  console.error(`[api-contract] ${error.message}`)
  process.exitCode = 1
} finally {
  await stop()
  rmSync(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 300 })
}

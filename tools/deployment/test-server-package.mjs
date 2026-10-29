import assert from 'node:assert/strict'
import { spawn, execFileSync } from 'node:child_process'
import { once } from 'node:events'
import { existsSync, mkdtempSync, rmSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { createServer } from 'node:net'

const args = process.argv.slice(2)
function argument(name) { const i = args.indexOf(name); return i < 0 ? undefined : args[i + 1] }
if (!argument('--package')) throw new Error('Usage: node test-server-package.mjs --package <directory> [--previous-package <directory>]')
const packagePath = resolve(argument('--package'))
const previous = argument('--previous-package') ? resolve(argument('--previous-package')) : packagePath
const root = mkdtempSync(join(tmpdir(), 'scada-package-test-'))
const probe = createServer()
probe.listen(0, '127.0.0.1')
await once(probe, 'listening')
const port = probe.address().port
await new Promise((done, reject) => probe.close(error => error ? reject(error) : done()))
const baseUrl = `http://127.0.0.1:${port}`
let child
let logs = ''
async function stop() {
  if (!child || child.exitCode !== null || child.signalCode !== null) return
  const exited = once(child, 'exit')
  child.kill()
  await exited
}
async function start(directory, dataRoot) {
  if (existsSync(join(directory, 'appsettings.Site.json')))
    throw new Error('Use a clean binary package without site configuration; never run this check against an installed site.')
  logs = ''
  child = spawn(join(directory, 'Scada.Server.exe'), [
    '--environment=Production', `--contentRoot=${directory}`, `--Scada:DataRoot=${dataRoot}`,
    `--Kestrel:Endpoints:Http:Url=${baseUrl}`, '--Scada:SeedDemoData=false', '--Scada:OpenBrowserOnStart=false'
  ], { cwd: root, windowsHide: true, env: { ...process.env, SCADA_NO_BROWSER: '1', SCADA_NO_TRAY: '1' }, stdio: ['ignore', 'pipe', 'pipe'] })
  let spawnError
  child.on('error', error => { spawnError = error })
  for (const stream of [child.stdout, child.stderr]) stream.on('data', data => { logs = (logs + data).slice(-65536) })
  for (let attempt = 0; attempt < 120; attempt++) {
    if (spawnError) throw spawnError
    if (child.exitCode !== null) throw new Error(`Package exited ${child.exitCode}:\n${logs}`)
    try {
      const response = await fetch(`${baseUrl}/health`, { signal: AbortSignal.timeout(1000) })
      if (response.ok) return
    } catch (error) {
      if (error.cause?.code !== 'ECONNREFUSED' && error.name !== 'TimeoutError') throw error
    }
    await new Promise(done => setTimeout(done, 500))
  }
  throw new Error(`Package startup timeout:\n${logs}`)
}
async function json(path, method = 'GET', body, token) {
  const response = await fetch(baseUrl + path, { method, signal: AbortSignal.timeout(60000),
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body) })
  assert.ok(response.ok, `${method} ${path}: ${response.status} ${await response.clone().text()}`)
  return response.json()
}
const login = async () => (await json('/api/auth/login', 'POST', { userName: 'admin', password: 'admin123' })).token
try {
  const data = join(root, 'data')
  await start(previous, data)
  let token = await login()
  await json('/api/devices', 'POST', { name: 'package-retained-device', protocol: 'ModbusTcp', host: '127.0.0.1',
    port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 500, enabled: false }, token)
  const backup = await json('/api/maintenance/backups', 'POST', {}, token)
  const verified = await json(`/api/maintenance/backups/${backup.fileName}/verify`, 'POST', {}, token)
  assert.equal(verified.valid, true, JSON.stringify(verified))
  await stop()
  await start(packagePath, data)
  token = await login()
  assert.ok((await json('/api/devices', 'GET', undefined, token)).some(device => device.name === 'package-retained-device'))
  const index = await fetch(baseUrl + '/traceability')
  assert.equal(index.status, 200)
  const html = await index.text()
  const scripts = [...html.matchAll(/src="([^"]+\.js)"/g)].map(match => match[1])
  assert.ok(scripts.length > 0, 'SPA must reference built JavaScript')
  for (const script of scripts) {
    const asset = await fetch(new URL(script, baseUrl))
    assert.equal(asset.status, 200)
    assert.match(asset.headers.get('content-type'), /javascript/)
  }
  const drill = await json(`/api/maintenance/backups/${backup.fileName}/drill`, 'POST', {}, token)
  assert.equal(drill.ok, true, JSON.stringify(drill))
  await stop()
  const restored = join(root, 'restored')
  const settings = JSON.parse(readFileSync(join(data, 'appsettings.Site.json'), 'utf8'))
  execFileSync('dotnet', [resolve('tools/Scada.BackupTool/bin/Release/net10.0/Scada.BackupTool.dll'), 'restore',
    '--backup', join(settings.Scada.BackupRoot, backup.fileName), '--target', restored], { stdio: 'pipe' })
  await start(previous, restored)
  token = await login()
  assert.ok((await json('/api/devices', 'GET', undefined, token)).some(device => device.name === 'package-retained-device'))
  console.log(previous === packagePath
    ? 'PASS: package startup, SPA assets, login, persistence, backup verification, restore and restart. Cross-version binaries not supplied.'
    : 'PASS: old package -> new package upgrade; backup restored and old package restarted successfully.')
} catch (error) {
  console.error(logs)
  throw error
} finally {
  await stop()
  rmSync(root, { recursive: true, force: true, maxRetries: 3, retryDelay: 500 })
}

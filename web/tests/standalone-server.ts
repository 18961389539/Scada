import { spawn } from 'node:child_process'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve, sep } from 'node:path'

export interface StandaloneServer {
  baseUrl: string
  stop(): Promise<void>
}

// 会留下不可删除数据的用例（ISA-18.2 报警事件没有删除接口）自己起一个实例：
// 整套 E2E 只共享一个服务器，脏数据会顺着文件字母序污染后面的用例。
export async function startStandaloneServer(port: number): Promise<StandaloneServer> {
  const root = mkdtempSync(join(tmpdir(), 'scada-e2e-iso-'))
  const cwd = resolve('../src/Scada.Server')
  const baseUrl = `http://127.0.0.1:${port}`
  const child = spawn('dotnet', [
    join(cwd, 'bin/Release/net10.0/Scada.Server.dll'),
    `--Kestrel:Endpoints:Http:Url=${baseUrl}`,
    `--Scada:ConfigDbPath=${join(root, 'config.db')}`,
    `--Scada:HistoryRoot=${join(root, 'history')}`,
    `--Scada:BackupRoot=${join(root, 'backups')}`,
    `--Scada:ReportsRoot=${join(root, 'reports')}`,
    '--Scada:SeedDemoData=false',
    `--webroot=${resolve('dist')}`
  ], { cwd, env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'ignore', 'inherit'], windowsHide: true })

  const deadline = Date.now() + 60_000
  for (;;) {
    try {
      if ((await fetch(`${baseUrl}/api/health`)).ok) break
    } catch {
      // 还没开始监听
    }
    if (child.exitCode !== null) throw new Error(`独立实例启动失败，退出码 ${child.exitCode}`)
    if (Date.now() > deadline) {
      child.kill()
      throw new Error('等待独立实例健康检查超时')
    }
    await new Promise((done) => setTimeout(done, 300))
  }

  return {
    baseUrl,
    async stop() {
      // Windows 上 kill 是异步的，进程没退出前 SQLite 文件仍被占用，rmSync 会 EPERM
      const exited = new Promise<void>((done) => child.once('exit', () => done()))
      child.kill()
      await Promise.race([exited, new Promise<void>((done) => setTimeout(done, 10_000))])
      // 只清理本用例自己创建的临时目录
      if (!resolve(root).startsWith(resolve(tmpdir()) + sep) || !root.split(sep).at(-1)?.startsWith('scada-e2e-iso-')) return
      for (let attempt = 0; attempt < 3; attempt++) {
        try {
          rmSync(root, { recursive: true, force: true })
          return
        } catch (error) {
          if (attempt === 2) console.warn(`[scada-e2e] 临时目录清理失败，请手动删除 ${root}：${String(error)}`)
          else await new Promise<void>((done) => setTimeout(done, 500))
        }
      }
    }
  }
}

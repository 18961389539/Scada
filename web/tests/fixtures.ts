import { expect, test as base, type APIRequestContext, type Locator, type Page } from '@playwright/test'
import { execFileSync, fork } from 'node:child_process'
import { once } from 'node:events'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

export * from '@playwright/test'

// 只有真正连接 OPC 模拟器的用例才拉起模拟器，其余用例省掉一段启动/关闭开销。
function needsOpcSimulator(specFile: string): boolean {
  try { return /18583|opc\.tcp/i.test(readFileSync(specFile, 'utf8')) } catch { return false }
}

export const test = base.extend<{ isolatedServer: void }>({
  isolatedServer: [async ({}, use, testInfo) => {
    if (process.env.PW_BASE_URL) {
      throw new Error('Isolated tests cannot use PW_BASE_URL. Run package smoke with its dedicated runner instead.')
    }
    const args = ['tests/start-server.mjs', ...(needsOpcSimulator(testInfo.file) ? ['--opc'] : [])]
    const child = fork(resolve(args[0]), args.slice(1), { silent: true })
    let output = ''
    const collect = (data: Buffer) => { output = (output + data.toString()).slice(-65536) }
    child.stdout?.on('data', collect)
    child.stderr?.on('data', collect)
    const exited = once(child, 'exit')
    try {
      await new Promise<void>((ready, reject) => {
        const timer = setTimeout(() => reject(new Error(`Test server startup timed out:\n${output}`)), 60000)
        child.once('error', error => { clearTimeout(timer); reject(error) })
        child.once('exit', code => { clearTimeout(timer); reject(new Error(`Test server exited (${code}):\n${output}`)) })
        child.on('message', message => {
          if (message === 'ready') { clearTimeout(timer); ready() }
        })
      })
      await use()
    } finally {
      if (child.connected) child.send('stop')
      let forcedStop = false
      const timer = setTimeout(() => {
        forcedStop = true
        if (process.platform === 'win32' && child.pid) {
          execFileSync('taskkill', ['/PID', String(child.pid), '/T', '/F'], { windowsHide: true })
        } else child.kill('SIGKILL')
      }, 15000)
      let code: number | null
      try { [code] = await exited } finally { clearTimeout(timer) }
      if (testInfo.status !== testInfo.expectedStatus) {
        await testInfo.attach('isolated-server.log', { body: output, contentType: 'text/plain' })
      }
      if (forcedStop || code !== 0) throw new Error(`Test server cleanup failed (${code}):\n${output}`)
    }
  }, { auto: true, timeout: 90000 }]
})

export async function revealConsoleControl(page: Page, testId: string): Promise<Locator> {
  const control = page.locator(`[data-test="${testId}"]`)
  if (await control.isVisible()) return control
  await page.locator('[data-test="console-more"]').click()
  await expect(control).toBeVisible()
  return control
}

// 共享登录助手：返回完整登录结果并附带 Authorization 头，覆盖各用例对 token/user/headers 的取用。
export async function loginApi(request: APIRequestContext, userName = 'admin', password = 'admin123') {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

// 共享会话注入：在下一次导航前把令牌/用户写入 localStorage（addInitScript 时序）。
export async function applySession(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

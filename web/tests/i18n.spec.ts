import { test, expect, type APIRequestContext } from './fixtures'
import { execSync } from 'node:child_process'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(r.ok()).toBeTruthy()
  const result = await r.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('i18n: no untranslated keys or leftover literals', () => {
  expect(execSync('node scripts/i18n-check.mjs', { encoding: 'utf8' })).toContain('i18n ok')
})

test('i18n: switch to English, Element Plus locale, server-side preference, backend messages and multilingual HMI text', async ({ page, request, browser }) => {
  const auth = await login(request), h = auth.headers, u = `${Date.now()}`.slice(-6)
  const s = await (await request.post('/api/screens', { headers: h, data: { name: `多语言-${u}` } })).json()
  const nodes = [{ id: 'lbl', type: 'text', x: 40, y: 40, width: 300, height: 60, rotation: 0, text: '启动泵', texts: { 'en-US': 'Start pump' }, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, action: { type: 'none', tagId: null, screenId: null, value: 0 } }]
  const put = await request.put(`/api/screens/${s.id}`, { headers: h, data: { name: `多语言-${u}`, revision: s.revision, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes } } }); expect(put.ok(), await put.text()).toBeTruthy()
  expect((await request.post(`/api/screens/${s.id}/publish`, { headers: h, data: { revision: (await put.json()).revision } })).ok()).toBeTruthy()
  try {
    await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
    await page.goto(`/screens/${s.id}`)
    await expect(page.getByRole('menuitem', { name: '总览' })).toBeVisible()
    await expect(page.locator('[data-node-id="lbl"]')).toContainText('启动泵')
    await page.locator('.user-chip').click()
    const saved = page.waitForRequest(r => r.url().endsWith('/api/auth/me/locale') && r.method() === 'PUT')
    await page.locator('[data-test="locale-en-US"]').click(); await saved
    await expect(page.getByRole('menuitem', { name: 'Overview' })).toBeVisible()
    await expect(page.locator('html')).toHaveAttribute('lang', 'en-US')
    await expect(page.locator('[data-node-id="lbl"]')).toContainText('Start pump')
    await expect(page.getByRole('button', { name: 'Refresh' }).first()).toBeVisible()

    // Element Plus 内置文案
    await page.goto('/system'); await page.getByRole('tab', { name: 'Operation log' }).click()
    await expect(page.locator('.el-pagination__total')).toContainText('Total')

    // 新浏览器（本机无语言记录）登录后按服务端偏好显示英文；后端错误提示也为英文
    const fresh = await browser.newContext(); const p2 = await fresh.newPage()
    await p2.goto('/login')
    await p2.locator('[data-test="login-locale"]').click(); await p2.getByRole('option', { name: 'English' }).click()
    await expect(p2.getByRole('button', { name: /Sign in/ })).toBeVisible()
    await p2.locator('input').nth(0).fill('admin'); await p2.locator('input').nth(1).fill('bad-password')
    await p2.getByRole('button', { name: /Sign in/ }).click()
    await expect(p2.getByText('Incorrect user name or password')).toBeVisible()
    await p2.evaluate(() => localStorage.removeItem('scada_locale')); await p2.reload()
    await expect(p2.getByRole('button', { name: /登 录/ })).toBeVisible()
    await p2.locator('input').nth(0).fill('admin'); await p2.locator('input').nth(1).fill('admin123')
    await p2.getByRole('button', { name: /登 录/ }).click()
    await expect(p2.getByRole('menuitem', { name: 'Overview' })).toBeVisible()
    await p2.screenshot({ path: test.info().outputPath('english.png') })
    await fresh.close()
  } finally {
    await request.put('/api/auth/me/locale', { headers: h, data: { locale: 'zh-CN' } })
  }
})

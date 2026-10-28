import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

function gate() {
  let release!: () => void
  return { promise: new Promise<void>(resolve => { release = resolve }), release: () => release() }
}
const runs = [7, 8].map(id => ({
  id, templateId: 1, templateName: `归档报表 ${id}`, templateRevision: 1, areaId: 1, periodKey: '2026-10-26',
  fromTs: 0, toTs: 1, version: 1, trigger: 'manual', generatedBy: 'admin', generatedTs: Date.now(),
  xlsxSha256: 'a'.repeat(16), pdfSha256: 'b'.repeat(16), purgedTs: null, signed: true,
  signerThumbprint: null, signerSubject: null, signedTs: null, signatureError: null, approvalStatus: 'none'
}))
function table(id: number) { return { title: `归档报表 ${id}`, columns: [{ key: 'value', title: '产量', decimals: 0 }], rows: [{ label: '早班', values: [id] }], summary: { label: '合计', values: [id] } } }
function verification(id: number) { return { runId: id, purged: false, signed: true, signerThumbprint: null, signerSubject: `签名人 ${id}`, signedTs: null, signatureError: null, files: [], approvals: [], status: 'none', ok: true, currentThumbprint: null } }
async function prepare(page: Page, request: APIRequestContext) {
  await sign(page, await login(request))
  await page.route(url => url.pathname === '/api/report-templates', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/report-runs', route => route.fulfill({ json: runs }))
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => ['/api/tags', '/api/calc-jobs', '/api/notification-channels'].includes(url.pathname), route => route.fulfill({ json: [] }))
  await page.route(url => /^\/api\/report-runs\/\d+(\/verify)?$/.test(url.pathname), route => {
    const path = new URL(route.request().url()).pathname, id = Number(path.split('/')[3])
    return route.fulfill({ json: path.endsWith('/verify') ? verification(id) : { table: table(id) } })
  })
  await page.goto('/report-center')
  await expect(page.locator('[data-test="report-run-table"] .el-table__row')).toHaveCount(2)
}

for (const kind of ['preview', 'verify'] as const) {
  for (const outcome of ['success', 'failure'] as const) {
    test(`other pages review ignores a closed report ${kind} ${outcome} after another report opens`, async ({ page, request }) => {
      await prepare(page, request)
      const pending = gate(), path = `/api/report-runs/7${kind === 'verify' ? '/verify' : ''}`
      await page.route(url => url.pathname === path, async route => {
        await pending.promise
        return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '旧报表失败' } } : { json: kind === 'verify' ? verification(7) : { table: table(7) } })
      })
      await page.locator(`[data-test="report-${kind === 'preview' ? 'view' : 'verify'}-7"]`).click()
      try {
        await expect(page.locator(`[data-test="report-${kind}-loading"]`)).toBeVisible()
        await page.keyboard.press('Escape')
        await expect(page.locator(`[data-test="report-${kind}-loading"]`)).not.toBeVisible()
        await page.locator(`[data-test="report-${kind === 'preview' ? 'view' : 'verify'}-8"]`).click()
        const dialog = kind === 'verify' ? page.locator('[data-test="report-verify-dialog"]') : page.getByRole('dialog', { name: '归档报表 8', exact: true })
        await expect(dialog).toContainText(kind === 'verify' ? '签名人 8' : '早班')
        const done = page.waitForResponse(response => new URL(response.url()).pathname === path)
        pending.release(); await (await done).finished()
        await page.evaluate(() => new Promise(requestAnimationFrame))
        await expect(dialog).toBeVisible()
        await expect(dialog).not.toContainText(kind === 'verify' ? '签名人 7' : '归档报表 7')
        await expect(dialog.locator(`[data-test="report-${kind}-error"]`)).toHaveCount(0)
      } finally { pending.release() }
    })
  }
}

for (const outcome of ['success', 'failure'] as const) {
  test(`other pages review ignores a late file ${outcome} after a new copy is checked`, async ({ page, request }) => {
    await prepare(page, request)
    const pending = gate()
    let checks = 0
    await page.route(url => url.pathname === '/api/report-runs/7/verify-file', async route => {
      if (++checks === 1) {
        await pending.promise
        return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '旧副本核对失败' } } : { json: { ok: false, matched: null } })
      }
      return route.fulfill({ json: { ok: true, matched: 'pdf' } })
    })
    await page.locator('[data-test="report-verify-7"]').click()
    const input = page.locator('[data-test="report-verify-file"]')
    await input.setInputFiles({ name: 'first.pdf', mimeType: 'application/pdf', buffer: Buffer.from('first') })
    try {
      await expect.poll(() => checks).toBe(1)
      await input.setInputFiles({ name: 'second.pdf', mimeType: 'application/pdf', buffer: Buffer.from('second') })
      await expect(page.locator('[data-test="report-verify-file-result"]')).toContainText('签名有效')
      const done = page.waitForResponse(response => new URL(response.url()).pathname === '/api/report-runs/7/verify-file')
      pending.release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="report-verify-file-result"]')).toContainText('签名有效')
      await expect(page.locator('[data-test="report-verify-file-error"]')).toHaveCount(0)
    } finally { pending.release() }
  })
}

for (const outcome of ['success', 'verify-failure', 'write-failure'] as const) {
  test(`other pages review locks signing and handles ${outcome} without duplicate approval writes`, async ({ page, request }) => {
    await prepare(page, request)
    const pending = gate()
    let writes = 0, verified = 0
    await page.route(url => url.pathname === '/api/report-runs/7/verify', route => {
      if (++verified > 1 && outcome === 'verify-failure') return route.fulfill({ status: 500, json: { message: '签署后验签读取失败' } })
      return route.fulfill({ json: { ...verification(7), status: writes > 0 && outcome !== 'write-failure' ? 'approved' : 'none' } })
    })
    await page.route(url => url.pathname === '/api/report-runs/7/approvals', async route => {
      writes++
      expect(route.request().postDataJSON()).toMatchObject({ step: 'review', reason: '核对无误', decision: 'approved' })
      await pending.promise
      return route.fulfill(outcome === 'write-failure' ? { status: 500, json: { message: '签署暂不可用' } } : { json: { status: 'approved' } })
    })
    await page.locator('[data-test="report-verify-7"]').click()
    const dialog = page.locator('[data-test="report-verify-dialog"]')
    const reason = dialog.locator('[data-test="report-sign-reason"]'), password = dialog.locator('[data-test="report-sign-password"]')
    await reason.fill('核对无误'); await password.fill('test-only-password')
    await dialog.locator('[data-test="report-sign-submit"]').click()
    try {
      await expect.poll(() => writes).toBe(1)
      await expect(reason).toBeDisabled(); await expect(password).toBeDisabled()
      await dialog.locator('[data-test="report-sign-submit"]').dispatchEvent('click')
      await page.keyboard.press('Escape')
      await expect(dialog).toBeVisible()
      expect(writes).toBe(1)
      pending.release()
      if (outcome === 'write-failure') {
        await expect(dialog.locator('[data-test="report-sign-form"]')).toContainText('签署暂不可用')
        await expect(reason).toBeEnabled(); await expect(reason).toHaveValue('核对无误'); await expect(password).toHaveValue('')
      } else {
        await expect(dialog.locator('[data-test="report-sign-form"]')).toHaveCount(0)
        if (outcome === 'verify-failure') {
          await expect(dialog.locator('[data-test="report-verify-error"]')).toContainText('签署后验签读取失败')
          await expect(dialog.locator('[data-test="report-verify-result"]')).toHaveCount(0)
        } else await expect(dialog.locator('[data-test="report-verify-result"]')).toContainText('验证通过')
      }
      expect(writes).toBe(1)
    } finally { pending.release() }
  })
}

for (const outcome of ['success', 'failure'] as const) {
  test(`other pages review ignores a late archive filter ${outcome} after switching templates`, async ({ page, request }) => {
    await prepare(page, request)
    const pending = gate()
    const templates = [1, 2].map(id => ({ id, name: `模板 ${id}`, period: 'Day', areaId: 1, revision: 1, columns: [], scheduleEnabled: false }))
    await page.route(url => url.pathname === '/api/report-templates', route => route.fulfill({ json: templates }))
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await expect(page.locator('[data-test="report-records-2"]')).toBeVisible()
    await page.route(url => url.pathname === '/api/report-runs', async route => {
      const id = new URL(route.request().url()).searchParams.get('templateId')
      if (id === '1') {
        await pending.promise
        return route.fulfill(outcome === 'failure' ? { status: 500, json: { message: '旧模板读取失败' } } : { json: [runs[0]] })
      }
      return route.fulfill({ json: [runs[1]] })
    })
    const started = page.waitForRequest(request => new URL(request.url()).searchParams.get('templateId') === '1')
    await page.locator('[data-test="report-records-1"]').click(); await started
    try {
      await expect(page.locator('[data-test="report-run-table"] .el-table__row')).toHaveCount(0)
      await page.locator('[data-test="report-records-2"]').focus()
      await page.keyboard.press('Enter')
      await expect(page.locator('[data-test="report-run-table"]')).toContainText('归档报表 8')
      const done = page.waitForResponse(response => new URL(response.url()).searchParams.get('templateId') === '1')
      pending.release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(page.locator('[data-test="report-run-table"]')).toContainText('归档报表 8')
      await expect(page.locator('[data-test="report-run-table"]')).not.toContainText('归档报表 7')
      await expect(page.locator('[data-test="report-center-error"]')).toHaveCount(0)
    } finally { pending.release() }
  })
}

test('other pages review keeps archive verification readable on desktop and mobile', async ({ page, request }, testInfo) => {
  await prepare(page, request)
  for (const width of [1600, 390, 320]) {
    await page.setViewportSize({ width, height: 900 })
    await page.reload()
    await page.locator('[data-test="report-verify-7"]').click()
    await expect(page.locator('[data-test="report-verify-result"]')).toContainText('验证通过')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    await page.screenshot({ path: testInfo.outputPath(`verification-${width}.png`), fullPage: true, animations: 'disabled' })
    await page.keyboard.press('Escape')
  }
})

test('report preview and verification failures stay in their dialogs', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const run = {
    id: 7, templateId: 1, templateName: '日报', templateRevision: 1, areaId: 1, periodKey: '2026-10-26',
    fromTs: 0, toTs: 1, version: 2, trigger: 'manual', generatedBy: 'admin', generatedTs: Date.now(),
    xlsxSha256: 'a'.repeat(16), pdfSha256: 'b'.repeat(16), purgedTs: null, signed: false,
    signerThumbprint: null, signerSubject: null, signedTs: null, signatureError: null, approvalStatus: 'none'
  }
  await page.route((url) => url.pathname === '/api/report-runs', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify([run]) })
  })
  await page.route((url) => url.pathname === '/api/report-runs/7', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '报表不可用' }) }))
  await page.route((url) => url.pathname === '/api/report-runs/7/verify', (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '验证不可用' }) }))
  await page.goto('/report-center')
  await page.locator('[data-test="report-view-7"]').click()
  const preview = page.getByRole('dialog', { name: '报表' })
  await expect(preview.locator('[data-test="report-preview-error"]')).toContainText('报表不可用')
  await expect(preview.locator('[data-test="report-preview"]')).toHaveCount(0)
  await page.keyboard.press('Escape')
  await expect(preview).not.toBeVisible()
  await page.locator('[data-test="report-verify-7"]').click()
  const verify = page.locator('[data-test="report-verify-dialog"]')
  await expect(verify.locator('[data-test="report-verify-error"]')).toContainText('验证不可用')
  await expect(verify.locator('[data-test="report-verify-result"]')).toHaveCount(0)
})

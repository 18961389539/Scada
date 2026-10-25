import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } }); expect(r.ok()).toBeTruthy()
  const result = await r.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('symbol library and templates: place ISA symbol, save as template, instance follows template change, SVG import is sanitized', async ({ page, request }) => {
  const auth = await login(request), u = `${Date.now()}`.slice(-6)
  const errors: string[] = []; page.on('pageerror', e => errors.push(e.message))
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const screen = await (await request.post('/api/screens', { headers: auth.headers, data: { name: `图库画面-${u}` } })).json()
  await page.goto(`/config/screens/${screen.id}`)
  await page.locator('[data-test="symbol-library"]').getByText('泵与压缩机').click()
  await page.locator('.palette [data-symbol="isa:pump"]').click()
  await expect(page.locator('[data-node-type="symbol"] [data-symbol="isa:pump"]')).toHaveCount(1)
  await page.locator('[data-test="save-as-template"]').click()
  await page.locator('[data-test="template-key"]').fill(`pump${u}`); await page.locator('[data-test="template-name"]').fill(`泵模板${u}`)
  await page.locator('[data-test="template-create"]').click()
  await expect(page.locator('[data-node-type="template"]')).toHaveCount(1)
  await expect(page.locator('[data-node-type="symbol"] [data-symbol="isa:pump"]')).toHaveCount(1) // 实例展开显示模板内容
  await page.screenshot({ path: test.info().outputPath('designer.png') })
  await page.getByRole('button', { name: '发布', exact: true }).click(); await expect(page.getByText('已发布，运行画面已更新')).toBeVisible()

  await page.goto(`/screens/${screen.id}`)
  const runtime = page.locator('[data-testid="screen-runtime"]')
  await expect(runtime.locator('[data-symbol="isa:pump"]')).toHaveCount(1)
  // 一处修改：模板换成电机符号，已发布画面的实例随之更新
  const item = (await (await request.get('/api/hmi/library', { headers: auth.headers })).json()).find((x: { key: string }) => x.key === `pump${u}`)
  item.document.nodes[0].symbol = 'isa:motor'
  const upd = await request.put(`/api/hmi/library/${item.id}`, { headers: auth.headers, data: item }); expect(upd.ok(), await upd.text()).toBeTruthy()
  expect((await upd.json()).usedBy).toContain(`图库画面-${u}`)
  await page.reload(); await expect(runtime.locator('[data-symbol="isa:motor"]')).toHaveCount(1)

  // 图库页：内置符号、SVG 导入清洗、导出
  await page.goto('/config/hmi-library')
  expect(await page.locator('[data-test="builtin-gallery"] figure').count()).toBeGreaterThanOrEqual(25)
  await page.screenshot({ path: test.info().outputPath('gallery.png') })
  await page.locator('[data-test="library-svg-file"]').setInputFiles({ name: `evil${u}.svg`, mimeType: 'image/svg+xml',
    buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 40 40"><script>alert(1)</script><rect width="40" height="40" onclick="alert(2)" fill="currentColor"/><image href="http://evil/x.png"/></svg>') })
  await expect(page.locator('[data-test="library-items"]')).toContainText(`evil${u}`)
  const sym = (await (await request.get('/api/hmi/library', { headers: auth.headers })).json()).find((x: { key: string }) => x.key === `evil${u}`)
  expect(sym.svg).toContain('<rect'); expect(sym.svg).not.toMatch(/script|onclick|image/)
  const download = page.waitForEvent('download'); await page.locator('[data-test="library-export"]').click()
  expect((await download).suggestedFilename()).toBe('scada-hmi-library.json')
  expect(errors).toEqual([])
})

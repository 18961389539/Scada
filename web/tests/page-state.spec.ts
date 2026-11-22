import { test, expect, type APIRequestContext, type Page } from './fixtures'

const device = { id: 901, name: 'Pump A', protocol: 'ModbusTcp', host: '10.40.1.1', port: 502, areaId: 1, enabled: true }
const tag = {
  id: 31, deviceId: 901, name: 'Pump 出口压力', kind: 'Physical', areaId: 1, area: 'HoldingRegister',
  address: 0, dataType: 'Float', description: '', scale: 1, offset: 0, writable: false,
  archiveMode: 'None', deadband: 0, archivePeriodSec: 600, dbNumber: 1, bitOffset: 0, nodeId: '', unit: 'MPa'
}

async function signIn(page: Page, request: APIRequestContext): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
    localStorage.setItem('scada_locale', 'zh-CN')
  }, auth)
}

async function mockCatalog(page: Page, tags: (search: string) => typeof tag[]): Promise<void> {
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device], headers: { 'X-Total-Count': '1' } }))
  await page.route(url => url.pathname === '/api/tags', route => {
    const search = new URL(route.request().url()).searchParams.get('search')?.trim() ?? ''
    const items = tags(search)
    return route.fulfill({ json: items, headers: { 'X-Total-Count': String(items.length) } })
  })
}

test('empty catalog renders the shared empty state instead of a bare string', async ({ page, request }) => {
  await signIn(page, request)
  await mockCatalog(page, () => [])
  await page.goto('/config/tags')

  const state = page.locator('[data-test="page-state-empty"]')
  await expect(state).toBeVisible()
  await expect(state).toContainText('暂无测点')
})

test('a filtered catalog with no match offers the clear action inside the empty state', async ({ page, request }) => {
  await signIn(page, request)
  await mockCatalog(page, search => search ? [] : [tag])
  await page.goto('/config/tags')
  await expect(page.locator('[data-test="tags-table"] .el-table__body-wrapper .el-table__row')).toHaveCount(1)

  await page.getByRole('textbox', { name: '搜索测点名称/描述' }).fill('not-here')
  await page.locator('[data-test="tag-query"]').click()

  const state = page.locator('[data-test="page-state-empty"]')
  await expect(state).toBeVisible()
  await expect(state).toContainText('没有符合筛选条件的记录')
  await state.getByRole('button', { name: '清除筛选' }).click()
  await expect(state).toHaveCount(0)
  await expect(page.locator('[data-test="tags-table"] .el-table__body-wrapper .el-table__row')).toHaveCount(1)
})

test('a failed first load shows the error state; retry recovers to the empty state', async ({ page, request }) => {
  await signIn(page, request)
  let fail = true
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device], headers: { 'X-Total-Count': '1' } }))
  await page.route(url => url.pathname === '/api/tags', route => {
    if (fail) return route.fulfill({ status: 500, json: { message: '测点列表不可用' } })
    return route.fulfill({ json: [], headers: { 'X-Total-Count': '0' } })
  })
  await page.goto('/config/tags')

  const error = page.locator('[data-test="page-state-error"]')
  await expect(error).toContainText('测点列表不可用')
  await expect(page.locator('[data-test="page-state-empty"]')).toHaveCount(0)

  fail = false
  await error.locator('[data-test="page-state-retry"]').click()
  await expect(error).toHaveCount(0)
  await expect(page.locator('[data-test="page-state-empty"]')).toBeVisible()
})
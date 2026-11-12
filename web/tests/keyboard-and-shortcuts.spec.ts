import { test, expect, type APIRequestContext, type Page, loginApi } from './fixtures'

// 键盘 / 快捷键 / 选点器这几处都依赖「弹层是否打开」的判断。
// Element Plus 把 .el-overlay 常驻在 DOM 里（关闭只是 display:none），
// 曾因此让报警页键盘与全局 ? 全部失效——这些用例就是防它回归的。

async function mockApiFallback(page: Page) {
  // 未显式 mock 的接口一律回空数组：否则请求会落到静态服务器，把 index.html 当 JSON 解析
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
}

async function prepareAlarms(page: Page, request: APIRequestContext) {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  const now = Date.now()
  const alarms = [1, 2, 3].map(index => ({
    id: 900 + index, tagId: 8400 + index, deviceId: 8301, ruleId: 10 + index,
    level: index === 1 ? 'Critical' : 'Warning', value: 500 + index,
    startTs: now - index * 60_000, endTs: null, ackUser: null, ackTs: null,
    message: `键盘验证报警 ${index}`, state: 'Active', shelved: false, suppressed: false
  }))
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 8301, name: '验证设备', areaId: 1 }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 8401, name: '温度', deviceId: 8301, unit: '℃' }] }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({
    json: { unack: 3, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0, flood: { active: false } }
  }))
  await page.route(url => url.pathname === '/api/alarms/page', route => route.fulfill({ json: { items: alarms, total: alarms.length } }))
  await page.route(url => url.pathname === '/api/annunciators', route => route.fulfill({ json: [] }))
}

test('alarm list: arrow keys focus rows, A opens the ack dialog, ? opens shortcut help', async ({ page, request }) => {
  await prepareAlarms(page, request)
  await page.goto('/alarms')
  await expect(page.locator('.alarm-desktop-table tbody tr')).toHaveCount(3)

  await page.keyboard.press('ArrowDown')
  await expect(page.locator('tr.alarm-row-focused')).toHaveCount(1)
  await page.keyboard.press('ArrowDown')
  await expect(page.locator('tr.alarm-row-focused')).toContainText('键盘验证报警 2')

  // A 只负责打开确认弹窗，不能直接改状态
  await page.keyboard.press('a')
  await expect(page.locator('[data-test="note-input"]')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="note-input"]')).toBeHidden()

  await page.keyboard.press('?')
  const help = page.locator('[data-test="shortcut-help"]')
  await expect(help).toBeVisible()
  await expect(help).toContainText('报警列表')
  await expect(help).toContainText('确认该报警')
})

test('? opens shortcut help on the page but stays literal text inside an input', async ({ page, request }) => {
  await prepareAlarms(page, request)
  await page.goto('/alarms')
  await expect(page.locator('.alarm-desktop-table tbody tr')).toHaveCount(3)

  const keyword = page.locator('[data-test="alarm-keyword"]')
  await keyword.click()
  await page.keyboard.type('?')
  await expect(keyword).toHaveValue('?')
  await expect(page.locator('[data-test="shortcut-help"]')).toHaveCount(0)

  await keyword.fill('')
  await keyword.blur()
  await page.keyboard.press('?')
  await expect(page.locator('[data-test="shortcut-help"]')).toBeVisible()
})

test('trend tag picker keeps groups and option roles with the virtual list', async ({ page, request }) => {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 8301, name: '验证设备', areaId: 1 }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: [
      { id: 8401, name: '出口压力', deviceId: 8301, unit: 'kPa', dataType: 'Float' },
      { id: 8402, name: '入口温度', deviceId: 8301, unit: '℃', dataType: 'Float' }
    ]
  }))
  await page.route(url => url.pathname === '/api/history', route => route.fulfill({ json: { resolution: 'Raw', series: [] } }))
  await page.route(url => url.pathname === '/api/history/analysis', route => route.fulfill({ json: { tags: [] } }))

  await page.goto('/trends?tagId=8401')
  const picker = page.locator('[data-test="tag-picker"]')
  await expect(picker).toContainText('验证设备 / 出口压力')

  await picker.click()
  await expect(page.locator('.el-select-dropdown')).toContainText('全部测点')
  await expect(page.getByRole('option', { name: '验证设备 / 入口温度', exact: true })).toBeVisible()
  await page.getByRole('option', { name: '验证设备 / 入口温度', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(page.locator('[data-test="selected-count"]')).toContainText('2')
})

test('OEE config dialog asks before discarding edits, and closes silently when untouched', async ({ page, request }) => {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await mockApiFallback(page)
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  for (const path of ['/api/oee/lines', '/api/oee/products', '/api/oee/shifts', '/api/oee/reasons']) {
    await page.route(url => url.pathname === path, route => route.fulfill({ json: [] }))
  }

  await page.goto('/config/oee')
  await page.locator('[data-test="oee-add-line"]').click()
  const name = page.locator('[data-test="oee-line-name"]')
  await name.fill('验证产线')
  await page.locator('[data-test="oee-line-name"]').press('Escape')

  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  await expect(box).toContainText('尚有未保存的修改')
  await page.getByRole('button', { name: '继续编辑' }).click()
  await expect(page.locator('[data-test="oee-line-save"]')).toBeVisible()

  // 取消按钮同样要确认，不能成为「不告而别」的丢弃入口
  await page.getByRole('button', { name: '取消', exact: true }).click()
  await expect(box).toBeVisible()
  await page.getByRole('button', { name: '放弃修改' }).click()
  await expect(page.locator('[data-test="oee-line-save"]')).toBeHidden()

  await page.locator('[data-test="oee-add-line"]').click()
  await page.locator('[data-test="oee-line-name"]').press('Escape')
  await expect(page.locator('.el-message-box')).toBeHidden()
  await expect(page.locator('[data-test="oee-line-save"]')).toBeHidden()
})

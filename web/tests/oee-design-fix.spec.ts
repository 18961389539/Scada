import { test, expect, type APIRequestContext } from './fixtures'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  const body = await r.json()
  return { headers: { Authorization: `Bearer ${body.token}` } }
}

// 设计修正回归：OEE 主数据的删除此前一点就删且失败无提示，现在必须先确认。
test('oee design fix: destructive deletes require confirmation', async ({ page, request }) => {
  test.setTimeout(90000)
  const { headers: h } = await login(request)
  const suffix = `${Date.now()}`.slice(-6)
  const created = await request.post('/api/oee/lines', {
    headers: h,
    data: {
      name: `UX产线-${suffix}`, areaId: 1, description: null, stateTagId: null, stateMapJson: '{}',
      goodCountTagId: null, rejectCountTagId: null, totalCountTagId: null, productId: null,
      idealCycleSec: 1, ratedUnitsPerHour: null, targetUnitsPerHour: null, enabled: true
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/config/oee')

  // 产线列表（data-test 挂在 tab-pane 内容区上，不能点；必须点 tab 头）
  await page.getByRole('tab', { name: '产线', exact: true }).click()

  const row = page.getByRole('row').filter({ hasText: `UX产线-${suffix}` })
  await expect(row).toBeVisible({ timeout: 15000 })

  // 删除必须先弹确认框；取消后记录仍在
  await row.getByRole('button', { name: '删除', exact: true }).click()
  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  await expect(box).toContainText('不可恢复')
  await box.getByRole('button', { name: '取消' }).click()
  await expect(row).toBeVisible()
})

// 设计修正回归：OEE 表单此前 7 个表单零校验、保存无 try/catch，
// 空名称或重名点保存表现为「按钮点了没反应」，对话框既不关也没提示。
test('oee design fix: form validation and server errors are visible', async ({ page, request }) => {
  test.setTimeout(90000)
  const { headers: h } = await login(request)
  const suffix = `${Date.now()}`.slice(-6)
  const name = `UX-VAL-${suffix}`
  const created = await request.post('/api/oee/lines', {
    headers: h,
    data: {
      name, areaId: 1, description: null, stateTagId: null, stateMapJson: '{}',
      goodCountTagId: null, rejectCountTagId: null, totalCountTagId: null, productId: null,
      idealCycleSec: 1, ratedUnitsPerHour: null, targetUnitsPerHour: null, enabled: true
    }
  })
  expect(created.ok(), await created.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/config/oee')
  await page.getByRole('tab', { name: '产线', exact: true }).click()
  await page.locator('[data-test="oee-add-line"]').click()

  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog).toBeVisible()

  // 名称留空：前端校验拦截，对话框保留
  await dialog.locator('[data-test="oee-line-save"]').click()
  await expect(dialog).toContainText('名称不能为空')

  // 重名：服务端 400 文案落到名称字段，对话框仍打开
  await dialog.locator('[data-test="oee-line-name"]').fill(name)
  await dialog.locator('[data-test="oee-line-save"]').click()
  await expect(dialog).toContainText('产线名称已存在', { timeout: 15000 })
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: '取消' }).click()

  // 产品对话框补了区域归属，避免仅有区域级配置权限的用户被服务端拒绝
  await page.getByRole('tab', { name: '产品', exact: true }).click()
  await page.locator('[data-test="oee-add-product"]').click()
  await expect(page.locator('.el-dialog:visible').locator('[data-test="oee-product-area"]')).toBeVisible()
})

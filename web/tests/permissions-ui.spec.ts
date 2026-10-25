import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const r = await request.post('/api/auth/login', { data: { userName, password } })
  expect(r.ok(), await r.text()).toBeTruthy()
  const body = await r.json()
  return { ...body, headers: { Authorization: `Bearer ${body.token}` } }
}

// 权限口径回归：服务端按「全局 / 区域」两种口径判权，界面必须同口径。
// 之前测点 Excel 导入（需全局 Configure）与 OEE 阈值（需全局 Configure）对区域配置员可见，
// 用户填完表单才被 403；OEE 产线与声光面板则完全没有行级判权。
test('permission-gated controls match the server: global-only hidden, row actions follow area', async ({ page, request }) => {
  test.setTimeout(120000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: admin, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  const areaA = await post('/api/areas', { name: `PERM-A-${suffix}` })
  const areaB = await post('/api/areas', { name: `PERM-B-${suffix}` })
  const lineBody = (name: string, areaId: number) => ({
    name, areaId, description: null, stateTagId: null, stateMapJson: '{}',
    goodCountTagId: null, rejectCountTagId: null, totalCountTagId: null, productId: null,
    idealCycleSec: 1, ratedUnitsPerHour: null, targetUnitsPerHour: null, enabled: true
  })
  await post('/api/oee/lines', lineBody(`PERM-LA-${suffix}`, areaA.id))
  await post('/api/oee/lines', lineBody(`PERM-LB-${suffix}`, areaB.id))
  // 全局产品（areaId 为空）对区域用户可见（只读），但编辑需要全局 Configure —— 行级判权的关键场景
  const productBody = (name: string, areaId: number | null) => ({ name, code: null, areaId, idealCycleSec: 1, ratedUnitsPerHour: null, description: null })
  await post('/api/oee/products', productBody(`PERM-PG-${suffix}`, null))
  await post('/api/oee/products', productBody(`PERM-PA-${suffix}`, areaA.id))

  const role = await post('/api/roles', { name: `PERM-配置员-${suffix}`, permissions: ['View', 'Configure'] })
  const user = await post('/api/users', { userName: `perm-${suffix}`, password: 'secret123', displayName: 'A区配置员', role: 'None', enabled: true })
  const assigned = await request.put(`/api/users/${user.id}/assignments`, { headers: admin, data: { assignments: [{ roleId: role.id, areaId: areaA.id }] } })
  expect(assigned.ok(), await assigned.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(user.userName)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  // 1) 测点 Excel 导入需要全局 Configure → 按钮不出现；新增只需区域权限，仍然可用
  await page.goto('/config/tags')
  await expect(page.locator('[data-test="tag-create"]')).toBeVisible({ timeout: 15000 })
  await expect(page.locator('[data-test="tag-import"]')).toHaveCount(0)

  // 2) OEE 阈值是全局配置 → 按钮不出现
  await page.goto('/oee')
  await expect(page.locator('[data-test="kpi-oee"]')).toBeVisible({ timeout: 20000 })
  await expect(page.locator('[data-test="oee-thresholds"]')).toHaveCount(0)

  // 3) OEE 行级判权：自己区域的行可编辑；全局记录（areaId 为空）只读，编辑按钮不出现
  await page.locator('[data-test="oee-tab-config"]').click()
  await page.getByRole('tab', { name: '产线', exact: true }).click()
  const rowLineA = page.getByRole('row').filter({ hasText: `PERM-LA-${suffix}` })
  await expect(rowLineA).toBeVisible()
  await expect(rowLineA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)

  await page.getByRole('tab', { name: '产品', exact: true }).click()
  const rowProductA = page.getByRole('row').filter({ hasText: `PERM-PA-${suffix}` })
  const rowProductGlobal = page.getByRole('row').filter({ hasText: `PERM-PG-${suffix}` })
  await expect(rowProductA).toBeVisible()
  await expect(rowProductGlobal).toBeVisible()
  await expect(rowProductA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(rowProductGlobal.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)

  // 4) 声光面板新建时默认落到其可配置区域，而不是「全局」（全局面板需全局权限，必然 403）
  await page.goto('/config/annunciators')
  await page.locator('[data-test="ann-add"]').click()
  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText(`PERM-A-${suffix}`)
  await page.keyboard.press('Escape')

  // 5) 仅 Global:System 可进的页面：导航已隐藏，直接输 URL 也必须挡回总览
  await page.goto('/config/external-api')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/ops')
  await expect(page).toHaveURL(/overview/)
})

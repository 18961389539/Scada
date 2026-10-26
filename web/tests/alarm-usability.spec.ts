import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  const result = await r.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

// 报警页易用性：危险操作二次确认、批量确认（进度 + 部分失败明细）、等级/关键词过滤、历史时间快捷。
test('alarm usability: confirm guards, batch ack, level filter and history presets', async ({ page, request }) => {
  test.setTimeout(120000)
  const auth = await login(request)
  const h = auth.headers
  const suffix = `${Date.now()}`
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: h, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 恒为 50 的计算测点 + 高限 10 的规则：立刻产生一条严重、一条警告报警
  const mk = async (level: string, message: string) => {
    const tag = await post('/api/calculated-tags', { name: `UX-ACK-${level}-${suffix}`, expression: '50', intervalMs: 200 })
    return post('/api/alarm-rules', {
      tagId: tag.id, type: 'High', threshold: 10, level, delaySec: 0, recoverDeadband: 0, enabled: true, message
    })
  }
  const criticalMsg = `UX严重-${suffix}`
  const warningMsg = `UX警告-${suffix}`
  const ruleC = await mk('Critical', criticalMsg)
  const ruleW = await mk('Warning', warningMsg)

  await expect.poll(async () => {
    const list = await (await request.get('/api/alarms?live=true', { headers: h })).json()
    return [ruleC.id, ruleW.id].every((id) => list.some((x: any) => x.ruleId === id && x.state === 'Unack'))
  }, { timeout: 30000 }).toBe(true)

  // 用真实登录流程：注入 localStorage 的 user 不带权限字段，canAck 会为 false
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/alarms')
  const rowC = page.getByRole('row').filter({ hasText: criticalMsg })
  const rowW = page.getByRole('row').filter({ hasText: warningMsg })
  await expect(rowC).toBeVisible()

  // 未选中时批量确认禁用
  const batchBtn = page.locator('[data-test="batch-ack"]')
  await expect(batchBtn).toBeDisabled()

  // 「确认可见」是一键批量且不可撤销：先弹确认框，取消后状态不变
  await page.locator('[data-test="ack-visible"]').click()
  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  await expect(box).toContainText('确认后不可撤销')
  await box.getByRole('button', { name: '取消' }).click()
  await expect(rowC.locator('[data-test="alarm-state"]')).toHaveText('未确认')

  // 勾选两条：按钮可用且显示数量；确认后两条都变已确认
  await rowC.locator('.el-checkbox').click()
  await rowW.locator('.el-checkbox').click()
  await expect(batchBtn).toBeEnabled()
  await expect(batchBtn).toContainText('(2)')
  await batchBtn.click()
  await expect(box).toContainText('将确认选中的 2 条报警')
  await box.getByRole('button', { name: '批量确认' }).click()
  await expect(rowC.locator('[data-test="alarm-state"]')).toHaveText('已确认')
  await expect(rowW.locator('[data-test="alarm-state"]')).toHaveText('已确认')

  // 等级筛选：选「严重」后警告行被过滤掉
  await page.locator('[data-test="level-filter"]').click()
  await page.getByRole('option', { name: '严重' }).click()
  await expect(rowC).toBeVisible()
  await expect(rowW).toHaveCount(0)

  // 关键词与等级可叠加，仍能看到目标报警
  await page.locator('[data-test="alarm-keyword"]').fill(suffix)
  await expect(rowC).toBeVisible()

  // 历史页：时间快捷范围直接触发查询
  await page.getByRole('tab', { name: /历史/ }).click()
  await page.locator('[data-test="range-preset"]').click()
  await page.getByRole('option', { name: '最近 1 小时' }).click()
  await expect(page.getByRole('row').filter({ hasText: criticalMsg })).toBeVisible()
})

// 数据披露回归：服务端按 limit 裁剪报警结果，超出部分静默丢弃。
// 页面多取 1 条判断是否截断，必须明确告诉用户「这不是全部」。
test('alarm usability: truncated result is disclosed', async ({ page }) => {
  test.setTimeout(90000)
  const fake = (id: number) => ({
    id, ruleId: 1, tagId: 1, level: 'Warning', message: `TRUNC-${id}`, state: 'Active',
    startTs: Date.now() - 60000, endTs: null, ackTs: null, ackUser: null, endReason: null,
    gatewayId: null, shelved: false, suppressed: false, chattering: false, untilTs: null,
    value: 1, suppressReason: null
  })
  const items = Array.from({ length: 1001 }, (_, i) => fake(i + 1))
  await page.route(
    (url) => url.pathname === '/api/alarms' && url.searchParams.get('live') === 'true',
    (route) => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(items) })
  )

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/alarms')

  const notice = page.locator('[data-test="alarms-truncated"]')
  await expect(notice).toBeVisible({ timeout: 20000 })
  await expect(notice).toContainText('仅显示前 1000 条')
})

test('calculated alarm ack follows tag areas when the alarm has no device', async ({ page, request }) => {
  const admin = await login(request)
  const suffix = `${Date.now()}`
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: admin.headers, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }
  const areaA = await post('/api/areas', { name: `ACK-A-${suffix}` })
  const areaB = await post('/api/areas', { name: `ACK-B-${suffix}` })
  const viewRole = await post('/api/roles', { name: `ACK-查看-${suffix}`, permissions: ['View'] })
  const ackRole = await post('/api/roles', { name: `ACK-确认-${suffix}`, permissions: ['AckAlarm'] })
  const user = await post('/api/users', { userName: `ack${suffix}`, password: 'secret123', displayName: '分区确认', role: 'None', enabled: true })
  expect((await request.put(`/api/users/${user.id}/assignments`, { headers: admin.headers, data: { assignments: [{ roleId: viewRole.id, areaId: areaA.id }, { roleId: viewRole.id, areaId: areaB.id }, { roleId: ackRole.id, areaId: areaA.id }] } })).ok()).toBeTruthy()
  const row = (id: number, message: string, tagId: number | null, areas: number[]) => ({
    id, ruleId: tagId ? 1 : null, tagId, deviceId: null, areas, level: 'Warning', message,
    state: 'Unack', startTs: Date.now(), endTs: null, ackTs: null, ackUser: null, value: 1
  })
  await page.route(
    (url) => url.pathname === '/api/alarms' && url.searchParams.get('live') === 'true',
    (route) => route.fulfill({
      status: 200, contentType: 'application/json',
      body: JSON.stringify([row(1, '本区计算', 11, [areaA.id]), row(2, '他区计算', 12, [areaB.id]), row(3, '系统报警', null, [])])
    })
  )
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(`ack${suffix}`)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/alarms')
  const line = (text: string) => page.getByRole('row').filter({ hasText: text })
  await expect(line('本区计算').locator('[data-test="ack"]')).toHaveCount(1)
  await expect(line('他区计算').locator('[data-test="ack"]')).toHaveCount(0)
  await expect(line('系统报警').locator('[data-test="ack"]')).toHaveCount(1)
})

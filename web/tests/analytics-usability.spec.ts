import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext) {
  const r = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(r.ok(), await r.text()).toBeTruthy()
  const body = await r.json()
  return { headers: { Authorization: `Bearer ${body.token}` } }
}

// 报警分析易用性：无数据时不再整页空白、EEMUA 评级有阈值说明。
test('analytics usability: empty state and EEMUA rating hint', async ({ page, request }) => {
  test.setTimeout(120000)
  const { headers: h } = await login(request)
  const suffix = `${Date.now()}`
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: h, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  // 恒为 50 的计算测点 + 高限 10 → 产生一条「严重」报警，保证分析有数据
  const tag = await post('/api/calculated-tags', { name: `UX-AA-${suffix}`, expression: '50', intervalMs: 200 })
  const rule = await post('/api/alarm-rules', {
    tagId: tag.id, type: 'High', threshold: 10, level: 'Critical', delaySec: 0, recoverDeadband: 0, enabled: true, message: `UX分析-${suffix}`
  })
  await expect.poll(async () => {
    const list = await (await request.get('/api/alarms?live=true', { headers: h })).json()
    return list.some((x: any) => x.ruleId === rule.id && x.state === 'Unack')
  }, { timeout: 30000 }).toBe(true)

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/alarm-analytics')

  // 有数据：KPI 卡片渲染，评级卡片的悬停说明给出 EEMUA 阈值依据
  const rateCard = page.locator('[data-test="kpi-rate10"]')
  await expect(rateCard).toBeVisible({ timeout: 20000 })
  await expect(page.locator('[data-test="aa-empty"]')).toHaveCount(0)
  await rateCard.hover()
  await expect(page.getByRole('tooltip').filter({ hasText: 'EEMUA 191' })).toBeVisible({ timeout: 10000 })
  await page.mouse.move(800, 950)

  // 只保留「提示」等级：严重报警被筛掉 → 应给出「没有报警数据」而不是空白
  // （这条同时回归后端 Bug：AlarmLevel.Info = 0 曾被 ParseIds 过滤掉，导致筛选静默失效）
  await page.locator('[data-test="aa-levels"]').click()
  await page.getByRole('option', { name: '提示' }).click()
  await page.keyboard.press('Escape')
  await page.locator('[data-test="aa-query"]').click()
  const empty = page.locator('[data-test="aa-empty"]')
  await expect(empty).toBeVisible({ timeout: 20000 })
  await expect(empty).toContainText('没有报警数据')
})

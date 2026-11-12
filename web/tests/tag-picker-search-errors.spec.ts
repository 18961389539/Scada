import { test, expect, type APIRequestContext } from './fixtures'

// 测点选择器：服务端搜索失败必须显示「失败 + 重试」，不能伪装成「没有匹配」，
// 否则现场会以为目录里真的没有这个测点，转而新建一个重复点位。
// 组件只在目录超过 400 条时走服务端搜索，这里用一批合成测点触发远程模式。

async function auth(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok(), await response.text()).toBeTruthy()
  return response.json()
}

test('tag picker reports a failed remote search instead of "no match" and retries in place', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await auth(request)
  const name = `测点选择-${Date.now()}`
  const created = await request.post('/api/calc-jobs', {
    headers: { Authorization: `Bearer ${session.token}` },
    data: { name, kind: 'Script', areaId: 1, script: '@answer = 42' }
  })
  expect(created.status(), await created.text()).toBe(201)
  const job = await created.json()

  const catalog = Array.from({ length: 420 }, (_, index) => ({
    id: 900000 + index,
    name: `合成测点-${index}`,
    deviceId: null,
    kind: 'Internal',
    address: `mem:${index}`,
    unit: '',
    writable: false
  }))
  let attempts = 0
  await page.route(url => url.pathname === '/api/tags' && !url.searchParams.has('search'), route => route.fulfill({ json: catalog }))
  await page.route(url => url.pathname === '/api/tags' && url.searchParams.has('search'), route => {
    attempts += 1
    return attempts === 1
      ? route.fulfill({ status: 500, json: { message: '测点目录暂时不可用' } })
      : route.fulfill({ json: catalog.slice(0, 2) })
  })

  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, session)

  await page.goto('/calc-jobs')
  await page.locator('.el-table__row', { hasText: name }).getByRole('button', { name: '编辑', exact: true }).click()
  const picker = page.locator('[data-test="calc-job-read-pick"]')
  await expect(picker).toBeVisible()
  await picker.click()
  await picker.getByRole('combobox').fill('合成测点-1')

  const failure = page.locator('[data-test="tag-select-search-error"]')
  await expect(failure).toBeVisible({ timeout: 20000 })
  await expect(failure).toContainText('测点搜索失败')
  await expect(failure).toContainText('测点目录暂时不可用')
  // 失败时不能同时冒出「没有匹配的测点」，两者语义互斥
  await expect(page.locator('[data-test="tag-select-state"]:visible')).not.toContainText('没有匹配的测点')

  await page.locator('[data-test="tag-select-retry"]').click()
  await expect(page.locator('.el-select-dropdown__item:visible')).toHaveCount(2, { timeout: 20000 })
  await expect(failure).toHaveCount(0)
  expect(attempts).toBe(2)

  await page.keyboard.press('Escape')
  expect((await request.delete(`/api/calc-jobs/${job.id}?revision=1`, { headers: { Authorization: `Bearer ${session.token}` } })).status()).toBe(204)
})

import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } })
  expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json()
  return { ...result, headers: { Authorization: `Bearer ${result.token}` as string } }
}

test('external API admin page shows status and lists created key', async ({ page, request }) => {
  test.setTimeout(90000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const name = `e2e-key-${Date.now()}`
  const created = await request.post('/api/external/keys', {
    headers: admin,
    data: { name, scopes: ['ReadTags'], rateLimitPerMinute: 120 }
  })
  expect(created.ok(), await created.text()).toBeTruthy()
  const body = await created.json()
  const secret = body.secret as string
  const id = body.key.id as number

  await page.goto('/login')
  await page.getByPlaceholder(/用户名|Username/i).fill('admin')
  await page.getByPlaceholder(/密码|Password/i).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  await page.goto('/config/external-api')
  await expect(page.getByRole('heading', { name: /对外接口|External API/ })).toBeVisible()
  const statusRow = page.locator('[data-test="ext-status"]')
  await expect(statusRow).toContainText('REST')
  await expect(statusRow).toContainText('OPC UA')
  await expect(page.locator('[data-test="ext-table"]').getByText(name)).toBeVisible()

  const health = await request.get('/api/v1/health', { headers: { 'X-Api-Key': secret } })
  expect(health.ok(), await health.text()).toBeTruthy()

  await request.post(`/api/external/keys/${id}/revoke`, { headers: admin, data: {} })
})

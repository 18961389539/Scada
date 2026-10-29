import { test, expect, type APIRequestContext } from './fixtures'

async function admin(request: APIRequestContext) {
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  return { ...auth, headers: { Authorization: `Bearer ${auth.token}` } }
}

// 重置密码只有一个输入框。回车应保存新密码，同时不能把整页刷掉。
test('Enter in the password dialog resets that user without reloading', async ({ page, request }) => {
  test.setTimeout(60000)
  const auth = await admin(request)
  const name = `pw${Date.now()}`
  const created = await request.post('/api/users', { headers: auth.headers, data: { userName: name, password: 'old-password', displayName: '重置', role: 'Viewer', enabled: true } })
  expect(created.ok(), await created.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  await page.goto('/system')
  await page.getByRole('tab', { name: '用户管理', exact: true }).click()
  await page.getByRole('row').filter({ hasText: name }).getByRole('button', { name: '重置密码', exact: true }).click()

  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog).toBeVisible()
  const input = dialog.locator('input[type="password"]')
  await input.fill('short')
  await input.press('Enter')
  await expect(page.getByText('密码至少 6 个字符')).toBeVisible()
  await expect(dialog).toBeVisible()

  let navigations = 0
  page.on('framenavigated', (frame) => { if (frame === page.mainFrame()) navigations++ })
  await input.fill('Newpass1')
  await input.press('Enter')
  await expect(page.getByText('密码已重置')).toBeVisible()
  await expect(dialog).toBeHidden()
  expect(navigations).toBe(0)
  await expect(page).toHaveURL(/\/system/)
  expect((await request.post('/api/auth/login', { data: { userName: name, password: 'Newpass1' } })).ok()).toBeTruthy()
})

test('operation log results use interface text instead of the stored codes', async ({ page, request }) => {
  test.setTimeout(60000)
  const auth = await admin(request)
  await request.put('/api/auth/me/locale', { headers: auth.headers, data: { locale: 'en-US' } })
  try {
    await page.addInitScript(({ token, user }) => {
      localStorage.setItem('scada_token', token)
      localStorage.setItem('scada_user', JSON.stringify({ ...user, locale: 'en-US' }))
      localStorage.setItem('scada_locale', 'en-US')
    }, auth)
    await page.route('**/api/logs**', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        total: 5, page: 1, pageSize: 50,
        items: [
          { id: 1, userName: 'admin', ts: Date.now(), action: '登录', result: '成功', tagId: null, oldValue: null, newValue: null },
          { id: 2, userName: 'admin', ts: Date.now(), action: '登录', result: '失败：密码错误', tagId: null, oldValue: null, newValue: null },
          { id: 3, userName: 'admin', ts: Date.now(), action: '声光', result: '消音 5 分钟', tagId: null, oldValue: null, newValue: null },
          { id: 4, userName: 'admin', ts: Date.now(), action: '外部', result: '拒绝：限流', tagId: null, oldValue: null, newValue: null },
          { id: 5, userName: 'admin', ts: Date.now(), action: '备注', result: '现场备注', tagId: null, oldValue: null, newValue: null }
        ]
      })
    }))
    await page.goto('/system')
    await page.getByRole('tab', { name: 'Operation log', exact: true }).click()
    const results = page.locator('[data-test="log-result"]')
    await expect(results).toHaveText(['Success', 'Failed: 密码错误', 'Silenced for 5 min', 'Denied: 限流', '现场备注'])
  } finally {
    await request.put('/api/auth/me/locale', { headers: auth.headers, data: { locale: 'zh-CN' } })
  }
})

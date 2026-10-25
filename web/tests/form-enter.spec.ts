import { test, expect } from '@playwright/test'

// el-form 渲染成原生 <form>，Element Plus 不会拦 submit。当表单里只有 1 个会阻断隐式提交的字段时，
// 浏览器按回车会「隐式提交」→ 跳回当前 URL，等于整页刷新：对话框里刚填的内容全丢。
// 这类表单必须写 @submit.prevent（或明确把回车绑成提交）。
test('Enter in a single-input dialog does not reload the page', async ({ page }) => {
  test.setTimeout(60000)
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  await page.goto('/system')
  await page.getByRole('tab', { name: '用户管理', exact: true }).click()
  await page.getByRole('button', { name: '重置密码', exact: true }).first().click()

  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog).toBeVisible()
  const input = dialog.locator('input[type="password"]')
  await input.fill('Abc123456')

  let navigations = 0
  page.on('framenavigated', (frame) => { if (frame === page.mainFrame()) navigations++ })
  await input.press('Enter')
  await page.waitForTimeout(1200)

  expect(navigations, '回车不应触发页面跳转/刷新').toBe(0)
  await expect(dialog).toBeVisible()
  await expect(input).toHaveValue('Abc123456')
})

import { test, expect, type Page } from './fixtures'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

test('a failed download-progress poll stays on the recipe page', async ({ page }) => {
  await signIn(page)
  let seen = 0
  await page.route((url) => url.pathname === '/api/recipes/downloads', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    seen += 1
    if (seen === 1) {
      return route.fulfill({
        status: 200, contentType: 'application/json',
        body: JSON.stringify([{
          id: 7, recipeId: 1, recipeName: 'POLL-RCP', version: 1, templateId: 1, areaId: 1,
          status: 'Running', statusLabel: '下发中', requestedBy: 'admin', approvedBy: null, approvalId: null,
          createdTs: Date.now(), startedTs: Date.now(), endedTs: null, written: 0, message: null, results: []
        }])
      })
    }
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '下发进度不可用' }) })
  })
  await page.goto('/recipes')
  await expect(page.locator('[data-test="download-status-7"]')).toContainText('下发中')
  await expect(page.locator('[data-test="recipe-downloads-error"]')).toContainText('下发进度不可用', { timeout: 8000 })
  await expect(page.locator('[data-test="recipes-load-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="download-status-7"]')).toContainText('下发中')
})

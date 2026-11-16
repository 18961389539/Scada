import { test, expect, loginApi, applySession } from './fixtures'

test('protocol directory failure blocks configuration until explicit retry succeeds', async ({ page, request }) => {
  await applySession(page, await loginApi(request))
  let failed = true
  await page.route('**/api/protocols', async route => {
    if (failed) await route.fulfill({ status: 503, contentType: 'application/json', body: '{"message":"temporarily unavailable"}' })
    else await route.continue()
  })
  await page.goto('/config/devices')
  await expect(page.locator('[data-test="protocol-catalog-error"]')).toBeVisible()
  await expect(page.locator('[data-test="device-dialog"]')).toHaveCount(0)
  await expect(page.locator('main').getByRole('button')).toHaveCount(1)
  await page.goto('/overview')
  await expect(page.locator('[data-test="protocol-catalog-error"]')).toBeVisible()
  await expect(page.locator('.page').first()).toBeVisible()
  await page.goto('/config/devices')
  await expect(page.locator('.page')).toHaveCount(0)
  failed = false
  await page.locator('[data-test="protocol-catalog-retry"]').click()
  await expect(page.locator('[data-test="protocol-catalog-error"]')).toHaveCount(0)
  await expect(page.locator('[data-test="device-create"]')).toBeVisible()
})

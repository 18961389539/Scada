import { expect, type Page } from '@playwright/test'

export async function visibleNavigationIds(page: Page): Promise<string[]> {
  const mobileMenu = page.locator('[data-test="mobile-menu"]')
  const mobile = await mobileMenu.isVisible()
  if (mobile) await mobileMenu.click()
  const menu = page.locator('.nav-wrap').last()
  await expect(menu).toBeVisible()
  const config = menu.locator('[data-test="nav-config-group"]')
  if (await config.count()) {
    if (!await config.evaluate(element => element.classList.contains('is-opened'))) {
      await config.locator('.el-sub-menu__title').click()
    }
    await expect(config.getByRole('menu').first()).toBeVisible()
  }
  const items = menu.locator('[data-test^="nav-item-"]')
  await expect(items).not.toHaveCount(0)
  const ids = await items.evaluateAll(elements => elements.map(element => element.getAttribute('data-test')!.replace('nav-item-', 'command-palette-item-')).sort())
  if (mobile) {
    await page.keyboard.press('Escape')
    await expect(page.locator('[data-test="mobile-drawer"]')).toBeHidden()
  }
  return ids
}
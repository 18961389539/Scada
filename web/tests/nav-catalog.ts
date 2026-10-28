import { expect, type Page } from '@playwright/test'

export async function openNavigationGroup(page: Page, id: string): Promise<void> {
  const group = page.locator(`.nav-wrap [data-test="nav-${id}-group"]`).last()
  if (!await group.evaluate(element => element.classList.contains('is-opened'))) {
    await group.locator('.el-sub-menu__title').click()
  }
}

export async function visibleNavigationIds(page: Page): Promise<string[]> {
  const mobileMenu = page.locator('[data-test="mobile-menu"]')
  const mobile = await mobileMenu.isVisible()
  if (mobile) await mobileMenu.click()
  const menu = page.locator('.nav-wrap').last()
  await expect(menu).toBeVisible()
  const groups = menu.locator('.el-sub-menu')
  const ids = new Set<string>()
  for (let index = 0; index < await groups.count(); index++) {
    const group = groups.nth(index)
    if (!await group.evaluate(element => element.classList.contains('is-opened'))) {
      await group.locator('.el-sub-menu__title').click()
    }
    await expect(group.getByRole('menu').first()).toBeVisible()
    const items = group.locator('[data-test^="nav-item-"]')
    await expect(items).not.toHaveCount(0)
    for (const id of await items.evaluateAll(elements => elements.map(element => element.getAttribute('data-test')!.replace('nav-item-', 'command-palette-item-')))) ids.add(id)
  }
  if (mobile) {
    await page.keyboard.press('Escape')
    await expect(page.locator('[data-test="mobile-drawer"]')).toBeHidden()
  }
  return [...ids].sort()
}
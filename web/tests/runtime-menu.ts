import type { Locator, Page } from '@playwright/test'

function pageOf(root: Page | Locator): Page {
  return 'goto' in root ? root : root.page()
}

export async function openRuntimeMenu(root: Page | Locator): Promise<void> {
  const page = pageOf(root)
  const open = page.locator('.el-popover:visible')
  if (await open.locator('[data-test="runtime-zoom"]').count()) return
  await root.locator('[data-test="runtime-more"]').click()
}

export async function refreshRuntime(root: Page | Locator): Promise<void> {
  const page = pageOf(root)
  const button = page.locator('[data-test="runtime-refresh"]')
  if (!(await button.isVisible())) await openRuntimeMenu(root)
  await button.click()
}

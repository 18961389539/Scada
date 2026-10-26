import { test, expect, type Page } from '@playwright/test'

async function signIn(page: Page): Promise<void> {
  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill('admin')
  await page.getByPlaceholder(/密码/).fill('admin123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)
}

async function failGet(page: Page, apiPath: string, message: string): Promise<void> {
  await page.route((url) => url.pathname === apiPath, (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message }) })
  })
}

async function expectLoadError(page: Page, path: string, apiPath: string, message: string, testId: string, absent: string[]): Promise<void> {
  await failGet(page, apiPath, message)
  await page.goto(path)
  await expect(page.locator(`[data-test="${testId}"]`)).toContainText(message)
  for (const text of absent) await expect(page.getByText(text, { exact: true })).toHaveCount(0)
}

test('layout list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/layouts', '/api/hmi/layouts', '运行布局不可用', 'layouts-load-error', ['尚无运行布局', '尚未登记工作站'])
})

test('notification routes keep a load failure on the page', async ({ page }) => {
  await signIn(page)
  await failGet(page, '/api/notification-routes', '通知路由不可用')
  await failGet(page, '/api/notification-outbox/failed', '投递失败列表不可用')
  await page.goto('/config/notifications')
  await expect(page.locator('[data-test="notifications-load-error"]')).toContainText('通知路由不可用')
  await expect(page.getByText('暂无路由', { exact: true })).toHaveCount(0)
  await expect(page.getByText('请先在「通知通道」创建通道，再新增路由')).toHaveCount(0)
  await page.getByRole('tab', { name: '投递失败' }).click()
  await expect(page.locator('[data-test="notifications-failed-error"]')).toContainText('投递失败列表不可用')
  await expect(page.getByText('没有未知悉的投递失败', { exact: true })).toHaveCount(0)
})

test('external api keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/external-api', '/api/external/keys', '接口密钥不可用', 'external-load-error', ['暂无数据'])
})

test('annunciator list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/annunciators', '/api/annunciators', '声光面板不可用', 'annunciator-load-error', ['暂无声光面板'])
})

test('hmi library keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await failGet(page, '/api/hmi/library', '图库不可用')
  await page.goto('/config/hmi-library')
  await expect(page.locator('[data-test="library-load-error"]')).toContainText('图库不可用')
  await page.getByRole('tab', { name: /用户图库/ }).click()
  await expect(page.getByText('尚无用户符号或模板', { exact: true })).toHaveCount(0)
})

test('object templates keep a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/object-templates', '/api/object-templates', '对象模板不可用', 'object-load-error', ['创建模板后，可为同类设备批量生成配置'])
})

test('oee keeps a query and line-list failure on the page', async ({ page }) => {
  await signIn(page)
  await failGet(page, '/api/oee/analytics', '产量分析不可用')
  await failGet(page, '/api/oee/lines', '产线列表不可用')
  await page.goto('/oee')
  await expect(page.locator('[data-test="oee-query-error"]')).toContainText('产量分析不可用')
  await expect(page.locator('[data-test="oee-meta-error"]')).toContainText('产线列表不可用')
  await expect(page.locator('[data-test="oee-empty"]')).toHaveCount(0)
})

test('calculation list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/calculations', '/api/calculated-tags', '计算测点不可用', 'calc-read-error', ['尚无计算测点'])
})

test('gateway list keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/gateways', '/api/gateways', '网关列表不可用', 'gateway-read-error', ['暂无匹配网关'])
})

test('project versions keep a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/config/project', '/api/project/versions', '工程快照不可用', 'project-read-error', ['尚无快照'])
})

test('report center keeps a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/report-center', '/api/report-templates', '报表模板不可用', 'report-center-error', ['尚无报表模板', '尚无归档报表'])
})

test('calc jobs keep a load failure on the page', async ({ page }) => {
  await signIn(page)
  await expectLoadError(page, '/calc-jobs', '/api/calc-jobs', '计算任务不可用', 'calc-job-read-error', ['尚无计算任务'])
})

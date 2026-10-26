import { test, expect, type APIRequestContext, type Page } from '@playwright/test'
import type { Recipe, RecipeCheck, RecipeDownload, RecipeTemplate } from '../src/api/recipes'

const templates: RecipeTemplate[] = [
  { id: 901, name: 'Pump A', areaId: 1, description: '一车间泵站', dualApproval: false, revision: 1, updatedTs: 1, updatedBy: 'admin', parameters: [{ key: 'pressure', name: '出口压力', tagId: 921, unit: 'MPa', min: 0, max: 50, defaultValue: 10 }] },
  { id: 902, name: 'Boiler B', areaId: 2, description: '二车间锅炉', dualApproval: true, revision: 1, updatedTs: 1, updatedBy: 'admin', parameters: [{ key: 'temp', name: '温度', tagId: 922, unit: '℃', min: 0, max: 300, defaultValue: 100 }] }
]
const recipes: Recipe[] = [
  { id: 911, templateId: 901, templateName: 'Pump A', areaId: 1, name: 'Batch A', description: '夏季生产', version: 2, updatedTs: 2, updatedBy: 'admin', values: [{ key: 'pressure', value: 12 }], note: '降低压力', source: 'manual' },
  { id: 912, templateId: 902, templateName: 'Boiler B', areaId: 2, name: 'Batch B', description: '冬季生产', version: 3, updatedTs: 3, updatedBy: 'operator', values: [{ key: 'temp', value: 180 }], note: '高温', source: 'upload' },
  { id: 913, templateId: 901, templateName: 'Pump A', areaId: 1, name: 'Batch C', description: '试生产', version: 1, updatedTs: 1, updatedBy: 'admin', values: [{ key: 'pressure', value: 20 }], note: '验证', source: 'manual' }
]
const labels: Record<string, string> = { Completed: '完成', PartiallyApplied: '部分下发', Pending: '待复核', Running: '下发中', Rejected: '已拒绝' }
function record(recipe: Recipe, id: number, status: string): RecipeDownload {
  return { id, recipeId: recipe.id, recipeName: recipe.name, version: recipe.version, templateId: recipe.templateId, areaId: recipe.areaId, status, statusLabel: labels[status] ?? status,
    requestedBy: 'operator-a', approvedBy: null, approvalId: null, createdTs: Date.now(), startedTs: null, endedTs: null, written: 0, message: null, results: [] }
}
function check(recipe: Recipe): RecipeCheck {
  const template = templates.find(t => t.id === recipe.templateId)!
  return { recipeId: recipe.id, version: recipe.version, dualApproval: template.dualApproval, ok: true, refusals: [], parameters: template.parameters.map(p => ({
    key: p.key, name: p.name, tagId: p.tagId, tagName: p.name, unit: p.unit, target: recipe.values.find(v => v.key === p.key)!.value, current: 0, refusals: [], dualApproval: template.dualApproval
  })) }
}
function gate() {
  let resolve!: () => void
  return { promise: new Promise<void>(r => { resolve = r }), release: () => resolve() }
}
async function prepare(page: Page, request: APIRequestContext) {
  const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(login.ok()).toBeTruthy()
  const auth = await login.json()
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, auth)
  const fixture = { recipes: structuredClone(recipes), records: [record(recipes[0], 1001, 'Completed'), record(recipes[0], 1002, 'PartiallyApplied')] }
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }, { id: 2, name: '二车间' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 931, name: '泵站', areaId: 1 }, { id: 932, name: '锅炉', areaId: 2 }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{ id: 921, name: '出口压力', deviceId: 931, writable: true }, { id: 922, name: '温度', deviceId: 932, writable: true }] }))
  await page.route(url => url.pathname === '/api/recipes/templates', route => route.fulfill({ json: templates }))
  await page.route(url => url.pathname === '/api/recipes', route => route.fulfill({ json: fixture.recipes }))
  await page.route(url => url.pathname === '/api/recipes/downloads', route => route.fulfill({ json: fixture.records }))
  await page.route(/\/api\/recipes\/\d+\/check$/, route => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-2))
    return route.fulfill({ json: check(fixture.recipes.find(r => r.id === id)!) })
  })
  await page.goto('/recipes')
  await expect(page.locator('[data-test="recipes"] .el-table__row')).toHaveCount(3)
  return fixture
}
async function choose(page: Page, selector: string, label: string) {
  await page.locator(selector).click()
  await page.getByRole('option', { name: label, exact: true }).click()
}
const dialog = (page: Page) => page.locator('[data-test="recipe-download-dialog"]')
const confirm = (page: Page) => page.locator('[data-test="recipe-download-confirm"]')

test('recipe and template search combine names, parameters, areas and template filters', async ({ page, request }) => {
  await prepare(page, request)
  const search = page.getByRole('textbox', { name: '搜索配方名称、说明或参数', exact: true })
  await search.fill('PUMP 夏季')
  await expect(page.locator('[data-test="recipe-result-count"]')).toHaveText('显示 1 / 3 条')
  await expect(page.locator('[data-test="recipes"]')).toContainText('Batch A')
  await search.fill('压力=20'); await expect(page.locator('[data-test="recipes"] .el-table__row')).toHaveCount(1)
  await expect(page.locator('[data-test="recipes"]')).toContainText('Batch C')
  await search.fill('batch')
  await choose(page, '[data-test="recipe-template-filter"]', 'Boiler B')
  await expect(page.locator('[data-test="recipes"] .el-table__row')).toHaveCount(1)
  await search.fill('missing')
  await expect(page.locator('[data-test="recipes"]')).toContainText('没有符合筛选条件的记录')
  await page.locator('[data-test="recipe-clear-filters"]').click()
  await expect(page.locator('[data-test="recipes"] .el-table__row')).toHaveCount(3)
  await page.getByRole('tab', { name: '模板', exact: true }).click()
  await page.getByRole('textbox', { name: '搜索模板名称、区域或测点', exact: true }).fill('二车间 温度')
  await expect(page.locator('[data-test="recipe-template-count"]')).toHaveText('显示 1 / 2 条')
  await expect(page.locator('[data-test="recipe-templates"]')).toContainText('Boiler B')
})

test('download filters distinguish attention states and retain records for deleted recipes', async ({ page, request }) => {
  const fixture = await prepare(page, request)
  fixture.records.push({ ...record(recipes[1], 1003, 'Completed'), recipeId: 999, recipeName: '已删除配方' })
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await choose(page, '[data-test="download-state-filter"]', '失败 / 拒绝 / 中止')
  await expect(page.locator('[data-test="recipe-downloads"] .el-table__row')).toHaveCount(1)
  await expect(page.locator('[data-test="recipe-downloads"]')).toContainText('部分下发')
  await page.getByRole('textbox', { name: '搜索下发编号、配方或操作人', exact: true }).fill('missing')
  await expect(page.locator('[data-test="recipe-downloads"]')).toContainText('没有符合筛选条件的记录')
  await page.locator('[data-test="recipe-records-911"]').click()
  await expect(page.locator('[data-test="recipe-downloads"] .el-table__row')).toHaveCount(2)
  await expect(page.locator('[data-test="download-recipe-filter"]')).toContainText('#911 · Batch A')
  await page.locator('[data-test="download-clear-filters"]').click()
  await choose(page, '[data-test="download-recipe-filter"]', '#999 · 已删除配方')
  await expect(page.locator('[data-test="recipe-downloads"] .el-table__row')).toHaveCount(1)
  await expect(page.locator('[data-test="recipe-downloads"]')).toContainText('已删除配方')
})

test('a cancelled pre-check cannot reopen its dialog and repeated clicks issue one check', async ({ page, request }) => {
  await prepare(page, request)
  const pending = gate(); let calls = 0
  await page.route('**/api/recipes/911/check', async route => { calls++; await pending.promise; return route.fulfill({ json: check(recipes[0]) }) })
  const started = page.waitForRequest(r => r.url().endsWith('/recipes/911/check'))
  await page.locator('[data-test="recipe-download-911"]').click(); await started
  try {
    await expect(page.locator('[data-test="recipe-checking"]')).toContainText('正在预检查')
    await expect(dialog(page)).toContainText('Batch A v2')
    await expect(confirm(page)).toBeDisabled()
    await page.locator('[data-test="recipe-download-911"]').dispatchEvent('click')
    expect(calls).toBe(1)
    await dialog(page).getByRole('button', { name: '取消', exact: true }).click()
    const done = page.waitForResponse(r => r.url().endsWith('/recipes/911/check'))
    pending.release(); await (await done).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(dialog(page)).not.toBeVisible()
    await expect(page.locator('[data-test="recipes-action-error"]')).toHaveCount(0)
  } finally { pending.release() }
})

for (const outcome of ['success', 'failure'] as const) {
  test(`a late ${outcome} from another recipe cannot replace the current pre-check`, async ({ page, request }) => {
    const fixture = await prepare(page, request), pending = gate(), writes: { id: number; version: number }[] = []
    await page.route('**/api/recipes/911/check', async route => {
      await pending.promise
      return outcome === 'success' ? route.fulfill({ json: check(recipes[0]) }) : route.fulfill({ status: 500, json: { message: '过期检查失败' } })
    })
    await page.route('**/api/recipes/912/download', route => {
      writes.push({ id: 912, version: route.request().postDataJSON().version })
      const response = record(recipes[1], 1200, 'Completed'); fixture.records.unshift(response)
      return route.fulfill({ json: response })
    })
    const started = page.waitForRequest(r => r.url().endsWith('/recipes/911/check'))
    await page.locator('[data-test="recipe-download-911"]').click(); await started
    try {
      await page.locator('[data-test="recipe-download-912"]').dispatchEvent('click')
      await expect(confirm(page)).toBeEnabled()
      await expect(dialog(page)).toContainText('Batch B v3')
      const done = page.waitForResponse(r => r.url().endsWith('/recipes/911/check'))
      pending.release(); await (await done).finished()
      await page.evaluate(() => new Promise(requestAnimationFrame))
      await expect(dialog(page)).toContainText('Batch B v3')
      await expect(dialog(page)).not.toContainText('Batch A')
      await expect(page.locator('[data-test="recipe-download-error"]')).toHaveCount(0)
      await confirm(page).click()
      await expect(page.locator('[data-test="recipe-latest-download"]')).toContainText('#1200 Batch B v3')
      expect(writes).toEqual([{ id: 912, version: 3 }])
    } finally { pending.release() }
  })
}

test('failed and mismatched pre-checks require a fresh matching result before confirmation', async ({ page, request }) => {
  await prepare(page, request)
  let calls = 0, writes = 0
  await page.route('**/api/recipes/911/check', route => {
    calls++
    if (calls === 1) return route.fulfill({ status: 500, json: { message: '预检查暂时不可用' } })
    if (calls === 2) return route.fulfill({ json: { ...check(recipes[0]), version: 99 } })
    return route.fulfill({ json: check(recipes[0]) })
  })
  page.on('request', r => { if (r.url().endsWith('/recipes/911/download')) writes++ })
  await page.locator('[data-test="recipe-download-911"]').click()
  await expect(page.locator('[data-test="recipe-download-error"]')).toContainText('预检查暂时不可用')
  await expect(confirm(page)).toBeDisabled()
  await page.locator('[data-test="recipe-check-retry"]').click()
  await expect(page.locator('[data-test="recipe-download-error"]')).toContainText('版本不一致')
  await expect(confirm(page)).toBeDisabled()
  await page.locator('[data-test="recipe-check-retry"]').click()
  await expect(confirm(page)).toBeEnabled()
  expect(calls).toBe(3); expect(writes).toBe(0)
})

test('a pending submission freezes its checked version, cannot close, and rejects double confirmation', async ({ page, request }) => {
  const fixture = await prepare(page, request), pending = gate(), writes: number[] = []
  await page.route('**/api/recipes/911/download', async route => {
    writes.push(route.request().postDataJSON().version)
    await pending.promise
    const response = record(recipes[0], 1201, 'Completed'); fixture.records.unshift(response)
    return route.fulfill({ json: response })
  })
  await page.locator('[data-test="recipe-download-911"]').click()
  await expect(confirm(page)).toBeEnabled()
  const started = page.waitForRequest(r => r.url().endsWith('/recipes/911/download'))
  await confirm(page).click(); await started
  try {
    await expect(confirm(page)).toBeDisabled()
    await expect(dialog(page).getByRole('button', { name: '取消', exact: true })).toBeDisabled()
    await page.keyboard.press('Escape'); await expect(dialog(page)).toBeVisible()
    await confirm(page).dispatchEvent('click'); await confirm(page).dispatchEvent('click')
    fixture.recipes[0].version = 9; fixture.recipes[0].name = 'Changed recipe'
    await page.getByRole('button', { name: '刷新', exact: true }).dispatchEvent('click')
    await expect(page.locator('[data-test="recipes"]')).toContainText('Changed recipe')
    await expect(dialog(page)).toContainText('Batch A v2')
    expect(writes).toEqual([2])
    pending.release()
    await expect(dialog(page)).not.toBeVisible()
    await expect(page.locator('[data-test="recipe-latest-download"]')).toContainText('#1201 Batch A v2')
    await expect(page.locator('[data-test="download-recipe-filter"]')).toContainText('#911')
    await page.screenshot({ path: test.info().outputPath('recipe-submitted.png'), animations: 'disabled', fullPage: true })
  } finally { pending.release() }
})

for (const outcome of ['http-error', 'rejected'] as const) {
  test(`a ${outcome} submission never retries a write automatically and requires another pre-check`, async ({ page, request }) => {
    await prepare(page, request)
    let writes = 0, checks = 0
    await page.route('**/api/recipes/911/check', route => { checks++; return route.fulfill({ json: check(recipes[0]) }) })
    await page.route('**/api/recipes/911/download', route => {
      writes++
      return outcome === 'http-error' ? route.fulfill({ status: 500, json: { message: '提交暂时不可用' } }) : route.fulfill({ json: { ...record(recipes[0], 1202, 'Rejected'), message: '现场许可已变化' } })
    })
    await page.locator('[data-test="recipe-download-911"]').click(); await expect(confirm(page)).toBeEnabled()
    await confirm(page).click()
    await expect(page.locator('[data-test="recipe-download-error"]')).toContainText(outcome === 'http-error' ? '提交暂时不可用' : '现场许可已变化')
    await expect(confirm(page)).toBeDisabled()
    await confirm(page).dispatchEvent('click')
    expect(writes).toBe(1)
    await page.locator('[data-test="recipe-check-retry"]').click()
    await expect(confirm(page)).toBeEnabled()
    expect(checks).toBe(2); expect(writes).toBe(1)
  })
}

test('pending approvals keep polling through errors and progress while filters hide them', async ({ page, request }) => {
  const fixture = await prepare(page, request)
  const active = record(recipes[1], 1203, 'Pending')
  let reads = 0
  await page.route(url => url.pathname === '/api/recipes/downloads', route => {
    reads++
    if (reads === 2) return route.fulfill({ status: 500, json: { message: '进度暂时不可用' } })
    const status = reads === 1 ? 'Pending' : reads === 3 ? 'Running' : 'Completed'
    return route.fulfill({ json: [record(recipes[1], active.id, status), ...fixture.records] })
  })
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await choose(page, '[data-test="download-state-filter"]', '已完成')
  await expect(page.locator('[data-test="recipe-downloads-error"]')).toContainText('进度暂时不可用')
  await expect(page.locator('[data-test="download-result-count"]')).toContainText('1 条进行中')
  await expect(page.locator('[data-test="download-status-1203"]')).toHaveCount(0)
  await expect(page.locator('[data-test="download-result-count"]')).toContainText('0 条进行中', { timeout: 8000 })
  await expect(page.locator('[data-test="download-status-1203"]')).toHaveText('完成')
  await expect(page.locator('[data-test="recipe-downloads-error"]')).toHaveCount(0)
})

test('a late progress poll cannot overwrite a newer manual refresh', async ({ page, request }) => {
  const fixture = await prepare(page, request), pending = gate()
  let reads = 0
  await page.route(url => url.pathname === '/api/recipes/downloads', async route => {
    const current = ++reads
    if (current === 2) await pending.promise
    return route.fulfill({ json: [record(recipes[1], 1204, current <= 2 ? 'Running' : 'Completed'), ...fixture.records] })
  })
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect.poll(() => reads).toBe(2)
  try {
    await page.getByRole('button', { name: '刷新', exact: true }).click()
    await expect(page.locator('[data-test="download-status-1204"]')).toHaveText('完成')
    const done = page.waitForResponse(r => new URL(r.url()).pathname === '/api/recipes/downloads')
    pending.release(); await (await done).finished()
    await page.evaluate(() => new Promise(requestAnimationFrame))
    await expect(page.locator('[data-test="download-status-1204"]')).toHaveText('完成')
    await expect(page.locator('[data-test="recipe-downloads-error"]')).toHaveCount(0)
  } finally { pending.release() }
})

test('a progress load failure does not prevent loading or searching recipes', async ({ page, request }) => {
  await prepare(page, request)
  await page.route(url => url.pathname === '/api/recipes/downloads', route => route.fulfill({ status: 500, json: { message: '记录暂时不可用' } }))
  await page.getByRole('button', { name: '刷新', exact: true }).click()
  await expect(page.locator('[data-test="recipe-downloads-error"]')).toContainText('记录暂时不可用')
  await expect(page.locator('[data-test="recipes-load-error"]')).toHaveCount(0)
  await page.getByRole('textbox', { name: '搜索配方名称、说明或参数', exact: true }).fill('冬季')
  await expect(page.locator('[data-test="recipes"] .el-table__row')).toHaveCount(1)
  await expect(page.locator('[data-test="recipes"]')).toContainText('Batch B')
  await expect(page.locator('[data-test="recipe-downloads"] .el-table__row')).toHaveCount(2)
})

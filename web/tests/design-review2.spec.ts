import { test, expect, type APIRequestContext, type Page } from './fixtures'

// 第二十三轮：上一轮后续项（见 docs/quality/usability-audit.md 第 24 节）

async function login(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const body = await response.json()
  return { token: body.token as string, user: body.user, headers: { Authorization: `Bearer ${body.token}` } }
}

async function sign(page: Page, session: { token: string; user: unknown }): Promise<void> {
  await page.addInitScript((data) => {
    localStorage.setItem('scada_token', data.token)
    localStorage.setItem('scada_user', JSON.stringify(data.user))
  }, session)
}

test('授权激活先对照新旧差异，取消不提交激活', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/license', route => route.fulfill({
    json: {
      status: 'Active', machineId: 'M-1', message: '有效', licenseId: 'OLD', customer: '甲厂',
      expiresAt: '2027-12-31T00:00:00Z', daysRemaining: 400, maxDevices: 50, maxTags: 500,
      features: ['Reports'], trialEndsAt: null, trialDays: 0, blocked: false
    }
  }))
  let previews = 0, activates = 0
  await page.route(url => url.pathname === '/api/license/preview', route => {
    previews++
    return route.fulfill({
      json: {
        licenseId: 'NEW', customer: '乙厂', expiresAt: '2026-12-01T00:00:00Z', daysRemaining: 30,
        maxDevices: 10, maxTags: 80, features: [], devices: 12, tags: 90
      }
    })
  })
  await page.route(url => url.pathname === '/api/license/activate', route => { activates++; return route.fulfill({ json: {} }) })
  await page.goto('/license')
  await page.locator('[data-test="license-code"]').fill('TEST-CODE')
  await page.locator('[data-test="license-activate"]').click()
  const dialog = page.locator('[data-test="license-preview"]')
  await expect(dialog).toContainText('核对授权后再激活')
  await expect(dialog).toContainText('OLD')
  await expect(dialog).toContainText('NEW')
  await expect(dialog).toContainText('超过新授权上限')
  await expect(dialog).toContainText('另一客户')
  await dialog.getByRole('button', { name: '取消' }).click()
  expect(previews).toBe(1)
  expect(activates).toBe(0)
})

test('网关死信列出批次，可查看载荷，丢弃前确认', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const gateway = {
    id: 7, name: '一线网关', enabled: true, currentRevision: 'a', appliedRevision: 'a',
    lastSeenTs: Date.now(), serverTs: Date.now(), deviceCount: 1, enabledDeviceCount: 1,
    pendingBatches: 0, pendingBytes: 0, inboxBatches: 0, inboxBytes: 0,
    deadLetterBatches: 1, deadLetterBytes: 48, adminRevision: '1'
  }
  const letter = {
    gatewayId: 7, streamId: 's1', sequence: 9, deviceId: 3, revision: '1',
    receivedTs: Date.now() - 60000, failedTs: Date.now(), attempts: 3,
    error: '对端拒绝', payloadBytes: 48, payloadJson: null
  }
  await page.route(url => url.pathname === '/api/gateways', route => route.fulfill({ json: [gateway] }))
  await page.route(url => url.pathname === '/api/gateways/7/devices', route => route.fulfill({ json: [{ id: 3, name: '泵' }] }))
  await page.route(url => url.pathname === '/api/gateways/7/dead-letters', route => route.fulfill({ json: [letter] }))
  let discarded = 0
  await page.route(url => url.pathname === '/api/gateways/7/dead-letters/s1/9', route => {
    if (route.request().method() === 'GET') return route.fulfill({ json: { ...letter, payloadJson: '{"v":1}' } })
    discarded++
    return route.fulfill({ status: 204 })
  })
  await page.goto('/config/gateways?gatewayId=7')
  await expect(page.locator('[data-test="gateway-dead-letter-count"]')).toContainText('死信 1 批')
  await expect(page.locator('[data-test="gateway-dead-letters"]')).toContainText('对端拒绝')
  await page.locator('[data-test="gateway-dead-letter-view"]').click()
  await expect(page.locator('[data-test="gateway-dead-letter-payload"]')).toContainText('"v": 1')
  await page.keyboard.press('Escape')
  await page.locator('[data-test="gateway-dead-letter-discard"]').click()
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('无法恢复')
  await box.getByRole('button', { name: '取消' }).click()
  expect(discarded).toBe(0)
})

test('OEE 查询条件写入地址栏，快捷时间立即查询，帕累托其他行显示其他', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const catalogs: Record<string, unknown> = {
    '/api/areas': [{ id: 1, name: '一车间' }],
    '/api/oee/lines': [{ id: 11, name: '一线', areaId: 1 }],
    '/api/oee/products': [], '/api/oee/shifts': []
  }
  await page.route(url => url.pathname in catalogs, route => route.fulfill({ json: catalogs[new URL(route.request().url()).pathname] }))
  const calls: string[] = []
  await page.route(url => url.pathname === '/api/oee/analytics', route => {
    const q = new URL(route.request().url()).searchParams
    calls.push(`${q.get('from')}-${q.get('to')}`)
    const from = Number(q.get('from')), to = Number(q.get('to'))
    return route.fulfill({
      json: {
        kpi: {
          from, to, label: '', oee: 0.8, availability: 0.9, performance: 0.95, quality: 0.94,
          plannedMs: 3600000, runMs: 3000000, downMs: 600000, faultMs: 0, good: 90, reject: 10, scrap: 0, total: 100,
          scrapRate: 0.1, mtbfHours: 1, mttrHours: 0.1, faultCount: 0, productionVsTargetPercent: 90, target: 100,
          performanceOver100: false, performanceClamped: false,
          downtimePareto: [
            { code: 'A', name: '故障', category: 'Availability', isPlanned: false, hours: 0.8, count: 2, percent: 80, cumulativePercent: 80 },
            { code: '', name: '其他', category: '', isPlanned: false, hours: 0.2, count: 3, percent: 20, cumulativePercent: 100, isOther: true }
          ],
          byShift: [], byProduct: [], byDay: []
        },
        trend: [], detailFrom: from, usedRollups: false,
        thresholds: { targetOee: 0.85, targetAvailability: 0.9, targetPerformance: 0.95, targetQuality: 0.99, clampPerformance: true, topN: 10 }
      }
    })
  })
  await page.goto('/oee')
  await expect(page).toHaveURL(/from=\d+/)
  const first = calls.length
  await page.locator('[data-test="oee-quick-168"]').click()
  await expect.poll(() => calls.length).toBeGreaterThan(first)
  await expect(page).toHaveURL(/from=\d+/)
  await page.getByRole('tab', { name: '停机原因', exact: true }).click()
  await expect(page.locator('[data-test="oee-pareto-table"]')).toContainText('其他')
})

test('追溯精确 SN 无结果时写明是完全匹配', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/traceability/jobs', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/traceability/records', route => route.fulfill({ json: { items: [], total: 0 } }))
  await page.goto('/traceability?sn=SN-404')
  await expect(page.locator('[data-test="trace-records"]')).toContainText('没有与「SN-404」完全一致的 SN')
})

test('报表预览汇总行用合计，归档满 200 条时写明还有更早的', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  const run = (id: number) => ({
    id, templateId: 1, templateName: '班报', templateRevision: 1, periodKey: `2026-11-0${(id % 9) + 1}`, version: 1,
    generatedTs: Date.now(), trigger: 'Manual', generatedBy: 'admin', purgedTs: null,
    pdfSha256: 'aaaaaaaaaaaa', signed: true, signatureError: null, approvalStatus: 'none', areaId: 1
  })
  await page.route(url => url.pathname === '/api/report-templates', route => route.fulfill({ json: [{ id: 1, name: '班报', areaId: 1, period: 'Day' }] }))
  await page.route(url => url.pathname === '/api/report-runs', route => route.fulfill({ json: Array.from({ length: 200 }, (_, i) => run(i + 1)) }))
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一区' }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/calc-jobs', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/notification-channels', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/report-runs/1', route => route.fulfill({
    json: { run: run(1), table: { title: '班报', periodKey: 'p', columns: [{ key: 'a', title: '平均', unit: '', decimals: 2 }], rows: [{ label: '08:00', from: 0, to: 0, values: [1.5] }], summary: { label: '合计/汇总', from: 0, to: 0, values: [1.5] } } }
  }))
  await page.goto('/report-center')
  await expect(page.locator('[data-test="report-archive-limit"]')).toContainText('只列出最近 200 份归档')
  await page.locator('[data-test="report-view-1"]').click()
  await expect(page.locator('[data-test="report-preview"]')).toContainText('合计')
  await expect(page.locator('[data-test="report-preview"]')).not.toContainText('designReview2')
})

test('测点统计日报写明每行是小时聚合', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 1, name: '泵', protocol: 'ModbusTcp', enabled: false }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: [{ id: 11, name: '压力', deviceId: 1, kind: 'Physical', area: 'HoldingRegister', dataType: 'Float', archiveMode: 'OnChange', unit: 'MPa', writable: false }]
  }))
  await page.route(url => url.pathname === '/api/reports', route => {
    const q = new URL(route.request().url()).searchParams
    return route.fulfill({ json: { type: q.get('type'), from: Number(q.get('from')), to: Number(q.get('to')), series: [{ tagId: 11, points: [{ ts: Number(q.get('from')), avg: 1, min: 1, max: 1, count: 1 }] }] } })
  })
  await page.goto('/reports')
  await expect(page.locator('[data-test="reports-query"]')).toBeEnabled()
  await page.locator('[data-test="reports-query"]').click()
  await expect(page.locator('[data-test="reports-daily-grain"]')).toContainText('每行是一个小时的聚合')
})

test('图库导入同名键先确认，目录失败时禁止导入', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/hmi/library', route => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ json: [{ id: 1, key: 'pump', name: '泵', category: '符号', kind: 'symbol', svg: '<svg/>', width: 40, height: 40 }] })
  })
  let imported = 0
  await page.route(url => url.pathname === '/api/hmi/library/import', route => { imported++; return route.fulfill({ json: { added: 0, updated: 1 } }) })
  await page.goto('/config/hmi-library')
  await page.locator('[data-test="library-svg-file"]').setInputFiles({
    name: 'pump.svg', mimeType: 'image/svg+xml',
    buffer: Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10"/></svg>')
  })
  const box = page.locator('.el-message-box')
  await expect(box).toContainText('将覆盖已有项')
  await box.getByRole('button', { name: '取消' }).click()
  expect(imported).toBe(0)

  await page.route(url => url.pathname === '/api/hmi/library', route => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, json: { message: '图库不可用' } })
  })
  await page.goto('/reports')
  await page.goto('/config/hmi-library')
  await expect(page.locator('[data-test="library-load-error"]')).toBeVisible()
  await expect(page.locator('[data-test="library-import-svg"]')).toBeDisabled()
})

test('删除画面前写出引用它的工作台', async ({ page, request }) => {
  const session = await login(request)
  await sign(page, session)
  await page.route(url => url.pathname === '/api/screens', route => route.fulfill({
    json: [{ id: 88, name: '泵房', revision: 2, publishedRevision: 2, updatedTs: Date.now(), updatedBy: 'admin', hasDraftChanges: false, layoutCount: 1 }]
  }))
  await page.route(url => url.pathname === '/api/screens/88/usage', route => route.fulfill({
    json: {
      screenId: 88,
      layouts: [{ id: 3, name: '一线工作台', publishedName: '一线工作台', inDraft: true, inPublished: true }],
      workstations: [{ id: 5, name: '操作台A', layoutId: 3 }],
      objectInstances: [{ id: 9, name: '泵对象' }],
      referencedBy: [{ id: 4, name: '总貌' }]
    }
  }))
  let deletes = 0
  await page.route(url => url.pathname === '/api/screens/88', route => {
    if (route.request().method() === 'DELETE') { deletes++; return route.fulfill({ status: 204 }) }
    return route.continue()
  })
  await page.goto('/screens')
  await page.locator('[data-test="screen-more-88"]').click()
  await page.getByRole('menuitem', { name: '删除', exact: true }).click()
  const confirm = page.getByRole('dialog', { name: '删除画面' })
  await expect(confirm).toContainText('暂时不能删除')
  await expect(confirm).toContainText('一线工作台')
  await expect(confirm).toContainText('操作台A')
  await expect(confirm).toContainText('泵对象')
  await expect(confirm).toContainText('总貌')
  await expect(confirm.getByRole('button', { name: '取消' })).toHaveCount(0)
  await confirm.getByRole('button', { name: '确定' }).click()
  expect(deletes).toBe(0)
})

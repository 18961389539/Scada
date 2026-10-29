import { test, expect, type APIRequestContext, type Page } from './fixtures'

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

async function raiseAlarm(request: APIRequestContext, headers: Record<string, string>, message: string) {
  const calc = await request.post('/api/calculated-tags', { headers, data: { name: message, expression: '50', intervalMs: 200 } })
  expect(calc.ok(), await calc.text()).toBeTruthy()
  const tagId = (await calc.json()).id as number
  const rule = await request.post('/api/alarm-rules', {
    headers,
    data: { tagId, type: 'High', threshold: 10, level: 'Warning', delaySec: 0, recoverDeadband: 0, enabled: true, message }
  })
  expect(rule.ok(), await rule.text()).toBeTruthy()
  const ruleId = (await rule.json()).id as number
  let alarmId = 0
  await expect.poll(async () => {
    const list = await (await request.get('/api/alarms?live=true', { headers })).json()
    const hit = list.find((x: { ruleId: number; id: number }) => x.ruleId === ruleId)
    alarmId = hit?.id ?? 0
    return hit?.state
  }, { timeout: 20000 }).toBe('Unack')
  return { tagId, ruleId, alarmId }
}

test('alarm ack, note, timeline, shelve and page actions keep the failure visible', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const message = `失败${Date.now()}`
  const raised = await raiseAlarm(request, session.headers, message)
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/alarms/${raised.alarmId}/ack`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '确认不可用' }) }))
  await page.route((url) => url.pathname === `/api/alarms/${raised.alarmId}/timeline`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '时间线不可用' }) }))
  await page.route((url) => url.pathname === `/api/alarm-rules/${raised.ruleId}/shelve`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '暂挂不可用' }) }))
  await page.route((url) => url.pathname === `/api/alarm-rules/${raised.ruleId}/unshelve`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '解除不可用' }) }))
  await page.route((url) => url.pathname === '/api/annunciators', (route) => {
    if (route.request().method() !== 'GET') return route.continue()
    return route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '声光目录不可用' }) })
  })
  await page.goto('/alarms')
  const row = page.getByRole('row').filter({ hasText: message })
  await row.locator('[data-test="ack"]').click()
  const ackDialog = page.getByRole('dialog', { name: '确认报警' })
  await ackDialog.locator('[data-test="note-submit"]').click()
  await expect(ackDialog.locator('[data-test="ack-error"]')).toContainText('确认不可用')
  await expect(ackDialog).toBeVisible()
  await expect(row.locator('[data-test="alarm-state"]')).toHaveText('未确认')
  await ackDialog.getByRole('button', { name: '取消', exact: true }).click()

  await row.getByRole('button', { name: '备注', exact: true }).click()
  const noteDialog = page.getByRole('dialog', { name: '添加备注' })
  await noteDialog.locator('[data-test="note-submit"]').click()
  await expect(noteDialog.locator('[data-test="ack-error"]')).toContainText('请填写备注')
  await expect(noteDialog).toBeVisible()
  await noteDialog.getByRole('button', { name: '取消', exact: true }).click()

  await row.locator('[data-test="timeline"]').click()
  await expect(page.locator('[data-test="alarm-timeline-error"]')).toContainText('时间线不可用')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog', { name: '报警处理记录' })).not.toBeVisible()

  await page.locator('[data-test="ack-visible"]').click()
  await page.getByRole('dialog', { name: '请确认' }).getByRole('button', { name: '确认可见' }).click()
  await expect(page.locator('[data-test="alarm-batch-result"]')).toContainText('确认不可用')

  await page.locator('[data-test="silence-ann"]').click()
  await expect(page.locator('[data-test="alarms-action-error"]')).toContainText('声光目录不可用')

  await row.getByRole('button', { name: '暂挂', exact: true }).click()
  const shelveDialog = page.getByRole('dialog', { name: '暂挂报警' })
  await shelveDialog.locator('[data-test="shelve-reason"]').fill('检修')
  await shelveDialog.locator('[data-test="shelve-submit"]').click()
  await expect(shelveDialog.getByText('暂挂不可用')).toBeVisible()
  await expect(shelveDialog).toBeVisible()
  await shelveDialog.getByRole('button', { name: '取消', exact: true }).click()

  const shelfReason = `接口暂挂-${message}`
  const shelved = await request.post(`/api/alarm-rules/${raised.ruleId}/shelve`, { headers: session.headers, data: { minutes: 60, reason: shelfReason } })
  expect(shelved.ok(), await shelved.text()).toBeTruthy()
  await page.getByRole('tab', { name: /暂挂/ }).click()
  const shelfRow = page.getByRole('row').filter({ hasText: shelfReason })
  await shelfRow.getByRole('button', { name: '解除暂挂', exact: true }).click()
  await page.getByRole('dialog', { name: '请确认' }).getByRole('button', { name: '解除暂挂' }).click()
  await expect(page.locator('[data-test="alarms-action-error"]')).toContainText('解除不可用')
  await expect(shelfRow).toBeVisible()
})

test('console ack failure stays on the console', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const message = `操作台${Date.now()}`
  const raised = await raiseAlarm(request, session.headers, message)
  const layout = await request.post('/api/hmi/layouts', {
    headers: session.headers,
    data: { name: `台${Date.now()}`, document: { monitors: [{ name: '主屏', columns: 12, rows: 12, regions: [{ id: 'alarms', kind: 'alarmList', row: 1, col: 1, rowSpan: 12, colSpan: 12 }] }] } }
  })
  expect(layout.ok(), await layout.text()).toBeTruthy()
  const createdLayout = await layout.json(), layoutId = createdLayout.id
  const publication = await request.post(`/api/hmi/layouts/${layoutId}/publish`, { headers: session.headers, data: { revision: createdLayout.revision, note: '报警确认验收' } })
  expect(publication.ok(), await publication.text()).toBeTruthy()
  await sign(page, session)
  await page.route((url) => url.pathname === `/api/alarms/${raised.alarmId}/ack`, (route) =>
    route.fulfill({ status: 500, contentType: 'application/json', body: JSON.stringify({ message: '确认不可用' }) }))
  await page.goto(`/console/${layoutId}`)
  const row = page.locator('[data-test="alarm-list"] .alarm-row').filter({ hasText: message })
  await row.getByRole('button', { name: `确认报警：${message}`, exact: true }).click()
  await expect(page.locator('[data-test="console-ack-error"]')).toContainText('确认不可用')
  await expect(row).toBeVisible()
})

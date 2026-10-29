import { test, expect, type APIRequestContext } from '@playwright/test'

async function login(request: APIRequestContext, userName: string, password: string) {
  const r = await request.post('/api/auth/login', { data: { userName, password } })
  expect(r.ok(), await r.text()).toBeTruthy()
  const body = await r.json()
  return { ...body, headers: { Authorization: `Bearer ${body.token}` } }
}

// 权限口径回归：服务端按「全局 / 区域」两种口径判权，界面必须同口径。
// 之前测点 Excel 导入（需全局 Configure）与 OEE 阈值（需全局 Configure）对区域配置员可见，
// 用户填完表单才被 403；OEE 产线与声光面板则完全没有行级判权。
test('permission-gated controls match the server: global-only hidden, row actions follow area', async ({ page, request }) => {
  test.setTimeout(120000)
  const admin = (await login(request, 'admin', 'admin123')).headers
  const suffix = Date.now()
  const post = async (url: string, data: unknown) => {
    const r = await request.post(url, { headers: admin, data })
    expect(r.ok(), await r.text()).toBeTruthy()
    return r.json()
  }

  const areaA = await post('/api/areas', { name: `PERM-A-${suffix}` })
  const areaB = await post('/api/areas', { name: `PERM-B-${suffix}` })
  const lineBody = (name: string, areaId: number) => ({
    name, areaId, description: null, stateTagId: null, stateMapJson: '{}',
    goodCountTagId: null, rejectCountTagId: null, totalCountTagId: null, productId: null,
    idealCycleSec: 1, ratedUnitsPerHour: null, targetUnitsPerHour: null, enabled: true
  })
  const lineA = await post('/api/oee/lines', lineBody(`PERM-LA-${suffix}`, areaA.id))
  const lineB = await post('/api/oee/lines', lineBody(`PERM-LB-${suffix}`, areaB.id))
  const now = Date.now()
  const downtime = (lineId: number) => ({ lineId, startTs: now - 3600000, endTs: now - 60000, reasonId: null, state: 'Fault', note: null, confirmed: false })
  await post('/api/oee/downtimes', downtime(lineA.id))
  await post('/api/oee/downtimes', downtime(lineB.id))
  // 全局产品（areaId 为空）对区域用户可见（只读），但编辑需要全局 Configure —— 行级判权的关键场景
  const productBody = (name: string, areaId: number | null) => ({ name, code: null, areaId, idealCycleSec: 1, ratedUnitsPerHour: null, description: null })
  await post('/api/oee/products', productBody(`PERM-PG-${suffix}`, null))
  await post('/api/oee/products', productBody(`PERM-PA-${suffix}`, areaA.id))

  const role = await post('/api/roles', { name: `PERM-配置员-${suffix}`, permissions: ['View', 'Configure'] })
  const controlRole = await post('/api/roles', { name: `PERM-控制-${suffix}`, permissions: ['Control'] })
  const ackRole = await post('/api/roles', { name: `PERM-确认-${suffix}`, permissions: ['AckAlarm'] })
  const viewRole = await post('/api/roles', { name: `PERM-查看-${suffix}`, permissions: ['View'] })
  const user = await post('/api/users', { userName: `perm-${suffix}`, password: 'secret123', displayName: 'A区配置员', role: 'None', enabled: true })
  const assigned = await request.put(`/api/users/${user.id}/assignments`, { headers: admin, data: { assignments: [{ roleId: role.id, areaId: areaA.id }, { roleId: controlRole.id, areaId: areaA.id }, { roleId: ackRole.id, areaId: areaA.id }, { roleId: viewRole.id, areaId: areaB.id }] } })
  const column = { key: 'a', title: '报警', kind: 'AlarmCount', decimals: 0 }
  await post('/api/report-templates', { name: `PERM-RA-${suffix}`, period: 'Day', areaId: areaA.id, scheduleEnabled: false, columns: [column] })
  await post('/api/report-templates', { name: `PERM-RB-${suffix}`, period: 'Day', areaId: areaB.id, scheduleEnabled: false, columns: [column] })
  const port = 20000 + (suffix % 20000)
  const deviceBody = (name: string, areaId: number, devicePort: number) => ({
    name, protocol: 'ModbusTcp', host: '127.0.0.1', port: devicePort, stationNumber: 1,
    byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId
  })
  const devA = await post('/api/devices', deviceBody(`PERM-DA-${suffix}`, areaA.id, port))
  const devB = await post('/api/devices', deviceBody(`PERM-DB-${suffix}`, areaB.id, port + 1))
  const tagBody = (deviceId: number, name: string) => ({
    deviceId, name, area: 'HoldingRegister', address: 0, dataType: 'UInt16', scale: 1, offset: 0,
    writable: true, archiveMode: 'None', archivePeriodSec: 60, deadband: 0
  })
  const tagA = await post('/api/tags', tagBody(devA.id, `W-${suffix}-a`))
  const tagB = await post('/api/tags', tagBody(devB.id, `W-${suffix}-b`))
  const param = (tagId: number) => ({ key: 'p', name: 'P', tagId, unit: null, min: null, max: null, defaultValue: 0 })
  const tplA = await post('/api/recipes/templates', { name: `PERM-RTA-${suffix}`, areaId: areaA.id, description: null, dualApproval: false, parameters: [param(tagA.id)], revision: 0 })
  const tplB = await post('/api/recipes/templates', { name: `PERM-RTB-${suffix}`, areaId: areaB.id, description: null, dualApproval: false, parameters: [param(tagB.id)], revision: 0 })
  await post('/api/recipes', { templateId: tplA.id, name: `PERM-RCPA-${suffix}`, description: null, values: [{ key: 'p', value: 1 }], note: null, version: 0 })
  await post('/api/recipes', { templateId: tplB.id, name: `PERM-RCPB-${suffix}`, description: null, values: [{ key: 'p', value: 1 }], note: null, version: 0 })
  const endStep = { id: 's1', type: 'end', name: 'done' }
  await post('/api/control/sequences', { name: `PERM-SA-${suffix}`, areaId: areaA.id, description: null, dualApproval: false, steps: [endStep], revision: 0 })
  await post('/api/control/sequences', { name: `PERM-SB-${suffix}`, areaId: areaB.id, description: null, dualApproval: false, steps: [endStep], revision: 0 })
  const channel = await post('/api/notification-channels', {
    name: `PERM-CH-${suffix}`, type: 'Webhook', enabled: true,
    settings: { url: 'http://127.0.0.1:9/hook', port: 25, useSsl: false, timeoutMs: 5000 }, secret: null
  })
  const routeBody = (name: string, areaId: number) => ({
    name, enabled: true, channelId: channel.id, areaId, minLevel: 'Warning', shiftId: null,
    recipients: '', onRaise: true, onRecover: false, escalateAfterMinutes: null
  })
  await post('/api/notification-routes', routeBody(`PERM-NA-${suffix}`, areaA.id))
  await post('/api/notification-routes', routeBody(`PERM-NB-${suffix}`, areaB.id))
  const panelBody = (name: string, areaId: number) => ({
    name, areaId, enabled: true, latchMode: 'Follow', silenceMinutes: 5,
    flashOnMs: 400, flashOffMs: 400, criticalFlashOnMs: 200, criticalFlashOffMs: 200,
    browserSound: false, outputs: [], levels: []
  })
  const panelA = await post('/api/annunciators', panelBody(`PERM-ANNA-${suffix}`, areaA.id))
  const panelB = await post('/api/annunciators', panelBody(`PERM-ANNB-${suffix}`, areaB.id))
  const rule = (tagId: number, message: string) => ({ tagId, type: 'High', threshold: 1, level: 'Warning', delaySec: 0, recoverDeadband: 0, enabled: true, message, offDelaySec: 0, suppressTagId: null, suppressMode: 'None' })
  await post('/api/alarm-rules', rule(tagA.id, `PERM-RULEA-${suffix}`))
  await post('/api/alarm-rules', rule(tagB.id, `PERM-RULEB-${suffix}`))
  const screenName = `PERM-SCR-${suffix}`
  const screen = await post('/api/screens', { name: screenName })
  const command = (id: string, text: string, tagId: number, x: number) => ({
    id, type: 'button', x, y: 40, width: 180, height: 48, rotation: 0, text,
    color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId,
    action: { type: 'command', tagId, screenId: null, value: 1 }
  })
  const savedScreen = await request.put(`/api/screens/${screen.id}`, {
    headers: admin,
    data: {
      name: screenName, revision: screen.revision,
      document: { schemaVersion: 1, width: 800, height: 240, background: '#101b2d', nodes: [command('cmd-a', `PERM-CMDA-${suffix}`, tagA.id, 20), command('cmd-b', `PERM-CMDB-${suffix}`, tagB.id, 220)] }
    }
  })
  expect(savedScreen.ok(), await savedScreen.text()).toBeTruthy()
  const publishedScreen = await request.post(`/api/screens/${screen.id}/publish`, { headers: admin, data: { revision: (await savedScreen.json()).revision } })
  expect(publishedScreen.ok(), await publishedScreen.text()).toBeTruthy()
  expect(assigned.ok(), await assigned.text()).toBeTruthy()

  await page.goto('/login')
  await page.getByPlaceholder(/用户名/).fill(user.userName)
  await page.getByPlaceholder(/密码/).fill('secret123')
  await page.keyboard.press('Enter')
  await expect(page).toHaveURL(/overview/)

  // 1) 测点 Excel 导入需要全局 Configure → 按钮不出现；新增只需区域权限，仍然可用
  await page.goto('/config/tags')
  await expect(page.locator('[data-test="tag-create"]')).toBeVisible({ timeout: 15000 })
  await expect(page.locator('[data-test="tag-import"]')).toHaveCount(0)

  // 2) OEE 阈值是全局配置 → 配置页按钮不出现
  await page.goto('/oee')
  await expect(page.locator('[data-test="kpi-oee"]')).toBeVisible({ timeout: 20000 })
  await page.goto('/config/oee')
  await expect(page.locator('[data-test="oee-lines"]')).toBeVisible({ timeout: 15000 })
  await expect(page.locator('[data-test="oee-thresholds"]')).toHaveCount(0)

  // 3) OEE 行级判权：自己区域的行可编辑；全局记录（areaId 为空）只读，编辑按钮不出现
  await page.getByRole('tab', { name: '产线', exact: true }).click()
  const rowLineA = page.getByRole('row').filter({ hasText: `PERM-LA-${suffix}` })
  await expect(rowLineA).toBeVisible()
  await expect(rowLineA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)

  await page.getByRole('tab', { name: '产品', exact: true }).click()
  const rowProductA = page.getByRole('row').filter({ hasText: `PERM-PA-${suffix}` })
  const rowProductGlobal = page.getByRole('row').filter({ hasText: `PERM-PG-${suffix}` })
  await expect(rowProductA).toBeVisible()
  await expect(rowProductGlobal).toBeVisible()
  await expect(rowProductA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(rowProductGlobal.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)

  await page.getByRole('tab', { name: '产线', exact: true }).click()
  await page.locator('[data-test="oee-add-line"]').click()
  const lineDialog = page.locator('.el-dialog:visible')
  await lineDialog.locator('[data-test="oee-line-area"]').click()
  await expect(page.getByRole('option', { name: `PERM-A-${suffix}`, exact: true })).toHaveCount(1)
  await expect(page.getByRole('option', { name: `PERM-B-${suffix}`, exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await lineDialog.getByRole('button', { name: '取消', exact: true }).click()

  await expect(page.getByRole('tab', { name: '停机事件', exact: true })).toHaveCount(0)
  await page.goto('/production/events')
  const dtA = page.getByRole('row').filter({ hasText: `PERM-LA-${suffix}` })
  const dtB = page.getByRole('row').filter({ hasText: `PERM-LB-${suffix}` })
  await expect(dtA).toBeVisible()
  await expect(dtB).toBeVisible()
  await expect(dtA.getByRole('button', { name: '删除', exact: true })).toHaveCount(1)
  await expect(dtB.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)

  // 4) 声光面板新建时默认落到其可配置区域，而不是「全局」（全局面板需全局权限，必然 403）
  await page.goto('/config/annunciators')
  await page.locator('[data-test="ann-add"]').click()
  const dialog = page.locator('.el-dialog:visible')
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText(`PERM-A-${suffix}`)
  await dialog.locator('[data-test="ann-area"]').click()
  await expect(page.getByRole('option', { name: `PERM-A-${suffix}`, exact: true })).toHaveCount(1)
  await expect(page.getByRole('option', { name: `PERM-B-${suffix}`, exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await page.keyboard.press('Escape')
  const annA = page.getByRole('row').filter({ hasText: `PERM-ANNA-${suffix}` })
  const annB = page.getByRole('row').filter({ hasText: `PERM-ANNB-${suffix}` })
  await expect(annA).toBeVisible()
  await expect(annB).toBeVisible()
  await expect(annA.getByRole('button', { name: '消音', exact: true })).toHaveCount(1)
  await expect(annB.getByRole('button', { name: '消音', exact: true })).toHaveCount(0)

  // 5) 报表模板按行区域判权：能查看 B 区，但不能编辑/生成/删除 B 区模板
  await page.goto('/report-center')
  await page.locator('[data-test="report-center-templates"]').click()
  const reportA = page.getByRole('row').filter({ hasText: `PERM-RA-${suffix}` })
  const reportB = page.getByRole('row').filter({ hasText: `PERM-RB-${suffix}` })
  await expect(reportA).toBeVisible({ timeout: 15000 })
  await expect(reportB).toBeVisible()
  await expect(reportA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(reportB.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await expect(reportB.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)

  // 7) 顺控按行区域判权：A 区有 Configure 和 Control，B 区只有查看。
  await page.goto('/control')
  await page.getByRole('tab', { name: '顺控', exact: true }).click()
  const seqA = page.getByRole('row').filter({ hasText: `PERM-SA-${suffix}` })
  const seqB = page.getByRole('row').filter({ hasText: `PERM-SB-${suffix}` })
  await expect(seqA).toBeVisible({ timeout: 15000 })
  await expect(seqB).toBeVisible()
  await expect(seqA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(seqA.locator('[data-test^="sequence-start-"]')).toHaveCount(1)
  await expect(seqB.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await expect(seqB.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)
  await expect(seqB.locator('[data-test^="sequence-start-"]')).toHaveCount(0)
  await page.locator('[data-test="sequence-new"]').click()
  await page.locator('[data-test="sequence-area"]').click()
  await expect(page.getByRole('option', { name: `PERM-A-${suffix}`, exact: true })).toBeVisible()
  await expect(page.getByRole('option', { name: `PERM-B-${suffix}`, exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')

  // 8) 配方模板/配方：能查看 B 区，但不能编辑；下发只出现在有 Control 的 A 区
  await page.goto('/recipes')
  await page.getByRole('tab', { name: '模板', exact: true }).click()
  const tplRowA = page.getByRole('row').filter({ hasText: `PERM-RTA-${suffix}` })
  const tplRowB = page.getByRole('row').filter({ hasText: `PERM-RTB-${suffix}` })
  await expect(tplRowA).toBeVisible({ timeout: 15000 })
  await expect(tplRowA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(tplRowB.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await expect(tplRowB.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)
  await page.getByRole('tab', { name: '配方', exact: true }).click()
  const recipeA = page.getByRole('row').filter({ hasText: `PERM-RCPA-${suffix}` })
  const recipeB = page.getByRole('row').filter({ hasText: `PERM-RCPB-${suffix}` })
  await expect(recipeA).toBeVisible()
  await expect(recipeA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(recipeA.getByRole('button', { name: '下发', exact: true })).toHaveCount(1)
  await expect(recipeB.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await expect(recipeB.getByRole('button', { name: '下发', exact: true })).toHaveCount(0)

  // 9) 通知路由新建默认落到可配置区域；只能查看的区域不在下拉里，列表里也没有
  await page.goto('/config/notifications')
  await expect(page.getByRole('row').filter({ hasText: `PERM-NA-${suffix}` })).toBeVisible({ timeout: 15000 })
  await expect(page.getByText(`PERM-NB-${suffix}`)).toHaveCount(0)
  await page.locator('[data-test="route-add"]').click()
  await expect(page.locator('[data-test="route-area"]')).toContainText(`PERM-A-${suffix}`)
  await page.locator('[data-test="route-area"]').click()
  await expect(page.getByRole('option', { name: `PERM-A-${suffix}`, exact: true })).toBeVisible()
  await expect(page.getByRole('option', { name: `PERM-B-${suffix}`, exact: true })).toHaveCount(0)

  // 10) 更改采集位置和中心证书库要全局 Configure；区域配置员看不到入口
  await page.goto('/config/devices')
  await expect(page.getByRole('row').filter({ hasText: `PERM-DA-${suffix}` })).toBeVisible({ timeout: 15000 })
  await expect(page.locator('[data-test="device-assign"]')).toHaveCount(0)
  await expect(page.locator('[data-test="device-certificates"]')).toHaveCount(0)
  const deviceRowA = page.getByRole('row').filter({ hasText: `PERM-DA-${suffix}` })
  const deviceRowB = page.getByRole('row').filter({ hasText: `PERM-DB-${suffix}` })
  await expect(deviceRowA.getByRole('checkbox')).toBeEnabled()
  await expect(deviceRowB.getByRole('checkbox')).toBeDisabled()
  await expect(deviceRowA).toContainText('Modbus TCP')
  await expect(deviceRowA).not.toContainText('ModbusTcp')
  await page.locator('[data-test="device-create"]').click()
  const deviceDialog = page.locator('[data-test="device-dialog"]')
  await expect(deviceDialog.locator('[data-test="device-area"]')).toContainText(`PERM-A-${suffix}`)
  await deviceDialog.locator('[data-test="device-area"]').click()
  await expect(page.getByRole('option', { name: `PERM-A-${suffix}`, exact: true })).toBeVisible()
  await expect(page.getByRole('option', { name: `PERM-B-${suffix}`, exact: true })).toHaveCount(0)
  await expect(page.getByRole('option', { name: '默认区域', exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')
  await deviceDialog.getByRole('button', { name: '取消', exact: true }).click()

  // 11) 实时写值、测点编辑、报警规则按测点所在设备的区域判权
  await page.goto('/realtime')
  await page.locator('[data-test="realtime-search"]').fill(`W-${suffix}-a`)
  const liveA = page.getByRole('row').filter({ hasText: `W-${suffix}-a` })
  await expect(liveA).toBeVisible({ timeout: 15000 })
  await expect(liveA.getByRole('button', { name: '写值', exact: true })).toHaveCount(1)
  await page.locator('[data-test="realtime-search"]').fill(`W-${suffix}-b`)
  const liveB = page.getByRole('row').filter({ hasText: `W-${suffix}-b` })
  await expect(liveB).toBeVisible()
  await expect(liveB.getByRole('button', { name: '写值', exact: true })).toHaveCount(0)

  await page.goto('/config/tags')
  await page.locator('[data-test="tag-filter-device"]').click()
  await page.getByRole('option', { name: `PERM-DA-${suffix}`, exact: true }).click()
  await page.locator('[data-test="tag-query"]').click()
  const tagRowA = page.getByRole('row').filter({ hasText: `W-${suffix}-a` })
  await expect(tagRowA).toBeVisible({ timeout: 15000 })
  await expect(tagRowA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await page.locator('[data-test="tag-filter-device"]').click()
  await page.getByRole('option', { name: `PERM-DB-${suffix}`, exact: true }).click()
  await page.locator('[data-test="tag-query"]').click()
  const tagRowB = page.getByRole('row').filter({ hasText: `W-${suffix}-b` })
  await expect(tagRowB).toBeVisible()
  await expect(tagRowB.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await expect(tagRowB.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)
  await page.locator('[data-test="tag-preview"]').click()
  await page.locator('[data-test="tag-preview-device"]').click()
  await expect(page.getByRole('option', { name: `PERM-DA-${suffix}`, exact: true })).toHaveCount(1)
  await expect(page.getByRole('option', { name: `PERM-DB-${suffix}`, exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')

  await page.goto('/config/rules')
  const ruleA = page.getByRole('row').filter({ hasText: `PERM-RULEA-${suffix}` })
  const ruleB = page.getByRole('row').filter({ hasText: `PERM-RULEB-${suffix}` })
  await expect(ruleA).toBeVisible({ timeout: 15000 })
  await expect(ruleB).toBeVisible()
  await expect(ruleA.getByRole('button', { name: '编辑', exact: true })).toHaveCount(1)
  await expect(ruleB.getByRole('button', { name: '编辑', exact: true })).toHaveCount(0)
  await expect(ruleB.getByRole('button', { name: '删除', exact: true })).toHaveCount(0)

  await page.goto(`/screens/${screen.id}`)
  await expect(page.getByRole('button', { name: `PERM-CMDA-${suffix}` })).toBeVisible({ timeout: 15000 })
  await page.getByRole('button', { name: `PERM-CMDB-${suffix}` }).click()
  await expect(page.getByText('当前用户没有控制权限')).toBeVisible()
  await expect(page.locator('[data-test="control-confirm"]')).toHaveCount(0)
  await page.getByRole('button', { name: `PERM-CMDA-${suffix}` }).click()
  await expect(page.locator('[data-test="control-confirm"]')).toBeVisible()
  await page.keyboard.press('Escape')

  await page.goto('/config/calculations')
  await page.locator('[data-test="calc-new"]').click()
  const calcDialog = page.locator('.el-dialog:visible')
  await expect(calcDialog).toBeVisible()
  await calcDialog.locator('[data-test="calc-area"]').click()
  await expect(page.getByRole('option', { name: `PERM-A-${suffix}`, exact: true })).toHaveCount(1)
  await expect(page.getByRole('option', { name: `PERM-B-${suffix}`, exact: true })).toHaveCount(0)
  await page.keyboard.press('Escape')

  // 声光消音只提交有确认权的面板。B 区面板能看见，但不能消音。
  const silenced: number[] = []
  await page.route('**/api/annunciators/*/silence', async (route) => {
    const id = Number(new URL(route.request().url()).pathname.split('/').at(-2))
    silenced.push(id)
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ silenced: true }) })
  })
  await page.goto('/alarms')
  await page.locator('[data-test="silence-ann"]').click()
  const silenceBox = page.locator('.el-message-box')
  await expect(silenceBox).toBeVisible()
  await silenceBox.getByRole('button', { name: '声光消音' }).click()
  await expect.poll(() => silenced.includes(panelA.id)).toBeTruthy()
  expect(silenced).not.toContain(panelB.id)

  // 12) 仅 Global:System 可进的页面：导航已隐藏，直接输 URL 也必须挡回总览
  await page.goto('/config/external-api')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/ops')
  await expect(page).toHaveURL(/overview/)

  // 13) 仅 Global:Configure 的工程组态页：区域配置员直达 URL 同样挡回总览
  await page.goto('/config/layouts')
  await expect(page).toHaveURL(/overview/)
  await page.goto('/config/gateways')
  await expect(page).toHaveURL(/overview/)
})

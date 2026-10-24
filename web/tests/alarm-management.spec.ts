import { test, expect, type APIRequestContext } from '@playwright/test'
import http from 'node:http'
import type { AddressInfo } from 'node:net'

async function login(request: APIRequestContext, userName: string, password: string) {
  const response = await request.post('/api/auth/login', { data: { userName, password } }); expect(response.ok(), await response.text()).toBeTruthy()
  const result = await response.json(); return { ...result, headers: { Authorization: `Bearer ${result.token}` } }
}

test('ISA-18.2 alarm handling: ack with note, timeline, shelve, webhook routing, secrets masked, KPI page', async ({ page, request }) => {
  test.setTimeout(90000)
  const hits: { url: string; token?: string; body: string }[] = []
  const receiver = http.createServer((req, res) => {
    let body = ''; req.on('data', (c) => (body += c)); req.on('end', () => { hits.push({ url: req.url ?? '', token: req.headers['x-token'] as string | undefined, body }); res.end('ok') })
  })
  await new Promise<void>((resolve) => receiver.listen(0, '127.0.0.1', resolve))
  const hookUrl = `http://127.0.0.1:${(receiver.address() as AddressInfo).port}/hook`
  try {
    const admin = (await login(request, 'admin', 'admin123')).headers, suffix = Date.now()
    const post = async (url: string, data: unknown) => { const r = await request.post(url, { headers: admin, data }); expect(r.ok(), await r.text()).toBeTruthy(); return r.json() }

    // UI：新增 webhook 通道，凭据只显示 ***。
    await page.goto('/login')
    await page.getByPlaceholder(/用户名/).fill('admin'); await page.getByPlaceholder(/密码/).fill('admin123'); await page.keyboard.press('Enter')
    await expect(page).toHaveURL(/overview/)
    await page.goto('/config/notifications')
    await page.getByRole('tab', { name: '通知通道' }).click()
    await page.locator('[data-test="channel-add"]').click()
    await page.locator('[data-test="channel-name"]').fill(`E2E-hook-${suffix}`)
    await page.locator('[data-test="channel-url"]').fill(hookUrl)
    await page.locator('[data-test="channel-header"]').fill('X-Token')
    await page.locator('[data-test="channel-secret"]').fill('e2e-secret-token')
    await page.locator('[data-test="channel-save"]').click()
    await expect(page.getByText(`E2E-hook-${suffix}`)).toBeVisible()
    const channels = await (await request.get('/api/notification-channels', { headers: admin })).json()
    const channel = channels.find((x: any) => x.name === `E2E-hook-${suffix}`)
    expect(channel.secret).toBe('***'); expect(JSON.stringify(channels)).not.toContain('e2e-secret-token')
    await post('/api/notification-routes', { name: `E2E-route-${suffix}`, enabled: true, channelId: channel.id, areaId: null, minLevel: 'Info', shiftId: null, recipients: '', onRaise: true, onRecover: false, escalateAfterMinutes: null })

    // 计算测点恒为 50，高限 10 立即报警。
    const calc = await post('/api/calculated-tags', { name: `E2E-alarm-${suffix}`, expression: '50', intervalMs: 200 })
    const rule = await post('/api/alarm-rules', { tagId: calc.id, type: 'High', threshold: 10, level: 'Critical', delaySec: 0, recoverDeadband: 0, enabled: true, message: `E2E高温-${suffix}` })
    let alarm: any
    await expect.poll(async () => {
      const list = await (await request.get('/api/alarms?live=true', { headers: admin })).json()
      alarm = list.find((x: any) => x.ruleId === rule.id); return alarm?.state
    }, { timeout: 20000 }).toBe('Unack')
    await expect.poll(() => hits.some((x) => x.body.includes(`E2E高温-${suffix}`)), { timeout: 15000 }).toBe(true)
    expect(hits.find((x) => x.body.includes(`E2E高温-${suffix}`))!.token).toBe('e2e-secret-token')

    // UI：确认并写备注，状态变为已确认，处理记录可见。
    await page.goto('/alarms')
    const row = page.getByRole('row').filter({ hasText: `E2E高温-${suffix}` })
    await expect(row.locator('[data-test="alarm-state"]')).toHaveText('未确认')
    await row.locator('[data-test="ack"]').click()
    await page.locator('[data-test="note-input"]').fill('已通知现场巡检')
    await page.locator('[data-test="note-submit"]').click()
    await expect(row.locator('[data-test="alarm-state"]')).toHaveText('已确认')
    await row.locator('[data-test="timeline"]').click()
    await expect(page.locator('[data-test="timeline-item"]').filter({ hasText: '已通知现场巡检' })).toBeVisible()
    await page.keyboard.press('Escape')

    // UI：暂挂必须写原因；暂挂后默认列表隐藏，在“暂挂”页可见。
    await row.getByRole('button', { name: '暂挂', exact: true }).click()
    await expect(page.locator('[data-test="shelve-submit"]')).toBeDisabled()
    await page.locator('[data-test="shelve-reason"]').fill('E2E 仪表校验')
    await page.locator('[data-test="shelve-submit"]').click()
    await expect(page.getByRole('row').filter({ hasText: `E2E高温-${suffix}` })).toHaveCount(0)
    await page.getByRole('tab', { name: /暂挂/ }).click()
    await expect(page.getByRole('row').filter({ hasText: 'E2E 仪表校验' })).toBeVisible()
    const shelved = await (await request.get(`/api/alarms/${alarm.id}/timeline`, { headers: admin })).json()
    expect(shelved.alarm.shelved).toBe(true)
    expect(shelved.activities.map((x: any) => x.action)).toEqual(expect.arrayContaining(['raise', 'notify', 'ack', 'shelve']))

    // 报警分析页
    await page.goto('/alarm-analytics')
    await expect(page.locator('[data-test="kpi-top"]').getByText(`E2E高温-${suffix}`)).toBeVisible()

    await request.post(`/api/alarm-rules/${rule.id}/unshelve`, { headers: admin, data: {} })
    await request.delete(`/api/alarm-rules/${rule.id}`, { headers: admin })
  } finally {
    receiver.close()
  }
})

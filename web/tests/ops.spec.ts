import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

async function auth(request: APIRequestContext) {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy(); return response.json()
}
async function signIn(page: Page, session: { token: string; user: unknown }) {
  await page.goto('/login'); await page.evaluate(s => { localStorage.setItem('scada_token', s.token); localStorage.setItem('scada_user', JSON.stringify(s.user)) }, session)
}

test('ops page shows standalone HA, runs a backup with restore drill and metrics require auth', async ({ page, request }) => {
  const session = await auth(request), headers = { Authorization: `Bearer ${session.token}` }
  const role = await (await request.get('/api/ha/role')).json()
  expect(role.enabled).toBe(false)
  expect((await request.get('/metrics')).status()).toBe(401)
  const metrics = await request.get('/metrics', { headers }); expect(metrics.ok()).toBeTruthy()
  const text = await metrics.text()
  for (const name of ['scada_ha_primary', 'scada_device_cycle_ms', 'scada_alarms_active', 'scada_disk_free_bytes']) expect(text).toContain(name)

  await signIn(page, session); await page.goto('/ops')
  await expect(page.locator('[data-test=ha-standalone]')).toBeVisible()
  await expect(page.locator('[data-test=standby-banner]')).toHaveCount(0)
  // 服务启动时定时任务可能已生成一次备份，这里只认本次手动触发的记录。
  const before = ((await (await request.get('/api/maintenance/backups/runs', { headers })).json()) as { trigger: string }[]).filter(x => x.trigger === '手动').length
  await page.locator('[data-test=backup-run]').click()
  await expect(page.locator('.el-message--success')).toContainText('通过恢复演练', { timeout: 60000 })
  const runs = ((await (await request.get('/api/maintenance/backups/runs', { headers })).json()) as { trigger: string; ok: boolean; drill: { ok: boolean } }[]).filter(x => x.trigger === '手动')
  expect(runs.length).toBe(before + 1); expect(runs[0].ok).toBe(true); expect(runs[0].drill.ok).toBe(true)
  await expect(page.locator('[data-test=backup-runs]')).toContainText('手动')
  const audit = await (await request.get(`/api/logs?keyword=${encodeURIComponent('备份并演练')}`, { headers })).json()
  expect(audit.items.length).toBeGreaterThan(0)
})

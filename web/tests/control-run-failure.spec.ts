import { test, expect, type APIRequestContext, type Page, loginApi } from './fixtures'

// 顺控执行到一半失败时，操作员要判断三件事：失败在哪一步、为什么、现场已经被动到哪一步。
// 原来「流程图」弹窗只有节点图，失败原因只存在于运行列表的窄列里；这里固化摘要与重跑入口。

const steps = [1, 2, 3, 4, 5, 6].map(i => ({
  id: `s${i}`, type: 'write', name: `开阀${i}`, tagId: 8400 + i, value: 1, confirmExpression: null, timeoutMs: 10000, delayMs: 0
}))
const sequence = { id: 7, name: '开车顺控', areaId: 1, description: null, dualApproval: false, revision: 3, updatedTs: Date.now() - 7_200_000, updatedBy: 'admin', steps }

const stepResult = (index: number, status: string, message: string | null = null) => ({
  index, stepId: `s${index}`, type: 'write', executions: status === 'pending' ? 0 : 1, name: `开阀${index}`,
  tagId: 8400 + index, value: 1, status, message,
  startedTs: status === 'pending' ? null : Date.now() - 60_000 + index * 1000,
  endedTs: status === 'pending' ? null : Date.now() - 58_000 + index * 1000
})

const failedRun = {
  id: 42, sequenceId: 7, sequenceName: '开车顺控', areaId: 1, status: 'Failed',
  startedTs: Date.now() - 60_000, endedTs: Date.now() - 45_000, startedBy: 'admin', approvedBy: 'lead',
  message: '反馈确认超时（10000ms），未收到阀门3=开启',
  steps: [stepResult(1, 'done'), stepResult(2, 'done'), stepResult(3, 'failed', '反馈确认超时（10000ms），未收到阀门3=开启'), stepResult(4, 'pending'), stepResult(5, 'pending'), stepResult(6, 'pending')]
}
const abortedRun = {
  id: 43, sequenceId: 7, sequenceName: '开车顺控', areaId: 1, status: 'Aborted',
  startedTs: Date.now() - 300_000, endedTs: Date.now() - 280_000, startedBy: 'operator2', approvedBy: null, message: null,
  steps: [stepResult(1, 'done'), stepResult(2, 'failed', '服务停止，中断'), stepResult(3, 'pending')]
}
const completedRun = {
  id: 44, sequenceId: 7, sequenceName: '开车顺控', areaId: 1, status: 'Completed',
  startedTs: Date.now() - 900_000, endedTs: Date.now() - 880_000, startedBy: 'operator2', approvedBy: null, message: null,
  steps: [stepResult(1, 'done'), stepResult(2, 'done')]
}

async function openSequences(page: Page, request: APIRequestContext, runs: readonly unknown[], options: { missingSequence?: boolean } = {}) {
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({
    json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true, host: '127.0.0.1', port: 502, slaveId: 1 }]
  }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({
    json: steps.map(s => ({ id: s.tagId, name: `阀门${s.tagId - 8400}`, deviceId: 8301, unit: '', dataType: 'Bool', kind: 'Physical', address: 1, area: 'Coil', writable: true, enabled: true }))
  }))
  await page.route(url => url.pathname === '/api/control/sequences', route => route.fulfill({ json: options.missingSequence ? [] : [sequence] }))
  await page.route(url => url.pathname === '/api/control/runs', route => route.fulfill({ json: runs }))
  await page.goto('/control')
  await page.locator('.el-tabs__item', { hasText: '顺控' }).first().click()
}

test('失败运行的流程图给出可直接判断的摘要', async ({ page, request }) => {
  await openSequences(page, request, [failedRun])
  await page.locator('[data-test="run-flow-42"]').click()

  const panel = page.locator('[data-test="run-failure"]')
  await expect(panel).toBeVisible()
  // 失败在哪一步
  await expect(page.locator('[data-test="run-failure-headline"]')).toContainText('步骤 s3「开阀3」失败')
  // 为什么
  await expect(page.locator('[data-test="run-failure-reason"]')).toContainText('反馈确认超时')
  // 现场已经被动到哪一步：已完成的步骤必须点名，否则操作员不知道哪些设备动过
  await expect(page.locator('[data-test="run-failure-executed"]')).toContainText('已完成 2 步：s1、s2')
  // 下一步做什么
  await expect(page.locator('[data-test="run-failure-advice"]')).toContainText('核对现场')
  await expect(panel).toHaveClass(/is-failed/)
})

test('中止的运行给中止建议，不套用失败措辞', async ({ page, request }) => {
  await openSequences(page, request, [abortedRun])
  await page.locator('[data-test="run-flow-43"]').click()

  await expect(page.locator('[data-test="run-failure"]')).toHaveClass(/is-aborted/)
  await expect(page.locator('[data-test="run-failure-advice"]')).toContainText('中止')
})

test('正常完成的运行不显示失败摘要', async ({ page, request }) => {
  await openSequences(page, request, [completedRun])
  await page.locator('[data-test="run-flow-44"]').click()

  await expect(page.locator('[data-test="run-flow-dialog"]')).toBeVisible()
  await expect(page.locator('[data-test="run-failure"]')).toHaveCount(0)
  await expect(page.locator('[data-test="run-rerun"]')).toHaveCount(0)
})

test('重新执行先弹确认框并列出当前定义，确认后才下发', async ({ page, request }) => {
  await openSequences(page, request, [failedRun])
  let starts = 0
  await page.route(url => url.pathname === '/api/control/sequences/7/start', route => {
    starts++
    return route.fulfill({ json: { pending: false, approvalId: null, runId: 45 } })
  })
  await page.locator('[data-test="run-flow-42"]').click()
  await page.locator('[data-test="run-rerun"]').click()

  const box = page.locator('.el-message-box')
  await expect(box).toBeVisible()
  // 确认框里必须是「当前定义」，操作员据此判断能不能重跑
  await expect(box).toContainText('开阀1')
  await expect(box).toContainText('开阀6')
  expect(starts).toBe(0)

  await box.locator('button.el-button--primary').click()
  await expect.poll(() => starts).toBe(1)
})

test('顺控定义被删除时不给重跑入口', async ({ page, request }) => {
  await openSequences(page, request, [failedRun], { missingSequence: true })
  await expect(page.locator('[data-test="run-rerun-42"]')).toHaveCount(0)
  await page.locator('[data-test="run-flow-42"]').click()
  await expect(page.locator('[data-test="run-rerun"]')).toHaveCount(0)
})

test('停留在顺控页时，别人启动的运行会自动出现', async ({ page, request }) => {
  let runs: unknown[] = []
  const auth = await loginApi(request)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, auth)
  await page.route('**/api/**', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 8301, name: '验证设备', areaId: 1, protocol: 'ModbusTcp', enabled: true }] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
  await page.route(url => url.pathname === '/api/control/sequences', route => route.fulfill({ json: [sequence] }))
  await page.route(url => url.pathname === '/api/control/runs', route => route.fulfill({ json: runs }))
  await page.goto('/control')
  await page.locator('.el-tabs__item', { hasText: '顺控' }).first().click()
  await expect(page.locator('[data-test="runs"] tbody tr')).toHaveCount(0)

  // 另一位操作员在下位机侧启动了顺控：本页只靠轮询发现，没有推送
  runs = [{ ...failedRun, id: 51, status: 'Running', endedTs: null, message: null, startedBy: 'operator2', steps: [stepResult(1, 'running')] }]
  await expect(page.locator('[data-test="run-status-51"]')).toBeVisible({ timeout: 12_000 })
})

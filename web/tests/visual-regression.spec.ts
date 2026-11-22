import { test, expect, type APIRequestContext, type Page } from './fixtures'
import type { AlarmEvent, AlarmShelve, AlarmSummary } from '../src/types'

/**
 * 视觉基线：固定数据 + 固定时钟 + 禁用动画，只覆盖"表格/表单为主、无 canvas"的页面。
 * ECharts 页面（总览、趋势）暂不纳入：canvas 首帧时序不可控，先补 DOM/交互断言，避免引入 flaky。
 *
 * 基线用系统 Edge 生成（见下方 test.use），本地与 CI 同一通道才能比对。
 * 更新基线：npx playwright test tests/visual-regression.spec.ts --update-snapshots
 */
const NOW = new Date('2026-10-28T12:00:00Z').getTime()
const THEMES = ['light', 'dark'] as const
const VIEWPORTS = [
  { label: 'desktop', width: 1440, height: 900 },
  { label: 'phone', width: 390, height: 844 }
]

// 允许的差异只用于吸收不同机器间的抗锯齿噪声；版式/颜色变化会远超这个阈值。
const SHOT = { animations: 'disabled' as const, caret: 'hide' as const, maxDiffPixels: 500, threshold: 0.2, scale: 'css' as const }

// CI runner 默认 UTC，本机是 UTC+8：时间列会整体差 8 小时。浏览器时区必须钉死，基线才能跨环境比对。
// 通道固定为 Edge：CI（windows-latest）与本地都走系统 Edge，避免 Playwright Chromium 版本与抗锯齿
// 差异让基线在另一环境全红。Edge 大版本升级后若出现全页漂移，用 --update-snapshots 重生成。
test.use({ channel: 'msedge', timezoneId: 'Asia/Shanghai', locale: 'zh-CN' })

const emptySummary = {
  unack: 0, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 0, shelvedRules: 0, outOfServiceRules: 0,
  flood: { inFlood: false, count: 0, threshold: 10, windowStartTs: NOW - 600000, windowEndTs: NOW },
  floodGuard: { autoSuppress: false, firstOut: true, suppressMaxLevel: 'Warning', anyInFlood: false, areas: [] }
}

/**
 * 壳层公共数据：健康快照。真实服务的健康状态取决于离线设备数，会让顶栏状态条在不同环境里
 * 出现/消失；基线里固定为健康，状态条由交互用例单独覆盖。
 * 报警汇总按页面分别 mock（测点页 0、报警页 30）——同一个 URL 注册两次时，
 * 哪个 handler 生效取决于 Playwright 的匹配顺序，会让基线在两次运行间跳变。
 */
async function mockCommon(page: Page): Promise<void> {
  await page.route(url => url.pathname === '/api/health', route => route.fulfill({
    json: {
      status: 'ok', environment: 'Production', serverTime: new Date(NOW).toISOString(),
      uptimeSeconds: 3600, devices: [], onlineDevices: 1, totalDevices: 1, configuredTags: tags.length,
      busBacklog: 0, disk: { root: 'C:/', totalGb: 512, freeGb: 256 },
      ha: { role: 'Primary', primaryUrl: null }
    }
  }))
  await page.route(url => url.pathname === '/api/client-policy', route => route.fulfill({ json: { mobileControl: false } }))
}

async function setAppearance(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.clock.setFixedTime(NOW)
  await page.addInitScript(({ theme }) => {
    localStorage.setItem('scada_theme', theme)
    localStorage.setItem('scada_density', 'comfortable')
    localStorage.setItem('scada_locale', 'zh-CN')
    localStorage.setItem('scada_sound', 'off')
  }, { theme })
}

async function signIn(page: Page, request: APIRequestContext, theme: 'light' | 'dark'): Promise<void> {
  const response = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
  expect(response.ok()).toBeTruthy()
  const auth = await response.json()
  await setAppearance(page, theme)
  await page.addInitScript(({ token, user }) => {
    localStorage.setItem('scada_token', token)
    localStorage.setItem('scada_user', JSON.stringify(user))
  }, { token: auth.token, user: auth.user })
}

/** 截图前必须等加载遮罩消失：v-loading 的 spinner 与半透明遮罩会让整页文字变淡，截图会与基线差约 2%。
    注意 mask 元素会被 Element Plus 保留在 DOM 里，只能按"可见"判断，不能用 toHaveCount(0)。 */
async function settle(page: Page): Promise<void> {
  await expect(page.locator('.el-loading-mask:visible')).toHaveCount(0)
  // 遮罩消失后表格还会补一次行渲染（首出标志等徽标），等一帧再截才稳定。
  await page.evaluate(() => new Promise<void>(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve()))))
  await page.evaluate(() => document.fonts.ready)
}

const device = {
  id: 901, name: 'Pump A', protocol: 'ModbusTcp', host: '10.30.1.1', port: 502, stationNumber: 1,
  byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 3000, serialPort: 'COM1', baudRate: 9600,
  dataBits: 8, parity: 'None', stopBits: 'One', cpu: 'S71200', rack: 0, slot: 1,
  opcEndpointUrl: '', opcSecurityMode: 'None', opcSecurityPolicy: 'None', opcUserName: '', hasOpcPassword: false,
  enabled: true, gatewayId: null, areaId: 1
}
const tags = [
  { id: 31, deviceId: 901, name: 'Pump 出口压力', address: 0, unit: 'MPa', description: '' },
  { id: 32, deviceId: 901, name: 'Pump 出口温度', address: 2, unit: '℃', description: '' },
  { id: 33, deviceId: 901, name: 'Pump 运行状态', address: 4, unit: '', description: '' }
].map(tag => ({
  kind: 'Physical', areaId: 1, area: 'HoldingRegister', dataType: 'Float',
  scale: 1, offset: 0, writable: false, archiveMode: 'OnChange', deadband: 0.5,
  archivePeriodSec: 600, dbNumber: 1, bitOffset: 0, nodeId: '', ...tag
}))

async function mockTagCatalog(page: Page): Promise<void> {
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: emptySummary }))
  await page.route(url => url.pathname === '/api/areas', route => route.fulfill({ json: [{ id: 1, name: '一车间' }] }))
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device], headers: { 'X-Total-Count': '1' } }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tags, headers: { 'X-Total-Count': String(tags.length) } }))
}

async function mockAlarms(page: Page): Promise<void> {
  const levels = ['Critical', 'Warning', 'Info'] as const
  const items: AlarmEvent[] = Array.from({ length: 30 }, (_, index) => ({
    id: index + 1, ruleId: index + 1, tagId: 31, deviceId: 901, areas: [1], level: levels[index % 3]!,
    message: index === 0 ? '出口压力高高限' : index === 1 ? '出口温度上升过快' : `报警记录 ${index + 1}`,
    value: 12.34, state: 'Unack', startTs: NOW - (index + 1) * 60000,
    endTs: null, ackTs: null, ackUser: null, firstOut: index === 0, chattering: index === 2,
    shelved: false, suppressed: false
  }))
  const shelves: AlarmShelve[] = []
  const summary: AlarmSummary = {
    unack: items.length, acked: 0, rtnUnack: 0, shelved: 0, suppressed: 0, chattering: 1, shelvedRules: 0, outOfServiceRules: 0,
    flood: { inFlood: false, count: 0, threshold: 10, windowStartTs: NOW - 600000, windowEndTs: NOW },
    floodGuard: {
      autoSuppress: false, firstOut: true, suppressMaxLevel: 'Warning', anyInFlood: false, areas: []
    }
  }
  await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [device] }))
  await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: tags }))
  await page.route(url => url.pathname === '/api/alarms/summary', route => route.fulfill({ json: summary }))
  await page.route(url => url.pathname === '/api/alarm-shelves', route => route.fulfill({ json: shelves }))
  await page.route(url => url.pathname === '/api/alarms/page', route => {
    const query = new URL(route.request().url()).searchParams
    const offset = Number(query.get('offset') ?? 0), limit = Number(query.get('limit') ?? 100)
    return route.fulfill({ json: { items: items.slice(offset, offset + limit), total: items.length, offset, limit, capped: false } })
  })
}

for (const theme of THEMES) {
  test(`visual baseline (${theme})`, async ({ page, request }) => {
    test.setTimeout(180000)


    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height })

      await setAppearance(page, theme)
      await page.goto('/login')
      await expect(page.locator('.login-card')).toBeVisible()
      await settle(page)
      await expect(page).toHaveScreenshot(`login-${theme}-${viewport.label}.png`, SHOT)
    }

    await signIn(page, request, theme)
    await mockCommon(page)
    for (const viewport of VIEWPORTS) {
      await page.setViewportSize({ width: viewport.width, height: viewport.height })

      await mockTagCatalog(page)
      await page.goto('/config/tags')
      await expect(page.locator('[data-test="tags-table"] .el-table__body-wrapper .el-table__row')).toHaveCount(tags.length)
      await settle(page)
      await expect(page).toHaveScreenshot(`tags-${theme}-${viewport.label}.png`, SHOT)

      await mockAlarms(page)
      await page.goto('/alarms')
      // 手机端走卡片降级，桌面的 .alarm-panel 表格在该视口不存在。
      if (viewport.label === 'phone') {
        await expect(page.locator('[data-test="alarm-mobile-cards"] .alarm-card').first()).toBeVisible()
      } else {
        await expect(page.locator('.alarm-panel .el-table__body-wrapper .el-table__row').first()).toBeVisible()
      }
      await settle(page)
      await expect(page).toHaveScreenshot(`alarms-${theme}-${viewport.label}.png`, SHOT)
    }
  })
}
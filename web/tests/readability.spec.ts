import { test, expect } from './fixtures'

for (const theme of ['light', 'dark']) {
  test(`${theme}: table captions and hovered rows stay readable`, async ({ page, request }) => {
    const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
    expect(login.ok()).toBeTruthy()
    await page.addInitScript(({ session, theme }) => {
      localStorage.setItem('scada_token', session.token)
      localStorage.setItem('scada_user', JSON.stringify(session.user))
      localStorage.setItem('scada_theme', theme)
      localStorage.setItem('scada_density', 'comfortable')
      localStorage.setItem('scada_sound', 'off')
    }, { session: await login.json(), theme })
    await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{ id: 1, areaId: 1, name: '可读性测试设备', protocol: 'ModbusTcp', enabled: false }] }))
    await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [{
      id: 1, deviceId: 1, name: '出口压力', kind: 'Physical', area: 'HoldingRegister', address: 12,
      dataType: 'Float', unit: 'MPa', writable: false, archiveMode: 'OnChange'
    }] }))
    await page.goto('/config/tags')
    const caption = page.locator('[data-test="tags-table"] .el-table__row .tiny.muted').first()
    await expect(caption).toContainText('保持寄存器')
    const contrast = async () => caption.evaluate(el => {
      const rgb = (value: string) => value.match(/[\d.]+/g)!.slice(0, 3).map(Number)
      const luminance = (color: number[]) => color.map(value => {
        const channel = value / 255
        return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4
      }).reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index]!, 0)
      const foreground = getComputedStyle(el)
      let parent: Element | null = el
      let background = 'rgb(255, 255, 255)'
      while (parent) {
        const color = getComputedStyle(parent).backgroundColor
        if (color !== 'transparent' && color !== 'rgba(0, 0, 0, 0)') { background = color; break }
        parent = parent.parentElement
      }
      const a = luminance(rgb(foreground.color)), b = luminance(rgb(background))
      return { ratio: (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05), fontSize: parseFloat(foreground.fontSize) }
    })
    expect((await contrast()).fontSize).toBeGreaterThanOrEqual(13)
    expect((await contrast()).ratio).toBeGreaterThanOrEqual(4.5)
    await caption.hover()
    await expect.poll(async () => (await contrast()).ratio).toBeGreaterThanOrEqual(4.5)
    // Wait for the hover transition to settle before capturing its final contrast.
    await expect(caption.locator('xpath=ancestor::td[1]')).toHaveCSS('background-color', theme === 'dark' ? 'rgb(34, 48, 71)' : 'rgb(243, 247, 252)')
    expect((await contrast()).ratio).toBeGreaterThanOrEqual(4.5)
    await page.screenshot({ path: test.info().outputPath(`table-${theme}.png`) })
  })
}

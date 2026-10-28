import { test, expect } from '@playwright/test'

for (const width of [320, 390, 768, 1280]) {
  test(`tag editor keeps labels and controls inside the dialog at ${width}px`, async ({ page, request }) => {
    await page.setViewportSize({ width, height: 900 })
    const login = await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })
    expect(login.ok()).toBeTruthy()
    const session = await login.json()
    await page.addInitScript(({ token, user }) => {
      localStorage.setItem('scada_token', token)
      localStorage.setItem('scada_user', JSON.stringify(user))
    }, session)
    await page.route(url => url.pathname === '/api/devices', route => route.fulfill({ json: [{
      id: 901, name: '响应式测试设备', areaId: 1, protocol: 'ModbusTcp', enabled: false,
      host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD'
    }] }))
    await page.route(url => url.pathname === '/api/tags', route => route.fulfill({ json: [] }))
    await page.goto('/config/tags')
    await page.locator('[data-test="tag-create"]').click()
    const dialog = page.locator('[data-test="tag-dialog"]')
    await expect(dialog).toBeVisible()
    await expect(dialog.locator('[data-test="tag-name"]')).toBeFocused()

    const layout = await dialog.evaluate(el => {
      const bounds = el.getBoundingClientRect()
      const form = el.querySelector('.tag-form')!
      const columns = [...form.querySelectorAll('.el-col')].map(column => {
        const r = column.getBoundingClientRect()
        return { left: r.left, top: r.top, width: r.width }
      })
      const overflow = [...form.querySelectorAll('.el-input__wrapper, .el-select__wrapper, .el-switch')]
        .map(control => control.getBoundingClientRect())
        .filter(r => r.width && (r.left < bounds.left || r.right > bounds.right || r.width < 35))
        .map(r => ({ left: r.left, right: r.right, width: r.width }))
      const overlaps = [...form.querySelectorAll('.el-form-item')].filter(item => {
        const label = item.querySelector('.el-form-item__label')?.getBoundingClientRect()
        const content = item.querySelector('.el-form-item__content')?.getBoundingClientRect()
        return label && content && label.bottom > content.top + 1 && label.right > content.left + 1
      }).length
      return { columns, overflow, overlaps, width: bounds.width, scrollWidth: el.scrollWidth, clientWidth: el.clientWidth }
    })
    expect(layout.width).toBeLessThanOrEqual(width)
    expect(layout.scrollWidth).toBeLessThanOrEqual(layout.clientWidth + 1)
    expect(layout.overflow).toEqual([])
    expect(layout.overlaps).toBe(0)
    if (width < 768) {
      expect(layout.columns[1].top).toBeGreaterThan(layout.columns[0].top)
    } else {
      expect(layout.columns[1].top).toBe(layout.columns[0].top)
      expect(layout.columns[1].left).toBeGreaterThan(layout.columns[0].left)
    }

    // The formerly clipped name and address inputs remain editable.
    await dialog.locator('[data-test="tag-name"]').fill('小屏测点')
    await dialog.locator('[data-test="tag-address"] input').fill('42')
    await expect(dialog.locator('[data-test="tag-name"]')).toHaveValue('小屏测点')
    await expect(dialog.locator('[data-test="tag-address"] input')).toHaveValue('42')
    await page.screenshot({ path: test.info().outputPath(`tag-editor-${width}.png`), fullPage: true })
  })
}

import { test, expect, type APIRequestContext, type Page } from '@playwright/test'

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

function node(id: string, key: string, text: string) {
  return {
    id, type: 'template', x: 40, y: 40, width: 160, height: 80, rotation: 0,
    text, color: '#7394b8', activeColor: '#34d399', unit: '', decimals: 1,
    min: 0, max: 100, activeThreshold: 0.5, tagId: null, templateKey: key, bindings: [],
    action: { type: 'none', screenId: null, tagId: null, value: 1 }
  }
}

async function saveScreen(request: APIRequestContext, headers: Record<string, string>, name: string, document: unknown) {
  const created = await request.post('/api/screens', { headers, data: { name } })
  expect(created.status()).toBe(201)
  const screen = await created.json()
  const saved = await request.put(`/api/screens/${screen.id}`, { headers, data: { name, revision: screen.revision, document } })
  expect(saved.ok(), await saved.text()).toBeTruthy()
  return screen.id as number
}

test('designer blocks stay on the page when a template cannot be saved or synced', async ({ page, request }) => {
  test.setTimeout(60000)
  const session = await login(request)
  const suffix = `${Date.now()}`
  const library = await request.post('/api/hmi/library', {
    headers: session.headers,
    data: {
      key: `face${suffix}`, name: `面板${suffix}`, category: '自定义', kind: 'template', width: 160, height: 80, placeholders: [],
      document: { schemaVersion: 1, width: 320, height: 200, background: '#101b2d', nodes: [] }
    }
  })
  expect(library.ok(), await library.text()).toBeTruthy()
  const document = { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [] as unknown[] }
  const nestedId = await saveScreen(request, session.headers, `嵌套${suffix}`, {
    ...document, nodes: [node('nested-1', `face${suffix}`, `实例${suffix}`)]
  })
  const missingId = await saveScreen(request, session.headers, `缺失${suffix}`, {
    ...document, nodes: [node('missing-1', `gone${suffix}`, `缺失${suffix}`)]
  })
  await sign(page, session)
  await page.goto(`/config/screens/${nestedId}`)
  await page.getByRole('button', { name: `实例${suffix}` }).click()
  await page.locator('[data-test="save-as-template"]').click()
  await expect(page.locator('[data-test="designer-guard-error"]')).toContainText('模板实例不能再嵌套进模板')
  await expect(page.getByRole('dialog', { name: '存为模板' })).not.toBeVisible()

  await page.goto(`/config/screens/${missingId}`)
  await page.getByRole('button', { name: `缺失${suffix}` }).click()
  await page.getByRole('button', { name: '同步占位符', exact: true }).click()
  await expect(page.locator('[data-test="designer-guard-error"]')).toContainText('模板不存在')
})

test('object upgrade blocks stay on the page', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await login(request)
  const suffix = `${Date.now()}`.slice(-6)
  const device = await request.post('/api/devices', {
    headers: session.headers,
    data: {
      name: `PLC-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1,
      byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false, areaId: 1
    }
  })
  expect(device.ok(), await device.text()).toBeTruthy()
  const deviceId = (await device.json()).id as number
  const pump = await (await request.get('/api/object-templates/examples/pump', { headers: session.headers })).json()
  const names = [`甲${suffix}`, `乙${suffix}`]
  for (const [index, name] of names.entries()) {
    const created = await request.post('/api/object-templates', {
      headers: session.headers, data: { name: `模板${name}`, revision: 0, document: pump }
    })
    expect(created.status(), await created.text()).toBe(201)
    const template = await created.json()
    const published = await request.post(`/api/object-templates/${template.id}/publish`, {
      headers: session.headers, data: { revision: template.revision }
    })
    expect(published.ok(), await published.text()).toBeTruthy()
    const applied = await request.post(`/api/object-templates/${template.id}/instantiate`, {
      headers: session.headers,
      data: { version: 1, instances: [{ name, deviceId, baseAddress: 100 + index * 200 }] }
    })
    expect(applied.ok(), await applied.text()).toBeTruthy()
  }
  await sign(page, session)
  await page.goto('/config/object-templates')
  await page.getByRole('tab', { name: '对象实例' }).click()
  for (const name of names) await page.getByRole('row', { name }).locator('.el-checkbox').click()
  await page.getByRole('button', { name: '升级选中对象' }).click()
  const error = page.locator('[data-test="object-action-error"]')
  await expect(error).toContainText('请每次选择同一模板的对象')
  await expect(page.getByRole('dialog', { name: '预览模板升级' })).not.toBeVisible()
  await page.getByRole('row', { name: names[1] }).locator('.el-checkbox').click()
  await page.getByRole('button', { name: '升级选中对象' }).click()
  await expect(error).toContainText('选中对象已应用最新版本')
  await expect(page.getByRole('dialog', { name: '预览模板升级' })).not.toBeVisible()
})

test('a seventh console faceplate stays closed and explains the limit', async ({ page, request }) => {
  test.setTimeout(90000)
  const session = await login(request)
  const suffix = `${Date.now()}`.slice(-6)
  const headers = session.headers
  async function screen(name: string, nodes: object[]) {
    const created = await request.post('/api/screens', { headers, data: { name } })
    expect(created.status()).toBe(201)
    const draft = await created.json()
    const saved = await request.put(`/api/screens/${draft.id}`, {
      headers,
      data: { name, revision: draft.revision, document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes } }
    })
    expect(saved.ok(), await saved.text()).toBeTruthy()
    const published = await request.post(`/api/screens/${draft.id}/publish`, { headers, data: { revision: (await saved.json()).revision } })
    expect(published.ok(), await published.text()).toBeTruthy()
    return draft.id as number
  }
  const button = (id: string, text: string, screenId: number) => ({
    id, type: 'button', x: 40, y: 40, width: 180, height: 48, rotation: 0, text, color: '#7394b8', activeColor: '#34d399',
    unit: '', decimals: 1, min: 0, max: 100, activeThreshold: 0.5, tagId: null,
    action: { type: 'popup', screenId, tagId: null, value: 0 }
  })
  const face = await screen(`面板${suffix}`, [])
  const faceDoc = await request.get(`/api/screens/${face}?draft=true`, { headers })
  expect(faceDoc.ok()).toBeTruthy()
  const current = await faceDoc.json()
  const updated = await request.put(`/api/screens/${face}`, {
    headers,
    data: {
      name: current.name, revision: current.revision,
      document: { schemaVersion: 1, width: 1200, height: 720, background: '#101b2d', nodes: [button('again', '再开', face)] }
    }
  })
  expect(updated.ok(), await updated.text()).toBeTruthy()
  const republished = await request.post(`/api/screens/${face}/publish`, { headers, data: { revision: (await updated.json()).revision } })
  expect(republished.ok(), await republished.text()).toBeTruthy()
  const overview = await screen(`总览${suffix}`, [button('pop', '弹面板', face)])
  const layout = await request.post('/api/hmi/layouts', {
    headers,
    data: {
      name: `中控${suffix}`,
      document: {
        monitors: [{
          name: '主屏', columns: 12, rows: 12,
          regions: [{ id: 'main', kind: 'screen', screenId: overview, row: 1, col: 1, rowSpan: 12, colSpan: 12 }]
        }]
      }
    }
  })
  expect(layout.ok(), await layout.text()).toBeTruthy()
  const createdLayout = await layout.json()
  const publication = await request.post(`/api/hmi/layouts/${createdLayout.id}/publish`, { headers, data: { revision: createdLayout.revision, note: '客户端保护验收' } })
  expect(publication.ok(), await publication.text()).toBeTruthy()
  await sign(page, session)
  await page.goto(`/console/${createdLayout.id}`)
  await page.locator('[data-node-id="pop"]').click()
  const plates = page.locator('[data-test="faceplate"]')
  await expect(plates).toHaveCount(1)
  for (let i = 0; i < 5; i++) {
    await plates.last().locator('[data-node-id="again"]').click()
    await expect(plates).toHaveCount(i + 2)
  }
  await plates.last().locator('[data-node-id="again"]').click()
  await expect(plates).toHaveCount(6)
  await expect(page.locator('[data-test="console-client-error"]')).toContainText('最多同时打开 6 个面板窗口')
})

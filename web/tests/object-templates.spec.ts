import { test, expect, type APIRequestContext } from './fixtures'
import type { ObjectTemplateDocument } from '../src/api/objectTemplates'

async function setup(request: APIRequestContext) {
  const auth = await (await request.post('/api/auth/login', { data: { userName: 'admin', password: 'admin123' } })).json()
  const headers = { Authorization: `Bearer ${auth.token}` }, suffix = `${Date.now()}-${Math.floor(Math.random() * 10000)}`
  const deviceResponse = await request.post('/api/devices', { headers, data: { name: `PLC-${suffix}`, protocol: 'ModbusTcp', host: '127.0.0.1', port: 1502, stationNumber: 1, byteOrder: 'ABCD', pollIntervalMs: 1000, timeoutMs: 1000, enabled: false } })
  expect(deviceResponse.ok()).toBeTruthy(); const device = await deviceResponse.json()
  const document = await (await request.get('/api/object-templates/examples/pump', { headers })).json() as ObjectTemplateDocument
  const templateResponse = await request.post('/api/object-templates', { headers, data: { name: `模板-${suffix}`, revision: 0, document } })
  expect(templateResponse.status()).toBe(201); const template = await templateResponse.json()
  const publish = await request.post(`/api/object-templates/${template.id}/publish`, { headers, data: { revision: template.revision } })
  expect(publish.ok()).toBeTruthy(); const published = await publish.json()
  return { ...auth, headers, suffix, device, document, template, published }
}
async function publishNext(request: APIRequestContext, fixture: Awaited<ReturnType<typeof setup>>, document: ObjectTemplateDocument) {
  const current = await (await request.get(`/api/object-templates/${fixture.template.id}`, { headers: fixture.headers })).json()
  const save = await request.put(`/api/object-templates/${fixture.template.id}`, { headers: fixture.headers, data: { name: current.name, revision: current.revision, document } })
  expect(save.ok()).toBeTruthy(); const saved = await save.json()
  const publish = await request.post(`/api/object-templates/${fixture.template.id}/publish`, { headers: fixture.headers, data: { revision: saved.revision } })
  expect(publish.ok()).toBeTruthy(); return publish.json()
}

test('published template snapshots remain immutable; stale edits and non-admin access are rejected', async ({ request }) => {
  const fixture = await setup(request), { headers, template, document } = fixture
  document.tags[1].properties.maxValue = 80
  await publishNext(request, fixture, document)
  const original = await (await request.get(`/api/object-templates/${template.id}/versions/1`, { headers })).json()
  expect(original.document.tags[1].properties.maxValue).toBe(50)
  expect((await request.put(`/api/object-templates/${template.id}`, { headers, data: { name: '过期', revision: 1, document } })).status()).toBe(409)
  const user = await (await request.post('/api/users', { headers, data: { userName: `tv${fixture.suffix}`, password: 'test-password-123', displayName: '查看员', role: 'Viewer', enabled: true } })).json()
  const auth = await (await request.post('/api/auth/login', { data: { userName: user.userName, password: 'test-password-123' } })).json()
  expect((await request.get('/api/object-templates', { headers: { Authorization: `Bearer ${auth.token}` } })).status()).toBe(403)
})

test('batch preview and apply are atomic and resolve all generated bindings', async ({ request }) => {
  const f = await setup(request), { headers, template, device } = f
  const before = await (await request.get('/api/object-instances', { headers })).json()
  const invalid = { version: 1, instances: [{ name: 'valid', deviceId: device.id, baseAddress: 0 }, { name: 'overflow', deviceId: device.id, baseAddress: 65535 }] }
  const preview = await (await request.post(`/api/object-templates/${template.id}/instantiate/preview`, { headers, data: invalid })).json()
  expect(preview.canApply).toBe(false)
  expect((await request.post(`/api/object-templates/${template.id}/instantiate`, { headers, data: invalid })).status()).toBe(400)
  expect((await request.post('/api/object-instances/upgrade', { headers, data: { items: [null] } })).status()).toBe(400)
  expect((await (await request.get('/api/object-instances', { headers })).json()).length).toBe(before.length)
  const valid = { version: 1, instances: [{ name: 'P01', deviceId: device.id, baseAddress: 100 }, { name: 'P02', deviceId: device.id, baseAddress: 200 }] }
  const apply = await request.post(`/api/object-templates/${template.id}/instantiate`, { headers, data: valid }); expect(apply.ok()).toBeTruthy()
  const objects = await apply.json(); expect(objects).toHaveLength(2)
  const object = await (await request.get(`/api/object-instances/${objects[0].id}`, { headers })).json()
  expect(object.baseline.tags.frequency.address).toBe(100); expect(object.baseline.tags.fault.address).toBe(102)
  const panel = await (await request.get(`/api/screens/${object.screenId}?draft=true`, { headers })).json()
  expect(panel.document.nodes.find((n: { id: string }) => n.id === 'start').action.tagId).toBe(object.baseline.tags.run.id)
  expect((await request.get(`/api/screens/${object.screenId}`, { headers })).status()).toBe(404)
  expect((await request.delete(`/api/screens/${object.screenId}?revision=${panel.revision}`, { headers })).status()).toBe(400)
  expect((await request.delete(`/api/devices/${device.id}`, { headers })).status()).toBe(400)
})

test('upgrade requires conflict resolution and rejects configuration changed after preview', async ({ request }) => {
  const f = await setup(request), { headers, template, device, document } = f
  const apply = await request.post(`/api/object-templates/${template.id}/instantiate`, { headers, data: { version: 1, instances: [{ name: 'conflict', deviceId: device.id, baseAddress: 10 }] } }); expect(apply.ok()).toBeTruthy()
  const [created] = await apply.json(), instance = await (await request.get(`/api/object-instances/${created.id}`, { headers })).json()
  const tag = await (await request.get(`/api/tags/${instance.baseline.tags.frequency.id}`, { headers })).json()
  expect((await request.put(`/api/tags/${tag.id}`, { headers, data: { ...tag, maxValue: 60 } })).ok()).toBeTruthy()
  document.tags[1].properties.maxValue = 80; document.tags[1].properties.description = '新版描述'; await publishNext(request, f, document)
  let preview = await (await request.post(`/api/object-instances/${instance.id}/upgrade/preview`, { headers, data: { version: 2, revision: instance.revision } })).json()
  expect(preview.canApply).toBe(false); expect(preview.conflicts.some((c: { key: string }) => c.key === 'tag.frequency.MaxValue')).toBe(true)
  const resolutions = { 'tag.frequency.MaxValue': 'keepLocal' }
  preview = await (await request.post(`/api/object-instances/${instance.id}/upgrade/preview`, { headers, data: { version: 2, revision: instance.revision, resolutions } })).json()
  expect(preview.canApply).toBe(true)
  await request.put(`/api/tags/${tag.id}`, { headers, data: { ...tag, maxValue: 60, unit: 'rpm' } })
  expect((await request.post('/api/object-instances/upgrade', { headers, data: { items: [{ id: instance.id, version: 2, revision: instance.revision, expectedState: preview.state, resolutions }] } })).status()).toBe(409)
  preview = await (await request.post(`/api/object-instances/${instance.id}/upgrade/preview`, { headers, data: { version: 2, revision: instance.revision, resolutions } })).json()
  const result = await request.post('/api/object-instances/upgrade', { headers, data: { items: [{ id: instance.id, version: 2, revision: instance.revision, expectedState: preview.state, resolutions }] } }); expect(result.ok()).toBeTruthy()
  const final = await (await request.get(`/api/tags/${tag.id}`, { headers })).json(); expect(final.maxValue).toBe(60); expect(final.unit).toBe('rpm'); expect(final.description).toBe('新版描述')
  expect((await request.delete(`/api/object-instances/${instance.id}?revision=2`, { headers })).status()).toBe(204)
  expect((await request.get(`/api/tags/${tag.id}`, { headers })).status()).toBe(200)
})

test('template editor uses the same data type labels as the tag list', async ({ page, request }) => {
  const f = await setup(request)
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, f)
  await page.goto(`/config/object-templates/${f.template.id}`)
  const tags = page.locator('.el-table').first()
  await expect(tags.getByText('布尔', { exact: true }).first()).toBeVisible()
  await expect(tags.getByText('浮点', { exact: true }).first()).toBeVisible()
  await expect(tags.getByText('Bool', { exact: true })).toHaveCount(0)
  await expect(tags.getByText('Float', { exact: true })).toHaveCount(0)
  await page.getByRole('tab', { name: '发布历史', exact: true }).click()
  await page.getByRole('button', { name: '查看版本', exact: true }).click()
  const history = page.locator('[data-test="template-version-tags"]')
  await expect(history.getByText('布尔', { exact: true })).toBeVisible()
  await expect(history.getByText('浮点', { exact: true })).toBeVisible()
  await expect(history.getByText('UInt16', { exact: true })).toBeVisible()
})

test('engineer edits a panel template, creates a batch and resolves an upgrade in the UI', async ({ page, request }) => {
  const f = await setup(request), { headers, template, device } = f
  const name = `面板模板-${f.suffix}`
  await page.addInitScript(({ token, user }) => { localStorage.setItem('scada_token', token); localStorage.setItem('scada_user', JSON.stringify(user)) }, f)
  const errors: string[] = []; page.on('pageerror', e => errors.push(e.message))
  await page.goto(`/config/object-templates/${template.id}`)
  await page.getByRole('textbox', { name: '模板名称', exact: true }).fill(name)
  await page.getByRole('tab', { name: '面板模板', exact: true }).click()
  await page.locator('[data-node-id="title"]').click()
  await page.getByRole('textbox', { name: '图元文字' }).fill('标准泵面板'); await page.keyboard.press('Control+s')
  await expect(page.getByText('模板草稿已保存')).toBeVisible()
  await page.getByRole('button', { name: '发布新版本', exact: true }).click(); await expect(page.getByText('模板 v2 已发布')).toBeVisible()
  await page.screenshot({ path: 'test-results/object-template-editor.png', fullPage: true })
  const definition = (await (await request.get(`/api/object-templates/${template.id}`, { headers })).json()).document as ObjectTemplateDocument
  expect(definition.panel.nodes.find(n => n.id === 'title')?.text).toBe('标准泵面板')
  expect(definition.panel.nodes.find(n => n.id === 'pump')?.tagId).toBeNull(); expect(definition.bindings.pump).toBe('run')
  await page.getByRole('button', { name: '模板列表', exact: true }).click()
  await page.locator('.template-card').filter({ hasText: name }).getByRole('button', { name: '批量生成对象' }).click()
  await page.getByRole('spinbutton', { name: '起始基址' }).fill('65535')
  await page.getByRole('spinbutton', { name: '起始基址' }).blur()
  await expect(page.locator('[data-test="object-address-error"]')).toContainText('65535')
  await expect(page.getByRole('button', { name: '预览生成', exact: true })).toBeDisabled()
  await page.getByRole('spinbutton', { name: '起始基址' }).fill('0')
  await page.getByRole('spinbutton', { name: '起始基址' }).blur()
  await expect(page.getByRole('button', { name: '预览生成', exact: true })).toBeEnabled()
  await page.getByRole('combobox', { name: '实例采集设备' }).click(); await page.getByRole('option', { name: device.name, exact: true }).click()
  const prefix = `UI${Date.now()}`
  await page.getByRole('textbox', { name: '对象名称前缀' }).fill(prefix)
  await page.getByRole('spinbutton', { name: '对象数量', exact: true }).fill('2'); await page.getByRole('spinbutton', { name: '对象数量', exact: true }).blur()
  await page.getByRole('button', { name: '预览生成', exact: true }).click(); await expect(page.getByText('可生成', { exact: true })).toHaveCount(2)
  await page.getByRole('button', { name: '确认生成', exact: true }).click(); await expect(page.getByText('已生成 2 个对象，设备面板待检查发布')).toBeVisible()
  const all = await (await request.get('/api/object-instances', { headers })).json(), object = all.find((i: { name: string }) => i.name === prefix + '01')
  const detail = await (await request.get(`/api/object-instances/${object.id}`, { headers })).json(), tag = await (await request.get(`/api/tags/${detail.baseline.tags.frequency.id}`, { headers })).json()
  await request.put(`/api/tags/${tag.id}`, { headers, data: { ...tag, maxValue: 60 } })
  definition.tags[1].properties.maxValue = 80; await publishNext(request, f, definition)
  await page.reload(); await page.getByRole('tab', { name: '对象实例', exact: true }).click()
  await page.getByRole('row').filter({ hasText: prefix + '01' }).getByRole('button', { name: '预览升级', exact: true }).click()
  await page.getByRole('dialog').getByRole('button', { name: '预览升级', exact: true }).click()
  await expect(page.getByText('测点 frequency · 量程上限', { exact: true })).toBeVisible()
  await page.screenshot({ path: 'test-results/object-template-conflicts.png', fullPage: true })
  await page.getByRole('combobox', { name: '冲突处理方式' }).press('ArrowDown'); await page.getByRole('option', { name: '保留现场（移除时解除关联）', exact: true }).click()
  await page.getByRole('button', { name: '重新预览', exact: true }).last().click()
  await expect(page.getByRole('button', { name: '确认升级', exact: true })).toBeEnabled()
  await page.getByRole('button', { name: '确认升级', exact: true }).click(); await expect(page.getByText('对象模板升级完成，面板修改已保存为草稿')).toBeVisible()
  const finalTag = await (await request.get(`/api/tags/${tag.id}`, { headers })).json(); expect(finalTag.maxValue).toBe(60)
  expect(errors).toEqual([])
})

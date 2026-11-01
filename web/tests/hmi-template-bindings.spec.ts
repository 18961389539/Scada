import { test as pureTest } from '@playwright/test'
import { test, expect, type Page } from './fixtures'
import type { LibraryInput } from '../src/api/hmiLibrary'
import type { LibraryItem, ScreenDocument, ScreenNode } from '../src/screens/model'
import { TemplateBindingError, templateFromEditor, templatePlaceholderInUse, templateToEditor, type TemplatePlaceholder } from '../src/screens/templateBindings'

const placeholders: TemplatePlaceholder[] = [
  { key: 'unused', tagId: -17 }, { key: 'speed', tagId: -21 }, { key: 'alarm', tagId: -43 },
  { key: 'start', tagId: -60 }, { key: 'stop', tagId: -87 }
]
const node = (id: string, fields: Partial<ScreenNode> = {}): ScreenNode => ({
  id, type: 'value', x: 20, y: 20, width: 140, height: 80, rotation: 0, text: id, color: '#7394b8', activeColor: '#34d399',
  unit: '', decimals: 1, min: 0, max: 100, activeThreshold: .5, tagId: null, alarmTagId: null,
  action: { type: 'none', screenId: null, tagId: null, value: 1 }, ...fields
})
const documentWith = (nodes: ScreenNode[]): ScreenDocument => ({ schemaVersion: 1, width: 800, height: 600, background: '#101b2d', nodes })
function templateDocument(): ScreenDocument {
  return documentWith([
    node('panel', { type: 'equipment-panel', width: 320, height: 280, bind: 'speed', alarmBind: 'alarm',
      commands: [{ key: 'start-command', label: '启动泵', tagId: null, bind: 'start', value: 1 }, { key: 'stop-command', label: '停止泵', tagId: null, bind: 'stop', value: 0 }],
      states: [{ value: 0, label: '停止', color: '#7394b8' }, { value: 1, label: '运行', color: '#34d399' }] }),
    node('input', { type: 'input', x: 400, bind: 'speed', alarmBind: 'alarm', unit: 'Hz', min: -10, max: 50, decimals: 2,
      action: { type: 'command', screenId: null, tagId: null, bind: 'start', value: -7 } }),
    node('navigate', { type: 'button', x: 400, y: 150, action: { type: 'popup', screenId: 7, tagId: null, value: 1 } })
  ])
}

pureTest('@pure template binding round-trip preserves panel command metadata, input, alarm and action bindings', () => {
  const original = templateDocument(), before = structuredClone(original)
  const editor = templateToEditor(original, placeholders)
  expect(editor.nodes[0]).toMatchObject({ tagId: -21, alarmTagId: -43, bind: null, alarmBind: null,
    commands: [{ key: 'start-command', tagId: -60, bind: null }, { key: 'stop-command', tagId: -87, bind: null }] })
  expect(editor.nodes[1].action).toMatchObject({ tagId: -60, bind: null, value: -7 })
  expect(templateFromEditor(editor, placeholders)).toEqual(original)
  expect(original).toEqual(before)
})

pureTest('@pure deleting an earlier unused placeholder and adding a new one never retargets later bindings', () => {
  const original = templateDocument(), editor = templateToEditor(original, placeholders)
  const remaining = [...placeholders.filter(placeholder => placeholder.key !== 'unused'), { key: 'newPoint', tagId: -101 }]
  expect(templatePlaceholderInUse(editor, placeholders[0])).toBe(false)
  expect(templateFromEditor(editor, remaining)).toEqual(original)
  const command = editor.nodes[0].commands?.[0]
  if (!command) throw new Error('Missing fixture command')
  command.tagId = -87
  expect(templateFromEditor(editor, remaining).nodes[0].commands?.[0]).toEqual({ key: 'start-command', label: '启动泵', tagId: null, bind: 'stop', value: 1 })
})

const usageCases: [string, Partial<ScreenNode>][] = [
  ['input', { tagId: -21 }], ['alarm', { alarmTagId: -21 }],
  ['action', { action: { type: 'command', screenId: null, tagId: -21, value: 9 } }],
  ['panel command', { commands: [{ key: 'run', label: 'Run', tagId: -21, value: 1 }] }],
  ['chart tags', { tags: [-21] }], ['nested bindings', { bindings: [{ key: 'inner', tagId: -21 }] }],
  ['animation', { animation: { type: 'rotate', source: 'value', tagId: -21, from: 0, to: 100, outFrom: 0, outTo: 360 } }],
  ['named input', { bind: 'speed' }], ['named alarm', { alarmBind: 'speed' }],
  ['named action', { action: { type: 'command', screenId: null, tagId: null, bind: 'speed', value: 9 } }],
  ['named panel command', { commands: [{ key: 'run', label: 'Run', tagId: null, bind: 'speed', value: 1 }] }]
]
for (const [name, fields] of usageCases) {
  pureTest(`@pure placeholder use is detected in ${name}`, () => {
    expect(templatePlaceholderInUse(documentWith([node('used', fields)]), placeholders[1])).toBe(true)
  })
}

for (const [name, fields] of usageCases.slice(0, 7)) {
  pureTest(`@pure stale or unsupported pseudo IDs in ${name} reject save without mutating the draft`, () => {
    const editor = documentWith([node('stale', fields)]), before = structuredClone(editor)
    expect(() => templateFromEditor(editor, placeholders.filter(placeholder => placeholder.key !== 'speed'))).toThrow(TemplateBindingError)
    expect(editor).toEqual(before)
  })
}

pureTest('@pure unknown named placeholders reject opening instead of silently clearing bindings', () => {
  const original = templateDocument()
  expect(() => templateToEditor(original, placeholders.filter(placeholder => placeholder.key !== 'start'))).toThrow(TemplateBindingError)
  expect(original.nodes[0].commands?.[0].bind).toBe('start')
})

pureTest('@pure unbound commands and positive auxiliary tag IDs remain intact without editor IDs', () => {
  const original = documentWith([node('unbound', { tags: [71], bindings: [{ key: 'existing', tagId: 72 }],
    animation: { type: 'rotate', source: 'value', tagId: 73, from: 0, to: 100, outFrom: 0, outTo: 360 },
    commands: [{ key: 'unbound', label: '未绑定', tagId: null, value: -2 }] })])
  expect(templateFromEditor(templateToEditor(original, placeholders), placeholders)).toEqual(original)
})

async function fixture(page: Page, options: { beforeFirstSave?: () => Promise<void>; failFirstSave?: boolean } = {}) {
  await page.addInitScript(() => {
    localStorage.setItem('scada_token', 'template-bindings-test')
    localStorage.setItem('scada_user', JSON.stringify({ id: 1, userName: 'admin', displayName: '管理员', role: 'Admin', access: { global: ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System'], areas: [] } }))
    localStorage.setItem('scada_locale', 'zh-CN')
  })
  const item: LibraryItem = { id: 901, key: 'binding-panel', name: '绑定测试面板', category: '自定义', kind: 'template', svg: null,
    width: 800, height: 600, revision: 3, updatedTs: 1, updatedBy: 'admin', placeholders: placeholders.map(placeholder => placeholder.key), document: templateDocument() }
  const state = { item, saved: [] as LibraryInput[] }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/hmi/library') return route.fulfill({ json: [state.item] })
    if (path === '/api/hmi/library/901' && route.request().method() === 'PUT') {
      const body = route.request().postDataJSON() as LibraryInput
      state.saved.push(body)
      if (state.saved.length === 1) {
        await options.beforeFirstSave?.()
        if (options.failFirstSave) return route.fulfill({ status: 500, json: { message: '模板保存暂不可用' } })
      }
      state.item = { ...state.item, ...body, revision: state.item.revision + 1 }
      return route.fulfill({ json: { item: state.item, usedBy: ['泵站画面'] } })
    }
    if (path === '/api/auth/site-info') return route.fulfill({ json: { siteName: '测试项目', demoMode: false } })
    if (path === '/api/license') return route.fulfill({ json: { status: 'Active', blocked: false } })
    if (path === '/api/health') return route.fulfill({ json: { status: 'ok' } })
    if (path === '/api/alarms/summary') return route.fulfill({ json: { unack: 0, acked: 0, rtnUnack: 0, flood: false } })
    return route.fulfill({ json: [] })
  })
  return state
}
async function openEditor(page: Page): Promise<void> {
  await page.goto('/config/hmi-library')
  await page.getByRole('tab', { name: /用户图库/ }).click()
  await page.locator('[data-test="library-edit-binding-panel"]').click()
  await expect(page.locator('[data-test="library-template-dialog"] .designer')).toBeVisible()
  await expect(page.locator('[data-test="library-template-dialog"] .el-loading-mask:visible')).toHaveCount(0)
}
function expectNoPseudoIds(document: ScreenDocument | null | undefined): void {
  expect(document).toBeTruthy()
  for (const node of document?.nodes ?? []) {
    const ids = [node.tagId, node.alarmTagId, node.action.tagId, ...(node.commands ?? []).map(command => command.tagId),
      ...(node.tags ?? []), ...(node.bindings ?? []).map(binding => binding.tagId), node.animation?.tagId]
    for (const id of ids) if (id != null) expect(id).toBeGreaterThan(0)
  }
}

test('template binding editor keeps commands and other bindings after deleting an unused earlier placeholder', async ({ page }) => {
  const state = await fixture(page)
  await openEditor(page)
  const dialog = page.locator('[data-test="library-template-dialog"]')
  await dialog.locator('[data-layer-id="panel"]').click()
  const commands = dialog.locator('[data-test="panel-command-row"]')
  await expect(commands.nth(0).locator('.el-select')).toContainText('start')
  await expect(commands.nth(1).locator('.el-select')).toContainText('stop')
  await dialog.locator('[data-test="placeholder-unused"] .el-tag__close').click()
  await expect(dialog.locator('[data-test="placeholder-unused"]')).toHaveCount(0)
  await dialog.getByRole('textbox', { name: '新占位符', exact: true }).fill('newPoint')
  await dialog.getByRole('button', { name: '添加', exact: true }).click()
  await expect(commands.nth(0).locator('.el-select')).toContainText('start')
  await commands.nth(0).getByRole('textbox', { name: '操作名称', exact: true }).fill('启动给水泵')
  await commands.nth(0).getByRole('spinbutton', { name: '命令值', exact: true }).fill('5')
  await commands.nth(0).getByRole('spinbutton', { name: '命令值', exact: true }).press('Tab')
  await commands.nth(0).getByRole('combobox', { name: '控制测点', exact: true }).click()
  await page.getByRole('listbox', { name: '控制测点', exact: true }).getByRole('option', { name: 'stop', exact: true }).click()
  await dialog.locator('[data-test="library-template-save"]').click()
  await expect(dialog).not.toBeVisible()
  expect(state.saved).toHaveLength(1)
  const saved = state.saved[0]
  expect(saved.placeholders).toEqual(['speed', 'alarm', 'start', 'stop', 'newPoint'])
  expect(saved.document?.nodes[0]).toMatchObject({ bind: 'speed', alarmBind: 'alarm', commands: [
    { key: 'start-command', label: '启动给水泵', value: 5, bind: 'stop', tagId: null },
    { key: 'stop-command', label: '停止泵', value: 0, bind: 'stop', tagId: null }
  ] })
  expect(saved.document?.nodes[1]).toMatchObject({ bind: 'speed', alarmBind: 'alarm', unit: 'Hz', min: -10, max: 50, decimals: 2,
    action: { type: 'command', bind: 'start', value: -7, tagId: null } })
  expect(saved.document?.nodes[2].action).toEqual({ type: 'popup', screenId: 7, tagId: null, value: 1 })
  expectNoPseudoIds(saved.document)
  await expect(page.getByText('模板已保存。以下画面需重新发布后才会更新运行版本：泵站画面', { exact: true })).toBeVisible()
  await expect(page.locator('[data-test="library-items"]')).toContainText('r4')
  await page.locator('[data-test="library-edit-binding-panel"]').click()
  await dialog.locator('[data-layer-id="panel"]').click()
  await expect(commands.nth(0).locator('.el-select')).toContainText('stop')
  await expect(commands.nth(0).getByRole('textbox', { name: '操作名称', exact: true })).toHaveValue('启动给水泵')
})

test('template binding editor refuses deletion of used input, alarm and panel command placeholders', async ({ page }) => {
  const state = await fixture(page)
  await openEditor(page)
  const dialog = page.locator('[data-test="library-template-dialog"]')
  for (const key of ['speed', 'alarm', 'stop']) {
    await dialog.locator(`[data-test="placeholder-${key}"] .el-tag__close`).click()
    await expect(dialog.locator(`[data-test="placeholder-${key}"]`)).toBeVisible()
    await expect(dialog.locator('[data-test="library-template-error"]')).toContainText(`占位符“${key}”仍被`)
  }
  await dialog.locator('[data-test="library-template-save"]').click()
  await expect(dialog).not.toBeVisible()
  expect(state.saved[0].placeholders).toEqual(placeholders.map(placeholder => placeholder.key))
  expect(state.saved[0].document?.nodes[0].commands?.[1].bind).toBe('stop')
  expectNoPseudoIds(state.saved[0].document)
})

test('template binding editor rejects stale bindings restored by undo rather than retargeting another placeholder', async ({ page }) => {
  const state = await fixture(page)
  await openEditor(page)
  const dialog = page.locator('[data-test="library-template-dialog"]')
  await dialog.locator('[data-layer-id="panel"]').click()
  await dialog.locator('[data-test="panel-command-row"]').nth(1).getByRole('button', { name: '删除', exact: true }).click()
  await dialog.locator('[data-test="placeholder-stop"] .el-tag__close').click()
  await expect(dialog.locator('[data-test="placeholder-stop"]')).toHaveCount(0)
  await dialog.getByRole('textbox', { name: '新占位符', exact: true }).fill('replacement')
  await dialog.getByRole('button', { name: '添加', exact: true }).click()
  await dialog.getByRole('button', { name: '撤销', exact: true }).click()
  await dialog.locator('[data-layer-id="panel"]').click()
  await expect(dialog.locator('[data-test="panel-command-row"]')).toHaveCount(2)
  await dialog.locator('[data-test="library-template-save"]').click()
  await expect(dialog.locator('[data-test="library-template-error"]')).toContainText('无效或不支持占位符，未保存')
  await expect(dialog).toBeVisible()
  expect(state.saved).toHaveLength(0)
})

test('template binding editor blocks duplicate saves and retains edits after an update failure', async ({ page }) => {
  let releaseFirstSave = () => {}
  const firstSave = new Promise<void>(resolve => { releaseFirstSave = resolve })
  const state = await fixture(page, { beforeFirstSave: () => firstSave, failFirstSave: true })
  await openEditor(page)
  const dialog = page.locator('[data-test="library-template-dialog"]')
  await dialog.locator('[data-layer-id="panel"]').click()
  const label = dialog.locator('[data-test="panel-command-row"]').nth(0).getByRole('textbox', { name: '操作名称', exact: true })
  await label.fill('失败后保留的名称'); await label.blur()
  const save = dialog.locator('[data-test="library-template-save"]')
  try {
    await save.evaluate(button => {
      button.dispatchEvent(new MouseEvent('click', { bubbles: true }))
      button.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    })
    await expect.poll(() => state.saved.length).toBe(1)
    await expect(save).toBeDisabled()
    await expect(dialog.getByRole('button', { name: '取消', exact: true })).toBeDisabled()
    await save.dispatchEvent('click')
    await page.keyboard.press('Escape')
    await expect(dialog).toBeVisible()
    expect(state.saved).toHaveLength(1)
  } finally { releaseFirstSave() }
  await expect(dialog.locator('[data-test="library-template-error"]')).toContainText('模板保存暂不可用')
  await expect(save).toBeEnabled()
  await expect(label).toHaveValue('失败后保留的名称')
  await save.click()
  await expect(dialog).not.toBeVisible()
  expect(state.saved).toHaveLength(2)
  expect(state.saved[1]).toEqual(state.saved[0])
  expectNoPseudoIds(state.saved[1].document)
})

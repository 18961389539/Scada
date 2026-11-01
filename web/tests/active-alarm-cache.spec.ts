import { test, expect } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'

interface AlarmCacheHarness {
  api: typeof import('../src/composables/useActiveAlarms')
  mock: { calls: number; ids: number[]; failNext: boolean }
}
declare global { interface Window { alarmCache: AlarmCacheHarness } }

const source = ts.transpileModule(readFileSync(fileURLToPath(new URL('../src/composables/useActiveAlarms.ts', import.meta.url)), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext }
}).outputText

test.beforeEach(async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') })
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'))
  await page.route('http://alarm-cache.test/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<!doctype html><script type="importmap">{"imports":{"vue":"/vue.js"}}</script>' })
    if (path === '/src/composables/useActiveAlarms.js') return route.fulfill({ contentType: 'text/javascript', body: source })
    if (path === '/vue.js') return route.fulfill({ contentType: 'text/javascript', body: 'export const ref = value => ({ value })' })
    if (path === '/src/api') return route.fulfill({ contentType: 'text/javascript', body: `
      export const mock = { calls: 0, ids: [1], failNext: false }
      export const alarmApi = { query: async () => {
        mock.calls++
        if (mock.failNext) { mock.failNext = false; throw new Error('alarm query failed') }
        return mock.ids.map(id => ({ id }))
      } }
    ` })
    return route.abort()
  })
  await page.goto('http://alarm-cache.test/')
  await page.evaluate(async () => {
    const modulePath = '/src/composables/useActiveAlarms.js', dependencyPath = '/src/api'
    const api: AlarmCacheHarness['api'] = await import(modulePath)
    const dependency: { mock: AlarmCacheHarness['mock'] } = await import(dependencyPath)
    window.alarmCache = { api, mock: dependency.mock }
  })
})

test('successful alarms are shared briefly but explicit refresh bypasses the cache', async ({ page }) => {
  const result = await page.evaluate(async () => {
    const { api, mock } = window.alarmCache
    const initial = await api.refreshActiveAlarms(8000)
    mock.ids = [2]
    const cached = await api.refreshActiveAlarms(8000)
    const refreshed = await api.refreshActiveAlarms()
    return { ids: [initial, cached, refreshed].map(items => items.map(item => item.id)), calls: mock.calls }
  })
  expect(result).toEqual({ ids: [[1], [1], [2]], calls: 2 })
})

test('a failed refresh cannot mark old alarm data fresh or hide the next failure', async ({ page }) => {
  const result = await page.evaluate(async () => {
    const { api, mock } = window.alarmCache
    await api.refreshActiveAlarms()
    mock.failNext = true
    const failed = await api.refreshActiveAlarms().then(() => false, () => true)
    mock.failNext = true
    const failedAgain = await api.refreshActiveAlarms(8000).then(() => false, () => true)
    mock.ids = [3]
    const recovered = await api.refreshActiveAlarms(8000)
    return { failed, failedAgain, ids: recovered.map(item => item.id), calls: mock.calls }
  })
  expect(result).toEqual({ failed: true, failedAgain: true, ids: [3], calls: 4 })
})

test('concurrent consumers share one request and the cache expires after a successful read', async ({ page }) => {
  expect(await page.evaluate(async () => {
    const { api, mock } = window.alarmCache
    await Promise.all([api.refreshActiveAlarms(), api.refreshActiveAlarms(8000), api.refreshActiveAlarms()])
    mock.ids = [4]
    return mock.calls
  })).toBe(1)
  await page.clock.runFor(8001)
  expect(await page.evaluate(async () => {
    const { api, mock } = window.alarmCache
    return { ids: (await api.refreshActiveAlarms(8000)).map(item => item.id), calls: mock.calls }
  })).toEqual({ ids: [4], calls: 2 })
})

import { test, expect, type Page } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import ts from 'typescript'
import type { TagSubscription } from '../src/realtime/hub'

interface StubConnection {
  state: string
  startCalls: number
  stopCalls: number
  calls: { method: string; ids: number[] }[]
  subscribed: Set<number>
  finishStart: () => void
  rejectStart: () => void
  finishInvoke: () => void
  close: () => void
  reconnecting: () => void
  reconnected: () => void
  emit: (event: string, payload: unknown) => void
}
interface RecoveryHarness {
  api: typeof import('../src/realtime/hub')
  mock: {
    connections: StubConnection[]
    holdStarts: boolean
    failStarts: number
    holdSubscriptions: boolean
    failSubscriptions: number
  }
  subscriptions: Record<string, TagSubscription>
  pending: Record<string, Promise<void>>
  disposers: Record<string, () => void>
  seen: Record<string, number[]>
  snapshots: number[][]
  closed: number
  alarms: number
  watch: () => void
  subscribe: (name: string, ids: number[]) => Promise<void>
}
declare global {
  interface Window { hubRecovery: RecoveryHarness }
}

const hubJavaScript = ts.transpileModule(readFileSync(fileURLToPath(new URL('../src/realtime/hub.ts', import.meta.url)), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.ESNext }
}).outputText

// Only dependencies are stubbed; each browser imports the real production module in isolation.
const signalRStub = `
export const HubConnectionState = { Disconnected: 'Disconnected', Connecting: 'Connecting', Connected: 'Connected', Reconnecting: 'Reconnecting' }
export const mock = { connections: [], holdStarts: false, failStarts: 0, holdSubscriptions: false, failSubscriptions: 0 }
class Hub {
  state = 'Disconnected'; startCalls = 0; stopCalls = 0; calls = []; subscribed = new Set(); events = {}; pendingInvokes = []
  on(event, handler) { this.events[event] = handler }
  onclose(handler) { this.closed = handler }
  onreconnecting(handler) { this.reconnectingHandler = handler }
  onreconnected(handler) { this.reconnectedHandler = handler }
  emit(event, payload) { this.events[event]?.(payload) }
  start() {
    this.startCalls++; this.state = 'Connecting'
    return new Promise((resolve, reject) => {
      this.finishStart = () => { this.state = 'Connected'; resolve() }
      this.rejectStart = () => { this.state = 'Disconnected'; reject(new Error('start failed')) }
      if (!mock.holdStarts) {
        if (mock.failStarts > 0) { mock.failStarts--; this.rejectStart() }
        else this.finishStart()
      }
    })
  }
  stop() { this.stopCalls++; this.state = 'Disconnected'; this.closed?.(); return Promise.resolve() }
  close() { this.state = 'Disconnected'; this.subscribed.clear(); this.closed?.() }
  reconnecting() { this.state = 'Reconnecting'; this.subscribed.clear(); this.reconnectingHandler?.() }
  reconnected() { this.state = 'Connected'; this.subscribed.clear(); this.reconnectedHandler?.() }
  invoke(method, ids) {
    this.calls.push({ method, ids: [...ids] })
    if (method === 'Subscribe' && mock.failSubscriptions > 0) {
      mock.failSubscriptions--; return Promise.reject(new Error('subscribe failed'))
    }
    return new Promise(resolve => {
      const finish = () => {
        for (const id of ids) method === 'Subscribe' ? this.subscribed.add(id) : this.subscribed.delete(id)
        resolve()
      }
      if (method === 'Subscribe' && mock.holdSubscriptions) this.pendingInvokes.push(finish)
      else finish()
    })
  }
  finishInvoke() { this.pendingInvokes.shift()?.() }
}
export class HubConnectionBuilder {
  withUrl() { return this }
  withAutomaticReconnect() { return this }
  build() { const hub = new Hub(); mock.connections.push(hub); return hub }
}
`

async function state(page: Page) {
  return page.evaluate(() => {
    const h = window.hubRecovery
    return {
      state: h.api.hubState().value, connections: h.mock.connections.length,
      snapshots: h.snapshots, closed: h.closed, alarms: h.alarms,
      calls: h.mock.connections.map(hub => hub.calls), seen: h.seen
    }
  })
}

async function recovered(page: Page, connections: number, snapshots: number) {
  await expect.poll(async () => {
    const current = await state(page)
    return [current.state, current.connections, current.snapshots.length]
  }).toEqual(['connected', connections, snapshots])
}

test.beforeEach(async ({ page }) => {
  await page.clock.install({ time: new Date('2026-01-01T00:00:00Z') })
  await page.clock.pauseAt(new Date('2026-01-01T00:00:01Z'))
  await page.route('http://hub-recovery.test/**', route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/') return route.fulfill({ contentType: 'text/html', body: '<!doctype html><script type="importmap">{"imports":{"@microsoft/signalr":"/signalr.js","vue":"/vue.js"}}</script>' })
    if (path === '/src/realtime/hub.js') return route.fulfill({ contentType: 'text/javascript', body: hubJavaScript })
    if (path === '/signalr.js') return route.fulfill({ contentType: 'text/javascript', body: signalRStub })
    if (path === '/vue.js') return route.fulfill({ contentType: 'text/javascript', body: 'export const ref = value => ({ value })' })
    if (path === '/src/api/http') return route.fulfill({ contentType: 'text/javascript', body: "export const TOKEN_KEY = 'scada_token'" })
    return route.abort()
  })
  await page.goto('http://hub-recovery.test/')
  await page.evaluate(async () => {
    localStorage.setItem('scada_token', 'hub-recovery-test')
    const hubUrl = '/src/realtime/hub.js', stubUrl = '/signalr.js'
    const api = await import(hubUrl), { mock } = await import(stubUrl)
    const h: RecoveryHarness = window.hubRecovery = {
      api, mock, subscriptions: {}, pending: {}, disposers: {}, seen: {}, snapshots: [], closed: 0, alarms: 0,
      watch: () => {
        h.disposers.reconnected = api.onHubReconnected(() => { h.snapshots.push([...mock.connections.at(-1).subscribed]) })
        h.disposers.closed = api.onHubClosed(() => { h.closed++ })
      },
      subscribe: async (name, ids) => {
        h.seen[name] = []
        h.subscriptions[name] = await api.subscribeTags(ids, (value: { tagId: number }) => { h.seen[name].push(value.tagId) })
      }
    }
  })
})

test('terminal close retries once, restores only live tags and notifies snapshots after subscribing', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery; h.watch()
    await Promise.all([h.subscribe('main', [11, 12, 12]), h.subscribe('popup', [12, 13])])
  })
  await recovered(page, 1, 0)
  expect((await state(page)).calls[0]).toEqual([{ method: 'Subscribe', ids: [11, 12, 13] }])
  await page.evaluate(() => {
    const h = window.hubRecovery
    h.subscriptions.main.dispose(); h.subscriptions.main.dispose()
  })
  await expect.poll(async () => (await state(page)).calls[0]).toEqual([
    { method: 'Subscribe', ids: [11, 12, 13] }, { method: 'Unsubscribe', ids: [11] }
  ])
  await page.evaluate(() => {
    const h = window.hubRecovery
    h.mock.connections[0].close(); h.mock.connections[0].close()
  })
  expect(await state(page)).toMatchObject({ state: 'disconnected', closed: 1 })
  await page.clock.runFor(4999)
  expect((await state(page)).connections).toBe(1)
  await page.clock.runFor(1)
  await recovered(page, 2, 1)
  expect((await state(page)).calls[1]).toEqual([{ method: 'Subscribe', ids: [12, 13] }])
  expect((await state(page)).snapshots).toEqual([[12, 13]])
  await page.evaluate(() => {
    window.hubRecovery.mock.connections[1].emit('values', [{ tagId: 12 }])
  })
  expect((await state(page)).seen).toEqual({ main: [], popup: [12] })
  await page.clock.runFor(60000)
  expect((await state(page)).connections).toBe(2)
})

test('automatic reconnect restores current refs, isolates callbacks and waits for subscription acknowledgement', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery
    h.disposers.throwing = h.api.onHubReconnected(() => { throw new Error('consumer failed') })
    h.watch()
    await Promise.all([h.subscribe('main', [21, 22]), h.subscribe('popup', [22])])
    h.mock.connections[0].reconnecting()
    h.subscriptions.main.dispose()
    h.mock.holdSubscriptions = true
    h.mock.connections[0].reconnected()
  })
  expect(await state(page)).toMatchObject({ state: 'reconnecting', snapshots: [] })
  await page.evaluate(() => { window.hubRecovery.mock.connections[0].finishInvoke() })
  await recovered(page, 1, 1)
  expect((await state(page)).calls[0]).toEqual([
    { method: 'Subscribe', ids: [21, 22] }, { method: 'Subscribe', ids: [22] }
  ])
  expect((await state(page)).snapshots).toEqual([[22]])
})

test('failed first starts do not announce recovery, while failed recovery subscriptions remain disconnected and retry', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery; h.mock.failStarts = 2; h.watch()
    await h.subscribe('main', [31])
  })
  expect(await state(page)).toMatchObject({ state: 'disconnected', snapshots: [], connections: 1 })
  await page.clock.runFor(5000)
  expect(await state(page)).toMatchObject({ state: 'disconnected', snapshots: [], connections: 2 })
  await page.clock.runFor(5000)
  await recovered(page, 3, 0)
  await page.evaluate(() => {
    const h = window.hubRecovery; h.mock.failSubscriptions = 1; h.mock.connections[2].close()
  })
  await page.clock.runFor(5000)
  await expect.poll(async () => (await state(page)).state).toBe('disconnected')
  expect(await state(page)).toMatchObject({ snapshots: [], connections: 4 })
  await page.clock.runFor(5000)
  await recovered(page, 5, 1)
  expect((await state(page)).snapshots).toEqual([[31]])
})

for (const mode of ['logout', 'reset'] as const) {
  test(`${mode} cancels terminal retries and obsolete callbacks cannot restart the connection`, async ({ page }) => {
    await page.evaluate(async () => {
      const h = window.hubRecovery; h.watch(); await h.subscribe('main', [41])
      h.mock.connections[0].close()
    })
    await page.evaluate(mode => {
      const h = window.hubRecovery
      if (mode === 'logout') localStorage.removeItem('scada_token')
      else h.api.resetHub()
      h.mock.connections[0].reconnected(); h.mock.connections[0].reconnecting(); h.mock.connections[0].close()
    }, mode)
    await page.clock.runFor(60000)
    expect(await state(page)).toMatchObject({ state: 'disconnected', connections: 1, snapshots: [], closed: 1 })
    if (mode === 'logout') {
      await page.evaluate(async () => {
        const h = window.hubRecovery
        await h.subscribe('logged-out', [42]); h.subscriptions['logged-out'].dispose()
      })
      expect((await state(page)).connections).toBe(1)
    }
    await page.evaluate(async () => {
      const h = window.hubRecovery
      localStorage.setItem('scada_token', 'new-login')
      h.subscriptions.main.dispose()
      await h.subscribe('new-main', [43])
    })
    await recovered(page, 2, 0)
    expect((await state(page)).calls[1]).toEqual([{ method: 'Subscribe', ids: [43] }])
  })
}

for (const settlement of ['resolve', 'reject'] as const) {
  test(`retired pending start ${settlement} cannot mutate or replace a new pending connection`, async ({ page }) => {
    await page.evaluate(() => {
      const h = window.hubRecovery; h.mock.holdStarts = true
      h.pending.old = h.subscribe('old', [51])
    })
    await page.evaluate(() => {
      const h = window.hubRecovery
      localStorage.removeItem('scada_token'); h.api.resetHub()
      localStorage.setItem('scada_token', 'new-login')
      h.pending.current = h.subscribe('current', [52])
    })
    await page.evaluate(async settlement => {
      const h = window.hubRecovery, old = h.mock.connections[0]
      if (settlement === 'resolve') old.finishStart()
      else old.rejectStart()
      await h.pending.old
      h.subscriptions.old.dispose()
      old.reconnected(); old.reconnecting(); old.close()
      h.pending.popup = h.subscribe('popup', [52])
    }, settlement)
    expect(await state(page)).toMatchObject({ state: 'connecting', connections: 2, snapshots: [], closed: 0 })
    await page.evaluate(async () => {
      const h = window.hubRecovery
      h.mock.connections[1].finishStart()
      await Promise.all([h.pending.current, h.pending.popup])
      h.subscriptions.current.dispose()
      h.mock.connections[0].emit('values', [{ tagId: 52 }])
      h.mock.connections[1].emit('values', [{ tagId: 52 }])
    })
    await recovered(page, 2, 0)
    expect((await state(page)).calls[1]).toEqual([{ method: 'Subscribe', ids: [52] }])
    expect((await state(page)).seen).toEqual({ old: [], current: [], popup: [52] })
    await page.clock.runFor(60000)
    expect((await state(page)).connections).toBe(2)
  })
}

test('late recovery subscription completion and stale events cannot pollute a new connection', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery; h.watch()
    h.disposers.alarm = h.api.onHubEvent('alarm', () => { h.alarms++ })
    await h.subscribe('main', [61])
    h.mock.holdSubscriptions = true
    h.mock.connections[0].reconnecting(); h.mock.connections[0].reconnected()
  })
  await page.evaluate(() => {
    const h = window.hubRecovery; h.api.resetHub(); h.mock.holdSubscriptions = false
    h.disposers.newAlarm = h.api.onHubEvent('alarm', () => { h.alarms++ })
  })
  await recovered(page, 2, 0)
  await page.evaluate(() => {
    const h = window.hubRecovery, old = h.mock.connections[0]
    old.finishInvoke(); old.close(); old.reconnected(); old.reconnecting()
    old.emit('alarm', {}); old.emit('values', [{ tagId: 61 }])
    h.mock.connections[1].emit('values', [{ tagId: 61 }])
  })
  await recovered(page, 2, 0)
  expect(await state(page)).toMatchObject({ closed: 0, alarms: 0, seen: { main: [61] } })
  await page.clock.runFor(60000)
  expect((await state(page)).connections).toBe(2)
})

test('disposing the last consumer stops retries, and a newly mounted consumer can start again', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery; await h.subscribe('main', [71])
    h.mock.connections[0].close(); h.subscriptions.main.dispose()
  })
  await page.clock.runFor(60000)
  expect(await state(page)).toMatchObject({ state: 'disconnected', connections: 1 })
  await page.evaluate(async () => { await window.hubRecovery.subscribe('popup', [71]) })
  await recovered(page, 2, 0)
  await page.evaluate(() => { window.hubRecovery.subscriptions.popup.dispose() })
  expect(await state(page)).toMatchObject({ state: 'disconnected', connections: 2 })
  expect(await page.evaluate(() => window.hubRecovery.mock.connections[1].stopCalls)).toBe(1)
})

test('a new consumer starts immediately during retry delay without leaving a second timer', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery; h.watch(); await h.subscribe('main', [81])
    h.mock.connections[0].close()
    await h.subscribe('popup', [81, 82])
  })
  await recovered(page, 2, 1)
  expect((await state(page)).calls[1]).toEqual([{ method: 'Subscribe', ids: [81, 82] }])
  await page.clock.runFor(60000)
  expect((await state(page)).connections).toBe(2)
})

test('references changed during recovery converge before snapshots and shared consumers do not unsubscribe each other', async ({ page }) => {
  await page.evaluate(async () => {
    const h = window.hubRecovery; h.watch()
    await Promise.all([h.subscribe('main', [91, 92]), h.subscribe('popup', [92])])
    h.mock.holdSubscriptions = true
    h.mock.connections[0].reconnecting(); h.mock.connections[0].reconnected()
  })
  await page.evaluate(() => {
    const h = window.hubRecovery
    h.subscriptions.main.dispose()
    h.pending.new = h.subscribe('new', [92, 93])
    h.mock.connections[0].finishInvoke()
  })
  await expect.poll(async () => (await state(page)).calls[0]).toEqual([
    { method: 'Subscribe', ids: [91, 92] }, { method: 'Subscribe', ids: [91, 92] },
    { method: 'Unsubscribe', ids: [91] }, { method: 'Subscribe', ids: [93] }
  ])
  expect((await state(page)).snapshots).toEqual([])
  await page.evaluate(async () => {
    const h = window.hubRecovery; h.mock.connections[0].finishInvoke(); await h.pending.new
  })
  await recovered(page, 1, 1)
  expect((await state(page)).snapshots).toEqual([[92, 93]])
})

test('logout during pending start is stopped even before layout reset', async ({ page }) => {
  await page.evaluate(() => {
    const h = window.hubRecovery; h.mock.holdStarts = true
    h.pending.main = h.subscribe('main', [101])
  })
  await page.evaluate(async () => {
    const h = window.hubRecovery; localStorage.removeItem('scada_token')
    h.mock.connections[0].finishStart(); await h.pending.main
    h.mock.connections[0].reconnected(); h.mock.connections[0].close()
  })
  await page.clock.runFor(60000)
  expect(await state(page)).toMatchObject({ state: 'disconnected', connections: 1, snapshots: [] })
  expect((await state(page)).calls[0]).toEqual([])
  expect(await page.evaluate(() => window.hubRecovery.mock.connections[0].stopCalls)).toBe(1)
})

test('an old repeated event disposer cannot cancel a newly mounted event consumer', async ({ page }) => {
  await page.evaluate(() => {
    const h = window.hubRecovery
    const old = h.api.onHubEvent('alarm', () => {})
    old()
    h.disposers.alarm = h.api.onHubEvent('alarm', () => { h.alarms++ })
    old()
  })
  await recovered(page, 2, 0)
  await page.evaluate(() => { window.hubRecovery.mock.connections[1].emit('alarm', {}) })
  expect((await state(page)).alarms).toBe(1)
})

test('automatic reconnect racing start completion does not stop the current connection', async ({ page }) => {
  await page.evaluate(() => {
    const h = window.hubRecovery; h.mock.holdStarts = true; h.watch()
    h.pending.main = h.subscribe('main', [111])
  })
  await page.evaluate(async () => {
    const h = window.hubRecovery, hub = h.mock.connections[0]
    hub.finishStart(); hub.reconnecting(); await h.pending.main
  })
  expect(await state(page)).toMatchObject({ state: 'reconnecting', connections: 1, snapshots: [] })
  expect(await page.evaluate(() => window.hubRecovery.mock.connections[0].stopCalls)).toBe(0)
  await page.evaluate(() => { window.hubRecovery.mock.connections[0].reconnected() })
  await recovered(page, 1, 0)
  expect((await state(page)).calls[0]).toEqual([{ method: 'Subscribe', ids: [111] }])
  await page.evaluate(() => {
    const hub = window.hubRecovery.mock.connections[0]; hub.reconnecting(); hub.reconnected()
  })
  await recovered(page, 1, 1)
})

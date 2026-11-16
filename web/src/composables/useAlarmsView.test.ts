import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h, nextTick } from 'vue'
import type { AlarmEvent, AuthUser, Tag } from '../types'
import { useAlarmsView } from './useAlarmsView'

const mocks = vi.hoisted(() => ({
  auth: { user: null as AuthUser | null },
  route: { query: {} as Record<string, unknown> },
  router: { replace: vi.fn(async () => {}) },
  queryPage: vi.fn(),
  summary: vi.fn(),
  ack: vi.fn(),
  note: vi.fn(),
  timeline: vi.fn(),
  shelves: vi.fn(),
  shelve: vi.fn(),
  unshelve: vi.fn(),
  devices: vi.fn(),
  tags: vi.fn(),
  annList: vi.fn(),
  annSilence: vi.fn(),
  screenList: vi.fn(),
  screenGet: vi.fn(),
  confirm: vi.fn(),
  prompt: vi.fn(),
  alert: vi.fn(),
  success: vi.fn(),
  registerShortcuts: vi.fn(() => () => {}),
  onHubEvent: vi.fn(() => () => {})
}))

vi.mock('../api', () => ({
  alarmApi: {
    queryPage: mocks.queryPage,
    summary: mocks.summary,
    ack: mocks.ack,
    note: mocks.note,
    timeline: mocks.timeline
  },
  deviceApi: { list: mocks.devices },
  ruleApi: { shelves: mocks.shelves, shelve: mocks.shelve, unshelve: mocks.unshelve },
  tagApi: { list: mocks.tags }
}))
vi.mock('../api/annunciators', () => ({
  annunciatorApi: { list: mocks.annList, silence: mocks.annSilence }
}))
vi.mock('../api/screens', () => ({
  screenApi: { list: mocks.screenList, get: mocks.screenGet }
}))
vi.mock('../api/http', () => ({
  errorMessage: (error: unknown, fallback?: string) => fallback ?? (error instanceof Error ? error.message : String(error))
}))
vi.mock('../stores/auth', () => ({ useAuthStore: () => mocks.auth }))
vi.mock('../realtime/hub', () => ({ onHubEvent: mocks.onHubEvent }))
vi.mock('../shortcuts/registry', () => ({
  registerShortcuts: mocks.registerShortcuts,
  openShortcutHelp: vi.fn()
}))
vi.mock('../ui/overlay', () => ({ modalOpen: () => false }))
vi.mock('./usePolling', () => ({ usePolling: vi.fn() }))
vi.mock('./useNowTicker', () => ({ useNowTicker: () => ({ now: { value: Date.now() } }) }))
vi.mock('vue-router', () => ({
  useRoute: () => mocks.route,
  useRouter: () => mocks.router
}))
vi.mock('element-plus', () => ({
  ElMessage: { success: mocks.success, warning: vi.fn(), error: vi.fn() },
  ElMessageBox: { confirm: mocks.confirm, prompt: mocks.prompt, alert: mocks.alert }
}))

function alarm(id: number, overrides: Partial<AlarmEvent> = {}): AlarmEvent {
  return {
    id,
    tagId: 11,
    deviceId: 7,
    ruleId: 5,
    level: 'Warning',
    value: 90,
    startTs: 1000,
    endTs: null,
    ackUser: null,
    ackTs: null,
    message: `Alarm ${id}`,
    ...overrides
  }
}

async function flush(times = 12): Promise<void> {
  for (let index = 0; index < times; index++) {
    await Promise.resolve()
    await nextTick()
  }
}

async function mountView() {
  let view!: ReturnType<typeof useAlarmsView>
  const host = document.createElement('div')
  document.body.appendChild(host)
  const app = createApp({
    setup() {
      view = useAlarmsView()
      return () => h('div')
    }
  })
  app.mount(host)
  await flush()
  return {
    view,
    unmount: () => {
      app.unmount()
      host.remove()
    }
  }
}

beforeEach(() => {
  localStorage.clear()
  mocks.auth.user = { id: 2, userName: 'operator', displayName: 'Operator', role: 'Operator' }
  mocks.route.query = {}
  mocks.queryPage.mockReset().mockResolvedValue({ items: [], total: 0, capped: false })
  mocks.summary.mockReset().mockResolvedValue({
    active: 0, unack: 0, critical: 0, warning: 0, info: 0, shelved: 0, suppressed: 0
  })
  mocks.ack.mockReset().mockResolvedValue(undefined)
  mocks.note.mockReset().mockResolvedValue(undefined)
  mocks.timeline.mockReset().mockResolvedValue({ alarm: alarm(1), entries: [] })
  mocks.shelves.mockReset().mockResolvedValue([])
  mocks.shelve.mockReset().mockResolvedValue(undefined)
  mocks.unshelve.mockReset().mockResolvedValue(undefined)
  mocks.devices.mockReset().mockResolvedValue([])
  mocks.tags.mockReset().mockResolvedValue([])
  mocks.annList.mockReset().mockResolvedValue([])
  mocks.annSilence.mockReset().mockResolvedValue({ silenceUntilTs: null })
  mocks.screenList.mockReset().mockResolvedValue([])
  mocks.screenGet.mockReset().mockResolvedValue({ document: { nodes: [] } })
  mocks.confirm.mockReset().mockResolvedValue('confirm')
  mocks.prompt.mockReset().mockResolvedValue({ value: 'handled' })
  mocks.alert.mockReset().mockResolvedValue('confirm')
  mocks.success.mockReset()
  mocks.router.replace.mockReset().mockResolvedValue(undefined)
})

afterEach(() => {
  vi.useRealTimers()
  localStorage.clear()
  document.body.innerHTML = ''
})

describe('useAlarmsView', () => {
  it('queries active alarms with live pagination by default', async () => {
    const { view, unmount } = await mountView()

    expect(view.stateFilter.value).toBe('active')
    expect(mocks.queryPage).toHaveBeenCalledTimes(1)
    expect(mocks.queryPage).toHaveBeenCalledWith(expect.objectContaining({
      live: true,
      state: 'active',
      priorityFirst: true,
      offset: 0,
      limit: 100
    }))
    unmount()
  })

  it('reloads immediately for level filters and writes the filter to the URL', async () => {
    const { view, unmount } = await mountView()
    mocks.queryPage.mockClear()
    mocks.router.replace.mockClear()

    view.levelFilter.value = 'Critical'
    await flush()

    expect(mocks.queryPage).toHaveBeenCalledTimes(1)
    expect(mocks.queryPage).toHaveBeenCalledWith(expect.objectContaining({ level: 'Critical', offset: 0 }))
    expect(mocks.router.replace).toHaveBeenCalledWith({ query: expect.objectContaining({ level: 'Critical' }) })
    unmount()
  })

  it('debounces keyword input for 250ms without duplicate requests', async () => {
    vi.useFakeTimers()
    const { view, unmount } = await mountView()
    mocks.queryPage.mockClear()

    view.keyword.value = 'pump'
    await nextTick()
    expect(mocks.queryPage).not.toHaveBeenCalled()

    await vi.advanceTimersByTimeAsync(250)
    await flush()
    expect(mocks.queryPage).toHaveBeenCalledTimes(1)
    expect(mocks.queryPage).toHaveBeenCalledWith(expect.objectContaining({ keyword: 'pump', offset: 0 }))
    unmount()
  })

  it('falls back to the default page size when the remembered value is not offered', async () => {
    localStorage.setItem('scada_last_:alarms-live-page-size', '123')
    const first = await mountView()
    expect(first.view.livePageSize.value).toBe(100)
    first.unmount()
    localStorage.clear()

    localStorage.setItem('scada_last_:alarms-live-page-size', '200')
    const second = await mountView()
    expect(second.view.livePageSize.value).toBe(200)
    second.unmount()
  })

  it('returns to page one, remembers the page size and reloads with it', async () => {
    const { view, unmount } = await mountView()
    mocks.queryPage.mockClear()

    view.onLiveSizeChange(200)
    await flush()

    expect(view.livePageSize.value).toBe(200)
    expect(localStorage.getItem('scada_last_:alarms-live-page-size')).toBe('200')
    expect(mocks.queryPage).toHaveBeenCalledWith(expect.objectContaining({ offset: 0, limit: 200 }))
    unmount()
  })

  it('skips acknowledged rows, records failures and keeps them for retry', async () => {
    const { view, unmount } = await mountView()
    const rows = [alarm(1), alarm(2, { ackTs: 5 }), alarm(3)]
    view.alarms.value = rows
    view.selected.value = rows
    mocks.queryPage.mockResolvedValue({ items: rows, total: rows.length, capped: false })
    mocks.ack.mockImplementation(async (id: number) => {
      if (id === 3) throw new Error('device offline')
    })

    await view.batchAck()

    expect(mocks.ack).toHaveBeenCalledTimes(2)
    expect(mocks.ack).toHaveBeenCalledWith(1, '')
    expect(mocks.ack).toHaveBeenCalledWith(3, '')
    expect(view.batchOutcome.value?.ok).toBe(1)
    expect(view.batchOutcome.value?.failedIds).toEqual([3])
    expect(view.batchOutcome.value?.skipped).toBe(0)
    expect(view.retryRows.value.map(row => row.id)).toEqual([3])
    unmount()
  })

  it('opens the ack dialog with keyboard A only for unacknowledged alarms', async () => {
    const { view, unmount } = await mountView()
    view.alarms.value = [alarm(1), alarm(2, { ackTs: 5 })]

    view.focusedId.value = 2
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'a' }))
    expect(view.ackDialog.visible).toBe(false)

    view.focusedId.value = 1
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'a' }))
    expect(view.ackDialog.visible).toBe(true)
    unmount()
  })

  it('shows units for numeric alarms and raw 0/1 for boolean alarms', async () => {
    const { view, unmount } = await mountView()
    view.sourceTags.value = new Map([
      [11, { id: 11, deviceId: 7, dataType: 'Bool', unit: '℃' } as unknown as Tag],
      [12, { id: 12, deviceId: 7, dataType: 'Float32', unit: 'kPa' } as unknown as Tag]
    ])

    expect(view.alarmValueText(alarm(1, { tagId: 11, value: 1 }))).toBe('1')
    expect(view.alarmValueText(alarm(2, { tagId: 12, value: 500 }))).toBe('500.00 kPa')
    unmount()
  })
})

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, nextTick, ref } from 'vue'
import type { AlarmEvent } from '../types'
import { useWorkspaceAlarms, type WorkspaceTabRef } from './useWorkspaceAlarms'

const queryPage = vi.fn()
const getScreen = vi.fn()

vi.mock('../api', () => ({
  alarmApi: { queryPage: (...args: unknown[]) => queryPage(...args) }
}))
vi.mock('../api/screens', () => ({
  screenApi: { get: (...args: unknown[]) => getScreen(...args) }
}))
vi.mock('./usePolling', () => ({
  usePolling: (task: () => Promise<void>, _interval: number, options: { enabled?: () => boolean }) => ({
    run: async () => {
      if (options.enabled && !options.enabled()) return
      await task()
    }
  })
}))

function alarm(id: number, tagId: number): AlarmEvent {
  return {
    id, tagId, deviceId: 1, ruleId: 1, level: 'Warning', value: 1,
    startTs: id, endTs: null, ackUser: null, ackTs: null, message: `a${id}`
  }
}

function mount(tabs: WorkspaceTabRef[]) {
  const tabsRef = ref(tabs)
  let api!: ReturnType<typeof useWorkspaceAlarms>
  const host = document.createElement('div')
  document.body.appendChild(host)
  const app = createApp(defineComponent({
    setup() {
      api = useWorkspaceAlarms(tabsRef)
      return () => h('div')
    }
  }))
  app.mount(host)
  return {
    api,
    tabsRef,
    unmount: () => { app.unmount(); host.remove() }
  }
}

beforeEach(() => {
  queryPage.mockReset()
  getScreen.mockReset()
})

afterEach(() => {
  vi.useRealTimers()
})

describe('useWorkspaceAlarms', () => {
  it('matches unacked alarms to a tab and marks a 500-item cap as incomplete', async () => {
    const items = Array.from({ length: 500 }, (_, index) => alarm(index + 1, 7))
    queryPage.mockResolvedValue({ items, total: 500, offset: 0, limit: 500, capped: true })
    const { api, unmount } = mount([{ key: 1, screenId: 10 }])
    api.setTabTags(1, [7])
    await api.pollTabAlarms()
    const summary = api.tabAlarmSummary({ key: 1, screenId: 10 })
    expect(summary.count).toBe(500)
    expect(summary.incomplete).toBe(true)
    expect(summary.reason).toBe('capped')
    expect(api.tabAlarmBadgeText(summary)).toBe('99+')
    unmount()
  })

  it('keeps the previous count after a fetch failure but does not show a definite number', async () => {
    queryPage.mockResolvedValue({ items: [alarm(1, 7)], total: 1, offset: 0, limit: 500, capped: false })
    const { api, unmount } = mount([{ key: 1, screenId: 10 }])
    api.setTabTags(1, [7])
    await api.pollTabAlarms()
    expect(api.tabAlarmBadgeText(api.tabAlarmSummary({ key: 1, screenId: 10 }))).toBe('1')

    queryPage.mockRejectedValueOnce(new Error('network'))
    await api.pollTabAlarms()
    const summary = api.tabAlarmSummary({ key: 1, screenId: 10 })
    expect(summary.count).toBe(1)
    expect(summary.incomplete).toBe(true)
    expect(summary.reason).toBe('fetch-failed')
    expect(api.tabAlarmBadgeText(summary)).toBe('1+')
    unmount()
  })

  it('marks a prefetch failure as incomplete instead of a confident zero', async () => {
    getScreen.mockRejectedValue(new Error('missing screen'))
    const { api, unmount } = mount([{ key: 2, screenId: 99 }])
    await api.pollTabAlarms()
    await nextTick()
    await Promise.resolve()
    const summary = api.tabAlarmSummary({ key: 2, screenId: 99 })
    expect(summary.count).toBe(0)
    expect(summary.incomplete).toBe(true)
    expect(summary.reason).toBe('prefetch-failed')
    expect(api.tabAlarmBadgeText(summary)).toBe('?')
    unmount()
  })
})

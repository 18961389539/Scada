import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, defineComponent, h, nextTick, ref, type Ref } from 'vue'
import type { ControlRequest } from '../api/control'
import {
  isPendingControlRequest,
  pendingApprovalCount,
  pendingApprovalsPath,
  resetPendingApprovalsForTests,
  setPendingApprovalRows,
  usePendingApprovals,
  type ControlRequestRow
} from './pendingApprovals'

vi.mock('../api/control', () => ({
  controlApi: {
    requests: vi.fn(async () => [] as ControlRequestRow[])
  }
}))

vi.mock('../stores/auth', () => ({
  useAuthStore: () => ({ user: { id: 1, userName: 'op' } })
}))

vi.mock('../access/policies', () => ({
  allows: vi.fn(() => true)
}))

function request(partial: Partial<ControlRequest> = {}): ControlRequest {
  return {
    id: 'r1',
    kind: 'command',
    tagId: 1,
    value: 10,
    sequenceId: null,
    source: 'runtime',
    requestedByUserId: 1,
    requestedBy: 'op',
    requestedTs: 1,
    expiresTs: Date.now() + 60_000,
    status: 'Pending',
    decidedBy: null,
    decidedTs: null,
    comment: null,
    resultMessage: null,
    runId: null,
    ...partial
  }
}

function row(partial: Partial<ControlRequest> = {}): ControlRequestRow {
  return { request: request(partial), sequenceName: null, mine: false }
}

function mount(enabled?: Ref<boolean>) {
  let api!: ReturnType<typeof usePendingApprovals>
  const host = document.createElement('div')
  document.body.appendChild(host)
  const app = createApp(defineComponent({
    setup() {
      api = usePendingApprovals(enabled ? { enabled } : undefined)
      return () => h('div')
    }
  }))
  app.mount(host)
  return {
    api,
    unmount: () => {
      app.unmount()
      host.remove()
    }
  }
}

beforeEach(() => {
  resetPendingApprovalsForTests()
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-11-22T10:00:00+08:00'))
})

afterEach(() => {
  resetPendingApprovalsForTests()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('pendingApprovals', () => {
  it('counts only unexpired pending requests', () => {
    const now = Date.now()
    const rows = [
      row({ id: 'a', status: 'Pending', expiresTs: now + 1000 }),
      row({ id: 'b', status: 'Pending', expiresTs: now }),
      row({ id: 'c', status: 'Approved', expiresTs: now + 1000 })
    ]
    expect(isPendingControlRequest(rows[0]!.request, now)).toBe(true)
    expect(isPendingControlRequest(rows[1]!.request, now)).toBe(false)
    expect(pendingApprovalCount(rows, now)).toBe(1)
  })

  it('opens the control page requests tab by default', () => {
    expect(pendingApprovalsPath).toBe('/control')
  })

  it('shares one list across subscribers and stops polling after the last unmount', async () => {
    const { controlApi } = await import('../api/control')
    vi.mocked(controlApi.requests).mockResolvedValue([row({ id: 'live' })])

    const first = mount()
    await vi.advanceTimersByTimeAsync(0)
    await Promise.resolve()
    expect(controlApi.requests).toHaveBeenCalledTimes(1)
    expect(first.api.pendingCount.value).toBe(1)

    const second = mount()
    expect(second.api.pendingCount.value).toBe(1)
    await vi.advanceTimersByTimeAsync(5000)
    await Promise.resolve()
    expect(controlApi.requests).toHaveBeenCalledTimes(2)

    first.unmount()
    second.unmount()
    const calls = vi.mocked(controlApi.requests).mock.calls.length
    await vi.advanceTimersByTimeAsync(10_000)
    expect(controlApi.requests).toHaveBeenCalledTimes(calls)
  })

  it('does not poll when disabled, such as an inert preview that only needs a static count', async () => {
    const { controlApi } = await import('../api/control')
    setPendingApprovalRows([row({ id: 'cached' })])
    const enabled = ref(false)
    const preview = mount(enabled)
    await nextTick()
    expect(controlApi.requests).not.toHaveBeenCalled()
    expect(preview.api.pendingCount.value).toBe(1)
    preview.unmount()
  })
})

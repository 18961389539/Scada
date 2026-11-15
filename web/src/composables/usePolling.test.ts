import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h } from 'vue'
import { usePolling, type PollingOptions } from './usePolling'

let hidden = false

const flush = async (): Promise<void> => {
  for (let index = 0; index < 5; index++) await Promise.resolve()
}

function mountPolling(task: () => unknown | Promise<unknown>, intervalMs = 1000, options: PollingOptions = {}) {
  let polling!: ReturnType<typeof usePolling>
  const host = document.createElement('div')
  document.body.appendChild(host)
  const app = createApp({
    setup() {
      polling = usePolling(task, intervalMs, options)
      return () => h('div')
    }
  })
  app.mount(host)
  return {
    polling,
    unmount: () => {
      if (!host.isConnected) return
      app.unmount()
      host.remove()
    }
  }
}

beforeEach(() => {
  vi.useFakeTimers()
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
})

afterEach(() => {
  vi.clearAllTimers()
  vi.useRealTimers()
})

describe('usePolling', () => {
  it('runs on the interval and stops after unmount', async () => {
    const task = vi.fn(async () => undefined)
    const { unmount } = mountPolling(task)

    await vi.advanceTimersByTimeAsync(1000)
    expect(task).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(2000)
    expect(task).toHaveBeenCalledTimes(3)

    unmount()
    await vi.advanceTimersByTimeAsync(2000)
    expect(task).toHaveBeenCalledTimes(3)
  })

  it('can run immediately on mount', () => {
    const task = vi.fn(async () => undefined)
    const { unmount } = mountPolling(task, 1000, { immediate: true })

    expect(task).toHaveBeenCalledTimes(1)
    unmount()
  })

  it('does not overlap requests and reruns once after the current one finishes', async () => {
    let release!: () => void
    const task = vi.fn(() => new Promise<void>(resolve => { release = resolve }))
    const { unmount } = mountPolling(task)

    await vi.advanceTimersByTimeAsync(1000)
    expect(task).toHaveBeenCalledTimes(1)
    await vi.advanceTimersByTimeAsync(2000)
    expect(task).toHaveBeenCalledTimes(1)

    release()
    await flush()
    expect(task).toHaveBeenCalledTimes(2)

    release()
    await flush()
    unmount()
  })

  it('skips rounds while disabled and resumes when enabled', async () => {
    let enabled = false
    const task = vi.fn(async () => undefined)
    const { unmount } = mountPolling(task, 1000, { enabled: () => enabled })

    await vi.advanceTimersByTimeAsync(1000)
    expect(task).not.toHaveBeenCalled()

    enabled = true
    await vi.advanceTimersByTimeAsync(1000)
    expect(task).toHaveBeenCalledTimes(1)
    unmount()
  })

  it('pauses on a hidden page and refreshes when it becomes visible', async () => {
    const task = vi.fn(async () => undefined)
    const { polling, unmount } = mountPolling(task)

    hidden = true
    await vi.advanceTimersByTimeAsync(2000)
    await polling.run()
    expect(task).not.toHaveBeenCalled()

    hidden = false
    document.dispatchEvent(new Event('visibilitychange'))
    await flush()
    expect(task).toHaveBeenCalledTimes(1)
    unmount()
  })
})

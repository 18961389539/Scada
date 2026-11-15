import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createApp, h } from 'vue'
import { useNowTicker } from './useNowTicker'

let hidden = false
const mounted: (() => void)[] = []

function mountTicker() {
  let ticker!: ReturnType<typeof useNowTicker>
  const host = document.createElement('div')
  document.body.appendChild(host)
  const app = createApp({
    setup() {
      ticker = useNowTicker()
      return () => h('div')
    }
  })
  app.mount(host)
  const unmount = () => {
    if (!host.isConnected) return
    app.unmount()
    host.remove()
  }
  mounted.push(unmount)
  return { now: ticker.now, unmount }
}

beforeEach(() => {
  vi.useFakeTimers()
  vi.setSystemTime(new Date('2026-01-01T00:00:00Z'))
  hidden = false
  Object.defineProperty(document, 'hidden', { configurable: true, get: () => hidden })
})

afterEach(() => {
  while (mounted.length) mounted.pop()!()
  vi.clearAllTimers()
  vi.useRealTimers()
})

describe('useNowTicker', () => {
  it('shares one timer across subscribers and stops after the last unmount', () => {
    const setInterval = vi.spyOn(window, 'setInterval')
    const first = mountTicker()
    const second = mountTicker()

    expect(setInterval).toHaveBeenCalledTimes(1)
    expect(first.now.value).toBe(Date.now())

    vi.advanceTimersByTime(1000)
    expect(first.now.value).toBe(Date.now())
    expect(second.now.value).toBe(first.now.value)

    first.unmount()
    second.unmount()
    const stopped = first.now.value
    vi.advanceTimersByTime(3000)
    expect(first.now.value).toBe(stopped)
  })

  it('stops while the page is hidden and corrects immediately when visible again', () => {
    const { now, unmount } = mountTicker()

    hidden = true
    document.dispatchEvent(new Event('visibilitychange'))
    const stopped = now.value
    vi.advanceTimersByTime(5000)
    expect(now.value).toBe(stopped)

    hidden = false
    document.dispatchEvent(new Event('visibilitychange'))
    expect(now.value).toBe(Date.now())

    vi.advanceTimersByTime(1000)
    expect(now.value).toBe(stopped + 6000)
    unmount()
  })
})

import { describe, expect, it, vi } from 'vitest'
import { runBatch, summarizeBatch, type BatchProgress } from './batchRunner'

describe('runBatch', () => {
  it('runs items in order and reports progress', async () => {
    const seen: number[] = []
    const progress: BatchProgress[] = []
    const report = await runBatch([1, 2, 3], item => `#${item}`, async item => { seen.push(item) }, event => progress.push(event))

    expect(seen).toEqual([1, 2, 3])
    expect(report).toEqual({ ok: 3, failed: [], total: 3, canceled: false })
    expect(progress).toEqual([
      { done: 0, total: 3, current: '#1' },
      { done: 1, total: 3, current: '#2' },
      { done: 2, total: 3, current: '#3' },
      { done: 3, total: 3, current: '' }
    ])
  })

  it('collects failures and keeps running the rest', async () => {
    const report = await runBatch([1, 2, 3], item => `#${item}`, async item => {
      if (item === 2) throw new Error('boom')
    })

    expect(report.ok).toBe(2)
    expect(report.failed).toEqual([{ label: '#2', error: 'boom' }])
    expect(report.canceled).toBe(false)
  })

  it('stops before the next item when asked', async () => {
    const action = vi.fn(async () => undefined)
    const report = await runBatch([1, 2, 3], item => `#${item}`, action, undefined, () => action.mock.calls.length >= 1)

    expect(action).toHaveBeenCalledTimes(1)
    expect(report).toMatchObject({ ok: 1, total: 3, canceled: true })
  })
})

describe('summarizeBatch', () => {
  it('returns a short line when everything succeeds', () => {
    expect(summarizeBatch({ ok: 2, failed: [], total: 2, canceled: false }, 'items')).toBe('2/2 items')
  })

  it('lists partial failures', () => {
    const summary = summarizeBatch({ ok: 1, failed: [{ label: 'A', error: 'x' }], total: 2, canceled: false }, 'items')

    expect(summary).toContain('1/2')
    expect(summary).toContain('A')
    expect(summary).toContain('x')
  })
})

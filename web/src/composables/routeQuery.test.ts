import { describe, expect, it, vi } from 'vitest'
import type { LocationQuery, Router } from 'vue-router'
import { createQuerySync, projectQuery, queryNumber, queryNumbers, queryRange, replaceQuery } from './routeQuery'

describe('query scalar parsing', () => {
  it('parses finite numbers and comma-separated lists', () => {
    expect(queryNumber('12')).toBe(12)
    expect(queryNumber('')).toBeUndefined()
    expect(queryNumber('abc')).toBeUndefined()
    expect(queryNumber(['1'])).toBeUndefined()
    expect(queryNumbers('1,2,x')).toEqual([1, 2])
    expect(queryNumbers('')).toEqual([])
  })

  it('returns a range only when both bounds are valid and ordered', () => {
    expect(queryRange({ from: '1000', to: '2000' })).toEqual([new Date(1000), new Date(2000)])
    expect(queryRange({ from: '2000', to: '1000' })).toBeNull()
    expect(queryRange({ from: '1000' })).toBeNull()
  })
})

describe('projectQuery', () => {
  it('removes empty values and joins arrays', () => {
    const query = projectQuery({ page: '1' }, { page: 2, keyword: '', tagIds: [3, 4], empty: [], missing: null })

    expect(query).toEqual({ page: '2', tagIds: '3,4' })
  })
})

describe('replaceQuery', () => {
  it('only navigates when the projected query changes', () => {
    const replace = vi.fn()
    const router = { replace } as unknown as Router

    replaceQuery(router, { page: '1' }, { page: 1 })
    expect(replace).not.toHaveBeenCalled()

    replaceQuery(router, { page: '1' }, { page: 2 })
    expect(replace).toHaveBeenCalledWith({ query: { page: '2' } })
  })
})

describe('createQuerySync', () => {
  it('ignores the query this page just wrote and detects a later back navigation', () => {
    let current: LocationQuery = { page: '1', tab: 'live' }
    const sync = createQuerySync(() => current, ['page'])

    sync.note(current, { page: 2 })
    current = { page: '2', tab: 'live' }
    expect(sync.changed()).toBe(false)

    current = { page: '1', tab: 'live' }
    expect(sync.changed()).toBe(true)
    expect(sync.changed()).toBe(false)
  })
})

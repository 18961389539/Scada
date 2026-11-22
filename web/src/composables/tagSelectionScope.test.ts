import { describe, expect, it } from 'vitest'
import { tagExportIsUnfiltered, tagExportNeedsDisclosure, tagQueryHasFilters } from './tagSelectionScope'

describe('tagSelectionScope', () => {
  it('detects current catalog filters', () => {
    expect(tagQueryHasFilters({})).toBe(false)
    expect(tagQueryHasFilters({ search: '  ' })).toBe(false)
    expect(tagQueryHasFilters({ deviceId: 3 })).toBe(true)
    expect(tagQueryHasFilters({ search: 'pump' })).toBe(true)
    expect(tagQueryHasFilters({ quality: 'abnormal' })).toBe(true)
  })

  it('requires an export disclosure because the API still exports every tag', () => {
    expect(tagExportNeedsDisclosure({ search: 'pump' })).toBe(true)
    expect(tagExportNeedsDisclosure({})).toBe(true)
    expect(tagExportIsUnfiltered({ search: 'pump' })).toBe(false)
    expect(tagExportIsUnfiltered({})).toBe(true)
  })
})

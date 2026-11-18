import { describe, expect, it } from 'vitest'
import { screenCardPrimaryAction } from './screenCardActions'

describe('screenCardPrimaryAction', () => {
  it('gives operators a single run primary on published screens', () => {
    expect(screenCardPrimaryAction({ publishedRevision: 3, hasDraftChanges: false }, false)).toBe('run')
  })

  it('gives editors an edit primary on drafts and pending changes', () => {
    expect(screenCardPrimaryAction({ publishedRevision: null, hasDraftChanges: false }, true)).toBe('edit')
    expect(screenCardPrimaryAction({ publishedRevision: 2, hasDraftChanges: true }, true)).toBe('edit')
  })

  it('keeps run as the primary for published screens without draft changes, even for editors', () => {
    expect(screenCardPrimaryAction({ publishedRevision: 2, hasDraftChanges: false }, true)).toBe('run')
  })

  it('has no primary for unpublished screens the user cannot edit', () => {
    expect(screenCardPrimaryAction({ publishedRevision: null }, false)).toBeNull()
  })
})

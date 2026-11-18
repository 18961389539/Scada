import { afterEach, describe, expect, it } from 'vitest'
import { readSidebarMiniPreference, rememberSidebarMiniPreference, SIDEBAR_MINI_KEY, sidebarMiniFromMedia } from './sidebarMini'

const USER_KEY = 'scada_user'

afterEach(() => {
  localStorage.clear()
})

describe('sidebarMiniFromMedia', () => {
  it('follows the media query when the user has not chosen', () => {
    expect(sidebarMiniFromMedia(null, true)).toBe(true)
    expect(sidebarMiniFromMedia(null, false)).toBe(false)
  })

  it('keeps a manual expand even when the media query wants a mini sidebar', () => {
    expect(sidebarMiniFromMedia(false, true)).toBe(false)
  })

  it('keeps a manual collapse even when the media query wants a full sidebar', () => {
    expect(sidebarMiniFromMedia(true, false)).toBe(true)
  })
})

describe('sidebar mini preference', () => {
  it('round-trips expand and collapse for the current user', () => {
    localStorage.setItem(USER_KEY, JSON.stringify({ userName: 'operator' }))
    expect(readSidebarMiniPreference()).toBeNull()
    rememberSidebarMiniPreference(false)
    expect(readSidebarMiniPreference()).toBe(false)
    expect(localStorage.getItem(`scada_last_operator:${SIDEBAR_MINI_KEY}`)).toBe('expanded')
    rememberSidebarMiniPreference(true)
    expect(readSidebarMiniPreference()).toBe(true)
  })
})

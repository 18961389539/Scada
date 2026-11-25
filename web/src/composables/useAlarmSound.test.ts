import { afterEach, describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import { resetAlarmSoundForTests, soundStorageKey, useAlarmSound } from './useAlarmSound'

afterEach(() => {
  resetAlarmSoundForTests()
  localStorage.clear()
})

describe('useAlarmSound storage sync', () => {
  it('writes the shared scada_sound key when the mute switch changes', async () => {
    localStorage.setItem('scada_sound', 'on')
    const sound = useAlarmSound()
    expect(sound.soundEnabled.value).toBe(true)
    sound.soundEnabled.value = false
    await nextTick()
    expect(localStorage.getItem('scada_sound')).toBe('off')
  })

  it('follows scada_sound changes from another window via the storage event', async () => {
    localStorage.setItem('scada_sound', 'on')
    const sound = useAlarmSound()
    expect(sound.soundEnabled.value).toBe(true)
    localStorage.setItem('scada_sound', 'off')
    window.dispatchEvent(new StorageEvent('storage', { key: 'scada_sound', newValue: 'off', storageArea: localStorage }))
    await nextTick()
    expect(sound.soundEnabled.value).toBe(false)
  })
})

describe('useAlarmSound per-user preference', () => {
  it('scopes mute preference by the signed-in user and does not leak to the next user', async () => {
    localStorage.setItem('scada_user', JSON.stringify({ userName: 'Alice' }))
    const sound = useAlarmSound()
    sound.syncFromUser()
    sound.soundEnabled.value = false
    await nextTick()
    expect(localStorage.getItem(soundStorageKey('alice'))).toBe('off')
    expect(sound.soundEnabled.value).toBe(false)

    localStorage.setItem('scada_user', JSON.stringify({ userName: 'Bob' }))
    sound.syncFromUser()
    expect(sound.soundEnabled.value).toBe(true)
    sound.soundEnabled.value = true
    await nextTick()
    expect(localStorage.getItem(soundStorageKey('alice'))).toBe('off')
  })
})

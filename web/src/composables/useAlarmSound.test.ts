import { afterEach, describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import { resetAlarmSoundForTests, useAlarmSound } from './useAlarmSound'

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

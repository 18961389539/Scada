import { effectScope, ref, watch } from 'vue'
import { USER_KEY } from '../api/http'
import { annunciatorApi } from '../api/annunciators'
import { setAlarmMute, startAlarmLoop, stopAlarmLoop } from '../alarmAudio'

const LEGACY_KEY = 'scada_sound'

export function currentSoundUser(): string {
  try {
    return (JSON.parse(localStorage.getItem(USER_KEY) ?? '{}') as { userName?: string }).userName?.toLocaleLowerCase() ?? ''
  } catch {
    return ''
  }
}

export function soundStorageKey(userName = currentSoundUser()): string {
  return userName ? `${LEGACY_KEY}:${userName}` : LEGACY_KEY
}

export function readSoundEnabled(userName = currentSoundUser()): boolean {
  try {
    const keyed = localStorage.getItem(soundStorageKey(userName))
    if (keyed === 'off') return false
    if (keyed === 'on') return true
    return localStorage.getItem(LEGACY_KEY) !== 'off'
  } catch {
    return true
  }
}

/**
 * 报警声音：静音偏好、声光面板播音键同步、报警洪峰下的 300ms 合批重算。
 * 全应用共用一份状态，避免布局卸载后运行工作台/Kiosk 各自再造一份。
 * 静音按登录用户保存在本机；未登录时仍使用旧的 scada_sound 键。
 */
function createAlarmSound() {
  const soundEnabled = ref(readSoundEnabled())
  let boundUser = currentSoundUser()
  let annunciatorTimer: number | undefined
  let annunciatorBusy = false
  let annunciatorQueued = false
  const scope = effectScope()

  async function refresh(): Promise<void> {
    if (!soundEnabled.value) {
      stopAlarmLoop()
      return
    }
    if (annunciatorBusy) {
      annunciatorQueued = true
      return
    }
    annunciatorBusy = true
    try {
      const statuses = await annunciatorApi.status()
      const active = statuses.filter(status => status.enabled && status.soundKey && !status.silenced)
      const rank = (key: string | null) => key === 'critical' ? 3 : key === 'warning' ? 2 : key === 'info' ? 1 : 0
      const best = active.sort((a, b) => rank(b.soundKey) - rank(a.soundKey))[0]
      if (best?.soundKey) startAlarmLoop(best.soundKey)
      else stopAlarmLoop()
    } catch {
      // 状态取不到就沿用上一次的播音状态（正在响的继续响、原本静默的保持静默），下一条报警推送会再试。
    } finally {
      annunciatorBusy = false
    }
    if (annunciatorQueued) {
      annunciatorQueued = false
      await refresh()
    }
  }

  // 报警洪峰时每条推送都要求重算播音状态：300ms 合批，听不出延迟也不会把请求翻倍。
  function schedule(): void {
    if (annunciatorTimer !== undefined) window.clearTimeout(annunciatorTimer)
    annunciatorTimer = window.setTimeout(() => {
      annunciatorTimer = undefined
      void refresh()
    }, 300)
  }

  function persist(enabled: boolean): void {
    const key = soundStorageKey(boundUser)
    try {
      localStorage.setItem(key, enabled ? 'on' : 'off')
      if (key !== LEGACY_KEY && localStorage.getItem(LEGACY_KEY) != null) {
        // 不再把上一班的偏好写回共享键，避免下一位未完成登录同步的页面读到过期值。
      }
    } catch {
      // 本地存储不可用时仍保持内存中的开关。
    }
  }

  function onStorage(event: StorageEvent): void {
    if (event.storageArea && event.storageArea !== localStorage) return
    const key = soundStorageKey(boundUser)
    if (event.key !== key && !(event.key === LEGACY_KEY && key === LEGACY_KEY) && event.key !== USER_KEY) return
    if (event.key === USER_KEY) {
      syncFromUser()
      return
    }
    const enabled = event.newValue !== 'off'
    if (soundEnabled.value !== enabled) soundEnabled.value = enabled
  }

  function syncFromUser(): void {
    const nextUser = currentSoundUser()
    if (nextUser === boundUser) {
      const enabled = readSoundEnabled(nextUser)
      if (soundEnabled.value !== enabled) soundEnabled.value = enabled
      return
    }
    boundUser = nextUser
    soundEnabled.value = readSoundEnabled(nextUser)
  }

  scope.run(() => {
    watch(soundEnabled, (enabled) => {
      persist(enabled)
      setAlarmMute(!enabled)
      if (!enabled) stopAlarmLoop()
      else void refresh()
    })
  })
  window.addEventListener('storage', onStorage)

  function start(): void {
    syncFromUser()
    setAlarmMute(!soundEnabled.value)
  }

  function dispose(): void {
    stopAlarmLoop()
    if (annunciatorTimer !== undefined) window.clearTimeout(annunciatorTimer)
    annunciatorTimer = undefined
  }

  function reset(): void {
    dispose()
    window.removeEventListener('storage', onStorage)
    scope.stop()
  }

  return { soundEnabled, refresh, schedule, start, dispose, reset, syncFromUser }
}

let instance: ReturnType<typeof createAlarmSound> | undefined

export function useAlarmSound() {
  instance ??= createAlarmSound()
  return instance
}

export function resetAlarmSoundForTests(): void {
  instance?.reset()
  instance = undefined
}

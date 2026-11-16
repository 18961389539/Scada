import { ref, watch } from 'vue'
import { annunciatorApi } from '../api/annunciators'
import { setAlarmMute, startAlarmLoop, stopAlarmLoop } from '../alarmAudio'

/**
 * 报警声音：静音偏好、声光面板播音键同步、报警洪峰下的 300ms 合批重算。
 * 从 AppLayout 抽出后，布局只负责在挂载/卸载时调用 start / dispose。
 */
export function useAlarmSound() {
  const soundEnabled = ref(localStorage.getItem('scada_sound') !== 'off')
  let annunciatorTimer: number | undefined
  let annunciatorBusy = false

  async function refresh(): Promise<void> {
    if (!soundEnabled.value) {
      stopAlarmLoop()
      return
    }
    if (annunciatorBusy) return
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
  }

  // 报警洪峰时每条推送都要求重算播音状态：300ms 合批，听不出延迟也不会把请求翻倍。
  function schedule(): void {
    if (annunciatorTimer !== undefined) window.clearTimeout(annunciatorTimer)
    annunciatorTimer = window.setTimeout(() => {
      annunciatorTimer = undefined
      void refresh()
    }, 300)
  }

  watch(soundEnabled, (enabled) => {
    localStorage.setItem('scada_sound', enabled ? 'on' : 'off')
    setAlarmMute(!enabled)
    if (!enabled) stopAlarmLoop()
    else void refresh()
  })

  function start(): void {
    setAlarmMute(!soundEnabled.value)
  }

  function dispose(): void {
    stopAlarmLoop()
    if (annunciatorTimer !== undefined) window.clearTimeout(annunciatorTimer)
    annunciatorTimer = undefined
  }

  return { soundEnabled, refresh, schedule, start, dispose }
}

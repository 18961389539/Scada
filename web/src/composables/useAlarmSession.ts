import { h } from 'vue'
import { ElNotification } from 'element-plus'
import type { AlarmEvent } from '../types'
import { handleAlarmPush, primeAlarmAudio } from '../alarmAudio'
import { useAlarmSound } from './useAlarmSound'
import { onHubClosed, onHubEvent, resetHub } from '../realtime/hub'
import { useAuthStore } from '../stores/auth'
import { alarmMessage, levelLabel, notificationType } from '../utils'
import { tr } from '../i18n'

type NotificationHandle = ReturnType<typeof ElNotification>
const CRITICAL_TOAST_LIMIT = 3

let running = false
let disposeAlarmHandler: (() => void) | undefined
let disposeHubClosed: (() => void) | undefined
const criticalToasts = new Set<NotificationHandle>()

function unlockAudio(): void {
  primeAlarmAudio()
  window.removeEventListener('pointerdown', unlockAudio, true)
  window.removeEventListener('keydown', unlockAudio, true)
}

export type AlarmToastSurface = 'toast' | 'console' | 'kiosk' | 'none'

/** 控制室 / Kiosk / 布局预览不要用会改路由的 Critical toast。 */
export function alarmToastSurface(path: string): AlarmToastSurface {
  const route = path.split('?')[0] ?? path
  if (route.startsWith('/config/layout-preview')) return 'none'
  if (route.startsWith('/console')) return 'console'
  if (route.startsWith('/kiosk')) return 'kiosk'
  return 'toast'
}

type CriticalAlarmListener = (alarm: AlarmEvent) => void
const criticalListeners = new Set<CriticalAlarmListener>()

export function onCriticalAlarmRaise(listener: CriticalAlarmListener): () => void {
  criticalListeners.add(listener)
  return () => { criticalListeners.delete(listener) }
}

function emitCriticalAlarm(alarm: AlarmEvent): void {
  for (const listener of criticalListeners) listener(alarm)
}

function openAlarmsFromToast(alarm: AlarmEvent, navigate?: (path: string, query: Record<string, string>) => void): void {
  const query: Record<string, string> = { state: 'unack' }
  if (alarm.tagId) query.tagId = String(alarm.tagId)
  if (navigate) {
    navigate('/alarms', query)
    return
  }
  void import('../router').then(({ default: router }) => {
    void router.push({ path: '/alarms', query })
  })
}

function notifyRaise(alarm: AlarmEvent): void {
  const critical = alarm.level === 'Critical'
  const surface = alarmToastSurface(window.location.pathname)
  if (critical && surface !== 'none') emitCriticalAlarm(alarm)
  // 控制室改为聚焦本布局报警条，Kiosk 用屏内横幅；预览不弹可点击 toast。
  if (critical && surface !== 'toast') return
  const handle: NotificationHandle = ElNotification({
    title: tr('appLayout.k8e59ff', [levelLabel(alarm.level)]),
    message: critical
      ? h('div', [
          h('div', alarmMessage(alarm)),
          h('div', { style: 'margin-top: 4px; font-size: 12px; opacity: 0.72' }, tr('alarmToast.openHint'))
        ])
      : alarmMessage(alarm),
    type: notificationType(alarm.level),
    duration: critical ? 0 : 6000,
    ...(critical ? {
      customClass: 'scada-alarm-toast',
      onClose: () => { criticalToasts.delete(handle) },
      onClick: () => { openAlarmsFromToast(alarm); handle.close() }
    } : {})
  })
  if (!critical) return
  criticalToasts.add(handle)
  while (criticalToasts.size > CRITICAL_TOAST_LIMIT) {
    const oldest = criticalToasts.values().next().value
    if (!oldest) break
    criticalToasts.delete(oldest)
    oldest.close()
  }
}

function closeCriticalToasts(): void {
  for (const toast of criticalToasts) toast.close()
  criticalToasts.clear()
}

export function shouldRunAlarmSession(state: { token?: string; mustChangePassword?: boolean; isPublic?: boolean; suppressAlarmSession?: boolean }): boolean {
  return !!state.token && !state.mustChangePassword && !state.isPublic && !state.suppressAlarmSession
}

export function alarmSessionActive(): boolean {
  return running
}

/**
 * 报警发声、静音和 Critical 弹窗与 AppLayout 解耦：登录后在根组件启动，
 * 进入 /console、Kiosk、布局预览时不得当作登出拆掉；只有真正登出才释放音频和共享连接。
 */
export function useAlarmSession() {
  const sound = useAlarmSound()

  function start(): void {
    if (running) return
    running = true
    sound.start()
    window.addEventListener('pointerdown', unlockAudio, true)
    window.addEventListener('keydown', unlockAudio, true)
    disposeAlarmHandler = onHubEvent<{ action: string; alarm: AlarmEvent }>('alarm', (payload) => {
      if (payload.action === 'raise') notifyRaise(payload.alarm)
      handleAlarmPush(payload)
      sound.schedule()
    })
    const auth = useAuthStore()
    disposeHubClosed = onHubClosed(() => { void auth.confirmSession().catch(() => undefined) })
  }

  function stop(): void {
    const wasRunning = running
    running = false
    window.removeEventListener('pointerdown', unlockAudio, true)
    window.removeEventListener('keydown', unlockAudio, true)
    disposeAlarmHandler?.()
    disposeHubClosed?.()
    disposeAlarmHandler = undefined
    disposeHubClosed = undefined
    sound.dispose()
    closeCriticalToasts()
    // 登出或未登录才断开共享连接；从总览进入运行工作台不会走到这里。
    if (wasRunning) resetHub()
  }

  return {
    soundEnabled: sound.soundEnabled,
    schedule: sound.schedule,
    start,
    stop
  }
}

export function resetAlarmSessionForTests(): void {
  running = false
  window.removeEventListener('pointerdown', unlockAudio, true)
  window.removeEventListener('keydown', unlockAudio, true)
  disposeAlarmHandler?.()
  disposeHubClosed?.()
  disposeAlarmHandler = undefined
  disposeHubClosed = undefined
  criticalListeners.clear()
  closeCriticalToasts()
}

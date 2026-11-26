import { h, ref } from 'vue'
import { ElMessageBox, ElNotification } from 'element-plus'
import type { AlarmEvent } from '../types'
import { alarmApi } from '../api'
import { handleAlarmPush, primeAlarmAudio } from '../alarmAudio'
import { useAlarmSound } from './useAlarmSound'
import { onHubClosed, onHubEvent, onHubReconnected, resetHub } from '../realtime/hub'
import { useAuthStore } from '../stores/auth'
import { alarmMessage, levelLabel, notificationType } from '../utils'
import { tr } from '../i18n'

type NotificationHandle = ReturnType<typeof ElNotification>
const CRITICAL_TOAST_LIMIT = 3

export type MissedCriticalNotice = { count: number }

export function missedCriticalAlarms(events: AlarmEvent[], fromMs: number, toMs: number): AlarmEvent[] {
  return events.filter(alarm => {
    if (alarm.simulated) return false
    if (alarm.level !== 'Critical') return false
    if (alarm.ackTs && alarm.endTs) return false
    if (alarm.ackTs && !alarm.endTs) return false
    const start = Number(alarm.startTs) || 0
    const end = alarm.endTs ? Number(alarm.endTs) : 0
    const touched = (start >= fromMs && start <= toMs) || (end > 0 && end >= fromMs && end <= toMs)
    return touched && !alarm.ackTs
  })
}

export const missedCriticalNotice = ref<MissedCriticalNotice | null>(null)
let disconnectSince: number | null = null
let mutePrompted = false

let running = false
let disposeAlarmHandler: (() => void) | undefined
let disposeHubClosed: (() => void) | undefined
let disposeReconnect: (() => void) | undefined
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
  if (alarm.simulated) {
    if (alarmToastSurface(window.location.pathname) === 'none') return
    ElNotification({
      title: tr('alarmToast.simulatedTitle', [levelLabel(alarm.level)]),
      message: h('div', [
        h('div', alarmMessage(alarm)),
        h('div', { style: 'margin-top: 4px; font-size: 12px; opacity: 0.72' }, tr('alarmToast.simulatedHint'))
      ]),
      type: 'info',
      duration: 6000
    })
    return
  }
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
    disposeReconnect = onHubReconnected(() => {
      void sound.refresh()
      void reviewMissedCriticalAlarms()
    })
    const auth = useAuthStore()
    disposeHubClosed = onHubClosed(() => {
      disconnectSince = Date.now()
      void auth.confirmSession().catch(() => undefined)
    })
    void maybeConfirmMute()
  }

  function stop(): void {
    const wasRunning = running
    running = false
    window.removeEventListener('pointerdown', unlockAudio, true)
    window.removeEventListener('keydown', unlockAudio, true)
    disposeAlarmHandler?.()
    disposeHubClosed?.()
    disposeReconnect?.()
    disposeAlarmHandler = undefined
    disposeHubClosed = undefined
    disposeReconnect = undefined
    sound.dispose()
    closeCriticalToasts()
    missedCriticalNotice.value = null
    disconnectSince = null
    mutePrompted = false
    // 登出或未登录才断开共享连接；从总览进入运行工作台不会走到这里。
    if (wasRunning) resetHub()
  }

  return {
    soundEnabled: sound.soundEnabled,
    schedule: sound.schedule,
    start,
    stop,
    missedCriticalNotice,
    dismissMissedCriticalNotice
  }
}

export function dismissMissedCriticalNotice(): void {
  missedCriticalNotice.value = null
}

export async function maybeConfirmMute(): Promise<void> {
  const sound = useAlarmSound()
  sound.syncFromUser()
  if (sound.soundEnabled.value || mutePrompted) return
  mutePrompted = true
  try {
    await ElMessageBox.confirm(tr('soundMute.confirmBody'), tr('soundMute.confirmTitle'), {
      confirmButtonText: tr('soundMute.acknowledge'),
      cancelButtonText: tr('soundMute.unmute'),
      closeOnClickModal: false,
      closeOnPressEscape: false,
      showClose: false,
      distinguishCancelAndClose: true,
      type: 'warning'
    })
  } catch {
    sound.soundEnabled.value = true
  }
}

async function reviewMissedCriticalAlarms(): Promise<void> {
  const from = disconnectSince
  disconnectSince = null
  if (from == null) return
  const to = Date.now()
  try {
    const events = await alarmApi.query({ live: true, unackedOnly: true, includeShelved: true, priorityFirst: true, limit: 2000 })
    const missed = missedCriticalAlarms(events, from, to)
    if (!missed.length) return
    missedCriticalNotice.value = { count: missed.length }
    const surface = alarmToastSurface(window.location.pathname)
    if (surface !== 'toast') return
    const handle: NotificationHandle = ElNotification({
      title: tr('alarmReconnect.missedTitle'),
      message: h('div', [
        h('div', tr('alarmReconnect.missedBody', [missed.length])),
        h('div', { style: 'margin-top: 4px; font-size: 12px; opacity: 0.72' }, tr('alarmReconnect.open'))
      ]),
      type: 'warning',
      duration: 0,
      customClass: 'scada-alarm-toast',
      onClick: () => {
        void import('../router').then(({ default: router }) => {
          void router.push({ path: '/alarms', query: { state: 'unack' } })
        })
        handle.close()
        missedCriticalNotice.value = null
      }
    })
  } catch {
    // 补查失败时仍依赖重连后的列表与徽标刷新，不阻断其它恢复步骤。
  }
}

export function resetAlarmSessionForTests(): void {
  running = false
  missedCriticalNotice.value = null
  disconnectSince = null
  mutePrompted = false
  window.removeEventListener('pointerdown', unlockAudio, true)
  window.removeEventListener('keydown', unlockAudio, true)
  disposeAlarmHandler?.()
  disposeHubClosed?.()
  disposeReconnect?.()
  disposeAlarmHandler = undefined
  disposeHubClosed = undefined
  disposeReconnect = undefined
  criticalListeners.clear()
  closeCriticalToasts()
}

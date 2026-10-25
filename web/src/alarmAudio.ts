// 浏览器报警声：按最高未确认等级循环播放；尊重用户静音；消音/无未确认时停止。
import type { AlarmEvent, AlarmLevel } from './types'

type Tone = { freq: number; type: OscillatorType; gap: number }

const tones: Record<string, Tone> = {
  critical: { freq: 880, type: 'square', gap: 350 },
  warning: { freq: 620, type: 'sine', gap: 700 },
  info: { freq: 440, type: 'triangle', gap: 1200 }
}

let ctx: AudioContext | null = null
let timer: number | undefined
let currentKey: string | null = null
let muted = false

function ensureCtx(): AudioContext | null {
  try {
    const Ctor = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    if (!Ctor) return null
    ctx ??= new Ctor()
    if (ctx.state === 'suspended') void ctx.resume()
    return ctx
  } catch {
    return null
  }
}

function beep(key: string): void {
  const tone = tones[key] ?? tones.warning
  const audio = ensureCtx()
  if (!audio) return
  // 浏览器自动播放策略：未经用户手势解锁时 AudioContext 处于 suspended，
  // 此时排队的振荡器不会发声，恢复后又会集中爆响，因此直接跳过。
  if (audio.state !== 'running') return
  const osc = audio.createOscillator()
  const gain = audio.createGain()
  osc.connect(gain)
  gain.connect(audio.destination)
  osc.type = tone.type
  osc.frequency.value = tone.freq
  gain.gain.setValueAtTime(0.07, audio.currentTime)
  gain.gain.exponentialRampToValueAtTime(0.0001, audio.currentTime + 0.28)
  osc.start()
  osc.stop(audio.currentTime + 0.3)
}

export function setAlarmMute(value: boolean): void {
  muted = value
  if (muted) stopAlarmLoop()
}

export function playAlarmOnce(level: AlarmLevel | string): void {
  if (muted) return
  beep(String(level).toLowerCase())
}

/**
 * 在用户手势（点击/按键）回调中调用一次，解锁浏览器的自动播放限制。
 * 不预热的话，页面加载后第一条推送到达时没有手势，AudioContext 一直是 suspended，
 * 表现为「第一次报警不响」。
 */
export function primeAlarmAudio(): void {
  const audio = ensureCtx()
  if (audio && audio.state === 'suspended') void audio.resume()
}

/**
 * 处理服务端 alarm 推送并决定是否发声。
 * 这是全应用唯一的报警发声入口：页面只负责刷新数据，不再各自播放声音，
 * 否则共享连接后仍会出现重复发声，且绕过静音开关。
 */
export function handleAlarmPush(payload: { action: string; alarm: AlarmEvent }): void {
  if (muted) return
  const alarm = payload.alarm
  // 已暂挂/抑制/抖动的报警只入库不出声
  if (alarm.shelved || alarm.suppressed || alarm.chattering) return
  const raised = payload.action === 'raise'
  const restored =
    (payload.action === 'unshelve' || payload.action === 'unsuppress') && !alarm.ackTs && !alarm.endTs
  if (!raised && !restored) return
  // 循环音正在按最高未确认等级播放时不再叠加单次提示音，避免听成急促双响
  if (timer !== undefined) return
  beep(String(alarm.level).toLowerCase())
}

export function startAlarmLoop(level: AlarmLevel | string | null | undefined): void {
  if (muted || !level) { stopAlarmLoop(); return }
  const key = String(level).toLowerCase()
  if (currentKey === key && timer !== undefined) return
  stopAlarmLoop()
  currentKey = key
  const tone = tones[key] ?? tones.warning
  beep(key)
  timer = window.setInterval(() => beep(key), tone.gap + 300)
}

export function stopAlarmLoop(): void {
  if (timer !== undefined) window.clearInterval(timer)
  timer = undefined
  currentKey = null
}

export function currentAlarmSound(): string | null { return currentKey }

// 浏览器报警声：按最高未确认等级循环播放；尊重用户静音；消音/无未确认时停止。
import type { AlarmLevel } from './types'

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

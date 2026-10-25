import { isTextProtocol } from './protocols'
import type { AlarmLevel, Permission, TagQuality, UserAccess } from './types'
import { tr } from './i18n'

export function formatTime(ms?: number | null): string {
  if (!ms) return '-'
  const d = new Date(ms)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes()
  )}:${pad(d.getSeconds())}`
}

export function formatShortTime(ms?: number | null): string {
  if (!ms) return '-'
  const d = new Date(ms)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(
    d.getSeconds()
  )}`
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(2)} GB`
}

export function formatValue(value?: number | null, digits = 2): string {
  if (value === null || value === undefined || Number.isNaN(value)) return '-'
  if (Math.abs(value) >= 100000 || (value !== 0 && Math.abs(value) < 0.001)) return value.toExponential(2)
  return value.toFixed(digits)
}

export const qualityLabels: Record<TagQuality, string> = {
  Good: tr('utils.kf78d03'),
  Bad: tr('utils.k5e3fe5'),
  CommFailure: tr('utils.k05e165'),
  Uncertain: tr('utils.k520965')
}

export function qualityType(quality: TagQuality): 'success' | 'warning' | 'danger' {
  if (quality === 'Good') return 'success'
  if (quality === 'Bad' || quality === 'Uncertain') return 'warning'
  return 'danger'
}

export const levelLabels: Record<AlarmLevel, string> = {
  Info: tr('utils.kab3656'),
  Warning: tr('utils.k5521e3'),
  Critical: tr('utils.k81ffc6')
}

export function levelType(level: AlarmLevel): 'info' | 'warning' | 'danger' {
  if (level === 'Critical') return 'danger'
  if (level === 'Warning') return 'warning'
  return 'info'
}

export function levelLabel(level?: string | null): string {
  return level ? (levelLabels[level as AlarmLevel] ?? level) : '-'
}

export function notificationType(level?: string | null): 'info' | 'warning' | 'error' {
  if (level === 'Critical') return 'error'
  if (level === 'Warning') return 'warning'
  return 'info'
}

export const areaLabels: Record<string, string> = {
  DataBlock: tr('utils.kfe529c'), Memory: tr('utils.k909a63'), Input: tr('utils.k8bcfd0'), Output: tr('utils.k4341c2'),
  OpcNode: tr('utils.kf7748d'),
  Address: tr('utils.k349db8'),
  Coil: tr('utils.kf019ca'),
  DiscreteInput: tr('utils.k97accb'),
  HoldingRegister: tr('utils.k21dca7'),
  InputRegister: tr('utils.kbfbcca')
}

export const dataTypeLabels: Record<string, string> = {
  Bool: tr('utils.ka091ff'),
  Int16: 'Int16',
  UInt16: 'UInt16',
  Int32: 'Int32',
  UInt32: 'UInt32',
  Float: tr('utils.k1a1a34'),
  Double: tr('utils.k42ceb5')
}

export function protocolAreas(protocol?: string): Record<string, string> {
  const keys = isTextProtocol(protocol) ? ['Address'] : protocol === 'OpcUa' ? ['OpcNode'] : protocol === 'SiemensS7' ? ['DataBlock', 'Memory', 'Input', 'Output'] : ['Coil', 'DiscreteInput', 'HoldingRegister', 'InputRegister']
  return Object.fromEntries(Object.entries(areaLabels).filter(([key]) => keys.includes(key)))
}

export const archiveModeLabels: Record<string, string> = {
  None: tr('utils.kde1b2a'),
  OnChange: tr('utils.kda2ce3'),
  Periodic: tr('utils.k164395')
}

export const alarmTypeLabels: Record<string, string> = {
  High: tr('utils.k88deca'),
  HighHigh: tr('utils.kfe8edb'),
  Low: tr('utils.kb44804'),
  LowLow: tr('utils.k09df8e'),
  StateChange: tr('utils.kc5277e')
}

export const roleLabels: Record<string, string> = {
  Admin: tr('utils.kef84e7'),
  Operator: tr('utils.k0c7c09'),
  Viewer: tr('utils.k41d997'),
  None: tr('utils.kcc3f9c')
}

export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}

export function exportCsv(fileName: string, headers: string[], rows: (string | number)[][]): void {
  const escape = (cell: string | number) => {
    const text = String(cell ?? '')
    return /[",\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
  }
  const lines = [headers.map(escape).join(',')]
  for (const row of rows) {
    lines.push(row.map(escape).join(','))
  }
  const blob = new Blob(['\uFEFF' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' })
  downloadBlob(blob, fileName)
}

export function playAlarmSound(level: AlarmLevel): void {
  try {
    const AudioCtor = window.AudioContext ?? (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext
    if (!AudioCtor) return
    const ctx = new AudioCtor()
    const oscillator = ctx.createOscillator()
    const gain = ctx.createGain()
    oscillator.connect(gain)
    gain.connect(ctx.destination)
    oscillator.type = level === 'Critical' ? 'square' : 'sine'
    oscillator.frequency.value = level === 'Critical' ? 880 : 620
    gain.gain.setValueAtTime(0.08, ctx.currentTime)
    gain.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.45)
    oscillator.start()
    oscillator.stop(ctx.currentTime + 0.45)
    setTimeout(() => ctx.close(), 800)
  } catch {
    // audio is a nice-to-have
  }
}

export function isAdmin(role?: string): boolean {
  return role === 'Admin'
}

export function canOperate(role?: string): boolean {
  return role === 'Admin' || role === 'Operator'
}

// 权限判断：前端只用于隐藏/禁用入口，最终以服务端校验为准。
type AccessHolder = { role?: string; access?: UserAccess } | null | undefined

export function hasGlobal(user: AccessHolder, permission: Permission): boolean {
  if (!user) return false
  if (user.access) return user.access.global.includes(permission)
  return legacy(user.role, permission)
}

export function hasAny(user: AccessHolder, permission: Permission): boolean {
  if (!user) return false
  if (user.access) return user.access.global.includes(permission) || user.access.areas.some(a => a.permissions.includes(permission))
  return legacy(user.role, permission)
}

export function hasInArea(user: AccessHolder, permission: Permission, areaId?: number | null): boolean {
  if (!user) return false
  if (hasGlobal(user, permission)) return true
  if (areaId == null || !user.access) return false
  return user.access.areas.some(a => a.areaId === areaId && a.permissions.includes(permission))
}

function legacy(role: string | undefined, permission: Permission): boolean {
  if (role === 'Admin') return true
  if (role === 'Operator') return permission === 'View' || permission === 'Control' || permission === 'AckAlarm'
  if (role === 'Viewer') return permission === 'View'
  return false
}

export const permissionLabels: Record<Permission, string> = {
  View: tr('utils.kf7acef'),
  Control: tr('utils.k223826'),
  AckAlarm: tr('utils.kff8fce'),
  Configure: tr('utils.kd7d7ce'),
  ManageUsers: tr('utils.kbaf847'),
  System: tr('utils.k8f7aed')
}

export const alarmStateLabels: Record<string, string> = {
  Unack: tr('utils.k0642fa'),
  Acked: tr('utils.kd9fea6'),
  RtnUnack: tr('utils.kffe3f8'),
  Normal: tr('utils.k802043')
}

export function alarmStateType(state?: string | null): 'danger' | 'warning' | 'info' | 'success' {
  if (state === 'Unack') return 'danger'
  if (state === 'RtnUnack') return 'warning'
  if (state === 'Acked') return 'info'
  return 'success'
}

export const alarmActionLabels: Record<string, string> = {
  raise: tr('utils.k27eccf'),
  change: tr('utils.kc5277e'),
  recover: tr('utils.k79748c'),
  ack: tr('utils.kb56d9a'),
  comment: tr('utils.ke03614'),
  shelve: tr('utils.k06d561'),
  unshelve: tr('utils.kcd308e'),
  suppress: tr('utils.kaa2e2b'),
  unsuppress: tr('utils.k5e6c1d'),
  notify: tr('utils.k7a66c0'),
  escalate: tr('utils.k7b4e6e')
}

export const suppressModeLabels: Record<string, string> = {
  None: tr('utils.k58c2fc'),
  WhenOn: tr('utils.k709394'),
  WhenOff: tr('utils.k86ec86')
}

export const channelTypeLabels: Record<string, string> = {
  Email: tr('utils.k4c544f'),
  Webhook: 'Webhook',
  SmsHttp: tr('utils.ka50103')
}

export function minuteToTime(minute: number): string {
  const m = ((minute % 1440) + 1440) % 1440
  return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
}

export function timeToMinute(text: string): number {
  const [h, m] = text.split(':').map((x) => Number(x))
  return (Number.isFinite(h) ? h : 0) * 60 + (Number.isFinite(m) ? m : 0)
}

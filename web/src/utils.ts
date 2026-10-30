import { isTextProtocol } from './protocols'
import type { AlarmLevel, Permission, TagQuality } from './types'
import { tr } from './i18n'
import { playAlarmOnce } from './alarmAudio'
import { hasAny, hasGlobal, hasInArea, type AccessHolder } from './access/policies'

// 兼容旧导入：权限判定实现已收敛到 access/policies.ts。
export { hasAny, hasGlobal, hasInArea }
export type { AccessHolder }

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
  playAlarmOnce(level)
}

export function isAdmin(role?: string): boolean {
  return role === 'Admin'
}

export function canOperate(role?: string): boolean {
  return role === 'Admin' || role === 'Operator'
}

// 权限判断实现见 access/policies.ts，这里仅保留业务级组合判断。

// 确认按钮与服务端 CanAlarm 一致。areas 来自报警列表：有测点或设备时必须覆盖全部区域；都没有则是系统报警，有任一确认权即可。
export function canAckAlarmEvent(
  user: AccessHolder,
  alarm: { tagId?: number | null; deviceId?: number | null; areas?: number[] | null },
  deviceAreas?: ReadonlyMap<number, number>
): boolean {
  if (!hasAny(user, 'AckAlarm')) return false
  if (alarm.areas) {
    if (alarm.areas.length === 0) return (alarm.tagId == null && alarm.deviceId == null) || hasGlobal(user, 'AckAlarm')
    return alarm.areas.every((area) => hasInArea(user, 'AckAlarm', area))
  }
  if (alarm.deviceId == null) return true
  const area = deviceAreas?.get(alarm.deviceId)
  if (area == null) return true
  return hasInArea(user, 'AckAlarm', area)
}

// 已知设备区域时按该区域判权。设备列表还没到、或设备不在可见范围内时，仅无授权明细或持全局权限者放行，其余返回 false（按钮隐藏），服务端仍会再校验。
export function canActOnDevice(user: AccessHolder, permission: Permission, devices: readonly { id: number; areaId?: number | null }[], deviceId?: number | null): boolean {
  if (!hasAny(user, permission)) return false
  if (deviceId == null) return false
  const area = devices.find(d => d.id === deviceId)?.areaId
  if (area == null) return !user?.access || hasGlobal(user, permission)
  return hasInArea(user, permission, area)
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

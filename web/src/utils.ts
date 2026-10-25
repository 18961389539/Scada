import { isTextProtocol } from './protocols'
import type { AlarmLevel, Permission, TagQuality, UserAccess } from './types'

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
  Good: '正常',
  Bad: '故障',
  CommFailure: '通信中断',
  Uncertain: '质量不确定'
}

export function qualityType(quality: TagQuality): 'success' | 'warning' | 'danger' {
  if (quality === 'Good') return 'success'
  if (quality === 'Bad' || quality === 'Uncertain') return 'warning'
  return 'danger'
}

export const levelLabels: Record<AlarmLevel, string> = {
  Info: '提示',
  Warning: '警告',
  Critical: '严重'
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
  DataBlock: '数据块 DB', Memory: '存储区 M', Input: '输入 I（只读）', Output: '输出 Q',
  OpcNode: 'OPC UA 节点',
  Address: '文本地址',
  Coil: '线圈',
  DiscreteInput: '离散输入',
  HoldingRegister: '保持寄存器',
  InputRegister: '输入寄存器'
}

export const dataTypeLabels: Record<string, string> = {
  Bool: '布尔',
  Int16: 'Int16',
  UInt16: 'UInt16',
  Int32: 'Int32',
  UInt32: 'UInt32',
  Float: '浮点',
  Double: '双精度'
}

export function protocolAreas(protocol?: string): Record<string, string> {
  const keys = isTextProtocol(protocol) ? ['Address'] : protocol === 'OpcUa' ? ['OpcNode'] : protocol === 'SiemensS7' ? ['DataBlock', 'Memory', 'Input', 'Output'] : ['Coil', 'DiscreteInput', 'HoldingRegister', 'InputRegister']
  return Object.fromEntries(Object.entries(areaLabels).filter(([key]) => keys.includes(key)))
}

export const archiveModeLabels: Record<string, string> = {
  None: '不存储',
  OnChange: '变化存储',
  Periodic: '周期存储'
}

export const alarmTypeLabels: Record<string, string> = {
  High: '高限',
  HighHigh: '高高限',
  Low: '低限',
  LowLow: '低低限',
  StateChange: '状态变化'
}

export const roleLabels: Record<string, string> = {
  Admin: '管理员',
  Operator: '操作员',
  Viewer: '查看员',
  None: '仅区域授权'
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
  View: '查看',
  Control: '控制',
  AckAlarm: '确认报警',
  Configure: '配置',
  ManageUsers: '用户管理',
  System: '系统维护'
}

export const alarmStateLabels: Record<string, string> = {
  Unack: '未确认',
  Acked: '已确认',
  RtnUnack: '已恢复未确认',
  Normal: '已恢复已确认'
}

export function alarmStateType(state?: string | null): 'danger' | 'warning' | 'info' | 'success' {
  if (state === 'Unack') return 'danger'
  if (state === 'RtnUnack') return 'warning'
  if (state === 'Acked') return 'info'
  return 'success'
}

export const alarmActionLabels: Record<string, string> = {
  raise: '触发',
  change: '状态变化',
  recover: '恢复',
  ack: '确认',
  comment: '备注',
  shelve: '暂挂',
  unshelve: '解除暂挂',
  suppress: '抑制',
  unsuppress: '解除抑制',
  notify: '通知',
  escalate: '升级通知'
}

export const suppressModeLabels: Record<string, string> = {
  None: '不抑制',
  WhenOn: '条件测点=1 时抑制',
  WhenOff: '条件测点=0 时抑制（如设备停机）'
}

export const channelTypeLabels: Record<string, string> = {
  Email: '邮件 (SMTP)',
  Webhook: 'Webhook',
  SmsHttp: '短信 (HTTP 网关)'
}

export function minuteToTime(minute: number): string {
  const m = ((minute % 1440) + 1440) % 1440
  return `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
}

export function timeToMinute(text: string): number {
  const [h, m] = text.split(':').map((x) => Number(x))
  return (Number.isFinite(h) ? h : 0) * 60 + (Number.isFinite(m) ? m : 0)
}

export type Role = 'Admin' | 'Operator' | 'Viewer'
export type TagArea = 'Coil' | 'DiscreteInput' | 'HoldingRegister' | 'InputRegister' | 'DataBlock' | 'Memory' | 'Input' | 'Output' | 'OpcNode'
export type TagDataType = 'Bool' | 'Int16' | 'UInt16' | 'Int32' | 'UInt32' | 'Float' | 'Double'
export type ByteOrder = 'ABCD' | 'BADC' | 'CDAB' | 'DCBA'
export type ArchiveMode = 'None' | 'OnChange' | 'Periodic'
export type TagQuality = 'Good' | 'Bad' | 'CommFailure' | 'Uncertain'
export type AlarmType = 'High' | 'HighHigh' | 'Low' | 'LowLow' | 'StateChange'
export type AlarmLevel = 'Info' | 'Warning' | 'Critical'

export interface AuthUser {
  id: number
  userName: string
  displayName: string
  role: Role
}

export interface LoginResult {
  token: string
  expiresAt: string
  user: AuthUser
}

export interface Device {
  id: number
  name: string
  protocol: string
  host: string
  port: number
  stationNumber: number
  byteOrder: ByteOrder
  pollIntervalMs: number
  timeoutMs: number
  enabled: boolean
  serialPort: string
  baudRate: number
  dataBits: number
  parity: 'None' | 'Odd' | 'Even'
  stopBits: 'One' | 'Two'
  cpu: 'S7200' | 'S7300' | 'S7400' | 'S71200' | 'S71500'
  rack: number
  slot: number
  opcEndpointUrl: string
  opcSecurityMode: 'None' | 'Sign' | 'SignAndEncrypt'
  opcSecurityPolicy: 'None' | 'Basic256Sha256' | 'Aes128Sha256RsaOaep' | 'Aes256Sha256RsaPss'
  opcUserName: string
  hasOpcPassword: boolean
}

export type DeviceInput = Omit<Device, 'id' | 'protocol' | 'hasOpcPassword'> & { protocol: string; opcPassword?: string | null; clearOpcPassword?: boolean }

export interface Tag {
  id: number
  deviceId: number
  name: string
  description?: string | null
  area: TagArea
  address: number
  dbNumber: number
  bitOffset: number
  nodeId: string
  dataType: TagDataType
  scale: number
  offset: number
  unit?: string | null
  writable: boolean
  archiveMode: ArchiveMode
  deadband: number
  archivePeriodSec: number
  minValue?: number | null
  maxValue?: number | null
  registerCount: number
}

export type TagInput = Omit<Tag, 'id' | 'registerCount'>

export interface AlarmRule {
  id: number
  tagId: number
  type: AlarmType
  threshold: number
  level: AlarmLevel
  delaySec: number
  recoverDeadband: number
  enabled: boolean
  message?: string | null
}

export type AlarmRuleInput = Omit<AlarmRule, 'id'>

export interface AlarmEvent {
  id: number
  tagId: number | null
  deviceId: number | null
  ruleId: number | null
  level: AlarmLevel
  value: number
  startTs: number
  endTs: number | null
  ackUser: string | null
  ackTs: number | null
  message: string
}

export interface TagValue {
  tagId: number
  value: number
  quality: TagQuality
  ts: number
}

export interface HistoryPoint {
  ts: number
  value: number
  quality: TagQuality
}

export interface HistorySeries {
  tagId: number
  points: HistoryPoint[]
}

export interface HistoryResult {
  resolution: string
  series: HistorySeries[]
}

export interface ReportPoint {
  ts: number
  avg: number
  min: number
  max: number
  count: number
}

export interface ReportSeries {
  tagId: number
  points: ReportPoint[]
}

export interface ReportResult {
  type: string
  from: number
  to: number
  series: ReportSeries[]
}

export interface OperationLog {
  id: number
  userId: number | null
  userName: string
  ts: number
  action: string
  tagId: number | null
  oldValue: string | null
  newValue: string | null
  result: string | null
}

export interface UserItem {
  id: number
  userName: string
  displayName: string
  role: Role
  enabled: boolean
}

export interface DeviceStatus {
  deviceId: number
  name: string
  online: boolean
  error: string | null
  lastPollTs: number
  lastCycleMs: number
  tagCount: number
  reconnectAttempts: number
  commFailures: number
}

export interface Health {
  status: string
  environment: string
  serverTime: string
  uptimeSeconds: number
  devices: DeviceStatus[]
  onlineDevices: number
  totalDevices: number
  configuredTags: number
  busBacklog: number
  disk: { root: string; totalGb: number; freeGb: number }
  storage: { configDb: string; configDbBytes: number; historyBytes: number }
}

export interface ImportResult {
  created: number
  updated: number
  skipped: number
  errors: string[]
}

export interface Paged<T> {
  total: number
  page: number
  pageSize: number
  items: T[]
}

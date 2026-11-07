export type Role = 'Admin' | 'Operator' | 'Viewer' | 'None'
export type Permission = 'View' | 'Control' | 'AckAlarm' | 'Configure' | 'ManageUsers' | 'System'

export interface UserAccess {
  global: Permission[]
  areas: { areaId: number; permissions: Permission[] }[]
}

export interface Area {
  id: number
  name: string
  description?: string | null
  deviceCount?: number
  isDefault?: boolean
}

export interface AccessRoleItem {
  id: number
  name: string
  description?: string | null
  permissions: Permission[]
  assignments: number
}

export interface RoleCatalog {
  permissions: Permission[]
  builtIn: { role: Role; permissions: Permission[] }[]
  roles: AccessRoleItem[]
}

export interface UserAssignment {
  id: number
  roleId: number
  roleName: string
  areaId: number | null
  areaName: string | null
  permissions: Permission[]
}
export type TagArea = 'Coil' | 'DiscreteInput' | 'HoldingRegister' | 'InputRegister' | 'DataBlock' | 'Memory' | 'Input' | 'Output' | 'OpcNode' | 'Address'
export type TagDataType = 'Bool' | 'Int16' | 'UInt16' | 'Int32' | 'UInt32' | 'Float' | 'Double'
export type ByteOrder = 'ABCD' | 'BADC' | 'CDAB' | 'DCBA'
export type ArchiveMode = 'None' | 'OnChange' | 'Periodic'
export type TagQuality = 'Good' | 'Bad' | 'CommFailure' | 'Uncertain'
export type AlarmType = 'High' | 'HighHigh' | 'Low' | 'LowLow' | 'StateChange' | 'Discrete' | 'BadQuality' | 'Expression'
export type AlarmActiveWhen = 'On' | 'Off' | 'Change'
export type AlarmLevel = 'Info' | 'Warning' | 'Critical'
export type AlarmCategory = 'Process' | 'Equipment' | 'Safety' | 'Diagnostic'
export type AlarmConsequence = 'Minor' | 'Major' | 'Severe' | 'Safety'

export interface AuthUser {
  id: number
  userName: string
  displayName: string
  role: Role
  access?: UserAccess
  locale?: string | null
  /** 首登或口令被管理员重置后为 true：改密前服务端只放行查自己与改密。 */
  mustChangePassword?: boolean
}

export interface LoginResult {
  token: string
  expiresAt: string
  user: AuthUser
}

export interface Device {
  id: number
  gatewayId: number | null
  areaId?: number
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
  protocolOptions?: string
  simulationEnabled: boolean
  simulationOptions: string
}

export type DeviceInput = Omit<Device, 'id' | 'protocol' | 'hasOpcPassword' | 'gatewayId'> & { protocol: string; opcPassword?: string | null; clearOpcPassword?: boolean }

export interface SimulationTagView {
  tagId: number
  name: string
  dataType: string
  value: number
  forced: boolean
  quality: string
  error?: string | null
}

export interface SimulationView {
  deviceId: number
  deviceName: string
  enabled: boolean
  waveform: string
  periodSec: number
  min?: number | null
  max?: number | null
  noisePercent: number
  fault: string
  faultUntilTs: number
  items: SimulationTagView[]
}

export interface Tag {
  id: number
  deviceId: number | null
  areaId?: number | null
  kind: 'Physical' | 'Calculated' | 'Derived'
  jobId?: number | null
  jobOutput?: string | null
  expression: string
  calculationEnabled: boolean
  calculationIntervalMs: number
  inputMaxAgeMs: number
  calculationRevision: number
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

export type TagInput = Omit<Tag, 'id' | 'registerCount' | 'kind' | 'expression' | 'calculationEnabled' | 'calculationIntervalMs' | 'inputMaxAgeMs' | 'calculationRevision' | 'deviceId'> & { deviceId: number }

export interface AlarmRule {
  id: number
  tagId: number
  type: AlarmType
  /** 位报警（Discrete）的触发条件：On=读到 1 报警，Off=读到 0 报警，Change=每次翻转报警。 */
  activeWhen?: AlarmActiveWhen
  threshold: number
  level: AlarmLevel
  category: AlarmCategory
  consequence: AlarmConsequence
  responseTimeSec: number
  autoPriority: boolean
  delaySec: number
  recoverDeadband: number
  enabled: boolean
  message?: string | null
  cause?: string | null
  action?: string | null
  messageEn?: string | null
  causeEn?: string | null
  actionEn?: string | null
  offDelaySec?: number
  /** 数值字位报警的二进制位号（0 起）；Bool 测点为空。 */
  bitIndex?: number | null
  /** 复合条件表达式（#测点号引用多个测点）。 */
  expression?: string | null
  suppressTagId?: number | null
  suppressMode?: AlarmSuppressMode
  outOfService?: boolean
  outOfServiceReason?: string | null
}

export type AlarmSuppressMode = 'None' | 'WhenOn' | 'WhenOff'
export type AlarmRuleInput = Omit<AlarmRule, 'id' | 'outOfService' | 'outOfServiceReason'>
export type AlarmState = 'Unack' | 'Acked' | 'RtnUnack' | 'Normal'
export type AlarmListState = 'active' | 'unack' | 'rtnUnack' | 'acked' | 'shelved' | 'suppressed'

export interface AlarmPageQuery {
  from?: number
  to?: number
  live?: boolean
  priorityFirst?: boolean
  state?: AlarmListState
  level?: AlarmLevel
  category?: AlarmCategory
  keyword?: string
  tagId?: number
  offset?: number
  limit?: number
}

export interface AlarmPage {
  items: AlarmEvent[]
  total: number
  offset: number
  limit: number
  capped: boolean
}

export interface AlarmActivity {
  id: number
  alarmEventId: number
  ts: number
  user?: string | null
  action: string
  note?: string | null
}

export interface AlarmNotificationLog {
  id: number
  alarmEventId: number
  routeId: number
  channelId: number
  action: string
  ts: number
  success: boolean
  error?: string | null
}

export interface AlarmTimeline {
  alarm: AlarmEvent
  activities: AlarmActivity[]
  notifications: AlarmNotificationLog[]
  guidance?: { cause?: string | null; action?: string | null; causeEn?: string | null; actionEn?: string | null } | null
}

export interface AlarmShelve {
  id: number
  ruleId: number
  tagId: number
  reason: string
  user: string
  startTs: number
  untilTs: number
  clearedTs?: number | null
  clearedBy?: string | null
}

export interface AlarmFloodStatus {
  count: number
  threshold: number
  inFlood: boolean
  windowStartTs: number
  windowEndTs: number
}

export interface AlarmFloodAreaStatus {
  areaId: number | null
  areaName: string | null
  count: number
  threshold: number
  exitThreshold: number
  inFlood: boolean
  windowStartTs: number
  windowEndTs: number
  floodStartTs: number | null
  peak: number
  firstOutEventId: number | null
  suppressedCount: number
}

export interface AlarmFloodGuardStatus {
  autoSuppress: boolean
  firstOut: boolean
  suppressMaxLevel: AlarmLevel
  anyInFlood: boolean
  areas: AlarmFloodAreaStatus[]
}

export interface AlarmSummary {
  unack: number
  /** 当前用户能够确认的未确认报警；可选字段，界面目前未读取（确认按钮权限由 canAckAlarmEvent 计算）。 */
  ackableUnack?: number
  acked: number
  rtnUnack: number
  shelved: number
  suppressed: number
  chattering: number
  shelvedRules: number
  outOfServiceRules: number
  flood: AlarmFloodStatus
  floodGuard?: AlarmFloodGuardStatus
}

export interface AlarmKpi {
  from: number
  to: number
  hours: number
  total: number
  annunciated: number
  shelved: number
  suppressed: number
  chattering: number
  perHour: number
  per10Min: number
  peak10Min: number
  floodThreshold: number
  floodBuckets: number
  percentTimeInFlood: number
  floods: { startTs: number; count: number }[]
  operatorPositions: number
  perOperatorHour: number
  byLevel: Record<string, number>
  top: { ruleId?: number | null; tagId?: number | null; deviceId?: number | null; message: string; level: AlarmLevel; count: number; chattering: number }[]
  standing: { id: number; ruleId?: number | null; tagId?: number | null; message: string; level: AlarmLevel; startTs: number; hours: number; acked: boolean }[]
  operators: { user: string; acks: number; acksPerHour: number; avgAckSeconds: number; notes: number }[]
}

export type NotificationChannelType = 'Email' | 'Webhook' | 'SmsHttp'

export interface NotificationChannelSettings {
  host?: string | null
  port?: number
  useSsl?: boolean
  userName?: string | null
  from?: string | null
  url?: string | null
  headerName?: string | null
  timeoutMs?: number
}

export interface NotificationChannel {
  id: number
  name: string
  type: NotificationChannelType
  enabled: boolean
  settings: NotificationChannelSettings
  hasSecret: boolean
  secret?: string | null
}

export interface NotificationChannelInput {
  name: string
  type: NotificationChannelType
  enabled: boolean
  settings: NotificationChannelSettings
  secret?: string | null
}

export interface DutyShift {
  id: number
  name: string
  startMinute: number
  endMinute: number
  days: number
  enabled: boolean
}

export interface NotificationRoute {
  id: number
  name: string
  enabled: boolean
  channelId: number
  areaId: number | null
  minLevel: AlarmLevel
  shiftId: number | null
  recipients: string
  onRaise: boolean
  onRecover: boolean
  escalateAfterMinutes: number | null
}

export interface AlarmEvent {
  id: number
  tagId: number | null
  deviceId: number | null
  areas?: number[]
  gatewayId?: number | null
  endReason?: string | null
  ruleId: number | null
  level: AlarmLevel
  category?: AlarmCategory
  consequence?: AlarmConsequence
  responseTimeSec?: number
  value: number
  startTs: number
  endTs: number | null
  ackUser: string | null
  ackTs: number | null
  message: string
  messageEn?: string | null
  shelved?: boolean
  suppressed?: boolean
  suppressReason?: string | null
  chattering?: boolean
  firstOut?: boolean
  /** 设备级首出：同设备本次报警片段中最早触发的一条。 */
  firstUp?: boolean
  state?: AlarmState
}

export interface TagValue {
  tagId: number
  value: number
  quality: TagQuality
  ts: number
  sourceTs?: number | null
  error?: string | null
  definitionRevision?: number | null
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

export interface HistorySnapshotItem {
  tagId: number
  value?: number | null
  quality?: TagQuality | null
  sourceTs?: number | null
  error?: string | null
}

export interface HistorySnapshotResult {
  ts: number
  items: HistorySnapshotItem[]
}

export interface HistoryResult {
  resolution: string
  downsampled?: boolean
  pointLimit?: number
  series: HistorySeries[]
}

export interface HistoryQualityInterval {
  quality: TagQuality
  from: number
  to: number
}

export interface HistoryTagAnalysis {
  tagId: number
  samples: number
  goodSamples: number
  spanMs: number
  goodMs: number
  coverage: number
  timeWeightedAvg: number | null
  min: number | null
  max: number | null
  integralHours: number
  quality: HistoryQualityInterval[]
  qualityTruncated: boolean
}

export interface HistoryAnalysisResult {
  from: number
  to: number
  resolution: string
  tags: HistoryTagAnalysis[]
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
  targetType?: string | null
  targetId?: string | null
  details?: string | null
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

export interface HistoryCapacity {
  checkedAt: string
  totalBytes: number
  rawBytes: number
  aggregateBytes: number
  journalBytes: number
  spoolBytes: number
  otherBytes: number
  rawFiles: number
  aggregateFiles: number
  scanComplete: boolean
  disk: { root: string; totalBytes: number | null; freeBytes: number | null }
  growthBytesPerDay: number | null
  daysUntilDiskWarn: number | null
  rawRetentionMonths: number
  fiveMinuteRetentionDays: number
  hourRetentionDays: number
}

export interface Health {
  status: string
  environment: string
  serverTime: string
  /** 进程运行秒数（服务重启即归零） */
  uptimeSeconds: number
  /** 主机开机秒数（机器重启才归零） */
  hostUptimeSeconds?: number
  devices: DeviceStatus[]
  onlineDevices: number
  totalDevices: number
  configuredTags: number
  busBacklog: number
  busDropped?: number
  warnings?: { code: string; message: string }[]
  historyCapacity?: HistoryCapacity | null
  historyQueries?: {
    active: number; maxConcurrent: number; timeoutSeconds: number; maxReadPoints: number
    analysisMaxPoints: number; rejected: number; timedOut: number; readLimited: number
  } | null
  disk: { root: string; totalGb: number; freeGb: number }
  storage: { configDb: string; configDbBytes: number; historyBytes: number }
  ha?: { node: string; role: 'Primary' | 'Standby'; primaryUrl: string | null } | null
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

export type AssetStatus = 'InService' | 'Standby' | 'Maintenance' | 'Retired'
export type WorkOrderType = 'Corrective' | 'Preventive' | 'Inspection' | 'Improvement'
export type WorkOrderPriority = 'Low' | 'Normal' | 'High' | 'Urgent'
export type WorkOrderStatus = 'Open' | 'InProgress' | 'OnHold' | 'Completed' | 'Cancelled'
export type SparePartMovementType = 'In' | 'Out' | 'Adjust'

export interface Asset {
  id: number
  code: string
  name: string
  category: string
  model: string
  manufacturer: string
  serialNumber: string
  location: string
  areaId: number
  deviceId: number | null
  status: AssetStatus
  commissionedTs: number | null
  warrantyUntilTs: number | null
  description: string
  enabled: boolean
  createdTs: number
  updatedTs: number
  updatedBy: string
}

export type AssetInput = Omit<Asset, 'id' | 'createdTs' | 'updatedTs' | 'updatedBy'>

export interface MaintenanceWorkOrder {
  id: number
  number: string
  title: string
  assetId: number | null
  deviceId: number | null
  areaId: number
  type: WorkOrderType
  priority: WorkOrderPriority
  status: WorkOrderStatus
  description: string
  assignee: string
  reporter: string
  plannedStartTs: number | null
  plannedEndTs: number | null
  startedTs: number | null
  completedTs: number | null
  downtimeMinutes: number
  resolution: string
  createdTs: number
  updatedTs: number
  updatedBy: string
}

export type WorkOrderInput = Pick<MaintenanceWorkOrder,
  'title' | 'assetId' | 'deviceId' | 'areaId' | 'type' | 'priority' | 'description' | 'assignee' | 'reporter' | 'plannedStartTs' | 'plannedEndTs'>

export interface SparePart {
  id: number
  code: string
  name: string
  specification: string
  unit: string
  stock: number
  minStock: number
  location: string
  supplier: string
  unitPrice: number | null
  description: string
  enabled: boolean
  createdTs: number
  updatedTs: number
}

export type SparePartInput = Omit<SparePart, 'id' | 'createdTs' | 'updatedTs'>

export interface SparePartMovement {
  id: number
  partId: number
  type: SparePartMovementType
  quantity: number
  balanceAfter: number
  workOrderId: number | null
  operator: string
  note: string
  ts: number
}

export type SpcChartType = 'Individuals' | 'XbarR'
export type SpcViolationRule = 'Beyond3Sigma' | 'TwoOfThreeBeyond2Sigma' | 'FourOfFiveBeyond1Sigma' | 'EightOnOneSide' | 'RangeBeyondLimit'

export interface SpcSample {
  id: number
  ts: number
  mean: number
  range: number
  subgroupSize: number
  valuesJson: string
  centerLine: number | null
  upperControlLimit: number | null
  lowerControlLimit: number | null
  rangeCenter: number | null
  rangeUpperControlLimit: number | null
  rangeLowerControlLimit: number | null
  ruleCodes: string
  cp: number | null
  cpk: number | null
}

export interface SpcChart {
  id: number
  name: string
  enabled: boolean
  areaId: number
  tagId: number
  tagName: string
  unit: string | null
  chartType: SpcChartType
  subgroupSize: number
  samplingIntervalSec: number
  baselineSubgroups: number
  autoLimits: boolean
  centerLine: number | null
  upperControlLimit: number | null
  lowerControlLimit: number | null
  rangeCenter: number | null
  rangeUpperControlLimit: number | null
  rangeLowerControlLimit: number | null
  limitsEstablishedTs: number | null
  limitsProvisional: boolean
  specUpperLimit: number | null
  specLowerLimit: number | null
  ruleBeyond3Sigma: boolean
  ruleTwoOfThreeBeyond2Sigma: boolean
  ruleFourOfFiveBeyond1Sigma: boolean
  ruleEightOnOneSide: boolean
  ruleRangeBeyondLimit: boolean
  description: string
  revision: number
  latestSample: SpcSample | null
  activeRules: SpcViolationRule[]
}

export interface SpcChartInput {
  name: string
  enabled: boolean
  areaId: number
  tagId: number
  chartType: SpcChartType
  subgroupSize: number
  samplingIntervalSec: number
  baselineSubgroups: number
  autoLimits: boolean
  centerLine: number | null
  upperControlLimit: number | null
  lowerControlLimit: number | null
  specUpperLimit: number | null
  specLowerLimit: number | null
  ruleBeyond3Sigma: boolean
  ruleTwoOfThreeBeyond2Sigma: boolean
  ruleFourOfFiveBeyond1Sigma: boolean
  ruleEightOnOneSide: boolean
  ruleRangeBeyondLimit: boolean
  description: string
  revision: number
}

export interface SpcViolation {
  id: number
  chartId: number
  sampleId: number
  rule: SpcViolationRule
  ts: number
  alarmEventId: number | null
  recoveredTs: number | null
  message: string
}

export type QualityResult = 'Passed' | 'Failed' | 'Rework' | 'Scrap'

export interface QualityDefectCode {
  id: number
  code: string
  name: string
  category: string
  enabled: boolean
  description: string
}

export type QualityDefectCodeInput = Omit<QualityDefectCode, 'id'>

export interface QualityInspection {
  id: number
  sn: string
  batchNo: string
  product: string
  areaId: number
  deviceId: number | null
  tagId: number | null
  result: QualityResult
  defectCodeId: number | null
  defectCode: string | null
  defectName: string | null
  defectCategory: string | null
  inspector: string
  ts: number
  note: string
  createdBy: string
  deviceName: string | null
  tagName: string | null
}

export interface QualityInspectionInput {
  sn: string
  batchNo: string
  product: string
  areaId: number
  deviceId: number | null
  tagId: number | null
  result: QualityResult
  defectCodeId: number | null
  inspector: string
  ts: number | null
  note: string
}

export interface QualityInspectionPage {
  total: number
  page: number
  pageSize: number
  items: QualityInspection[]
}

export interface QualityDayPoint {
  date: string
  total: number
  passed: number
  failed: number
  rework: number
  scrap: number
  yield: number | null
}

export interface QualityDefectPoint {
  defectCodeId: number | null
  code: string
  name: string
  category: string
  count: number
}

export interface QualityAreaPoint {
  areaId: number
  areaName: string
  total: number
  passed: number
  failed: number
  yield: number | null
}

export interface QualityDevicePoint {
  deviceId: number
  deviceName: string
  total: number
  passed: number
  failed: number
  yield: number | null
}

export interface QualitySummary {
  total: number
  passed: number
  failed: number
  rework: number
  scrap: number
  yield: number | null
  scrapRate: number | null
  reworkRate: number | null
  defectRate: number | null
  byDay: QualityDayPoint[]
  byDefect: QualityDefectPoint[]
  byArea: QualityAreaPoint[]
  byDevice: QualityDevicePoint[]
}

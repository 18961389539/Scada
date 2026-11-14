import { http, readPage } from './http'
import type { CommandSubmissionResult, GatewayCommandStatus } from './commandTypes'
import { createCommandId } from './commandTypes'
import type {
  AccessRoleItem,
  Area,
  Permission,
  RoleCatalog,
  UserAssignment,
  AlarmEvent,
  AlarmPage,
  AlarmPageQuery,
  AlarmKpi,
  AlarmShelve,
  AlarmSummary,
  AlarmTimeline,
  DutyShift,
  NotificationChannel,
  NotificationChannelInput,
  NotificationRoute,
  AlarmRule,
  AlarmRuleInput,
  Asset,
  AssetInput,
  AssetStatus,
  AuthUser,
  Device,
  DeviceInput,
  MaintenanceWorkOrder,
  QualityDefectCode,
  QualityDefectCodeInput,
  QualityInspection,
  QualityInspectionInput,
  QualityInspectionPage,
  QualityResult,
  QualitySummary,
  SimulationView,
  SparePart,
  SparePartInput,
  SparePartMovement,
  SparePartMovementType,
  SpcChart,
  SpcChartInput,
  SpcSample,
  SpcViolation,
  WorkOrderInput,
  WorkOrderPriority,
  WorkOrderStatus,
  WorkOrderType,
  Health,
  HistoryAnalysisResult,
  HistoryResult,
  HistorySnapshotResult,
  ImportResult,
  LoginResult,
  OperationLog,
  Paged,
  ReportResult,
  Tag,
  TagInput,
  TagValue,
  UserItem
} from '../types'

export interface SiteInfo { siteName: string; demoMode: boolean }

export const authApi = {
  siteInfo: async () => (await http.get<SiteInfo>('/auth/site-info')).data,
  login: async (userName: string, password: string) =>
    (await http.post<LoginResult>('/auth/login', { userName, password })).data,
  me: async () => (await http.get<AuthUser>('/auth/me')).data,
  setLocale: async (locale: string) => { await http.put('/auth/me/locale', { locale }) },
  // 改密成功会连带换发令牌：服务端按口令哈希派生 CredentialsVersion，旧令牌随即失效。
  changePassword: async (currentPassword: string, newPassword: string) =>
    (await http.post<LoginResult>('/auth/password', { currentPassword, newPassword })).data
}

function headerText(headers: unknown, name: string): string {
  if (!headers || typeof headers !== 'object') return ''
  const readable = headers as { get?: (header: string) => unknown }
  const record = headers as Record<string, unknown>
  const value = typeof readable.get === 'function'
    ? readable.get(name) ?? readable.get(name.toLowerCase())
    : record[name] ?? record[name.toLowerCase()]
  return value == null ? '' : String(Array.isArray(value) ? value[0] : value)
}

export const deviceApi = {
  list: async () => (await http.get<Device[]>('/devices')).data,
  get: async (id: number) => (await http.get<Device>(`/devices/${id}`)).data,
  page: async (params: {
    search?: string
    protocol?: string
    enabled?: string
    gatewayId?: number | null
    skip?: number
    take: number
    sort?: string
    order?: 'ascending' | 'descending' | null
  }) => {
    const query: Record<string, string | number> = { skip: params.skip ?? 0, take: params.take }
    const search = params.search?.trim()
    if (search) query.search = search
    if (params.protocol && params.protocol !== 'all') query.protocol = params.protocol
    if (params.enabled && params.enabled !== 'all') query.enabled = params.enabled
    if (params.gatewayId != null) query.gatewayId = params.gatewayId
    if (params.sort && params.order) {
      query.sort = params.sort
      query.order = params.order
    }
    const response = await http.get<Device[]>('/devices', { params: query })
    const total = Number(headerText(response.headers, 'x-total-count'))
    return {
      items: response.data,
      total: Number.isFinite(total) ? total : 0,
      protocols: headerText(response.headers, 'x-device-protocols').split(',').map(item => item.trim()).filter(Boolean)
    }
  },
  create: async (input: DeviceInput) => (await http.post<Device>('/devices', input)).data,
  update: async (id: number, input: DeviceInput) => (await http.put<Device>(`/devices/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/devices/${id}`),
  test: async (id: number, area: string, address: number, dataType: string, dbNumber = 1, bitOffset = 0, nodeId = '', textLength = 0) =>
    (
      await http.post<{ connected: boolean; value?: number; message?: string; quality?: string }>(`/devices/${id}/test`, {
        area,
        address,
        dataType, dbNumber, bitOffset, nodeId, textLength
      })
    ).data,
  testDraft: async (device: DeviceInput, id: number | null = null) =>
    (
      await http.post<{ connected: boolean; value?: number; message?: string; quality?: string }>('/devices/test-draft', {
        id,
        device
      })
    ).data
}

export const simulationApi = {
  get: async (deviceId: number) => (await http.get<SimulationView>(`/simulation/${deviceId}`)).data,
  force: async (deviceId: number, tagId: number, value: number) =>
    (await http.post(`/simulation/${deviceId}/force`, { tagId, value })).data,
  release: async (deviceId: number, tagId?: number) =>
    (await http.post(`/simulation/${deviceId}/release`, { tagId })).data,
  fault: async (deviceId: number, kind: string, durationSec: number) =>
    (await http.post(`/simulation/${deviceId}/fault`, { kind, durationSec })).data,
  clearFault: async (deviceId: number) => http.delete(`/simulation/${deviceId}/fault`),
  reset: async (deviceId: number) => (await http.post(`/simulation/${deviceId}/reset`)).data
}

export const tagApi = {
  list: async (params?: { deviceId?: number; search?: string; ids?: number[] }) =>
    (await http.get<Tag[]>('/tags', { params: params?.ids ? { ...params, ids: params.ids.join(',') } : params })).data,
  page: async (params: { deviceId?: number; search?: string; quality?: 'good' | 'abnormal'; sort?: 'abnormal'; deviceIds?: number[]; skip: number; take: number }) => {
    const response = await http.get<Tag[]>('/tags', {
      params: { ...params, deviceIds: params.deviceIds?.join(','), quality: params.quality, sort: params.sort }
    })
    const header = (name: string) => Number(response.headers[name] ?? response.headers[name.toLowerCase()] ?? 0)
    return { items: response.data, total: header('x-total-count'), abnormal: header('x-abnormal-count') }
  },
  create: async (input: TagInput) => (await http.post<Tag>('/tags', input)).data,
  update: async (id: number, input: TagInput) => (await http.put<Tag>(`/tags/${id}`, input)).data,
  batchCreate: async (deviceId: number, tags: TagInput[]) =>
    (await http.post<{ created: number }>('/tags/batch', { deviceId, tags })).data,
  remove: async (id: number) => http.delete(`/tags/${id}`),
  export: async () => (await http.get('/tags/export', { responseType: 'blob' })).data as Blob,
  import: async (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return (
      await http.post<ImportResult>('/tags/import', form, {
        headers: { 'Content-Type': 'multipart/form-data' }
      })
    ).data
  }
}

export interface AlarmRuleSimulateResult {
  triggered: boolean | null
  recovered: boolean | null
  indeterminate: string | null
  evaluatedValue: number
  bitIndex: number | null
  expression: string | null
  message: string
  messageEn: string | null
  references: { tagId: number; tagName: string; value: number; quality: string }[]
}

export const ruleApi = {
  list: async (tagId?: number) => (await http.get<AlarmRule[]>('/alarm-rules', { params: { tagId } })).data,
  page: async (params: {
    tagId?: number
    search?: string
    state?: string
    category?: string
    skip?: number
    take: number
    sort?: string
    order?: 'ascending' | 'descending' | null
  }) => {
    const query: Record<string, string | number> = { skip: params.skip ?? 0, take: params.take }
    const search = params.search?.trim()
    if (params.tagId != null) query.tagId = params.tagId
    if (search) query.search = search
    if (params.state && params.state !== 'all') query.state = params.state
    if (params.category && params.category !== 'all') query.category = params.category
    if (params.sort && params.order) {
      query.sort = params.sort
      query.order = params.order
    }
    const response = await http.get<AlarmRule[]>('/alarm-rules', { params: query })
    const total = Number(headerText(response.headers, 'x-total-count'))
    return { items: response.data, total: Number.isFinite(total) ? total : 0 }
  },
  create: async (input: AlarmRuleInput) => (await http.post<AlarmRule>('/alarm-rules', input)).data,
  update: async (id: number, input: AlarmRuleInput) =>
    (await http.put<AlarmRule>(`/alarm-rules/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/alarm-rules/${id}`),
  shelve: async (id: number, minutes: number, reason: string) =>
    (await http.post<AlarmShelve>(`/alarm-rules/${id}/shelve`, { minutes, reason })).data,
  unshelve: async (id: number, reason?: string) => (await http.post(`/alarm-rules/${id}/unshelve`, { reason })).data,
  outOfService: async (id: number, outOfService: boolean, reason?: string) =>
    (await http.post<AlarmRule>(`/alarm-rules/${id}/out-of-service`, { outOfService, reason })).data,
  export: async (format: 'xlsx' | 'csv', tagId?: number) =>
    (await http.get('/alarm-rules/export', { params: { format, tagId }, responseType: 'blob' })).data as Blob,
  import: async (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return (await http.post<ImportResult>('/alarm-rules/import', form, { headers: { 'Content-Type': 'multipart/form-data' } })).data
  },
  simulate: async (id: number, body: { value: number; quality?: string; previousValue?: number; references?: Record<number, number>; referenceQualities?: Record<number, string> }) =>
    (await http.post<AlarmRuleSimulateResult>(`/alarm-rules/${id}/simulate`, body)).data,
  shelves: async (history = false) => (await http.get<AlarmShelve[]>('/alarm-shelves', { params: { history } })).data
}

export const realtimeApi = {
  get: async (tagIds?: number[]) =>
    (
      await http.get<TagValue[]>('/realtime', {
        params: tagIds?.length ? { tagIds: tagIds.join(',') } : undefined
      })
    ).data
}

export const historyApi = {
  query: async (tagIds: number[], from: number, to: number) =>
    (
      await http.get<HistoryResult>('/history', {
        params: { tagIds: tagIds.join(','), from, to }
      })
    ).data,
  analyze: async (tagIds: number[], from: number, to: number) =>
    (
      await http.get<HistoryAnalysisResult>('/history/analysis', {
        params: { tagIds: tagIds.join(','), from, to }
      })
    ).data,
  // 历史复盘：一次最多 300 个测点；大画面按 200 个分块，避免服务端拒绝。
  snapshot: async (tagIds: number[], ts: number): Promise<HistorySnapshotResult> => {
    const unique = [...new Set(tagIds.filter(id => Number.isInteger(id) && id > 0))]
    const items: HistorySnapshotResult['items'] = []
    for (let index = 0; index < unique.length; index += 200) {
      const chunk = unique.slice(index, index + 200)
      const result = (await http.get<HistorySnapshotResult>('/history/snapshot', {
        params: { tagIds: chunk.join(','), ts }
      })).data
      items.push(...result.items)
    }
    return { ts, items }
  }
}

export const alarmApi = {
  query: async (params: { from?: number; to?: number; activeOnly?: boolean; live?: boolean; unackedOnly?: boolean; includeShelved?: boolean; priorityFirst?: boolean; limit?: number; tagIds?: number[]; touch?: boolean }) => {
    const { tagIds, ...rest } = params
    return (await http.get<AlarmEvent[]>('/alarms', { params: { ...rest, tagIds: tagIds?.join(',') || undefined } })).data
  },
  queryPage: async (params: AlarmPageQuery) =>
    (await http.get<AlarmPage>('/alarms/page', { params })).data,
  ack: async (id: number, note?: string) => (await http.post(`/alarms/${id}/ack`, { note: note || null })).data,
  note: async (id: number, note: string) => (await http.post(`/alarms/${id}/notes`, { note })).data,
  timeline: async (id: number) => (await http.get<AlarmTimeline>(`/alarms/${id}/timeline`)).data,
  summary: async () => (await http.get<AlarmSummary>('/alarms/summary')).data,
  kpi: async (params: { from?: number; to?: number; top?: number; standingHours?: number }) =>
    (await http.get<AlarmKpi>('/alarms/kpi', { params })).data
}

export interface OutboxFailure { id: number; alarmEventId: number; alarmMessage: string; routeId: number; channelId: number; action: string; attempts: number; lastError: string | null; finishedTs: number | null }
export const notificationApi = {
  failed: async (before?: number) => readPage(await http.get<OutboxFailure[]>('/notification-outbox/failed', { params: { before } })),
  acknowledge: async (ids: number[]) => (await http.post<{ acknowledged: number }>('/notification-outbox/acknowledge', { ids })).data,
  channels: async () => (await http.get<NotificationChannel[]>('/notification-channels')).data,
  createChannel: async (input: NotificationChannelInput) => (await http.post<NotificationChannel>('/notification-channels', input)).data,
  updateChannel: async (id: number, input: NotificationChannelInput) => (await http.put<NotificationChannel>(`/notification-channels/${id}`, input)).data,
  removeChannel: async (id: number) => http.delete(`/notification-channels/${id}`),
  testChannel: async (id: number, recipients: string) =>
    (await http.post<{ success: boolean; message: string }>(`/notification-channels/${id}/test`, { recipients })).data,
  shifts: async () => (await http.get<DutyShift[]>('/duty-shifts')).data,
  createShift: async (input: Omit<DutyShift, 'id'>) => (await http.post<DutyShift>('/duty-shifts', input)).data,
  updateShift: async (id: number, input: Omit<DutyShift, 'id'>) => (await http.put<DutyShift>(`/duty-shifts/${id}`, input)).data,
  removeShift: async (id: number) => http.delete(`/duty-shifts/${id}`),
  routes: async () => (await http.get<NotificationRoute[]>('/notification-routes')).data,
  createRoute: async (input: Omit<NotificationRoute, 'id'>) => (await http.post<NotificationRoute>('/notification-routes', input)).data,
  updateRoute: async (id: number, input: Omit<NotificationRoute, 'id'>) => (await http.put<NotificationRoute>(`/notification-routes/${id}`, input)).data,
  removeRoute: async (id: number) => http.delete(`/notification-routes/${id}`)
}

export const commandApi = {
  write: async (tagId: number, value: number, requestId: string = createCommandId(), confirmed = true, text?: string) =>
    (await http.post<CommandSubmissionResult & { pending?: boolean; approvalId?: string | null; refusals?: string[] | null; readBackText?: string | null }>('/commands', {
      tagId,
      value, requestId, confirmed, text
    }, { timeout: 120000 })).data,
  status: async (id: string, signal?: AbortSignal) => (await http.get<GatewayCommandStatus>(`/commands/${id}`, { signal })).data
}

export const reportApi = {
  query: async (params: { type: string; from?: number; to?: number; tagIds: number[] }) =>
    (await http.get<ReportResult>('/reports', { params: { ...params, tagIds: params.tagIds.join(',') } })).data,
  export: async (params: { type: string; from?: number; to?: number; tagIds: number[] }) =>
    (
      await http.get('/reports/export', {
        params: { ...params, tagIds: params.tagIds.join(',') },
        responseType: 'blob'
      })
    ).data as Blob
}

export const logApi = {
  query: async (params: { from?: number; to?: number; keyword?: string; page?: number; pageSize?: number }) =>
    (await http.get<Paged<OperationLog>>('/logs', { params })).data
}

export const assetApi = {
  list: async (params?: { search?: string; status?: AssetStatus; areaId?: number; deviceId?: number; enabled?: boolean }) =>
    (await http.get<Asset[]>('/assets', { params })).data,
  create: async (input: AssetInput) => (await http.post<Asset>('/assets', input)).data,
  update: async (id: number, input: AssetInput) => (await http.put<Asset>(`/assets/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/assets/${id}`)
}

export const workOrderApi = {
  list: async (params?: {
    search?: string; status?: WorkOrderStatus; type?: WorkOrderType; priority?: WorkOrderPriority
    assetId?: number; areaId?: number; take?: number
  }) => (await http.get<MaintenanceWorkOrder[]>('/work-orders', { params })).data,
  create: async (input: WorkOrderInput) => (await http.post<MaintenanceWorkOrder>('/work-orders', input)).data,
  update: async (id: number, input: WorkOrderInput) => (await http.put<MaintenanceWorkOrder>(`/work-orders/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/work-orders/${id}`),
  setStatus: async (id: number, input: { status: WorkOrderStatus; resolution?: string; downtimeMinutes?: number; note?: string }) =>
    (await http.post<MaintenanceWorkOrder>(`/work-orders/${id}/status`, input)).data,
  parts: async (id: number) => (await http.get<SparePartMovement[]>(`/work-orders/${id}/parts`)).data,
  issuePart: async (id: number, input: { partId: number; quantity: number; note?: string }) =>
    (await http.post<{ partId: number; stock: number }>(`/work-orders/${id}/parts`, input)).data
}

export const sparePartApi = {
  list: async (params?: { search?: string; lowStock?: boolean; enabled?: boolean }) =>
    (await http.get<SparePart[]>('/spare-parts', { params })).data,
  create: async (input: SparePartInput) => (await http.post<SparePart>('/spare-parts', input)).data,
  update: async (id: number, input: SparePartInput) => (await http.put<SparePart>(`/spare-parts/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/spare-parts/${id}`),
  movements: async (id: number) => (await http.get<SparePartMovement[]>(`/spare-parts/${id}/movements`)).data,
  addMovement: async (id: number, input: { type: SparePartMovementType; quantity: number; note?: string; workOrderId?: number | null }) =>
    (await http.post<SparePart>(`/spare-parts/${id}/movements`, input)).data
}

export const spcApi = {
  charts: async () => (await http.get<SpcChart[]>('/spc/charts')).data,
  chart: async (id: number) => (await http.get<SpcChart>(`/spc/charts/${id}`)).data,
  samples: async (id: number, params?: { from?: number; to?: number; take?: number }) =>
    (await http.get<SpcSample[]>(`/spc/charts/${id}/samples`, { params })).data,
  violations: async (id: number, params?: { from?: number; to?: number; activeOnly?: boolean; take?: number }) =>
    (await http.get<SpcViolation[]>(`/spc/charts/${id}/violations`, { params })).data,
  create: async (input: SpcChartInput) => (await http.post<SpcChart>('/spc/charts', input)).data,
  update: async (id: number, input: SpcChartInput) => (await http.put<SpcChart>(`/spc/charts/${id}`, input)).data,
  resetLimits: async (id: number) => (await http.post<SpcChart>(`/spc/charts/${id}/reset-limits`, {})).data,
  remove: async (id: number) => http.delete(`/spc/charts/${id}`)
}

export const qualityApi = {
  defectCodes: async (params?: { enabled?: boolean }) =>
    (await http.get<QualityDefectCode[]>('/quality/defect-codes', { params })).data,
  createDefectCode: async (input: QualityDefectCodeInput) =>
    (await http.post<QualityDefectCode>('/quality/defect-codes', input)).data,
  updateDefectCode: async (id: number, input: QualityDefectCodeInput) =>
    (await http.put<QualityDefectCode>(`/quality/defect-codes/${id}`, input)).data,
  removeDefectCode: async (id: number) => http.delete(`/quality/defect-codes/${id}`),
  inspections: async (params: {
    search?: string; sn?: string; batchNo?: string; product?: string; result?: QualityResult
    defectCodeId?: number; areaId?: number; deviceId?: number; from?: number; to?: number
    page?: number; pageSize?: number
  }) => (await http.get<QualityInspectionPage>('/quality/inspections', { params })).data,
  createInspection: async (input: QualityInspectionInput) =>
    (await http.post<QualityInspection>('/quality/inspections', input)).data,
  updateInspection: async (id: number, input: QualityInspectionInput) =>
    (await http.put<QualityInspection>(`/quality/inspections/${id}`, input)).data,
  removeInspection: async (id: number) => http.delete(`/quality/inspections/${id}`),
  summary: async (params: { from?: number; to?: number; areaId?: number; deviceId?: number; product?: string; batchNo?: string }) =>
    (await http.get<QualitySummary>('/quality/summary', { params })).data
}

export const userApi = {
  list: async () => (await http.get<UserItem[]>('/users')).data,
  create: async (input: { userName: string; password: string; displayName: string; role: string; enabled: boolean }) =>
    (await http.post<UserItem>('/users', input)).data,
  update: async (id: number, input: { displayName: string; role: string; enabled: boolean }) =>
    (await http.put<UserItem>(`/users/${id}`, input)).data,
  resetPassword: async (id: number, password: string) =>
    (await http.post(`/users/${id}/password`, { password })).data,
  remove: async (id: number) => http.delete(`/users/${id}`)
}

export const areaApi = {
  list: async () => (await http.get<Area[]>('/areas')).data,
  create: async (input: { name: string; description?: string | null }) => (await http.post<Area>('/areas', input)).data,
  update: async (id: number, input: { name: string; description?: string | null }) => (await http.put<Area>(`/areas/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/areas/${id}`)
}

export const roleApi = {
  list: async () => (await http.get<RoleCatalog>('/roles')).data,
  create: async (input: { name: string; description?: string | null; permissions: Permission[] }) => (await http.post('/roles', input)).data,
  update: async (id: number, input: { name: string; description?: string | null; permissions: Permission[] }) => (await http.put(`/roles/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/roles/${id}`),
  assignments: async (userId: number) => (await http.get<UserAssignment[]>(`/users/${userId}/assignments`)).data,
  saveAssignments: async (userId: number, assignments: { roleId: number; areaId: number | null }[]) =>
    (await http.put<UserAssignment[]>(`/users/${userId}/assignments`, { assignments })).data
}

export const healthApi = {
  get: async () => (await http.get<Health>('/health')).data
}

export { annunciatorApi } from './annunciators'
export type { AnnunciatorPanel, AnnunciatorStatus, AnnunciatorPanelInput } from './annunciators'

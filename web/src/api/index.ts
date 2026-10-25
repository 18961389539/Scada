import { http } from './http'
import type { CommandSubmissionResult, GatewayCommandStatus } from './commandTypes'
import { createCommandId } from './commandTypes'
import type {
  AccessRoleItem,
  Area,
  Permission,
  RoleCatalog,
  UserAssignment,
  AlarmEvent,
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
  AuthUser,
  Device,
  DeviceInput,
  Health,
  HistoryResult,
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

export const authApi = {
  login: async (userName: string, password: string) =>
    (await http.post<LoginResult>('/auth/login', { userName, password })).data,
  me: async () => (await http.get<AuthUser>('/auth/me')).data,
  setLocale: async (locale: string) => { await http.put('/auth/me/locale', { locale }) }
}

export const deviceApi = {
  list: async () => (await http.get<Device[]>('/devices')).data,
  create: async (input: DeviceInput) => (await http.post<Device>('/devices', input)).data,
  update: async (id: number, input: DeviceInput) => (await http.put<Device>(`/devices/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/devices/${id}`),
  test: async (id: number, area: string, address: number, dataType: string, dbNumber = 1, bitOffset = 0, nodeId = '') =>
    (
      await http.post<{ connected: boolean; value?: number; message?: string; quality?: string }>(`/devices/${id}/test`, {
        area,
        address,
        dataType, dbNumber, bitOffset, nodeId
      })
    ).data
}

export const tagApi = {
  list: async (params?: { deviceId?: number; search?: string }) =>
    (await http.get<Tag[]>('/tags', { params })).data,
  create: async (input: TagInput) => (await http.post<Tag>('/tags', input)).data,
  update: async (id: number, input: TagInput) => (await http.put<Tag>(`/tags/${id}`, input)).data,
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

export const ruleApi = {
  list: async (tagId?: number) => (await http.get<AlarmRule[]>('/alarm-rules', { params: { tagId } })).data,
  create: async (input: AlarmRuleInput) => (await http.post<AlarmRule>('/alarm-rules', input)).data,
  update: async (id: number, input: AlarmRuleInput) =>
    (await http.put<AlarmRule>(`/alarm-rules/${id}`, input)).data,
  remove: async (id: number) => http.delete(`/alarm-rules/${id}`),
  shelve: async (id: number, minutes: number, reason: string) =>
    (await http.post<AlarmShelve>(`/alarm-rules/${id}/shelve`, { minutes, reason })).data,
  unshelve: async (id: number, reason?: string) => (await http.post(`/alarm-rules/${id}/unshelve`, { reason })).data,
  outOfService: async (id: number, outOfService: boolean, reason?: string) =>
    (await http.post<AlarmRule>(`/alarm-rules/${id}/out-of-service`, { outOfService, reason })).data,
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
    ).data
}

export const alarmApi = {
  query: async (params: { from?: number; to?: number; activeOnly?: boolean; live?: boolean; unackedOnly?: boolean; includeShelved?: boolean; limit?: number }) =>
    (await http.get<AlarmEvent[]>('/alarms', { params })).data,
  ack: async (id: number, note?: string) => (await http.post(`/alarms/${id}/ack`, { note: note || null })).data,
  note: async (id: number, note: string) => (await http.post(`/alarms/${id}/notes`, { note })).data,
  timeline: async (id: number) => (await http.get<AlarmTimeline>(`/alarms/${id}/timeline`)).data,
  summary: async () => (await http.get<AlarmSummary>('/alarms/summary')).data,
  kpi: async (params: { from?: number; to?: number; top?: number; standingHours?: number }) =>
    (await http.get<AlarmKpi>('/alarms/kpi', { params })).data
}

export interface OutboxFailure { id: number; alarmEventId: number; alarmMessage: string; routeId: number; channelId: number; action: string; attempts: number; lastError: string | null; finishedTs: number | null }
export const notificationApi = {
  failed: async () => (await http.get<OutboxFailure[]>('/notification-outbox/failed')).data,
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
  write: async (tagId: number, value: number, requestId: string = createCommandId(), confirmed = true) =>
    (await http.post<CommandSubmissionResult & { pending?: boolean; approvalId?: string | null; refusals?: string[] | null }>('/commands', {
      tagId,
      value, requestId, confirmed
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

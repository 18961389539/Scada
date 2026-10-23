import { http } from './http'
import type {
  AlarmEvent,
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
  me: async () => (await http.get<AuthUser>('/auth/me')).data
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
  remove: async (id: number) => http.delete(`/alarm-rules/${id}`)
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
  query: async (params: { from?: number; to?: number; activeOnly?: boolean; limit?: number }) =>
    (await http.get<AlarmEvent[]>('/alarms', { params })).data,
  ack: async (id: number) => (await http.post(`/alarms/${id}/ack`)).data
}

export const commandApi = {
  write: async (tagId: number, value: number) =>
    (await http.post<{ success: boolean; message: string; readBack: number | null }>('/commands', {
      tagId,
      value
    })).data
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

export const healthApi = {
  get: async () => (await http.get<Health>('/health')).data
}

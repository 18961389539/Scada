import { http } from './http'

export interface GatewayInfo {
  id: number; name: string; enabled: boolean; connected: boolean; lastSeenTs: number; leaseUntilTs: number
  agentVersion: string | null; appliedRevision: string | null; currentRevision: string; configurationConfirmed: boolean
  deviceCount: number; enabledDeviceCount: number; pendingBatches: number; pendingBytes: number; oldestPendingTs: number | null
  inboxBatches: number; inboxBytes: number; error: string | null; reportedError: string | null; inboxError: string | null
  deadLetterBatches: number; deadLetterBytes: number
  capabilities: string[]; adminRevision: string; serverTs: number
}
export interface GatewayDevice {
  id: number; name: string; protocol: string; enabled: boolean; tagCount: number; online: boolean; error: string | null; lastPollTs: number
}
export interface GatewayAssignment {
  deviceId: number; deviceName: string; gatewayId: number | null; gatewayName: string | null
  enabled: boolean; disabledSinceTs: number; canMove: boolean; blockReason: string | null
}
const match = (revision: string) => ({ headers: { 'If-Match': `"${revision}"` } })
export const gatewayApi = {
  list: async (signal?: AbortSignal) => (await http.get<GatewayInfo[]>('/gateways', { signal })).data,
  devices: async (id: number, signal?: AbortSignal) => (await http.get<GatewayDevice[]>(`/gateways/${id}/devices`, { signal })).data,
  create: async (name: string) => (await http.post<{ id: number; token: string }>('/gateways', { name })).data,
  update: async (gateway: GatewayInfo, name: string, enabled: boolean) => http.put(`/gateways/${gateway.id}`, { name, enabled }, match(gateway.adminRevision)),
  rotate: async (gateway: GatewayInfo) => (await http.post<{ token: string }>(`/gateways/${gateway.id}/rotate-key`, undefined, match(gateway.adminRevision))).data,
  release: async (gateway: GatewayInfo) => http.post(`/gateways/${gateway.id}/release-session`, undefined, match(gateway.adminRevision)),
  assignment: async (deviceId: number, signal?: AbortSignal) => (await http.get<GatewayAssignment>(`/gateways/assignments/${deviceId}`, { signal })).data,
  assign: async (source: GatewayAssignment, gatewayId: number | null) => http.post(`/gateways/assignments/${source.deviceId}`, {
    gatewayId, expectedGatewayId: source.gatewayId, expectedDisabledSinceTs: source.disabledSinceTs
  })
}

import { http } from './http'

export type ExternalApiKey = {
  id: number
  name: string
  keyPrefix: string
  scopes: string[]
  areaIds: number[] | null
  rateLimitPerMinute: number
  enabled: boolean
  createdTs: number
  createdBy?: string
  lastUsedTs?: number
  revokedTs?: number
  notes?: string
}

export type ExternalEndpointStatus = {
  enabled?: boolean
  listenAddress?: string
  port?: number
  tls?: boolean
  maxConnections?: number
  ipAllowlist?: string[]
  connections?: number
  running?: boolean
  mappedPoints?: number
  allowWrite?: boolean
  allowControl?: boolean
  autoAddressing?: boolean
  unitIds?: Array<number | string>
  commonAddresses?: Array<number | string>
  warnings?: string[]
}

export type ExternalStatus = {
  rest?: { enabled?: boolean; allowJwt?: boolean; cors?: string[]; defaultRateLimitPerMinute?: number }
  tcp?: ExternalEndpointStatus
  opcUa?: { enabled?: boolean; running?: boolean; endpoint?: string; endpointUrl?: string; allowWrite?: boolean }
  modbusSlave?: ExternalEndpointStatus
  iec104Server?: ExternalEndpointStatus
}

export const externalApi = {
  status: () => http.get<ExternalStatus>('/external/status').then(r => r.data),
  listKeys: () => http.get<ExternalApiKey[]>('/external/keys').then(r => r.data),
  createKey: (body: { name: string; scopes: string[]; areaIds?: number[] | null; rateLimitPerMinute?: number; notes?: string }) =>
    http.post<{ key: ExternalApiKey; secret: string }>('/external/keys', body).then(r => r.data),
  revokeKey: (id: number) => http.post<ExternalApiKey>(`/external/keys/${id}/revoke`, {}).then(r => r.data),
  setEnabled: (id: number, enabled: boolean) => http.put<ExternalApiKey>(`/external/keys/${id}/enabled`, { enabled }).then(r => r.data)
}

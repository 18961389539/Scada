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

export const externalApi = {
  status: () => http.get('/external/status').then(r => r.data),
  listKeys: () => http.get<ExternalApiKey[]>('/external/keys').then(r => r.data),
  createKey: (body: { name: string; scopes: string[]; areaIds?: number[] | null; rateLimitPerMinute?: number; notes?: string }) =>
    http.post<{ key: ExternalApiKey; secret: string }>('/external/keys', body).then(r => r.data),
  revokeKey: (id: number) => http.post<ExternalApiKey>(`/external/keys/${id}/revoke`, {}).then(r => r.data),
  setEnabled: (id: number, enabled: boolean) => http.put<ExternalApiKey>(`/external/keys/${id}/enabled`, { enabled }).then(r => r.data)
}

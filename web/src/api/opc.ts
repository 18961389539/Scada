import { http } from './http'
import type { TagDataType, Tag } from '../types'
export interface OpcNode { nodeId: string; displayName: string; nodeClass: string; dataType: TagDataType | null; readable: boolean; writable: boolean; error: string | null }
export interface OpcItem { nodeId: string; name: string; writable: boolean }
export interface OpcPreview { id: string; rows: { nodeId: string; name: string; dataType: TagDataType | null; writable: boolean; error: string | null }[]; errors: string[]; canApply: boolean; expiresAt: string }
export interface OpcCertificate { sha256: string; thumbprint: string; subject: string; issuer: string; notBefore: string; notAfter: string; store: string }
export const opcApi = {
  certificates: async () => (await http.get<OpcCertificate[]>('/opc/certificates')).data,
  trust: async (sha: string) => http.post(`/opc/certificates/${sha}/trust`),
  untrust: async (sha: string) => http.delete(`/opc/certificates/${sha}/trust`),
  publicCertificate: async (sha: string) => (await http.get<Blob>(`/opc/certificates/${sha}/public`, { responseType: 'blob' })).data,
  browse: async (id: number, nodeId: string, cursor: string | null) => (await http.post<{ nodes: OpcNode[]; cursor: string | null }>(`/devices/${id}/opc/browse`, { nodeId, limit: 100, cursor })).data,
  close: async (id: number, cursor: string) => http.delete(`/devices/${id}/opc/browse/${cursor}`),
  preview: async (id: number, items: OpcItem[]) => (await http.post<OpcPreview>(`/devices/${id}/opc/import/preview`, { items })).data,
  apply: async (id: number, preview: string) => (await http.post<Tag[]>(`/devices/${id}/opc/import/${preview}`)).data
}

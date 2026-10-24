import { http } from './http'
import type { TagDataType, Tag } from '../types'
export interface OpcNode { nodeId: string; displayName: string; nodeClass: string; dataType: TagDataType | null; readable: boolean; writable: boolean; error: string | null }
export interface OpcItem { nodeId: string; name: string; writable: boolean }
export interface OpcPreview { id: string; rows: { nodeId: string; name: string; dataType: TagDataType | null; writable: boolean; error: string | null }[]; errors: string[]; canApply: boolean; expiresAt: string }
export interface OpcCertificate { sha256: string; thumbprint: string; subject: string; issuer: string; notBefore: string; notAfter: string; store: string }
export interface OpcCertificateScope { deviceId: number; gatewayId: number }
const certificatePath = (scope?: OpcCertificateScope) => scope ? `/devices/${scope.deviceId}/opc/certificates` : '/opc/certificates'
const scopeParams = (scope?: OpcCertificateScope) => scope ? { gatewayId: scope.gatewayId } : undefined
export const opcApi = {
  certificates: async (scope?: OpcCertificateScope, signal?: AbortSignal) => (await http.get<OpcCertificate[]>(certificatePath(scope), { params: scopeParams(scope), signal })).data,
  trust: async (sha: string, scope?: OpcCertificateScope, signal?: AbortSignal) => http.post(`${certificatePath(scope)}/${sha}/trust`, undefined, { params: scopeParams(scope), signal }),
  untrust: async (sha: string, scope?: OpcCertificateScope, signal?: AbortSignal) => http.delete(`${certificatePath(scope)}/${sha}/trust`, { params: scopeParams(scope), signal }),
  publicCertificate: async (sha: string, scope?: OpcCertificateScope) => (await http.get<Blob>(`${certificatePath(scope)}/${sha}/public`, { responseType: 'blob', params: scopeParams(scope) })).data,
  browse: async (id: number, nodeId: string, cursor: string | null) => (await http.post<{ nodes: OpcNode[]; cursor: string | null }>(`/devices/${id}/opc/browse`, { nodeId, limit: 100, cursor })).data,
  close: async (id: number, cursor: string) => http.delete(`/devices/${id}/opc/browse/${cursor}`),
  preview: async (id: number, items: OpcItem[]) => (await http.post<OpcPreview>(`/devices/${id}/opc/import/preview`, { items })).data,
  apply: async (id: number, preview: string) => (await http.post<Tag[]>(`/devices/${id}/opc/import/${preview}`)).data
}

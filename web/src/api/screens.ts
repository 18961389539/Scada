import { http } from './http'
import type { CommandSubmissionResult } from './commandTypes'
import { createCommandId } from './commandTypes'
import type { ScreenDetail, ScreenDocument, ScreenIssue, ScreenSummary } from '../screens/model'

export interface ScreenPublicationResult { revision: number; publishedRevision: number; versionId?: number | null; warning?: string | null }
export interface ScreenUsageLayout { id: number; name: string; publishedName: string | null; inDraft: boolean; inPublished: boolean }
export interface ScreenUsageStation { id: number; name: string; layoutId: number | null }
export interface ScreenUsage { screenId: number; layouts: ScreenUsageLayout[]; workstations: ScreenUsageStation[] }
export interface ScreenVersionSummary { revision: number; name: string; publishedTs: number; publishedBy: string; note: string; kind: string; restoredFromRevision: number | null }
export interface ScreenVersion extends ScreenVersionSummary { document: ScreenDocument; requiresRepublish: boolean }

export const screenApi = {
  list: async (draft = false) => (await http.get<ScreenSummary[]>('/screens', { params: { draft } })).data,
  get: async (id: number, draft = false) => (await http.get<ScreenDetail>(`/screens/${id}`, { params: { draft } })).data,
  versions: async (id: number, beforeRevision?: number, limit = 50) => (await http.get<ScreenVersionSummary[]>(`/screens/${id}/versions`, { params: { limit, beforeRevision } })).data,
  version: async (id: number, revision: number) => (await http.get<ScreenVersion>(`/screens/${id}/versions/${revision}`)).data,
  rollback: async (id: number, revision: number, targetRevision: number, note: string) =>
    (await http.post<ScreenPublicationResult>(`/screens/${id}/rollback`, { revision, targetRevision, note })).data,
  create: async (name: string) => (await http.post<{ id: number; revision: number }>('/screens', { name })).data,
  save: async (id: number, name: string, revision: number, document: ScreenDocument) =>
    (await http.put<{ revision: number }>(`/screens/${id}`, { name, revision, document })).data,
  validate: async (document: ScreenDocument, screenId?: number) => (await http.post<{ issues: ScreenIssue[] }>('/screens/validate', document, { params: { screenId } })).data.issues,
  publish: async (id: number, revision: number) => (await http.post<ScreenPublicationResult>(`/screens/${id}/publish`, { revision })).data,
  usage: async (id: number) => (await http.get<ScreenUsage>(`/screens/${id}/usage`)).data,
  remove: async (id: number, revision: number) => http.delete(`/screens/${id}`, { params: { revision } }),
  command: async (id: number, nodeId: string, publishedRevision: number, requestId: string = createCommandId(), confirmed = true, value?: number, operation?: string, phase?: string) =>
    (await http.post<CommandSubmissionResult & { pending?: boolean; approvalId?: string | null }>(`/screens/${id}/actions/${nodeId}`, { publishedRevision, requestId, confirmed, value: value ?? null, operation: operation ?? null, phase: phase ?? null }, { timeout: 120000 })).data
}

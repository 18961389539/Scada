import { http } from './http'
import type { CommandSubmissionResult } from './commandTypes'
import { createCommandId } from './commandTypes'
import type { ScreenDetail, ScreenDocument, ScreenIssue, ScreenSummary } from '../screens/model'

export interface ScreenPublicationResult { revision: number; publishedRevision: number; versionId?: number | null; warning?: string | null; warnings?: ScreenIssue[] }
export interface ScreenUsageLayout { id: number; name: string; publishedName: string | null; inDraft: boolean; inPublished: boolean }
export interface ScreenUsageStation { id: number; name: string; layoutId: number | null }
export interface ScreenUsageRef { id: number; name: string }
export interface ScreenUsage {
  screenId: number
  layouts: ScreenUsageLayout[]
  workstations: ScreenUsageStation[]
  objectInstances?: ScreenUsageRef[]
  referencedBy?: ScreenUsageRef[]
  unreadable?: boolean
}
export interface ScreenVersionSummary { revision: number; name: string; publishedTs: number; publishedBy: string; note: string; kind: string; restoredFromRevision: number | null }
export interface ScreenVersion extends ScreenVersionSummary { document: ScreenDocument; requiresRepublish: boolean }
export interface ScreenThumbnailNode { id: string; type: string; x: number; y: number; width: number; height: number; rotation: number; color: string; text?: string | null; points?: { x: number; y: number }[] | null }
export interface ScreenThumbnail { width: number; height: number; background: string; truncated: boolean; nodes: ScreenThumbnailNode[] }

export const screenApi = {
  list: async (draft = false) => (await http.get<ScreenSummary[]>('/screens', { params: { draft } })).data,
  get: async (id: number, draft = false, opts?: { asOf?: number | null; revision?: number | null }) => (await http.get<ScreenDetail>(`/screens/${id}`, { params: { draft, asOf: opts?.asOf ?? undefined, revision: opts?.revision ?? undefined } })).data,
  thumbnail: async (id: number, draft = false) => (await http.get<ScreenThumbnail>(`/screens/${id}/thumbnail`, { params: { draft } })).data,
  versions: async (id: number, beforeRevision?: number, limit = 50) => (await http.get<ScreenVersionSummary[]>(`/screens/${id}/versions`, { params: { limit, beforeRevision } })).data,
  version: async (id: number, revision: number) => (await http.get<ScreenVersion>(`/screens/${id}/versions/${revision}`)).data,
  rollback: async (id: number, revision: number, targetRevision: number, note: string) =>
    (await http.post<ScreenPublicationResult>(`/screens/${id}/rollback`, { revision, targetRevision, note })).data,
  create: async (name: string, areaId?: number | null) => (await http.post<{ id: number; revision: number; areaId?: number | null }>('/screens', { name, areaId: areaId ?? null })).data,
  save: async (id: number, name: string, revision: number, document: ScreenDocument, areaId?: number | null, updateArea = false) =>
    (await http.put<{ revision: number; areaId?: number | null }>(`/screens/${id}`, { name, revision, document, areaId: areaId ?? null, updateArea })).data,
  validate: async (document: ScreenDocument, screenId?: number) => (await http.post<{ issues: ScreenIssue[]; warnings?: ScreenIssue[] }>('/screens/validate', document, { params: { screenId } })).data,
  publish: async (id: number, revision: number) => (await http.post<ScreenPublicationResult>(`/screens/${id}/publish`, { revision })).data,
  usage: async (id: number) => (await http.get<ScreenUsage>(`/screens/${id}/usage`)).data,
  reportRuntime: async (id: number, kind: string, message: string, publishedRevision?: number | null) =>
    await http.post(`/screens/${id}/runtime-report`, { kind, message, publishedRevision: publishedRevision ?? null }, { timeout: 15000 }),
  remove: async (id: number, revision: number) => http.delete(`/screens/${id}`, { params: { revision } }),
  command: async (id: number, nodeId: string, publishedRevision: number, requestId: string = createCommandId(), confirmed = true, value?: number, operation?: string, phase?: string) =>
    (await http.post<CommandSubmissionResult & { pending?: boolean; approvalId?: string | null }>(`/screens/${id}/actions/${nodeId}`, { publishedRevision, requestId, confirmed, value: value ?? null, operation: operation ?? null, phase: phase ?? null }, { timeout: 120000 })).data
}

import { http } from './http'
import type { CommandSubmissionResult } from './commandTypes'
import { createCommandId } from './commandTypes'
import type { ScreenDetail, ScreenDocument, ScreenIssue, ScreenSummary } from '../screens/model'
export const screenApi = {
  list: async (draft = false) => (await http.get<ScreenSummary[]>('/screens', { params: { draft } })).data,
  get: async (id: number, draft = false) => (await http.get<ScreenDetail>(`/screens/${id}`, { params: { draft } })).data,
  create: async (name: string) => (await http.post<{ id: number; revision: number }>('/screens', { name })).data,
  save: async (id: number, name: string, revision: number, document: ScreenDocument) =>
    (await http.put<{ revision: number }>(`/screens/${id}`, { name, revision, document })).data,
  validate: async (document: ScreenDocument, screenId?: number) => (await http.post<{ issues: ScreenIssue[] }>('/screens/validate', document, { params: { screenId } })).data.issues,
  publish: async (id: number, revision: number) => (await http.post<{ revision: number; publishedRevision: number; versionId?: number | null; warning?: string | null }>(`/screens/${id}/publish`, { revision })).data,
  remove: async (id: number, revision: number) => http.delete(`/screens/${id}`, { params: { revision } }),
  command: async (id: number, nodeId: string, publishedRevision: number, requestId: string = createCommandId()) =>
    (await http.post<CommandSubmissionResult>(`/screens/${id}/actions/${nodeId}`, { publishedRevision, requestId })).data
}

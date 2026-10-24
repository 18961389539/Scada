import { http } from './http'
import type { CommandSubmissionResult } from './commandTypes'

export interface ControlCheck {
  tagId: number; tagName: string; current: number | null; refusals: string[]; requireConfirm: boolean; dualApproval: boolean
  limitMin: number | null; limitMax: number | null; maxStep: number | null; feedback: string | null; mode: 'Remote' | 'Local'; lockouts: string[]
}
export interface ControlPoint {
  id?: number; tagId: number; requireConfirm: boolean; dualApproval: boolean; approvalTimeoutSec: number; permitExpression: string | null; permitMessage: string | null
  limitMin: number | null; limitMax: number | null; maxStep: number | null; feedbackExpression: string | null; feedbackTimeoutMs: number; updatedTs?: number; updatedBy?: string | null
}
export interface DeviceControlState { deviceId: number; mode: 'Remote' | 'Local'; reason: string | null; changedTs: number; changedBy: string | null }
export interface ControlLockout {
  id: number; deviceId: number | null; tagId: number | null; kind: 'NoOperate' | 'Maintenance'; reason: string; placedByUserId: number | null; placedBy: string; placedTs: number
  removedTs: number | null; removedBy: string | null; removeReason: string | null
}
export interface ControlRequest {
  id: string; kind: 'command' | 'sequence' | 'recipe'; tagId: number | null; value: number | null; sequenceId: number | null; source: string; requestedByUserId: number; requestedBy: string
  requestedTs: number; expiresTs: number; status: string; decidedBy: string | null; decidedTs: number | null; comment: string | null; resultMessage: string | null; runId: number | null
}
export interface ControlStep { name: string; tagId: number; value: number; confirmExpression: string | null; timeoutMs: number; delayMs: number }
export interface ControlSequence { id: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; updatedTs: number; updatedBy: string | null; steps: ControlStep[] }
export interface ControlStepResult { index: number; name: string; tagId: number; value: number; status: string; message: string | null; startedTs: number | null; endedTs: number | null }
export interface ControlRun { id: number; sequenceId: number; sequenceName: string; areaId: number; status: string; startedTs: number; endedTs: number | null; startedBy: string; approvedBy: string | null; message: string | null; steps: ControlStepResult[] }
export type ControlSubmission = CommandSubmissionResult & { pending?: boolean; approvalId?: string | null; refusals?: string[] | null }

export const controlApi = {
  check: async (tagId: number, value: number) => (await http.post<ControlCheck>('/control/check', { tagId, value })).data,
  points: async () => (await http.get<ControlPoint[]>('/control/points')).data,
  savePoint: async (p: ControlPoint) => (await http.put<ControlPoint>(`/control/points/${p.tagId}`, p)).data,
  removePoint: async (tagId: number) => http.delete(`/control/points/${tagId}`),
  modes: async () => (await http.get<DeviceControlState[]>('/control/modes')).data,
  setMode: async (deviceId: number, mode: 'Remote' | 'Local', reason: string) => (await http.put<DeviceControlState>(`/control/modes/${deviceId}`, { mode, reason })).data,
  lockouts: async (all = false) => (await http.get<ControlLockout[]>('/control/lockouts', { params: { all } })).data,
  placeLockout: async (input: { deviceId?: number | null; tagId?: number | null; kind: string; reason: string }) => (await http.post<ControlLockout>('/control/lockouts', input)).data,
  removeLockout: async (id: number, reason: string) => (await http.post<ControlLockout>(`/control/lockouts/${id}/remove`, { reason })).data,
  requests: async (status?: string) => (await http.get<{ request: ControlRequest; sequenceName: string | null; recipe?: import('./recipes').RecipeDownload | null; mine: boolean }[]>('/control/requests', { params: { status } })).data,
  approve: async (id: string, comment: string) => (await http.post<{ request: ControlRequest; result: ControlSubmission | null; runId: number | null }>(`/control/requests/${id}/approve`, { comment }, { timeout: 120000 })).data,
  reject: async (id: string, comment: string) => (await http.post(`/control/requests/${id}/reject`, { comment })).data,
  sequences: async () => (await http.get<ControlSequence[]>('/control/sequences')).data,
  saveSequence: async (s: Omit<ControlSequence, 'id' | 'updatedTs' | 'updatedBy'> & { id?: number }) =>
    (s.id ? await http.put<ControlSequence>(`/control/sequences/${s.id}`, s) : await http.post<ControlSequence>('/control/sequences', s)).data,
  removeSequence: async (id: number) => http.delete(`/control/sequences/${id}`),
  start: async (id: number) => (await http.post<{ pending: boolean; approvalId: string | null; runId: number | null }>(`/control/sequences/${id}/start`, { confirmed: true })).data,
  runs: async (sequenceId?: number) => (await http.get<ControlRun[]>('/control/runs', { params: { sequenceId } })).data,
  run: async (id: number) => (await http.get<ControlRun>(`/control/runs/${id}`)).data,
  abort: async (id: number) => http.post(`/control/runs/${id}/abort`)
}

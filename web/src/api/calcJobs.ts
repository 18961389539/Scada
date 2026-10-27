import { http } from './http'

export type CalcJobKind = 'Accumulator' | 'RunHours' | 'Energy' | 'Script' | 'CSharp'
export type AccumulatorMode = 'Pulse' | 'Flow'
export type CalcResetPeriod = 'None' | 'Shift' | 'Day' | 'Month'
export type BadQualityPolicy = 'Hold' | 'Skip' | 'Zero'

export interface CalcJob {
  id: number; name: string; kind: CalcJobKind; enabled: boolean; areaId: number; intervalMs: number; revision: number
  inputTagId: number | null; mode: AccumulatorMode; factor: number; counterMax: number | null; maxDelta: number | null
  badQuality: BadQualityPolicy; maxGapSec: number; runThreshold: number; maintenanceHours: number | null
  resetPeriod: CalcResetPeriod; shiftStarts: string; touJson: string; script: string; readTags: string; writeTags: string
  runAsUserId: number | null; runAsUserName: string; updatedTs: number
}
export interface CalcJobInput extends Omit<CalcJob, 'id' | 'runAsUserId' | 'runAsUserName' | 'updatedTs'> { unit: string }
export interface CalcJobStatus { jobId: number; values: Record<string, number>; periodKey: string; periodStartTs: number; lastRunTs: number | null; lastError: string | null; lastEvent: string | null; lastScriptSteps: number | null; lastScriptMs: number | null; isRunning?: boolean; pendingWrites?: Record<number, { commandId: string; value: number }> }
export interface CalcJobInfo { job: CalcJob; outputs: { name: string; tagId: number }[]; status: CalcJobStatus | null }
export interface CalcJobSnapshot { id: number; jobId: number; reason: string; periodKey: string; periodStartTs: number; ts: number; valuesJson: string }
export interface CSharpScriptCapabilities { enabled: boolean; timeoutMs: number; compileTimeoutMs: number; workerCount: number; maxSourceLength: number; maxInputs: number; maxWrites: number }
export interface CSharpScriptCheck { success: boolean; outputNames: string[]; outputs: Record<string, number>; writes: Record<number, number>; diagnostics: string[]; error: string | null; elapsedMs: number; logs: string[] }

export const calcJobApi = {
  capabilities: async () => (await http.get<CSharpScriptCapabilities>('/calc-jobs/csharp/capabilities')).data,
  check: async (input: CalcJobInput, preview: boolean, signal?: AbortSignal) => (await http.post<CSharpScriptCheck>(`/calc-jobs/csharp/${preview ? 'preview' : 'compile'}`, input, { signal })).data,
  acknowledgeWrites: async (id: number) => http.post(`/calc-jobs/${id}/acknowledge-writes`),
  list: async (signal?: AbortSignal) => (await http.get<CalcJobInfo[]>('/calc-jobs', { signal })).data,
  save: async (id: number | null, input: CalcJobInput) => (await (id === null ? http.post<CalcJob>('/calc-jobs', input) : http.put<CalcJob>(`/calc-jobs/${id}`, input))).data,
  remove: async (job: CalcJob) => http.delete(`/calc-jobs/${job.id}`, { params: { revision: job.revision } }),
  reset: async (id: number) => http.post(`/calc-jobs/${id}/reset`),
  maintenanceDone: async (id: number) => http.post(`/calc-jobs/${id}/maintenance-done`),
  snapshots: async (id: number) => (await http.get<CalcJobSnapshot[]>(`/calc-jobs/${id}/snapshots`)).data
}

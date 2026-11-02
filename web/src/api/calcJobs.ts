import { http } from './http'

export type CalcJobKind = 'Accumulator' | 'RunHours' | 'Energy' | 'Script' | 'CSharp'
export type AccumulatorMode = 'Pulse' | 'Flow'
export type CalcResetPeriod = 'None' | 'Shift' | 'Day' | 'Month'
export type BadQualityPolicy = 'Hold' | 'Skip' | 'Zero'
export type CalcTrigger = 'Periodic' | 'OnChange' | 'OnAlarm' | 'OnStartup'

export interface CalcJob {
  id: number; name: string; kind: CalcJobKind; enabled: boolean; areaId: number; intervalMs: number; revision: number
  inputTagId: number | null; mode: AccumulatorMode; factor: number; counterMax: number | null; maxDelta: number | null
  badQuality: BadQualityPolicy; maxGapSec: number; runThreshold: number; maintenanceHours: number | null
  resetPeriod: CalcResetPeriod; shiftStarts: string; touJson: string; script: string; readTags: string; writeTags: string
  trigger: CalcTrigger; changeDeadband: number; changeCooldownMs: number; alarmMinLevel: 'Info' | 'Warning' | 'Critical'; alarmOnRaise: boolean; alarmOnAck: boolean; alarmOnRecover: boolean
  historyWindowSec: number; alarmSnapshot: boolean
  libraries: string; publishedLibraries: string; publishedLibrarySource: string
  publishedScript: string; publishedReadTags: string; publishedWriteTags: string; publishedHistoryWindowSec: number; publishedAlarmSnapshot: boolean; publishedRevision: number
  publishedTrigger: CalcTrigger; publishedIntervalMs: number; publishedChangeDeadband: number; publishedChangeCooldownMs: number
  publishedAlarmMinLevel: 'Info' | 'Warning' | 'Critical'; publishedAlarmOnRaise: boolean; publishedAlarmOnAck: boolean; publishedAlarmOnRecover: boolean
  runAsUserId: number | null; runAsUserName: string; updatedTs: number
}
export interface CalcJobInput extends Omit<CalcJob, 'id' | 'runAsUserId' | 'runAsUserName' | 'updatedTs'> { unit: string }
export interface CalcJobStatus { jobId: number; values: Record<string, number>; periodKey: string; periodStartTs: number; lastRunTs: number | null; lastError: string | null; lastEvent: string | null; lastScriptSteps: number | null; lastScriptMs: number | null; isRunning?: boolean; pendingWrites?: Record<number, { commandId: string; value: number }> }
export interface CalcJobInfo { job: CalcJob; outputs: { name: string; tagId: number }[]; status: CalcJobStatus | null }
export interface CalcJobSnapshot { id: number; jobId: number; reason: string; periodKey: string; periodStartTs: number; ts: number; valuesJson: string }
export interface CalcJobPublication { id: number; jobId: number; revision: number; script: string; readTags: string; writeTags: string; historyWindowSec: number; alarmSnapshot: boolean; trigger: CalcTrigger; intervalMs: number; ts: number; userName: string }
export interface ScriptLibrary { id: number; name: string; source: string; publishedSource: string; publishedRevision: number; revision: number; updatedTs: number; userName: string }
export interface ScriptScreenReference { id: number; name: string; published: boolean }
export interface ScriptTagReference { token: string; tagId: number | null; currentName: string | null; resolved: boolean }
export interface ScriptJobReference { id: number; name: string }
export interface ScriptReferenceList { screens: ScriptScreenReference[]; tags: ScriptTagReference[] }
export interface CSharpScriptCapabilities { enabled: boolean; licensed: boolean; timeoutMs: number; compileTimeoutMs: number; workerCount: number; maxSourceLength: number; maxInputs: number; maxWrites: number }
export interface ScriptDiagnosticSpan { message: string; file: string; startLine: number; startColumn: number; endLine: number; endColumn: number }
export interface ScriptPreviewSample { tagId: number; name: string; value: number; quality: string; timestamp: number; good: boolean }
export interface CSharpScriptCheck { success: boolean; outputNames: string[]; outputs: Record<string, number>; writes: Record<number, number>; diagnostics: string[]; error: string | null; elapsedMs: number; logs: string[]; trace: string[]; spans?: ScriptDiagnosticSpan[]; inputs?: ScriptPreviewSample[] }

export const calcJobApi = {
  capabilities: async () => (await http.get<CSharpScriptCapabilities>('/calc-jobs/csharp/capabilities')).data,
  check: async (input: CalcJobInput, preview: boolean, signal?: AbortSignal, options?: { argument: number | null; inputs: { tagId: number; value: number; quality: string; timestamp: number }[] | null }) => (await http.post<CSharpScriptCheck>(`/calc-jobs/csharp/${preview ? 'preview' : 'compile'}`, { ...input, previewArgument: preview ? options?.argument ?? null : null, previewInputs: preview ? options?.inputs ?? null : null }, { signal })).data,
  compileLibrary: async (source: string, signal?: AbortSignal) => (await http.post<CSharpScriptCheck>('/calc-jobs/csharp/compile-library', { source }, { signal })).data,
  acknowledgeWrites: async (id: number) => http.post(`/calc-jobs/${id}/acknowledge-writes`),
  list: async (signal?: AbortSignal) => (await http.get<CalcJobInfo[]>('/calc-jobs', { signal })).data,
  save: async (id: number | null, input: CalcJobInput) => (await (id === null ? http.post<CalcJob>('/calc-jobs', input) : http.put<CalcJob>(`/calc-jobs/${id}`, input))).data,
  publish: async (id: number, revision: number) => (await http.post<CalcJob>(`/calc-jobs/${id}/publish`, null, { params: { revision } })).data,
  publications: async (id: number) => (await http.get<CalcJobPublication[]>(`/calc-jobs/${id}/publications`)).data,
  rollback: async (id: number, publicationId: number, revision: number) => (await http.post<CalcJob>(`/calc-jobs/${id}/rollback`, null, { params: { publicationId, revision } })).data,
  invoke: async (id: number, argument?: number) => http.post(`/calc-jobs/${id}/invoke`, null, { params: argument === undefined ? {} : { argument } }),
  references: async (id: number) => (await http.get<ScriptReferenceList>(`/calc-jobs/${id}/references`)).data,
  libraries: async () => (await http.get<ScriptLibrary[]>('/calc-jobs/libraries')).data,
  saveLibrary: async (id: number | null, name: string, source: string, revision: number) => (await (id === null ? http.post<ScriptLibrary>('/calc-jobs/libraries', { name, source, revision }) : http.put<ScriptLibrary>(`/calc-jobs/libraries/${id}`, { name, source, revision }))).data,
  publishLibrary: async (id: number, revision: number) => (await http.post<ScriptLibrary>(`/calc-jobs/libraries/${id}/publish`, null, { params: { revision } })).data,
  removeLibrary: async (id: number, revision: number) => http.delete(`/calc-jobs/libraries/${id}`, { params: { revision } }),
  libraryReferences: async (id: number) => (await http.get<ScriptJobReference[]>(`/calc-jobs/libraries/${id}/references`)).data,
  remove: async (job: CalcJob) => http.delete(`/calc-jobs/${job.id}`, { params: { revision: job.revision } }),
  reset: async (id: number) => http.post(`/calc-jobs/${id}/reset`),
  maintenanceDone: async (id: number) => http.post(`/calc-jobs/${id}/maintenance-done`),
  snapshots: async (id: number) => (await http.get<CalcJobSnapshot[]>(`/calc-jobs/${id}/snapshots`)).data
}

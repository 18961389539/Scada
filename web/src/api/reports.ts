import { http } from './http'

export type ReportPeriod = 'Shift' | 'Day' | 'Month'
export type ReportColumnKind = 'TagAvg' | 'TagMax' | 'TagMin' | 'TagDiff' | 'TagSum' | 'CalcSnapshot' | 'AlarmCount' | 'Formula' | 'AlarmKpi'
export interface ReportColumn {
  key: string; title: string; kind: ReportColumnKind; tagId?: number | null; jobId?: number | null; output?: string | null
  factor?: number | null; minLevel?: string | null; formula?: string | null; decimals: number; unit?: string | null
}
export interface ReportTemplate {
  id: number; name: string; period: ReportPeriod; areaId: number; shiftStarts: string; columns: ReportColumn[]
  scheduleEnabled: boolean; scheduleDelayMin: number; emailChannelId: number | null; emailRecipients: string; revision: number; updatedTs: number
}
export type ReportTemplateInput = Omit<ReportTemplate, 'id' | 'updatedTs'>
export interface ReportRun {
  id: number; templateId: number; templateName: string; templateRevision: number; areaId: number; periodKey: string; fromTs: number; toTs: number
  version: number; trigger: string; generatedBy: string; generatedTs: number; xlsxSha256: string; pdfSha256: string; purgedTs: number | null
  signed: boolean; signerThumbprint: string | null; signerSubject: string | null; signedTs: number | null; signatureError: string | null
  approvalStatus?: ApprovalStatus | null
}
export type ApprovalStatus = 'none' | 'reviewed' | 'approved' | 'rejected'
export interface SignatureCheck { present: boolean; valid: boolean; error: string | null; subject: string | null; thumbprint: string | null; signingTs: number | null; currentCertificate: boolean; chainTrusted: boolean; selfSigned: boolean }
export interface ReportApproval { id: number; runId: number; step: 'review' | 'approve'; decision: 'approved' | 'rejected'; userId: number; userName: string; displayName: string | null; reason: string; ts: number; signerThumbprint: string }
export interface ReportVerification {
  runId: number; purged: boolean; signed: boolean; signerThumbprint: string | null; signerSubject: string | null; signedTs: number | null; signatureError: string | null
  files: { format: 'xlsx' | 'pdf'; exists: boolean; hashOk: boolean; signature: SignatureCheck; ok: boolean }[]
  approvals: { approval: ReportApproval; signature: SignatureCheck; manifestMatches: boolean; ok: boolean }[]
  status: ApprovalStatus; ok: boolean; currentThumbprint: string | null
}
export interface ReportSignInput { step: 'review' | 'approve'; decision: 'approved' | 'rejected'; reason: string; password: string }
export interface ReportRowData { label: string; from: number; to: number; values: (number | null)[] }
export interface ReportTableData { title: string; periodKey: string; columns: { key: string; title: string; unit: string | null; decimals: number }[]; rows: ReportRowData[]; summary: ReportRowData }

export const reportCenterApi = {
  templates: async () => (await http.get<ReportTemplate[]>('/report-templates')).data,
  save: async (id: number | null, input: ReportTemplateInput) => (await (id === null ? http.post<ReportTemplate>('/report-templates', input) : http.put<ReportTemplate>(`/report-templates/${id}`, input))).data,
  remove: async (t: ReportTemplate) => http.delete(`/report-templates/${t.id}`, { params: { revision: t.revision } }),
  generate: async (id: number, at?: number) => (await http.post<ReportRun>(`/report-templates/${id}/generate`, { at: at ?? null })).data,
  runs: async (templateId?: number) => (await http.get<ReportRun[]>('/report-runs', { params: { templateId } })).data,
  run: async (id: number) => (await http.get<{ run: ReportRun; table: ReportTableData }>(`/report-runs/${id}`)).data,
  download: async (id: number, format: 'xlsx' | 'pdf') => (await http.get(`/report-runs/${id}/download`, { params: { format }, responseType: 'blob' })).data as Blob,
  signature: async (id: number, format: 'xlsx' | 'pdf') => (await http.get(`/report-runs/${id}/signature`, { params: { format }, responseType: 'blob' })).data as Blob,
  verify: async (id: number) => (await http.get<ReportVerification>(`/report-runs/${id}/verify`)).data,
  verifyFile: async (id: number, file: Blob) => (await http.post<{ sha256: string; matched: 'xlsx' | 'pdf' | null; signature: SignatureCheck; ok: boolean }>(`/report-runs/${id}/verify-file`, file, { headers: { 'Content-Type': 'application/octet-stream' } })).data,
  sign: async (id: number, input: ReportSignInput) => (await http.post<{ status: ApprovalStatus; approval: ReportApproval }>(`/report-runs/${id}/approvals`, input)).data
}

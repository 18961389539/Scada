import { http, readPage, type ItemPage } from './http'
import type { RecipeDownload, RecipeParamCheck } from './recipes'

export interface ProductionBatch {
  id: number
  batchNumber: string
  areaId: number
  recipeId: number
  recipeName: string
  recipeVersion: number
  sequenceId: number | null
  sequenceName: string | null
  recipeDownloadId: number | null
  controlSequenceRunId: number | null
  sequenceRequestId: string | null
  status: string
  statusLabel: string
  startedTs: number
  endedTs: number | null
  startedBy: string
  message: string | null
}

export interface ProductionBatchAudit {
  ts: number
  userName: string
  action: string
  result: string
  details: string | null
  targetType: string
}

export interface ProductionBatchSequenceRun {
  id: number
  sequenceId: number
  sequenceName: string
  areaId: number
  status: string
  startedTs: number
  endedTs: number | null
  startedBy: string
  approvedBy: string | null
  message: string | null
  steps: { index: number; name: string; status: string; message: string | null; tagId: number | null; value: number }[]
}

export interface ProductionBatchDetail {
  batch: ProductionBatch
  download: RecipeDownload | null
  sequenceRun: ProductionBatchSequenceRun | null
  sequenceRequest: { id: string; status: string; requestedBy: string; decidedBy: string | null; comment: string | null; runId: number | null } | null
  audits: ProductionBatchAudit[]
}

export interface ProductionBatchCheck {
  recipeId: number
  version: number
  sequenceId: number | null
  sequenceName: string | null
  areaId: number
  recipeName: string
  dualApproval: boolean
  ok: boolean
  refusals: string[]
  parameters: RecipeParamCheck[]
  confirmationChallenge: string
  confirmationExpiresTs: number
}

export const productionBatchApi = {
  list: async (batchNumber?: string, before?: number) =>
    readPage(await http.get<ProductionBatch[]>('/production-batches', { params: { batchNumber, before } })) as Promise<ItemPage<ProductionBatch>>,
  get: async (id: number) => (await http.get<ProductionBatchDetail>(`/production-batches/${id}`)).data,
  check: async (recipeId: number, version: number, sequenceId?: number | null) =>
    (await http.post<ProductionBatchCheck>('/production-batches/check', { recipeId, version, sequenceId: sequenceId ?? null })).data,
  start: async (batchNumber: string, recipeId: number, version: number, sequenceId: number | null | undefined, challenge: string) =>
    (await http.post<ProductionBatch>('/production-batches', { batchNumber, recipeId, version, sequenceId: sequenceId ?? null, confirmed: true, challenge })).data,
  abort: async (id: number) => { await http.post(`/production-batches/${id}/abort`) }
}

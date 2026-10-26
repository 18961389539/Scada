import { http } from './http'
import type { ArchiveMode, Tag, TagValue } from '../types'
export interface CalculationInput {
  name: string; expression: string; description: string | null; unit: string | null; enabled: boolean
  intervalMs: number; inputMaxAgeMs: number; archiveMode: ArchiveMode; deadband: number; archivePeriodSec: number; revision: number
  areaId: number | null
}
export interface CalculationInfo { tag: Tag; dependencies: number[]; current: TagValue | null; definitionError: string | null }
export interface CalculationPreview { dependencies: number[]; value: TagValue }
export const calculationApi = {
  list: async (signal?: AbortSignal) => (await http.get<CalculationInfo[]>('/calculated-tags', { signal })).data,
  inputs: async (signal?: AbortSignal) => (await http.get<Tag[]>('/tags', { signal })).data,
  save: async (id: number | null, input: CalculationInput) => (await (id === null ? http.post<Tag>('/calculated-tags', input) : http.put<Tag>(`/calculated-tags/${id}`, input))).data,
  preview: async (id: number | null, input: CalculationInput, signal?: AbortSignal) => (await http.post<CalculationPreview>('/calculated-tags/preview', input, { params: id ? { id } : undefined, signal })).data,
  remove: async (tag: Tag) => http.delete(`/calculated-tags/${tag.id}`, { params: { revision: tag.calculationRevision } })
}

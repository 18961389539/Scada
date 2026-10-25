import { http } from './http'

export type OeeLossCategory = 'Availability' | 'Performance' | 'Quality'
export type LineState = 'Unknown' | 'Running' | 'Stopped' | 'Fault' | 'Setup'

export interface ProductionLine {
  id: number; name: string; areaId: number; description?: string | null
  stateTagId?: number | null; stateMapJson: string
  goodCountTagId?: number | null; rejectCountTagId?: number | null; totalCountTagId?: number | null
  productId?: number | null; idealCycleSec: number; ratedUnitsPerHour?: number | null; targetUnitsPerHour?: number | null
  enabled: boolean; revision: number; updatedTs: number; updatedBy?: string | null
}
export interface ProductionProduct {
  id: number; name: string; code?: string | null; areaId?: number | null
  idealCycleSec: number; ratedUnitsPerHour?: number | null; description?: string | null; revision: number
}
export interface ProductionShift {
  id: number; name: string; areaId: number; startMinute: number; endMinute: number; days: number; enabled: boolean; revision: number
}
export interface DowntimeReason {
  id: number; code: string; name: string; areaId?: number | null; category: OeeLossCategory | string
  isPlanned: boolean; enabled: boolean; revision: number
}
export interface DowntimeEvent {
  id: number; lineId: number; lineName?: string; startTs: number; endTs?: number | null
  reasonId?: number | null; state: string; source: string; alarmEventId?: number | null
  note?: string | null; confirmed: boolean; createdBy: string; createdTs: number
}
export interface OeeThresholds {
  clampPerformance: boolean; targetAvailability: number; targetPerformance: number; targetQuality: number; targetOee: number; topN: number
}
export interface OeeKpi {
  from: number; to: number; availability?: number | null; performance?: number | null; quality?: number | null; oee?: number | null
  plannedMs: number; runMs: number; downMs: number; faultMs: number; good: number; reject: number; scrap: number; total: number
  scrapRate: number; mtbfHours?: number | null; mttrHours?: number | null; faultCount: number
  productionVsTargetPercent: number; target: number; performanceOver100: boolean; performanceClamped: boolean
  downtimePareto: { code: string; name: string; category: string; isPlanned: boolean; hours: number; count: number; percent: number; cumulativePercent: number }[]
  byShift: { label: string; shiftName?: string; availability?: number | null; performance?: number | null; quality?: number | null; oee?: number | null; good: number; total: number }[]
  byProduct: { label: string; productName?: string; availability?: number | null; performance?: number | null; quality?: number | null; oee?: number | null; good: number; total: number }[]
  byDay: { label: string; from: number; to: number; availability?: number | null; performance?: number | null; quality?: number | null; oee?: number | null; good: number; total: number }[]
}
export interface OeeReport { kpi: OeeKpi; detailFrom: number; usedRollups: boolean; trend: OeeKpi['byDay']; thresholds: OeeThresholds }

export const oeeApi = {
  lines: async () => (await http.get<ProductionLine[]>('/oee/lines')).data,
  saveLine: async (id: number | null, body: Omit<ProductionLine, 'id' | 'revision' | 'updatedTs' | 'updatedBy'> & { revision?: number }) =>
    id ? (await http.put<ProductionLine>(`/oee/lines/${id}`, body)).data : (await http.post<ProductionLine>('/oee/lines', body)).data,
  removeLine: async (id: number) => http.delete(`/oee/lines/${id}`),
  products: async () => (await http.get<ProductionProduct[]>('/oee/products')).data,
  saveProduct: async (id: number | null, body: any) => id ? (await http.put(`/oee/products/${id}`, body)).data : (await http.post('/oee/products', body)).data,
  removeProduct: async (id: number) => http.delete(`/oee/products/${id}`),
  shifts: async () => (await http.get<ProductionShift[]>('/oee/shifts')).data,
  saveShift: async (id: number | null, body: any) => id ? (await http.put(`/oee/shifts/${id}`, body)).data : (await http.post('/oee/shifts', body)).data,
  removeShift: async (id: number) => http.delete(`/oee/shifts/${id}`),
  reasons: async () => (await http.get<DowntimeReason[]>('/oee/reasons')).data,
  saveReason: async (id: number | null, body: any) => id ? (await http.put(`/oee/reasons/${id}`, body)).data : (await http.post('/oee/reasons', body)).data,
  removeReason: async (id: number) => http.delete(`/oee/reasons/${id}`),
  downtimes: async (params: Record<string, unknown>) => (await http.get<DowntimeEvent[]>('/oee/downtimes', { params })).data,
  createDowntime: async (body: any) => (await http.post('/oee/downtimes', body)).data,
  updateDowntime: async (id: number, body: any) => (await http.put(`/oee/downtimes/${id}`, body)).data,
  confirmDowntime: async (id: number) => (await http.post(`/oee/downtimes/${id}/confirm`)).data,
  removeDowntime: async (id: number) => http.delete(`/oee/downtimes/${id}`),
  correct: async (body: any) => (await http.post('/oee/corrections', body)).data,
  analytics: async (params: Record<string, unknown>) => (await http.get<OeeReport>('/oee/analytics', { params })).data,
  exportUrl: (params: Record<string, unknown>) => {
    const q = new URLSearchParams()
    Object.entries(params).forEach(([k, v]) => { if (v !== undefined && v !== null && v !== '') q.set(k, String(v)) })
    return `/api/oee/export?${q}`
  },
  thresholds: async () => (await http.get<{ thresholds: OeeThresholds; revision: number; defaults: OeeThresholds }>('/oee/thresholds')).data,
  saveThresholds: async (thresholds: OeeThresholds, revision: number) => (await http.put('/oee/thresholds', { thresholds, revision })).data,
  rollups: async () => (await http.get('/oee/rollups')).data,
  rebuild: async (days: number) => (await http.post('/oee/rollups/rebuild', { days })).data
}

export const oeeKpiMetrics = ['availability', 'performance', 'quality', 'oee', 'plannedHours', 'runHours', 'downHours', 'good', 'reject', 'scrap', 'total', 'scrapRate', 'mtbfHours', 'mttrHours', 'faultCount', 'productionVsTarget', 'target'] as const

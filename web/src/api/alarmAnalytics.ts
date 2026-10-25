import { http } from './http'
import type { AlarmLevel } from '../types'

export interface AlarmAnalyticsFilter { from: number; to: number; areaId?: number | null; levels?: number[]; rules?: number[]; top?: number }
export interface AlarmRef { id: number; source: string; ruleId: number | null; tagId: number | null; deviceId: number | null; message: string; level: AlarmLevel; startTs: number }
export interface AlarmSourceCount { source: string; ruleId: number | null; message: string; level: AlarmLevel; count: number }
export interface AlarmBadActor { source: string; ruleId: number | null; tagId: number | null; deviceId: number | null; message: string; level: AlarmLevel; count: number; annunciated: number; chatterEpisodes: number; fleeting: number; percent: number; cumulativePercent: number }
export interface AlarmChatterItem { source: string; ruleId: number | null; message: string; level: AlarmLevel; activations: number; episodes: number; maxInWindow: number }
export interface AlarmFleetingItem { source: string; ruleId: number | null; message: string; level: AlarmLevel; count: number; total: number; ratio: number; p90DurationSec: number }
export interface AlarmConsequentialPair { sourceA: string; messageA: string; ruleA: number | null; countA: number; sourceB: string; messageB: string; ruleB: number | null; countB: number; together: number; ratio: number }
export interface AlarmFloodPeriod { startTs: number; endTs: number; count: number; peak10Min: number; firstOut: AlarmRef | null; contributors: AlarmSourceCount[]; eventIds: number[] }
export interface AlarmPriorityItem { level: AlarmLevel; band: 'low' | 'medium' | 'high'; count: number; percent: number; targetPercent: number }
export interface AlarmActiveItem { id: number; source: string; ruleId: number | null; tagId: number | null; message: string; level: AlarmLevel; startTs: number; hours: number; acked: boolean }
export interface AlarmShelveStat { ruleId: number; count: number; hours: number }
export interface AlarmRateBucket { startTs: number; count: number }
export interface AlarmAnalyticsKpi {
  from: number; to: number; asOf: number; hours: number
  total: number; annunciated: number; shelved: number; suppressed: number; chattering: number; operatorPositions: number
  perOperator10Min: number; perOperatorHour: number; perOperatorDay: number; peak10Min: number; peak10MinTs: number | null; rating: string
  pctBucketsAcceptable: number; pctBucketsManageable: number; pctBucketsDemanding: number; pctBucketsFlood: number
  percentTimeInFlood: number; floods: AlarmFloodPeriod[]
  ackCount: number; avgAckSec: number | null; medianAckSec: number | null; clearCount: number; avgClearSec: number | null; medianClearSec: number | null
  standing: AlarmActiveItem[]; stale: AlarmActiveItem[]; standingDuringWindow: number
  shelveCount: number; shelvedHours: number; shelvedByRule: AlarmShelveStat[]; suppressedHours: number
  priority: AlarmPriorityItem[]; top: AlarmBadActor[]; chatteringSources: AlarmChatterItem[]; fleeting: AlarmFleetingItem[]; fleetingTotal: number
  consequential: AlarmConsequentialPair[]
}
export interface AlarmSuggestion { kind: string; source: string; ruleId: number | null; message: string; level: string; severity: number; evidence: Record<string, number>; current: Record<string, number>; suggested: Record<string, number> }
export interface AlarmThresholds {
  operatorPositions: number; acceptablePer10Min: number; manageablePer10Min: number; floodStartPer10Min: number; floodEndPer10Min: number
  chatterCount: number; chatterWindowSec: number; fleetingSec: number; standingHours: number; staleHours: number
  consequentialWindowSec: number; consequentialMinCount: number; consequentialMinRatio: number; frequentPerDay: number
  targetLowPercent: number; targetMediumPercent: number; targetHighPercent: number; topN: number
}
export interface AlarmAnalyticsReport { kpi: AlarmAnalyticsKpi; detailFrom: number; usedRollups: boolean; truncated: boolean; rate10: AlarmRateBucket[]; suggestions: AlarmSuggestion[]; thresholds: AlarmThresholds }
export interface AlarmTrendPoint { ts: number; total: number; annunciated: number; info: number; warning: number; critical: number }
export interface AlarmDrillEvent { id: number; ruleId: number | null; tagId: number | null; level: AlarmLevel; startTs: number; endTs: number | null; ackTs: number | null; ackUser: string | null; shelved: boolean; suppressed: boolean; chattering: boolean; message: string; source: string }

function params(f: AlarmAnalyticsFilter): Record<string, unknown> {
  return { from: f.from, to: f.to, areaId: f.areaId ?? undefined, levels: f.levels?.length ? f.levels.join(',') : undefined, rules: f.rules?.length ? f.rules.join(',') : undefined, top: f.top }
}

export const alarmAnalyticsApi = {
  analyze: async (f: AlarmAnalyticsFilter) => (await http.get<AlarmAnalyticsReport>('/alarm-analytics', { params: params(f), timeout: 120000 })).data,
  trend: async (f: AlarmAnalyticsFilter) => (await http.get<{ granularity: 'hour' | 'day'; points: AlarmTrendPoint[] }>('/alarm-analytics/trend', { params: params(f), timeout: 120000 })).data,
  events: async (f: AlarmAnalyticsFilter, drill: { source?: string; kind?: string; ids?: number[]; limit?: number }) =>
    (await http.get<{ total: number; items: AlarmDrillEvent[] }>('/alarm-analytics/events', { params: { ...params(f), source: drill.source, kind: drill.kind, ids: drill.ids?.length ? drill.ids.join(',') : undefined, limit: drill.limit ?? 500 } })).data,
  export: async (f: AlarmAnalyticsFilter, format: 'xlsx' | 'pdf') =>
    (await http.get('/alarm-analytics/export', { params: { ...params(f), format }, responseType: 'blob', timeout: 180000 })).data as Blob,
  thresholds: async () => (await http.get<{ thresholds: AlarmThresholds; revision: number; defaults: AlarmThresholds }>('/alarm-analytics/thresholds')).data,
  saveThresholds: async (thresholds: AlarmThresholds, revision: number) =>
    (await http.put<{ thresholds: AlarmThresholds; revision: number }>('/alarm-analytics/thresholds', { thresholds, revision })).data
}

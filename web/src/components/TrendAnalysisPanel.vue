<template>
  <div v-if="show" v-loading="loading" class="analysis" data-test="history-analysis">
    <div class="analysis-head">
      <span class="analysis-title">{{ $t('trendsView.analysisTitle') }}</span>
      <span class="tiny muted">{{ $t('trendsView.analysisHint') }}</span>
      <span class="spacer" />
      <el-tag v-if="rows.length" size="small" type="info">{{ $t('trendsView.coverageSummary', [coverageSummary]) }}</el-tag>
    </div>

    <el-alert v-if="!canAnalyze" :title="$t('trendsView.analysisLimit')" type="info" :closable="false" show-icon />
    <template v-else>
      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="trend-analysis-error" />
      <el-table v-if="rows.length" :data="rows" size="small" class="analysis-table" data-test="analysis-table">
        <el-table-column :label="$t('trendsView.tag')" min-width="210" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="pv-dot" :style="{ background: row.color }" />
            {{ row.name }}
          </template>
        </el-table-column>
        <el-table-column :label="$t('trendsView.weightedAvg')" width="130" align="right">
          <template #default="{ row }">
            <span class="mono">{{ row.timeWeightedAvg === null ? '-' : formatValue(row.timeWeightedAvg, 3) }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('trendsView.min')" width="100" align="right">
          <template #default="{ row }"><span class="mono">{{ formatValue(row.min, 3) }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('trendsView.max')" width="100" align="right">
          <template #default="{ row }"><span class="mono">{{ formatValue(row.max, 3) }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('trendsView.integral')" width="140" align="right">
          <template #default="{ row }"><span class="mono">{{ formatValue(row.integralHours, 4) }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('trendsView.coverage')" width="100" align="right">
          <template #default="{ row }">{{ row.coverage }}%</template>
        </el-table-column>
        <el-table-column :label="$t('trendsView.samples')" width="110" align="right">
          <template #default="{ row }">{{ row.goodSamples }} / {{ row.samples }}</template>
        </el-table-column>
      </el-table>

      <template v-if="rows.length">
        <div class="quality-legend-line">
          <span v-for="item in qualities" :key="item"><i class="quality-swatch" :class="`q-${item}`" />{{ qualityLabels[item] }}</span>
          <span class="spacer" />
          <span>{{ $t('trendsView.qualityHint') }}</span>
        </div>
        <div v-for="row in rows" :key="row.tagId" class="quality-row" :data-test="`quality-${row.tagId}`">
          <span class="quality-name" :title="row.name">{{ row.name }}</span>
          <div class="quality-strip">
            <span
              v-for="(interval, index) in row.quality"
              :key="index"
              class="quality-seg"
              :class="`q-${interval.quality}`"
              :style="{ left: percent(interval.from), width: qualityWidth(interval.from, interval.to) }"
              :title="`${qualityLabels[interval.quality]} · ${formatTime(interval.from)} ~ ${formatTime(interval.to)}`"
            />
          </div>
          <span v-if="row.qualityTruncated" class="tiny muted">{{ $t('trendsView.qualityTruncated') }}</span>
        </div>
      </template>
    </template>
  </div>
</template>

<script setup lang="ts">
import type { TagQuality } from '../types'
import { formatTime, formatValue, qualityLabels } from '../utils'

export interface TrendAnalysisRow {
  tagId: number
  name: string
  color: string
  timeWeightedAvg: number | null
  min: number | null
  max: number | null
  integralHours: number | null
  coverage: number
  goodSamples: number
  samples: number
  quality: { quality: TagQuality; from: number; to: number }[]
  qualityTruncated: boolean
}

const props = defineProps<{
  show: boolean
  loading: boolean
  error: string
  canAnalyze: boolean
  coverageSummary: string
  qualities: TagQuality[]
  rows: TrendAnalysisRow[]
  displayFrom: number
  displayTo: number
}>()

function percent(ts: number): string {
  if (props.displayTo <= props.displayFrom) return '0%'
  return `${Math.min(100, Math.max(0, ((ts - props.displayFrom) / (props.displayTo - props.displayFrom)) * 100))}%`
}
function qualityWidth(start: number, end: number): string {
  const span = props.displayTo - props.displayFrom
  if (span <= 0) return '0%'
  const width = Math.min(props.displayTo, end) - Math.max(props.displayFrom, start)
  return `${Math.max(0, width / span * 100)}%`
}
</script>

<style scoped>
.pv-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: none;
}
.analysis {
  margin-top: 12px;
  padding-top: 10px;
  border-top: 1px solid var(--scada-border);
}
.analysis-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 6px;
}
.analysis-title { font-weight: 600; }
.analysis-table { margin-bottom: 8px; }
.quality-legend-line {
  display: flex;
  align-items: center;
  gap: 14px;
  margin: 2px 0 6px;
  color: var(--scada-text-muted);
  font-size: var(--scada-fs-caption);
}
.quality-swatch {
  display: inline-block;
  width: 10px;
  height: 10px;
  border-radius: 2px;
  margin-right: 4px;
  vertical-align: -1px;
}
.quality-row {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 3px 0;
}
.quality-name {
  width: 210px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: var(--scada-fs-caption);
}
.quality-strip {
  position: relative;
  flex: 1;
  height: 12px;
  border-radius: 2px;
  overflow: hidden;
  background: var(--scada-panel-muted);
}
.quality-seg {
  position: absolute;
  top: 0;
  bottom: 0;
  min-width: 1px;
}
.q-Good { background: rgba(82, 196, 26, 0.55); }
.q-Bad { background: #d4380d; }
.q-CommFailure { background: #722ed1; }
.q-Uncertain { background: #d48806; }
</style>
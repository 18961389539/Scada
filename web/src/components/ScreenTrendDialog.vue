<template>
  <el-dialog v-model="visible" :title="title" width="82%" top="6vh" append-to-body destroy-on-close data-test="screen-trend-dialog" @opened="init" @closed="dispose">
    <el-alert v-if="unavailable" type="warning" :title="$t('hmi.trendUnavailable')" :closable="false" show-icon data-test="trend-unavailable" />
    <el-empty v-else-if="!hasData" :description="$t('hmi.trendEmpty')" data-test="trend-empty" />
    <template v-else>
      <div ref="chartEl" class="trend-chart" data-test="trend-chart" />
      <div class="trend-footer">
        <el-button size="small" data-test="trend-reset-zoom" @click="resetZoom">{{ $t('hmi.trendResetZoom') }}</el-button>
        <span class="muted tiny">{{ $t('hmi.trendHint') }}</span>
      </div>
      <el-table :data="latest" size="small" data-test="trend-latest">
        <el-table-column prop="name" :label="$t('hmi.trendTag')" min-width="160" show-overflow-tooltip />
        <el-table-column prop="value" :label="$t('hmi.trendLatest')" width="140" show-overflow-tooltip />
        <el-table-column prop="unit" :label="$t('hmi.trendUnit')" width="90" show-overflow-tooltip />
      </el-table>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import * as echarts from '../charts'
import { useI18n } from 'vue-i18n'

interface TrendSeries { tagId: number; name: string; unit: string; color: string; points: { ts: number; value: number }[] }
const props = defineProps<{ modelValue: boolean; title: string; series: TrendSeries[]; unavailable?: boolean; decimals?: number }>()
const emit = defineEmits<{ 'update:modelValue': [boolean] }>()
const visible = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const { locale } = useI18n()
const chartEl = ref<HTMLDivElement>(), chart = ref<echarts.ECharts | null>(null)
let resizeObserver: ResizeObserver | undefined
const hasData = computed(() => props.series.some(series => series.points.length > 0))
const latest = computed(() => props.series.map(series => {
  const last = series.points[series.points.length - 1]
  return { name: series.name, unit: series.unit, value: last == null ? '--' : last.value.toFixed(props.decimals ?? 2) }
}))
function option(): echarts.EChartsOption {
  return {
    backgroundColor: 'transparent', animation: false,
    tooltip: { trigger: 'axis', axisPointer: { type: 'cross', label: { backgroundColor: '#334155' } }, backgroundColor: '#0b1622', borderColor: '#334155', textStyle: { color: '#e5effa' } },
    legend: { top: 4, selectedMode: true, textStyle: { color: '#9fb3c8' } },
    grid: { left: 64, right: 28, top: 44, bottom: 84, containLabel: true },
    xAxis: { type: 'time', axisLabel: { color: '#9fb3c8' }, axisLine: { lineStyle: { color: '#243449' } }, splitLine: { show: false } },
    yAxis: { type: 'value', scale: true, axisLabel: { color: '#9fb3c8' }, splitLine: { lineStyle: { color: '#243449' } } },
    dataZoom: [{ type: 'inside' }, { type: 'slider', height: 24, bottom: 12, borderColor: '#243449', textStyle: { color: '#9fb3c8' }, labelFormatter: (value: number) => new Date(value).toLocaleTimeString(locale.value, { hour12: false }) }],
    series: props.series.map(series => ({
      name: series.name, type: 'line', showSymbol: false, sampling: 'lttb',
      data: series.points.map(point => [point.ts, point.value]),
      lineStyle: { color: series.color, width: 2 }, itemStyle: { color: series.color },
      tooltip: { valueFormatter: (value: unknown) => `${Number(value).toFixed(props.decimals ?? 2)}${series.unit ? ` ${series.unit}` : ''}` }
    }))
  }
}
function init(): void {
  if (!chartEl.value || chart.value) return
  chart.value = echarts.init(chartEl.value)
  chart.value.setOption(option())
  resizeObserver = new ResizeObserver(() => chart.value?.resize())
  resizeObserver.observe(chartEl.value)
}
function dispose(): void {
  resizeObserver?.disconnect(); resizeObserver = undefined
  chart.value?.dispose(); chart.value = null
}
function resetZoom(): void { chart.value?.dispatchAction({ type: 'dataZoom', start: 0, end: 100 }) }
watch(() => props.series, () => { chart.value?.setOption(option()) }, { deep: true })
// 历史不可用/无数据会卸载图表容器；恢复数据后要重新绑定实例，避免挂在已移除的节点上。
watch(() => [props.unavailable, hasData.value] as const, async () => {
  await nextTick()
  if (!hasData.value || props.unavailable) { dispose(); return }
  if (!chart.value) init()
})
onBeforeUnmount(dispose)
</script>

<style scoped>
.trend-chart { height: 440px; border-radius: 6px; background: #0f172a; border: 1px solid #243449; }
.trend-footer { display: flex; align-items: center; gap: 12px; margin: 10px 0; }
</style>

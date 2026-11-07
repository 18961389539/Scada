<template>
  <div class="stat-card kpi-card" :class="tone ? `is-${tone}` : ''">
    <div class="label">{{ label }}</div>
    <div class="kpi-main">
      <div class="value">{{ value }}</div>
      <svg v-if="points" class="kpi-spark" viewBox="0 0 72 24" preserveAspectRatio="none" aria-hidden="true">
        <line v-if="targetY !== null" x1="0" :y1="targetY" x2="72" :y2="targetY" class="kpi-target" />
        <polyline :points="points" fill="none" :stroke="stroke" stroke-width="1.5" stroke-linejoin="round" stroke-linecap="round" />
        <circle :cx="lastX" :cy="lastY" r="1.8" :fill="stroke" />
      </svg>
    </div>
    <div v-if="sub" class="sub">{{ sub }}</div>
    <slot />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = withDefaults(defineProps<{
  label: string
  value: string
  sub?: string
  /** 只在异常时着色：正常卡片保持中性。 */
  tone?: 'warn' | 'alarm'
  /** 采样点序列，2 个以上才画火花线。 */
  series?: number[]
  /** 目标/上限线，用于判断当前值离正常区有多远。 */
  target?: number
}>(), { sub: '', series: () => [], target: undefined })

const SPARK_W = 72
const SPARK_H = 24
const PAD = 2

const stroke = computed(() => props.tone === 'alarm' ? 'var(--scada-danger)' : props.tone === 'warn' ? 'var(--scada-warning)' : 'var(--scada-primary)')

const range = computed(() => {
  const data = props.series.slice(-40)
  if (data.length < 2) return null
  // 先看数据本身有没有变化：只加了目标线不算趋势，否则会画出一条孤零零的虚线。
  if (Math.max(...data) - Math.min(...data) < 1e-9) return null
  const all = props.target == null ? data : [...data, props.target]
  const min = Math.min(...all)
  const max = Math.max(...all)
  if (max - min < 1e-9) return null
  return { data, min, max }
})

const points = computed(() => {
  const r = range.value
  if (!r) return ''
  const step = (SPARK_W - PAD * 2) / (r.data.length - 1)
  return r.data.map((value, index) => {
    const x = PAD + index * step
    const y = SPARK_H - PAD - ((value - r.min) / (r.max - r.min)) * (SPARK_H - PAD * 2)
    return `${x.toFixed(1)},${y.toFixed(1)}`
  }).join(' ')
})

const lastX = computed(() => {
  const r = range.value
  if (!r) return 0
  return (PAD + (r.data.length - 1) * ((SPARK_W - PAD * 2) / (r.data.length - 1))).toFixed(1)
})
const lastY = computed(() => {
  const r = range.value
  if (!r) return 0
  const value = r.data[r.data.length - 1]
  return (SPARK_H - PAD - ((value - r.min) / (r.max - r.min)) * (SPARK_H - PAD * 2)).toFixed(1)
})

const targetY = computed(() => {
  const r = range.value
  if (!r || props.target == null) return null
  return (SPARK_H - PAD - ((props.target - r.min) / (r.max - r.min)) * (SPARK_H - PAD * 2)).toFixed(1)
})
</script>

<style scoped>
.kpi-card {
  min-width: 0;
}
.kpi-main {
  display: flex;
  align-items: flex-end;
  justify-content: space-between;
  gap: 8px;
}
.kpi-spark {
  flex: none;
  width: 72px;
  height: 24px;
  margin-bottom: 4px;
  overflow: visible;
}
.kpi-target {
  stroke: var(--scada-border-strong);
  stroke-width: 1;
  stroke-dasharray: 2 2;
}
.kpi-card.is-warn { border-left: 3px solid var(--scada-alarm-warning); }
.kpi-card.is-alarm { border-left: 3px solid var(--scada-alarm-critical); }
.kpi-card.is-warn .value { color: var(--scada-warning); }
.kpi-card.is-alarm .value { color: var(--scada-danger); }

@media (max-width: 900px) {
  .kpi-spark { display: none; }
}
</style>

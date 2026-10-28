<template>
  <el-alert v-if="alert" :type="alert.type" :title="alert.title" :closable="false" show-icon class="alarm-flood-banner">
    <span class="flood-detail">{{ compact ? $t('alarmsView.floodScope') : alert.detail }}</span>
    <el-button v-if="review" link type="primary" data-test="flood-review" @click="emit('review')">{{ $t('alarmsView.reviewFlood') }}</el-button>
  </el-alert>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { AlarmSummary } from '../types'
import { formatValue, levelLabel } from '../utils'
import { tr } from '../i18n'

const props = defineProps<{ summary: AlarmSummary | null; review?: boolean; compact?: boolean }>()
const emit = defineEmits<{ (event: 'review'): void }>()

function windowLabel(start: number, end: number): string {
  return Number.isFinite(start) && Number.isFinite(end) && end > start
    ? tr('alarmsView.floodWindow', [formatValue((end - start) / 60000, 1)])
    : tr('alarmsView.floodWindowUnknown')
}

const alert = computed(() => {
  const guard = props.summary?.floodGuard
  const areas = (guard?.areas ?? []).filter(area => area.inFlood)
  const scope = tr('alarmsView.floodScope')
  if (guard && areas.length) {
    const detail = [
      scope,
      guard.autoSuppress
        ? tr('alarmsView.floodSuppression', [areas.reduce((sum, area) => sum + area.suppressedCount, 0), levelLabel(guard.suppressMaxLevel), levelLabel('Critical')])
        : tr('alarmsView.floodGuardNoSuppress'),
      ...areas.map(area => tr('alarmsView.floodAreaExit', [area.areaName ?? tr('alarmsView.floodAreaUnnamed'), area.exitThreshold]))
    ]
    if (guard.firstOut) {
      detail.push(...areas.filter(area => area.firstOutEventId != null).map(area =>
        `${area.areaName ?? tr('alarmsView.floodAreaUnnamed')} ${tr('alarmsView.floodGuardFirstOut', [area.firstOutEventId])}`))
    }
    return {
      type: 'error' as const,
      title: tr('alarmsView.floodGuardTitle', [areas.map(area => tr('alarmsView.floodAreaWindow', [
        area.areaName ?? tr('alarmsView.floodAreaUnnamed'), windowLabel(area.windowStartTs, area.windowEndTs), area.count, area.threshold
      ])).join(' · ')]),
      detail: detail.join(' · ')
    }
  }
  const flood = props.summary?.flood
  if (!flood?.inFlood) return null
  return {
    type: 'warning' as const,
    title: tr('alarmsView.floodRate', [windowLabel(flood.windowStartTs, flood.windowEndTs), flood.count, flood.threshold]),
    detail: scope
  }
})
</script>

<style scoped>
.alarm-flood-banner { flex: none; margin-bottom: 12px; }
.flood-detail { display: block; margin: 4px 0; overflow-wrap: anywhere; }
.alarm-flood-banner :deep(.el-alert__content) { min-width: 0; overflow-wrap: anywhere; }
</style>

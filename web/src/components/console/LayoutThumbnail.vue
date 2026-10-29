<template>
  <div class="layout-thumbnails" :class="{ compact }" data-test="layout-thumbnails">
    <div v-for="(monitor, index) in monitors" :key="index" class="thumbnail-monitor">
      <div class="thumbnail-name">{{ monitor.name || $t('layoutLifecycle.monitorLocation', [index + 1]) }}</div>
      <div class="thumbnail-console" :style="{ aspectRatio: `${previewSize(monitor).width} / ${previewSize(monitor).height}` }" role="img" :aria-label="description(monitor)" :title="description(monitor)">
        <div class="thumbnail-header" /><div class="thumbnail-banner" />
        <div class="thumbnail-grid" :style="{ gridTemplateColumns: `repeat(${monitor.columns}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${monitor.rows}, minmax(0, 1fr))` }">
          <div v-for="(region, ri) in monitor.regions" :key="ri" :class="`kind-${region.kind}`" :style="position(monitor, region)"><span v-if="!compact">{{ region.title || regionLabel(region) }}</span></div>
        </div>
      </div>
    </div>
    <span v-if="document.monitors.length > limit" class="thumbnail-more">{{ $t('layoutCatalog.moreMonitors', [document.monitors.length - limit]) }}</span>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { HmiLayoutDocument, HmiMonitor, HmiRegion } from '../../api/hmiLayouts'
import { contentMonitor, previewSize } from './layoutGeometry'
import { tr } from '../../i18n'

const props = withDefaults(defineProps<{ document: HmiLayoutDocument; screenNames?: ReadonlyMap<number, string>; limit?: number; compact?: boolean }>(), { limit: 2, compact: false })
const monitors = computed(() => props.document.monitors.slice(0, props.limit).map(contentMonitor))
const kindKeys = { screen: 'layoutsView.k8a1840', alarmList: 'layoutsView.k459887', navigation: 'layoutsView.k6be0ed', alarmBanner: 'layoutsView.k77911d' }
function regionLabel(region: HmiRegion): string {
  return region.kind === 'screen' ? props.screenNames?.get(region.screenId ?? -1) || tr('layoutImprovements.unavailableScreen', [region.screenId ?? '—']) : tr(kindKeys[region.kind])
}
function description(monitor: HmiMonitor): string {
  return [monitor.name, tr('layoutPreview.fixedAlarm'), ...monitor.regions.map(region => `${region.id}: ${regionLabel(region)}`)].filter(Boolean).join(' · ')
}
function position(monitor: HmiMonitor, region: HmiRegion): Record<string, string> {
  const row = Math.max(1, Math.min(monitor.rows, region.row)), col = Math.max(1, Math.min(monitor.columns, region.col))
  return { gridRow: `${row} / span ${Math.max(1, Math.min(monitor.rows - row + 1, region.rowSpan))}`, gridColumn: `${col} / span ${Math.max(1, Math.min(monitor.columns - col + 1, region.colSpan))}` }
}
</script>

<style scoped>
.layout-thumbnails { display: flex; align-items: start; flex-wrap: wrap; gap: 8px; min-width: 0; }
.thumbnail-monitor { min-width: 0; flex: 1 1 120px; max-width: 240px; }
.thumbnail-name { font-size: 11px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; line-height: 1.7; }
.thumbnail-console { display: flex; flex-direction: column; background: #0a111b; overflow: hidden; border: 1px solid #36516f; border-radius: 3px; }
.thumbnail-header { height: 5px; background: #26394e; flex-shrink: 0; }
.thumbnail-banner { height: 4px; background: #916338; flex-shrink: 0; }
.thumbnail-grid { display: grid; gap: 2px; flex: 1; min-height: 0; padding: 2px; }
.thumbnail-grid > div { background: #31557c; color: #e1ebf4; min-width: 0; min-height: 0; overflow: hidden; font-size: 10px; padding: 2px; line-height: 1.3; }
.thumbnail-grid .kind-navigation { background: #34625e; }.thumbnail-grid .kind-alarmList { background: #77552e; }
.thumbnail-more { font-size: 11px; color: var(--scada-text-muted); align-self: center; }
.compact .thumbnail-monitor { flex: 0 1 100px; }.compact .thumbnail-grid > div { padding: 0; }
</style>

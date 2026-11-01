<template>
  <details class="geometry-tools" data-test="layout-geometry-tools">
    <summary>{{ $t('layoutGeometry.title') }}</summary>
    <div class="geometry-content">
      <strong>{{ $t('layoutGeometry.selectedRegion', [monitor.regions[selectedIndex]?.id || '?']) }}</strong>
      <p class="geometry-help">{{ $t('layoutGeometry.alignHelp') }}</p>
      <div class="geometry-actions">
        <WorkbenchTooltip v-for="alignment in alignments" :key="alignment" :content="!alignmentResults[alignment] ? $t('layoutGeometry.alignBlocked') : !alignmentChanged(alignment) ? $t('layoutGeometry.alreadyAligned') : $t('workbenchTips.alignRegion')" :disabled-trigger="(!alignmentResults[alignment] || !alignmentChanged(alignment))"><el-button size="small" :disabled="!alignmentResults[alignment] || !alignmentChanged(alignment)" :data-test="`layout-align-${alignment}`" @click="applyAlignment(alignment)">{{ $t(`layoutGeometry.${alignment}`) }}</el-button></WorkbenchTooltip>
      </div>
      <strong>{{ $t('layoutGeometry.allRegions', [monitor.regions.length]) }}</strong>
      <p class="geometry-help">{{ $t('layoutGeometry.divideHelp') }}</p>
      <div class="geometry-actions">
        <WorkbenchTooltip :content="!columns ? $t('layoutGeometry.divideColumnsBlocked') : !geometryChanged(monitor.regions, columns) ? $t('layoutGeometry.alreadyAligned') : $t('workbenchTips.divideColumns')" :disabled-trigger="(!columns || !geometryChanged(monitor.regions, columns))"><el-button size="small" data-test="layout-divide-columns" :disabled="!columns || !geometryChanged(monitor.regions, columns)" @click="columns && $emit('apply', columns)">{{ $t('layoutGeometry.divideColumns') }}</el-button></WorkbenchTooltip>
        <WorkbenchTooltip :content="!rows ? $t('layoutGeometry.divideRowsBlocked') : !geometryChanged(monitor.regions, rows) ? $t('layoutGeometry.alreadyAligned') : $t('workbenchTips.divideRows')" :disabled-trigger="(!rows || !geometryChanged(monitor.regions, rows))"><el-button size="small" data-test="layout-divide-rows" :disabled="!rows || !geometryChanged(monitor.regions, rows)" @click="rows && $emit('apply', rows)">{{ $t('layoutGeometry.divideRows') }}</el-button></WorkbenchTooltip>
        <WorkbenchTooltip :content="!fitted ? $t('layoutGeometry.fitBlocked') : !geometryChanged(monitor.regions, fitted) ? $t('layoutGeometry.alreadyAligned') : $t('layoutGeometry.fitHelp')" :disabled-trigger="(!fitted || !geometryChanged(monitor.regions, fitted))"><el-button size="small" data-test="layout-fit-grid" :disabled="!fitted || !geometryChanged(monitor.regions, fitted)" @click="fitted && $emit('apply', fitted)">{{ $t('layoutGeometry.fit') }}</el-button></WorkbenchTooltip>
      </div>
      <p v-if="!fitted" class="geometry-warning" role="status">{{ $t('layoutGeometry.fitBlocked') }}</p>
    </div>
  </details>
</template>

<script setup lang="ts">
import WorkbenchTooltip from './WorkbenchTooltip.vue'
import { computed } from 'vue'
import type { HmiMonitor } from '../../api/hmiLayouts'
import { alignRegion, divideRegions, fitRegionsToGrid, geometryChanged, type RegionAlignment, type RegionGeometry } from './layoutEditing'

const props = defineProps<{ monitor: HmiMonitor; selectedIndex: number }>()
const emit = defineEmits<{ apply: [geometry: RegionGeometry[]] }>()
const alignments: RegionAlignment[] = ['left', 'center', 'right', 'top', 'middle', 'bottom']
const alignmentResults = computed(() => Object.fromEntries(alignments.map(alignment => [alignment, alignRegion(props.monitor, props.selectedIndex, alignment)])) as Record<RegionAlignment, RegionGeometry | null>)
const columns = computed(() => divideRegions(props.monitor, 'columns'))
const rows = computed(() => divideRegions(props.monitor, 'rows'))
const fitted = computed(() => fitRegionsToGrid(props.monitor))
function alignmentChanged(alignment: RegionAlignment): boolean {
  const result = alignmentResults.value[alignment], region = props.monitor.regions[props.selectedIndex]
  return !!result && !!region && geometryChanged([region], [result])
}
function applyAlignment(alignment: RegionAlignment): void {
  const result = alignmentResults.value[alignment]
  if (result && alignmentChanged(alignment)) emit('apply', props.monitor.regions.map((region, index) => index === props.selectedIndex ? result : region))
}
</script>

<style scoped>
.geometry-tools { margin-top: 12px; border: 1px solid var(--scada-border); border-radius: 8px; font-size: 13px; }
summary { cursor: pointer; padding: 12px; min-height: 44px; box-sizing: border-box; }
summary:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.geometry-content { padding: 0 12px 12px; }
.geometry-content > strong { display: block; margin-top: 10px; overflow-wrap: anywhere; }
.geometry-help, .geometry-warning { font-size: 12px; line-height: 1.6; color: var(--scada-text-muted); margin: 6px 0 10px; }
.geometry-warning { color: var(--scada-warning); }
.geometry-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.geometry-actions :deep(.el-button) { margin: 0; }
@media (max-width: 768px) { .geometry-actions :deep(.el-button) { min-height: 44px; } }
</style>

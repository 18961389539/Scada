<template>
  <div v-if="cursorValues.length" class="playback-bar" data-test="playback-bar">
    <el-button
      circle
      size="small"
      :icon="playing ? VideoPause : VideoPlay"
      :title="playing ? $t('trendsView.pause') : $t('trendsView.play')"
      :aria-label="playing ? $t('trendsView.pause') : $t('trendsView.play')"
      data-test="playback-toggle"
      @click="emit('toggle')"
    />
    <el-button text size="small" :icon="RefreshLeft" :title="$t('trendsView.playbackReset')" data-test="playback-reset" @click="emit('reset')" />
    <el-slider
      :model-value="cursorTs"
      class="playback-slider"
      :min="displayFrom"
      :max="displayTo"
      :step="playStep"
      :format-tooltip="formatTime"
      data-test="playback-slider"
      @input="onSlider"
    />
    <span class="mono tiny cursor-time" data-test="cursor-time">{{ formatTime(cursorTs) }}</span>
    <el-select :model-value="speed" class="playback-speed" size="small" style="width: 92px" data-test="playback-speed" @update:model-value="onSpeed">
      <el-option v-for="item in speeds" :key="item" :label="`${item}×`" :value="item" />
    </el-select>
  </div>

  <div v-if="cursorValues.length" class="playback-values" data-test="playback-values">
    <div v-for="item in cursorValues" :key="item.tagId" class="pv-item">
      <span class="pv-dot" :style="{ background: item.color }" />
      <span class="pv-name">{{ item.name }}</span>
      <span class="pv-value mono">{{ item.displayValue ?? (item.value === null ? '-' : formatValue(item.value, 3)) }}</span>
      <el-tag v-if="item.quality" :type="qualityType(item.quality)" size="small" effect="plain">{{ qualityLabels[item.quality] }}</el-tag>
      <span v-else class="tiny muted">{{ $t('trendsView.noPointBefore') }}</span>
    </div>
  </div>
</template>

<script setup lang="ts">
import { RefreshLeft, VideoPause, VideoPlay } from '@element-plus/icons-vue'
import type { TagQuality } from '../types'
import { formatTime, formatValue, qualityLabels, qualityType } from '../utils'

export interface TrendCursorValue {
  tagId: number
  name: string
  color: string
  value: number | null
  quality?: TagQuality
  displayValue?: string
}

defineProps<{
  playing: boolean
  cursorTs: number
  displayFrom: number
  displayTo: number
  playStep: number
  speed: number
  cursorValues: TrendCursorValue[]
}>()

const emit = defineEmits<{
  (event: 'toggle'): void
  (event: 'reset'): void
  (event: 'seek', value: number): void
  (event: 'update:speed', value: number): void
}>()

const speeds = [1, 10, 60, 300]

function onSlider(value: number | number[]): void {
  emit('seek', Array.isArray(value) ? value[0] : value)
}
function onSpeed(value: number): void {
  emit('update:speed', value)
}
</script>
<style scoped>
.playback-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 8px;
  padding: 4px 10px;
  border: 1px solid var(--scada-border);
  border-radius: var(--scada-radius);
  background: var(--scada-panel-muted);
}
.playback-slider {
  flex: 1;
  min-width: 100px;
  margin: 0 10px;
}
.cursor-time {
  min-width: 150px;
  text-align: right;
}
.playback-values {
  display: flex;
  flex-wrap: wrap;
  gap: 6px 18px;
  padding: 8px 4px 2px;
}
.pv-item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-width: 220px;
}
.pv-dot {
  display: inline-block;
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex: none;
}
.pv-name {
  color: var(--scada-text-secondary);
  font-size: var(--scada-fs-caption);
}
.pv-value {
  font-weight: 600;
  min-width: 64px;
}
@media (max-width: 600px) {
  .playback-bar { display: grid; grid-template-columns: 28px 28px minmax(0, 1fr); }
  .playback-bar > .el-button { margin-left: 0; }
  .playback-slider { grid-column: 1 / -1; grid-row: 2; min-width: 0; width: auto; }
  .cursor-time { grid-column: 1 / -1; grid-row: 3; min-width: 0; text-align: left; }
  .playback-speed { grid-column: 3; grid-row: 1; justify-self: end; }
}
</style>
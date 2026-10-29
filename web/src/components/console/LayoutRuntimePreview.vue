<template>
  <div class="runtime-preview">
    <p class="preview-description">{{ $t('layoutPreview.readOnlyHelp') }}</p>
    <div class="preview-controls">
      <label>{{ $t('layoutPreview.monitor') }} <el-select v-model="monitorIndex" data-test="runtime-preview-monitor"><el-option v-for="(monitor, index) in document.monitors" :key="index" :value="index" :label="monitor.name || $t('layoutLifecycle.monitorLocation', [index + 1])" /></el-select></label>
      <label>{{ $t('layoutPreview.viewport') }} <el-select v-model="resolution" data-test="runtime-preview-resolution"><el-option v-for="size in sizes" :key="size" :value="size" :label="size" /></el-select></label>
      <span data-test="runtime-preview-size">{{ width }} × {{ height }} · {{ Math.round(scale * 100) }}%</span>
    </div>
    <div ref="container" class="preview-container">
      <div class="preview-scaled-frame" :style="{ width: `${width * scale}px`, height: `${height * scale}px` }">
        <iframe ref="frame" src="/config/layout-preview" :title="$t('layoutPreview.runtimePreview')" tabindex="-1" data-test="runtime-preview-frame" :style="{ width: `${width}px`, height: `${height}px`, transform: `scale(${scale})` }" @load="sendSnapshot" />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { HmiLayoutDocument } from '../../api/hmiLayouts'
import { previewSize } from './layoutGeometry'

const props = withDefaults(defineProps<{ document: HmiLayoutDocument; name: string; initialMonitor?: number }>(), { initialMonitor: 0 })
const monitorIndex = ref(props.initialMonitor)
const selected = computed(() => props.document.monitors[monitorIndex.value] ?? props.document.monitors[0])
const target = computed(() => selected.value ? previewSize(selected.value) : { width: 1920, height: 1080 })
const resolution = ref(`${target.value.width}×${target.value.height}`)
const sizes = computed(() => [...new Set([`${target.value.width}×${target.value.height}`, '1920×1080', '1366×768', '1280×1024', '1920×1200', '1080×1920', '390×844', '3840×2160'])])
const width = computed(() => Number(resolution.value.split('×')[0])), height = computed(() => Number(resolution.value.split('×')[1]))
const container = ref<HTMLElement>(), frame = ref<HTMLIFrameElement>(), availableWidth = ref(1)
const scale = computed(() => Math.min(1, Math.max(1, availableWidth.value) / width.value, 720 / height.value))
let observer: ResizeObserver | undefined
function sendSnapshot(): void {
  frame.value?.contentWindow?.postMessage({ type: 'scada-layout-preview', monitor: monitorIndex.value, layout: { id: 0, name: props.name, revision: 0, updatedTs: 0, updatedBy: '', document: JSON.parse(JSON.stringify(props.document)) } }, window.location.origin)
}
function receive(event: MessageEvent): void {
  if (event.source === frame.value?.contentWindow && event.origin === window.location.origin && event.data?.type === 'scada-layout-preview-ready') sendSnapshot()
}
watch(target, size => { resolution.value = `${size.width}×${size.height}` })
watch(() => [props.document, props.name, monitorIndex.value], sendSnapshot, { deep: true })
onMounted(() => {
  window.addEventListener('message', receive)
  observer = new ResizeObserver(entries => { availableWidth.value = entries[0]?.contentRect.width ?? 1 })
  if (container.value) observer.observe(container.value)
})
onBeforeUnmount(() => { observer?.disconnect(); window.removeEventListener('message', receive) })
</script>

<style scoped>
.preview-description { font-size: 12px; line-height: 1.7; color: var(--scada-text-muted); }
.preview-controls { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin: 12px 0; font-size: 12px; }
.preview-controls label { display: flex; align-items: center; gap: 8px; }
.preview-controls .el-select { width: 170px; }
.preview-container { width: 100%; min-width: 0; overflow: hidden; background: #0d1622; }
.preview-scaled-frame { position: relative; margin: auto; }
iframe { display: block; border: 0; transform-origin: top left; pointer-events: none; }
</style>

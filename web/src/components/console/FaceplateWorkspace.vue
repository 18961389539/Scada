<template>
  <div ref="workspace" class="faceplate-workspace" data-test="faceplate-workspace">
    <div v-for="panel in panels" v-show="!panel.minimized" :key="panel.key" :ref="el => setElement(panel.key, el)" class="faceplate" :class="{ 'is-docked': panel.dock }" :style="panelStyle(panel)" :data-faceplate-key="panel.key" data-test="faceplate" @pointerdown="raise(panel)" @focusin="raise(panel)" @keydown.esc.stop="close(panel.key)">
      <div class="faceplate-head" tabindex="0" :aria-label="screenName(panel.screenId)" @pointerdown="startMove($event, panel)">
        <span :title="screenName(panel.screenId)">{{ screenName(panel.screenId) }}</span>
        <el-button size="small" link :icon="Minus" :aria-label="$t('consoleImprovements.minimize')" data-test="faceplate-minimize" @click.stop="panel.minimized = true" />
        <el-dropdown trigger="click" @command="(command: string) => dock(panel, command)">
          <el-button size="small" link :icon="MoreFilled" :aria-label="$t('consoleImprovements.panelActions')" data-test="faceplate-actions" />
          <template #dropdown><el-dropdown-menu>
            <el-dropdown-item command="left">{{ $t('consoleImprovements.dockLeft') }}</el-dropdown-item>
            <el-dropdown-item command="right">{{ $t('consoleImprovements.dockRight') }}</el-dropdown-item>
            <el-dropdown-item command="float">{{ $t('consoleImprovements.float') }}</el-dropdown-item>
          </el-dropdown-menu></template>
        </el-dropdown>
        <el-button size="small" link @click.stop="$emit('detach', panel.screenId, panel.key)">{{ $t('consoleView.kad9ce6') }}</el-button>
        <el-button size="small" link :icon="Close" :aria-label="$t('layoutMonitor.closePanel')" data-test="faceplate-close" @click.stop="close(panel.key)" />
      </div>
      <div class="faceplate-body"><ScreenRuntime :key="panel.screenId" :screen-id="panel.screenId" navigate-mode="emit" compact fill initial-zoom="fit" toolbar-mode="embedded" :monitor-mode="monitorMode" @navigate="navigate(panel, $event)" @popup="open" /></div>
    </div>
    <div v-if="panels.length" class="panel-tray" data-test="faceplate-tray" :aria-label="$t('consoleImprovements.openPanels')">
      <button v-for="panel in panels" :key="panel.key" :class="{ minimized: panel.minimized }" :title="screenName(panel.screenId)" :aria-label="$t('consoleImprovements.restorePanel', [screenName(panel.screenId)])" :data-test="`faceplate-restore-${panel.screenId}`" @click="restore(panel)">{{ screenName(panel.screenId) }}</button>
      <el-button size="small" data-test="faceplate-arrange" @click="arrange">{{ $t('consoleImprovements.arrange') }}</el-button>
      <el-button size="small" data-test="faceplate-close-all" @click="clear">{{ $t('consoleImprovements.closeAll') }}</el-button>
    </div>
  </div>
</template>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, type ComponentPublicInstance, type CSSProperties } from 'vue'
import { Close, Minus, MoreFilled } from '@element-plus/icons-vue'
import ScreenRuntime from '../ScreenRuntime.vue'
import { tr } from '../../i18n'

defineProps<{ screenName: (id: number) => string; monitorMode: boolean }>()
const emit = defineEmits<{ detach: [screenId: number, key: number]; error: [message: string] }>()
interface Panel { key: number; screenId: number; x: number; y: number; z: number; minimized: boolean; dock: 'left' | 'right' | null }
const workspace = ref<HTMLElement>(), panels = ref<Panel[]>([])
const elements = new Map<number, HTMLElement>()
let sequence = 0, topZ = 0
const size = ref({ width: 0, height: 0 })
const observer = new ResizeObserver(() => {
  if (workspace.value && (size.value.width !== workspace.value.clientWidth || size.value.height !== workspace.value.clientHeight)) size.value = { width: workspace.value.clientWidth, height: workspace.value.clientHeight }
  constrain()
})
function setElement(key: number, element: Element | ComponentPublicInstance | null): void {
  const previous = elements.get(key)
  if (previous === element) return
  if (previous) observer.unobserve(previous)
  elements.delete(key)
  if (element instanceof HTMLElement) { elements.set(key, element); observer.observe(element) }
}
function availableHeight(): number { return Math.max(0, size.value.height - 48) }
function panelStyle(panel: Panel): CSSProperties {
  const width = Math.max(0, size.value.width - 16), height = Math.max(0, availableHeight() - 16)
  const dockWidth = size.value.width >= 900 ? Math.floor(width / 2) : width
  return {
    left: `${panel.dock === 'right' ? size.value.width - dockWidth - 8 : panel.dock ? 8 : panel.x}px`,
    top: `${panel.dock ? 8 : panel.y}px`, zIndex: panel.z,
    width: `${panel.dock ? dockWidth : Math.min(560, width)}px`,
    height: `${panel.dock ? height : Math.min(420, height)}px`,
    maxWidth: `${width}px`, maxHeight: `${height}px`
  }
}
function constrain(): void {
  for (const panel of panels.value) {
    const element = elements.get(panel.key)
    if (!element || panel.minimized || panel.dock) continue
    panel.x = Math.max(8, Math.min(panel.x, size.value.width - element.offsetWidth - 8))
    panel.y = Math.max(8, Math.min(panel.y, availableHeight() - element.offsetHeight - 8))
  }
}
function raise(panel: Panel): void { panel.z = ++topZ }
function restore(panel: Panel): void { panel.minimized = false; raise(panel); void nextTick(() => { constrain(); elements.get(panel.key)?.querySelector<HTMLElement>('.faceplate-head')?.focus() }) }
function open(screenId: number): void {
  const existing = panels.value.find(panel => panel.screenId === screenId)
  if (existing) { restore(existing); return }
  if (panels.value.length >= 6) { emit('error', tr('consoleView.k994124')); return }
  emit('error', '')
  const panel: Panel = { key: ++sequence, screenId, x: 16 + (panels.value.length % 5) * 28, y: 16 + (panels.value.length % 5) * 24, z: ++topZ, minimized: false, dock: null }
  panels.value.push(panel); void nextTick(() => restore(panel))
}
function navigate(panel: Panel, screenId: number): void {
  const existing = panels.value.find(item => item !== panel && item.screenId === screenId)
  if (existing) { close(panel.key); restore(existing) } else panel.screenId = screenId
}
function close(key: number): void { if (moving?.panel.key === key) moving = null; panels.value = panels.value.filter(panel => panel.key !== key) }
function clear(): void { panels.value = []; moving = null; emit('error', '') }
function dock(panel: Panel, command: string): void { panel.dock = command === 'left' || command === 'right' ? command : null; restore(panel) }
function arrange(): void { panels.value.forEach((panel, index) => { panel.dock = null; panel.minimized = false; panel.x = 16 + index * 28; panel.y = 16 + index * 24; raise(panel) }); void nextTick(constrain) }
let moving: { panel: Panel; dx: number; dy: number } | null = null
function startMove(event: PointerEvent, panel: Panel): void {
  if (event.button !== 0 || (event.target as Element).closest('button')) return
  const bounds = elements.get(panel.key)?.getBoundingClientRect(), container = workspace.value?.getBoundingClientRect()
  if (!bounds || !container) return
  // Restore a docked panel at the pointer before starting a normal drag.
  if (panel.dock) { panel.dock = null; panel.x = Math.max(8, event.clientX - container.left - 80); panel.y = Math.max(8, event.clientY - container.top - 12) }
  moving = { panel, dx: event.clientX - container.left - panel.x, dy: event.clientY - container.top - panel.y }
  event.preventDefault()
}
function onMove(event: PointerEvent): void {
  const bounds = workspace.value?.getBoundingClientRect(), element = moving && elements.get(moving.panel.key)
  if (!moving || !bounds || !element) return
  moving.panel.x = Math.max(8, Math.min(size.value.width - element.offsetWidth - 8, event.clientX - bounds.left - moving.dx))
  moving.panel.y = Math.max(8, Math.min(availableHeight() - element.offsetHeight - 8, event.clientY - bounds.top - moving.dy))
}
function onUp(): void { moving = null }
onMounted(() => { if (workspace.value) observer.observe(workspace.value); window.addEventListener('pointermove', onMove); window.addEventListener('pointerup', onUp); window.addEventListener('pointercancel', onUp) })
onBeforeUnmount(() => { observer.disconnect(); window.removeEventListener('pointermove', onMove); window.removeEventListener('pointerup', onUp); window.removeEventListener('pointercancel', onUp) })
defineExpose({ open, close, clear })
</script>

<style scoped>
.faceplate-workspace { position: absolute; inset: 0; pointer-events: none; overflow: hidden; }
.faceplate { position: absolute; pointer-events: auto; min-width: min(260px, calc(100% - 16px)); min-height: min(180px, calc(100% - 64px)); background: #101b2d; border: 1px solid #3b5878; border-radius: 6px; box-shadow: 0 8px 30px #000a; display: flex; flex-direction: column; resize: both; overflow: hidden; }
.faceplate.is-docked { resize: none; }
.faceplate-head { display: flex; align-items: center; gap: 6px; padding: 4px 8px; background: #22344d; cursor: move; font-size: 12px; user-select: none; touch-action: none; }
.faceplate-head > span:first-child { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.faceplate-head .el-button { flex-shrink: 0; color: #dce6f0; }
.faceplate-body { flex: 1; min-height: 0; padding: 4px; }
.panel-tray { pointer-events: auto; position: absolute; bottom: 0; left: 0; right: 0; height: 44px; padding: 6px 8px; display: flex; align-items: center; gap: 6px; overflow-x: auto; background: #101b2d; border-top: 1px solid #3b5878; z-index: 2147480000; }
.panel-tray > button { flex-shrink: 0; max-width: 180px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.panel-tray > button:not(.el-button) { padding: 5px 10px; border: 1px solid #55779c; border-radius: 4px; background: #22344d; color: #dce6f0; cursor: pointer; }
.panel-tray > button.minimized { border-style: dashed; opacity: .8; }
@media (max-width: 760px) { .faceplate { resize: none; } .faceplate-head { gap: 3px; padding: 4px; } }
</style>

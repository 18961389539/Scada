<template>
  <div class="region-screen" :data-test="`region-${region.id}`">
    <ScreenRuntime :key="current" :screen-id="current" :edit-return-to="editReturnTo" :monitor-mode="monitorMode" toolbar-mode="embedded" navigate-mode="emit" compact fill initial-zoom="fit" @screen-loaded="screenLoaded" @navigate="navigate" @popup="id => $emit('popup', id)">
      <template #toolbar-start>
        <div class="region-nav">
          <el-button-group size="small">
            <WorkbenchTooltip :content="$t('workbenchTips.historyBack')" :disabled-trigger="(index <= 0)"><el-button :disabled="index <= 0" :data-test="`region-back-${region.id}`" :icon="ArrowLeft" :aria-label="$t('regionScreen.k4cf4c1')" @click="go(-1)" /></WorkbenchTooltip>
            <WorkbenchTooltip :content="$t('workbenchTips.historyForward')" :disabled-trigger="(index >= stack.length - 1)"><el-button :disabled="index >= stack.length - 1" :data-test="`region-forward-${region.id}`" :icon="ArrowRight" :aria-label="$t('regionScreen.k320ffe')" @click="go(1)" /></WorkbenchTooltip>
            <WorkbenchTooltip :content="$t('workbenchTips.historyHome')" :disabled-trigger="(current === region.screenId)"><el-button :disabled="current === region.screenId" :icon="House" :aria-label="$t('regionScreen.k25c408')" @click="navigateHome" /></WorkbenchTooltip>
          </el-button-group>
          <strong class="region-title" :title="regionLabel" data-test="region-current-screen">{{ regionLabel }}</strong>
          <el-button size="small" link :data-test="`region-focus-${region.id}`" @click="$emit('focus')">{{ focused ? $t('consoleUx.focusExit') : $t('consoleUx.focusRegion') }}</el-button>
          <WorkbenchTooltip :content="$t('workbenchTips.detach')"><el-button size="small" link :data-test="`region-window-${region.id}`" @click="$emit('detach', current)">{{ $t('regionScreen.kad9ce6') }}</el-button></WorkbenchTooltip>
        </div>
      </template>
    </ScreenRuntime>
  </div>
</template>

<script setup lang="ts">
import WorkbenchTooltip from './WorkbenchTooltip.vue'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, ArrowRight, House } from '@element-plus/icons-vue'
import ScreenRuntime from '../ScreenRuntime.vue'
import type { HmiRegion } from '../../api/hmiLayouts'
const props = withDefaults(defineProps<{ region: HmiRegion; screenName: (id: number) => string; monitorMode?: boolean; focused?: boolean; layoutId?: number | null; monitorIndex?: number }>(), { monitorMode: false, focused: false, layoutId: null, monitorIndex: 0 })
const route = useRoute(), router = useRouter()
const emit = defineEmits<{ popup: [id: number]; detach: [id: number]; 'screen-change': [screenId: number]; focus: [] }>()
const HISTORY_LIMIT = 50
function historyKey(): string { return `scada-console-nav:${props.layoutId ?? 'preview'}:${props.monitorIndex}:${props.region.id}` }
function readHistory(): { stack: number[]; index: number } | null {
  try {
    const raw = JSON.parse(sessionStorage.getItem(historyKey()) ?? 'null') as { stack?: unknown; index?: unknown } | null
    const stack = Array.isArray(raw?.stack) ? raw.stack.filter((id): id is number => Number.isInteger(id) && id > 0).slice(-HISTORY_LIMIT) : []
    return stack.length ? { stack, index: Number.isInteger(raw?.index) ? Number(raw?.index) : stack.length - 1 } : null
  } catch { return null }
}
function writeHistory(stack: number[], index: number): void {
  try { sessionStorage.setItem(historyKey(), JSON.stringify({ stack, index })) } catch { /* this window keeps the trail */ }
}
const stored = readHistory()
const stack = ref<number[]>(stored?.stack ?? [props.region.screenId!]), index = ref(stored ? Math.max(0, Math.min(stored.stack.length - 1, stored.index)) : 0)
const current = computed(() => stack.value[index.value]!)
const loadedScreen = ref<{ id: number; name: string } | null>(null)
const currentName = computed(() => loadedScreen.value?.id === current.value ? loadedScreen.value.name : props.screenName(current.value))
const regionLabel = computed(() => props.region.title ? `${props.region.title} · ${currentName.value}` : currentName.value)
function screenLoaded(screen: { id: number; name: string } | null): void { if (!screen || screen.id === current.value) loadedScreen.value = screen }
watch(current, id => { loadedScreen.value = null; emit('screen-change', id) }, { immediate: true })
watch([stack, index], () => writeHistory(stack.value, index.value), { deep: true })
const editReturnTo = computed(() => router.resolve({ path: route.path, query: { ...route.query, runtimeRegion: props.region.id, runtimeScreen: String(current.value) } }).fullPath)
function navigate(id: number): void {
  if (id === current.value) return
  stack.value = [...stack.value.slice(0, index.value + 1), id].slice(-HISTORY_LIMIT); index.value = stack.value.length - 1
}
function navigateHome(): void { if (props.region.screenId != null) navigate(props.region.screenId) }
function go(delta: number): void { index.value = Math.max(0, Math.min(stack.value.length - 1, index.value + delta)) }
function history(): { stack: number[]; index: number } { return { stack: [...stack.value], index: index.value } }
function restore(state: { stack: number[]; index: number }): void {
  const next = state.stack.filter(id => Number.isInteger(id) && id > 0).slice(-HISTORY_LIMIT)
  if (!next.length) return
  stack.value = next
  index.value = Math.max(0, Math.min(next.length - 1, state.index))
}
defineExpose({ navigate, current, history, restore })
</script>

<style scoped>
.region-screen { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.region-nav { display: flex; align-items: center; gap: 8px; min-width: 0; flex: 1 1 auto; }
.region-title { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
.region-nav .el-button-group, .region-nav > .el-button { flex-shrink: 0; }
.region-nav > .el-button { color: #dce6f0; }
.region-screen :deep(.runtime) { flex: 1; min-height: 0; }
.region-screen :deep(.runtime-toolbar.embedded) { align-items: center; gap: 8px; margin: 0; padding: 2px 4px; background: #1b2838; color: #cfd8e3; }
.region-screen :deep(.runtime-stage) { margin: 0 4px 4px; }
</style>

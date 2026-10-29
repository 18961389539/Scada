<template>
  <div class="region-screen" :data-test="`region-${region.id}`">
    <div class="region-bar">
      <el-button-group size="small">
        <el-button :disabled="index <= 0" :data-test="`region-back-${region.id}`" :icon="ArrowLeft" :aria-label="$t('regionScreen.k4cf4c1')" :title="$t('regionScreen.k4cf4c1')" @click="go(-1)" />
        <el-button :disabled="index >= stack.length - 1" :data-test="`region-forward-${region.id}`" :icon="ArrowRight" :aria-label="$t('regionScreen.k320ffe')" :title="$t('regionScreen.k320ffe')" @click="go(1)" />
        <el-button :disabled="current === region.screenId" :icon="House" :aria-label="$t('regionScreen.k25c408')" :title="$t('regionScreen.k25c408')" @click="navigateHome" />
      </el-button-group>
      <strong class="region-title" :title="regionLabel" data-test="region-current-screen">{{ regionLabel }}</strong>
      <el-button size="small" link :data-test="`region-window-${region.id}`" @click="$emit('detach', current)">{{ $t('regionScreen.kad9ce6') }}</el-button>
    </div>
    <ScreenRuntime :key="current" :screen-id="current" :edit-return-to="editReturnTo" :monitor-mode="monitorMode" toolbar-mode="embedded" navigate-mode="emit" compact fill initial-zoom="fit" @screen-loaded="screenLoaded" @navigate="navigate" @popup="id => $emit('popup', id)" />
  </div>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft, ArrowRight, House } from '@element-plus/icons-vue'
import ScreenRuntime from '../ScreenRuntime.vue'
import type { HmiRegion } from '../../api/hmiLayouts'
const props = withDefaults(defineProps<{ region: HmiRegion; screenName: (id: number) => string; monitorMode?: boolean }>(), { monitorMode: false })
const route = useRoute(), router = useRouter()
const emit = defineEmits<{ popup: [id: number]; detach: [id: number]; 'screen-change': [screenId: number] }>()
// 区域内导航历史：导航动作在本区域内打开目标画面，可前进/后退（最多 50 步）。
const stack = ref<number[]>([props.region.screenId!]), index = ref(0)
const current = computed(() => stack.value[index.value]!)
const loadedScreen = ref<{ id: number; name: string } | null>(null)
const currentName = computed(() => loadedScreen.value?.id === current.value ? loadedScreen.value.name : props.screenName(current.value))
const regionLabel = computed(() => props.region.title ? `${props.region.title} · ${currentName.value}` : currentName.value)
function screenLoaded(screen: { id: number; name: string } | null): void { if (!screen || screen.id === current.value) loadedScreen.value = screen }
watch(current, id => { loadedScreen.value = null; emit('screen-change', id) }, { immediate: true })
const editReturnTo = computed(() => router.resolve({ path: route.path, query: { ...route.query, runtimeRegion: props.region.id, runtimeScreen: String(current.value) } }).fullPath)
function navigate(id: number): void {
  if (id === current.value) return
  stack.value = [...stack.value.slice(0, index.value + 1), id].slice(-50); index.value = stack.value.length - 1
}
function navigateHome(): void { if (props.region.screenId != null) navigate(props.region.screenId) }
function go(delta: number): void { index.value = Math.max(0, Math.min(stack.value.length - 1, index.value + delta)) }
defineExpose({ navigate, current })
</script>

<style scoped>
.region-screen { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.region-bar { display: flex; align-items: center; gap: 8px; padding: 2px 4px; background: #1b2838; color: #cfd8e3; font-size: 12px; }
.region-title { min-width: 0; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 600; }
.region-bar .el-button-group, .region-bar > .el-button { flex-shrink: 0; }
.region-bar > .el-button { color: #dce6f0; }
.region-screen :deep(.runtime) { flex: 1; min-height: 0; padding: 0 4px 4px; }
.region-screen :deep(.runtime-toolbar) { color: #cfd8e3; }
</style>

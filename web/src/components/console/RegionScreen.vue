<template>
  <div class="region-screen" :data-test="`region-${region.id}`">
    <div class="region-bar">
      <el-button-group size="small">
        <el-button :disabled="index <= 0" :data-test="`region-back-${region.id}`" :title="$t('regionScreen.k4cf4c1')" @click="go(-1)">◀</el-button>
        <el-button :disabled="index >= stack.length - 1" :data-test="`region-forward-${region.id}`" :title="$t('regionScreen.k320ffe')" @click="go(1)">▶</el-button>
        <el-button :disabled="current === region.screenId" :title="$t('regionScreen.k25c408')" @click="navigate(region.screenId!)">⌂</el-button>
      </el-button-group>
      <span class="region-title">{{ region.title || screenName(current) }}</span>
      <span class="spacer" />
      <el-button size="small" link :data-test="`region-window-${region.id}`" @click="$emit('detach', current)">{{ $t('regionScreen.kad9ce6') }}</el-button>
    </div>
    <ScreenRuntime :key="current" :screen-id="current" :edit-return-to="editReturnTo" navigate-mode="emit" compact fill initial-zoom="fit" @navigate="navigate" @popup="id => $emit('popup', id)" />
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ScreenRuntime from '../ScreenRuntime.vue'
import type { HmiRegion } from '../../api/hmiLayouts'
const props = defineProps<{ region: HmiRegion; screenName: (id: number) => string }>()
const route = useRoute(), router = useRouter()
defineEmits<{ popup: [id: number]; detach: [id: number] }>()
// 区域内导航历史：导航动作在本区域内打开目标画面，可前进/后退（最多 50 步）。
const stack = ref<number[]>([props.region.screenId!]), index = ref(0)
const current = computed(() => stack.value[index.value]!)
const editReturnTo = computed(() => router.resolve({ path: route.path, query: { ...route.query, runtimeRegion: props.region.id, runtimeScreen: String(current.value) } }).fullPath)
function navigate(id: number): void {
  if (id === current.value) return
  stack.value = [...stack.value.slice(0, index.value + 1), id].slice(-50); index.value = stack.value.length - 1
}
function go(delta: number): void { index.value = Math.max(0, Math.min(stack.value.length - 1, index.value + delta)) }
defineExpose({ navigate })
</script>

<style scoped>
.region-screen { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.region-bar { display: flex; align-items: center; gap: 8px; padding: 2px 4px; background: #1b2838; color: #cfd8e3; font-size: 12px; }
.region-title { font-weight: 600; } .spacer { flex: 1; }
.region-screen :deep(.runtime) { flex: 1; min-height: 0; padding: 0 4px 4px; }
.region-screen :deep(.runtime-toolbar) { color: #cfd8e3; }
</style>

<template>
  <span ref="host" class="palette-preview" :style="{ height: `${PREVIEW_HEIGHT}px` }" aria-hidden="true">
    <ScreenCanvas v-if="shown" :document="previewDocument" preview editing :zoom="scale" />
  </span>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import ScreenCanvas from './ScreenCanvas.vue'
import { createNode, type NodeType, type ScreenDocument } from '../screens/model'

// 组件面板的缩略图：直接复用画布的图元渲染，按默认尺寸等比缩放，
// 保证面板与画布外观一致；不维护第二套图标绘制代码。
// 背景取当前画面底色，否则画布上的白字/浅色描边在面板浅色底上几乎看不见。
const props = defineProps<{ type: NodeType; background?: string }>()
const PREVIEW_WIDTH = 64
const PREVIEW_HEIGHT = 40
const node = computed(() => createNode(props.type, 0, 0))
const previewDocument = computed<ScreenDocument>(() => ({ schemaVersion: 1, width: node.value.width, height: node.value.height, background: props.background ?? 'transparent', nodes: [node.value] }))
const scale = computed(() => Math.min(PREVIEW_WIDTH / node.value.width, PREVIEW_HEIGHT / node.value.height, 1))
const host = ref<HTMLElement>()
const shown = ref(false)
let observer: IntersectionObserver | undefined
// 面板一次列出 40+ 图元（其中含图表），缩略画布只在滚入可视区时挂载。
onMounted(() => {
  if (!host.value || typeof IntersectionObserver === 'undefined') { shown.value = true; return }
  observer = new IntersectionObserver(entries => {
    if (!entries.some(entry => entry.isIntersecting)) return
    shown.value = true
    observer?.disconnect()
    observer = undefined
  }, { rootMargin: '120px' })
  observer.observe(host.value)
})
onBeforeUnmount(() => { observer?.disconnect() })
</script>

<style scoped>
.palette-preview { display: flex; align-items: center; justify-content: center; margin-bottom: 2px; pointer-events: none; }
.palette-preview :deep(svg) { border-radius: 4px; }
</style>

<template>
  <ConsoleView v-if="snapshot" :key="generation" :preview-layout="snapshot.layout" :preview-monitor="snapshot.monitor" />
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import ConsoleView from './ConsoleView.vue'
import type { HmiLayout } from '../api/hmiLayouts'

const snapshot = ref<{ layout: HmiLayout; monitor: number } | null>(null), generation = ref(0)
function receive(event: MessageEvent): void {
  if (window.parent === window || event.source !== window.parent || event.origin !== window.location.origin) return
  if (event.data?.type !== 'scada-layout-preview' || !Array.isArray(event.data.layout?.document?.monitors)) return
  snapshot.value = { layout: event.data.layout, monitor: event.data.monitor }
  generation.value++
}
onMounted(() => {
  window.addEventListener('message', receive)
  if (window.parent !== window) window.parent.postMessage({ type: 'scada-layout-preview-ready' }, window.location.origin)
})
onBeforeUnmount(() => window.removeEventListener('message', receive))
</script>

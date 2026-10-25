<template><div class="kiosk"><ScreenRuntime :key="id" :screen-id="id" fill initial-zoom="fit" /></div></template>
<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
// 独立窗口（可放在任意显示器上）：只显示一个运行画面，导航与弹窗沿用普通运行逻辑。
const route = useRoute(), id = computed(() => Number(route.params.id))
</script>
<style scoped>.kiosk { height: 100vh; padding: 4px 8px; background: #0d1622; color: #dce6f0; display: flex; flex-direction: column; }</style>

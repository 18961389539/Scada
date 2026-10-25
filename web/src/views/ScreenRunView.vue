<template><div class="page"><div class="toolbar"><el-button @click="router.push('/screens')">返回画面列表</el-button>
  <el-button data-test="screen-new-window" @click="pick()">在新窗口打开</el-button></div><ScreenRuntime :key="id" :screen-id="id" />
  <el-dialog v-model="picking" title="选择显示器" width="420px">
    <el-radio-group v-model="pickIndex" style="display: flex; flex-direction: column; align-items: flex-start">
      <el-radio v-for="(m, i) in monitors" :key="i" :value="i">{{ m.label }}（{{ m.width }}×{{ m.height }}）</el-radio>
    </el-radio-group>
    <template #footer><el-button @click="picking = false">取消</el-button><el-button type="primary" @click="open()">打开</el-button></template>
  </el-dialog></div></template>
<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { listMonitors, openOnMonitor, type MonitorInfo } from '../components/console/windowing'
const route = useRoute(), router = useRouter(), id = computed(() => Number(route.params.id))
const picking = ref(false), monitors = ref<MonitorInfo[]>([]), pickIndex = ref(0)
async function pick(): Promise<void> { monitors.value = await listMonitors(); pickIndex.value = Math.min(1, monitors.value.length - 1); picking.value = true }
function open(): void { openOnMonitor(`/kiosk/screens/${id.value}`, `scada-screen-${id.value}`, monitors.value[pickIndex.value]); picking.value = false }
</script>

<template><div class="page"><div class="toolbar"><el-button data-test="screen-back" @click="router.push({ path: '/screens', query: route.query })">{{ $t('screenRunView.k79bad8') }}</el-button>
  <el-button data-test="screen-new-window" @click="pick()">{{ $t('screenRunView.kaa2769') }}</el-button></div><ScreenRuntime :key="id" :screen-id="id" />
  <el-dialog v-model="picking" :title="$t('screenRunView.k1782d1')" width="420px">
    <el-alert v-if="windowError" :title="windowError" type="warning" :closable="false" show-icon data-test="screen-window-error" />
    <el-radio-group v-model="pickIndex" style="display: flex; flex-direction: column; align-items: flex-start">
      <el-radio v-for="(m, i) in monitors" :key="i" :value="i">{{ m.label }}（{{ m.width }}×{{ m.height }}）</el-radio>
    </el-radio-group>
    <template #footer><el-button @click="picking = false">{{ $t('screenRunView.k4d0b46') }}</el-button><el-button type="primary" data-test="screen-open-window" @click="open()">{{ $t('screenRunView.k65fc81') }}</el-button></template>
  </el-dialog></div></template>
<script setup lang="ts">
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { listMonitors, openOnMonitor, type MonitorInfo } from '../components/console/windowing'
import { tr } from '../i18n'
const route = useRoute(), router = useRouter(), id = computed(() => Number(route.params.id))
const picking = ref(false), monitors = ref<MonitorInfo[]>([]), pickIndex = ref(0), windowError = ref('')
async function pick(): Promise<void> { windowError.value = ''; monitors.value = await listMonitors(); pickIndex.value = Math.min(1, monitors.value.length - 1); picking.value = true }
function open(): void {
  if (!openOnMonitor(`/kiosk/screens/${id.value}`, `scada-screen-${id.value}`, monitors.value[pickIndex.value])) {
    windowError.value = tr('screenRunView.popupBlocked')
    return
  }
  windowError.value = ''
  picking.value = false
}
</script>

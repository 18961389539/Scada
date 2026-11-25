<template>
  <div v-if="visible" class="runtime-banners">
    <el-alert v-if="isStandby" type="error" :closable="false" show-icon class="runtime-banner" data-test="standby-page-banner">
      <template #title>{{ $t('runtimeBanners.standbyTitle') }}</template>
      <p>{{ $t('runtimeBanners.standbyBody') }}</p>
      <el-button v-if="primaryUrl && allowPrimaryLink" type="primary" size="small" data-test="standby-open-primary" @click="openPrimary">
        {{ $t('runtimeBanners.openPrimary') }}
      </el-button>
      <p v-else-if="primaryUrl" class="tiny">{{ $t('runtimeBanners.primaryUrl', [primaryUrl]) }}</p>
    </el-alert>
    <el-alert v-if="takeoverMessage" type="warning" :closable="false" show-icon class="runtime-banner" data-test="takeover-recovery-banner">
      {{ takeoverMessage }}
    </el-alert>
    <el-alert v-if="simulationDevices.length" type="warning" :closable="false" show-icon class="runtime-banner" data-test="simulation-banner">
      <template #title>{{ $t('runtimeBanners.simulationTitle') }}</template>
      <p>{{ $t('runtimeBanners.simulationBody') }}</p>
      <ul class="simulation-device-list" data-test="simulation-device-list">
        <li v-for="device in simulationDevices" :key="device.id">{{ device.name }}</li>
      </ul>
    </el-alert>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { Health } from '../types'

const props = withDefaults(defineProps<{
  health: Health | null
  allowPrimaryLink?: boolean
}>(), { allowPrimaryLink: true })

const isStandby = computed(() => props.health?.ha?.role === 'Standby')
const primaryUrl = computed(() => props.health?.ha?.primaryUrl || '')
const takeoverMessage = computed(() => props.health?.takeoverRecovery?.active ? (props.health.takeoverRecovery.message || '') : '')
const simulationDevices = computed(() => props.health?.simulationDevices ?? [])
const visible = computed(() => isStandby.value || !!takeoverMessage.value || simulationDevices.value.length > 0)

function openPrimary(): void {
  if (primaryUrl.value) window.open(primaryUrl.value, '_blank', 'noopener')
}
</script>

<style scoped>
.runtime-banners { display: flex; flex-direction: column; gap: 8px; margin: 0 0 12px; }
.runtime-banner :deep(.el-alert__content) { min-width: 0; }
.runtime-banner p { margin: 4px 0 8px; }
.simulation-device-list { margin: 0; padding-left: 1.2em; }
.tiny { margin: 4px 0 0; font-size: var(--scada-fs-caption); }
</style>

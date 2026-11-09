<template>
  <div class="kiosk" role="main">
    <h1 class="kiosk-title">{{ $t('common.kiosk') }}</h1>
    <div v-if="invalidId" class="kiosk-error" role="alert" data-test="kiosk-invalid-id">
      {{ $t('screenUx.invalidScreen') }}
    </div>
    <ScreenRuntime v-else :key="id" :screen-id="id" :monitor-mode="monitorOnly" fill initial-zoom="fit" />
  </div>
</template>
<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import ScreenRuntime from '../components/ScreenRuntime.vue'
// 独立窗口（可放在任意显示器上）：只显示一个运行画面，导航与弹窗沿用普通运行逻辑。
const route = useRoute()
// 大屏通常无人值守：非法 id 必须给出解释，否则会一直空白并反复请求 /screens/NaN。
const id = computed(() => { const value = Number(route.params.id); return Number.isSafeInteger(value) && value > 0 ? value : Number.NaN })
const invalidId = computed(() => !Number.isFinite(id.value))
const monitorOnly = computed(() => route.query.monitorOnly === '1')
</script>
<style scoped>
.kiosk { height: 100vh; padding: 4px 8px; background: #0d1622; color: #dce6f0; display: flex; flex-direction: column; }
.kiosk-title { position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px; overflow: hidden; clip: rect(0, 0, 0, 0); white-space: nowrap; border: 0; }
.kiosk-error { margin: 16px auto; max-width: 640px; padding: 12px 16px; border: 1px solid #7d3535; border-radius: 6px; background: #341b20; color: #ffd9d9; }
</style>

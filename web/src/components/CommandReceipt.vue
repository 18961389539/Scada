<template>
  <el-alert class="command-receipt" :type="status?.result?.outcome === 'Unknown' ? 'warning' : status?.result?.outcome === 'Confirmed' ? 'success' : 'info'" :closable="false" :title="$t('commandReceipt.k1d4ec2')" show-icon>
    <p>{{ $t('commandReceipt.kcb4bba') }}<code>{{ id }}</code></p>
    <p v-if="status">{{ labels[status.result?.outcome ?? status.state] ?? status.state }} · {{ status.result?.message ?? $t('commandReceipt.kfcd9eb') }}</p>
    <p v-if="status?.result?.readBack != null">{{ $t('commandReceipt.k2ed382', [status.result.readBack]) }}</p>
    <p v-if="error">{{ error }}</p>
    <p v-if="status?.result?.outcome === 'Unknown'">{{ $t('commandReceipt.kd7b3ba') }}</p>
    <el-button size="small" :loading="loading" @click="load">{{ $t('commandReceipt.k07d7c2') }}</el-button>
  </el-alert>
</template>
<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { commandApi } from '../api'
import { errorMessage } from '../api/http'
import type { GatewayCommandStatus } from '../api/commandTypes'
import { tr } from '../i18n'
const props = defineProps<{ id: string }>()
const status = ref<GatewayCommandStatus | null>(null), error = ref(''), loading = ref(false)
const labels: Record<string, string> = { Confirmed: tr('commandReceipt.k05aba6'), Failed: tr('commandReceipt.k9746cf'), NotSent: tr('commandReceipt.k5bf97e'), Unknown: tr('commandReceipt.k4835a7'), Queued: tr('commandReceipt.k550b34'), Reserved: tr('commandReceipt.k947a7b'), Authorized: tr('commandReceipt.k4c4042') }
let controller: AbortController | undefined
async function load(): Promise<void> {
  controller?.abort(); const current = controller = new AbortController(); loading.value = true; error.value = ''
  try { const result = await commandApi.status(props.id, current.signal); if (!current.signal.aborted) status.value = result }
  catch (e) { if (!current.signal.aborted) error.value = (e as { response?: { status?: number } }).response?.status === 404 ? tr('commandReceipt.kc605fa') : errorMessage(e, tr('commandReceipt.k15528d')) }
  finally { if (controller === current) loading.value = false }
}
watch(() => props.id, () => { status.value = null; void load() }, { immediate: true })
onBeforeUnmount(() => controller?.abort())
</script>
<style scoped>
.command-receipt { margin: 12px 0; }
.command-receipt :deep(.el-alert__description) { color: #334155; font-size: 14px; line-height: 1.6; }
.command-receipt :deep(.el-alert__title) { color: #1e293b; font-weight: 600; }
.command-receipt p { margin: 4px 0; }
.command-receipt code { overflow-wrap: anywhere; font-size: 12px; }
</style>

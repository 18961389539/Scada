<template>
  <el-alert class="command-receipt" :type="status?.result?.outcome === 'Unknown' ? 'warning' : status?.result?.outcome === 'Confirmed' ? 'success' : 'info'" :closable="false" title="远程命令回执" show-icon>
    <p>命令编号：<code>{{ id }}</code></p>
    <p v-if="status">{{ labels[status.result?.outcome ?? status.state] ?? status.state }} · {{ status.result?.message ?? '等待网关回执' }}</p>
    <p v-if="status?.result?.readBack != null">回读值：{{ status.result.readBack }}</p>
    <p v-if="error">{{ error }}</p>
    <p v-if="status?.result?.outcome === 'Unknown'">请检查设备反馈并查询后续回执，系统不会自动重发此命令。</p>
    <el-button size="small" :loading="loading" @click="load">查询回执</el-button>
  </el-alert>
</template>
<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { commandApi } from '../api'
import { errorMessage } from '../api/http'
import type { GatewayCommandStatus } from '../api/commandTypes'
const props = defineProps<{ id: string }>()
const status = ref<GatewayCommandStatus | null>(null), error = ref(''), loading = ref(false)
const labels: Record<string, string> = { Confirmed: '写入及回读已确认', Failed: '执行失败', NotSent: '未下发', Unknown: '执行结果未知', Queued: '等待领取', Reserved: '等待执行授权', Authorized: '已授权，等待回执' }
let controller: AbortController | undefined
async function load(): Promise<void> {
  controller?.abort(); const current = controller = new AbortController(); loading.value = true; error.value = ''
  try { const result = await commandApi.status(props.id, current.signal); if (!current.signal.aborted) status.value = result }
  catch (e) { if (!current.signal.aborted) error.value = (e as { response?: { status?: number } }).response?.status === 404 ? '中心暂未找到该命令，请保留编号并稍后查询。' : errorMessage(e, '查询回执失败') }
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

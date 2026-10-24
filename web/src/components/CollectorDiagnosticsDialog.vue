<template>
  <el-dialog v-model="visible" :title="`采集诊断 · ${device.name}`" width="1280px" top="3vh">
    <div class="diagnostics" v-loading="busy && !result">
      <div class="toolbar">
        <el-tag v-if="snapshot" :type="stateType(snapshot.state)">{{ states[snapshot.state] ?? snapshot.state }}</el-tag>
        <el-tag v-if="snapshot?.gatewayId" type="info">采集位置：网关 #{{ snapshot.gatewayId }}</el-tag>
        <span v-if="snapshot" class="muted">{{ snapshot.protocol }} · {{ snapshot.connection }}</span>
        <span class="spacer" />
        <el-switch v-model="autoRefresh" active-text="每 3 秒刷新" />
        <el-button :loading="busy" @click="load">刷新</el-button>
        <el-button :loading="exporting" @click="download">导出诊断</el-button>
        <el-button :loading="reconnecting" :disabled="!snapshot?.enabled || !snapshot.pointCount" @click="reconnect">请求重连</el-button>
      </div>
      <el-alert v-if="error" :title="error" type="error" :closable="false" />
      <template v-if="snapshot">
        <el-alert v-if="snapshot.configurationPending" title="配置已变化，等待采集任务重建；以下为当前任务的运行信息。" type="info" :closable="false" />
        <el-alert v-if="snapshot.currentError" :title="snapshot.currentError" :type="snapshot.state === 'Offline' || snapshot.state === 'Faulted' ? 'error' : 'warning'" :closable="false" />
        <div class="metrics">
          <div><small>读取成功 / 尝试</small><strong>{{ snapshot.metrics.pollSuccesses }} / {{ snapshot.metrics.pollAttempts }}</strong><small>质量异常批次 {{ snapshot.metrics.degradedPolls }}</small></div>
          <div><small>连接失败 / 尝试</small><strong>{{ snapshot.metrics.connectFailures }} / {{ snapshot.metrics.connectAttempts }}</strong><small>连续通信失败 {{ snapshot.metrics.consecutiveFailures }}</small></div>
          <div><small>窗口平均 / P95 耗时 (ms)</small><strong>{{ ms(snapshot.metrics.windowAvgMs) }} / {{ ms(snapshot.metrics.windowP95Ms) }}</strong><small>最近 {{ snapshot.metrics.windowSamples }} 批，最大 {{ ms(snapshot.metrics.windowMaxMs) }}</small></div>
          <div><small>正常 / 异常 / 未采样</small><strong>{{ snapshot.quality.good }} / {{ snapshot.quality.bad + snapshot.quality.uncertain + snapshot.quality.commFailure }} / {{ snapshot.quality.unknown }}</strong><small>过期 {{ snapshot.quality.stale }}，阈值 {{ snapshot.staleAfterMs }} ms</small></div>
          <div><small>命令队列</small><strong>{{ snapshot.pendingCommands }} / 128</strong><small>{{ operations[snapshot.operation] ?? snapshot.operation }}{{ snapshot.operationTagId ? ` #${snapshot.operationTagId}` : '' }}</small></div>
          <div><small>写入成功 / 失败 / 未下发</small><strong>{{ snapshot.metrics.commandsSucceeded }} / {{ snapshot.metrics.commandsFailed }} / {{ snapshot.metrics.commandsExpired + snapshot.metrics.commandsRejected }}</strong><small>未下发含排队过期和前置拒绝</small></div>
        </div>
        <el-descriptions :column="3" size="small" border>
          <el-descriptions-item label="最近读取尝试">{{ formatTime(snapshot.metrics.lastAttemptTs) }}</el-descriptions-item>
          <el-descriptions-item label="最近读取成功">{{ formatTime(snapshot.metrics.lastSuccessTs) }}</el-descriptions-item>
          <el-descriptions-item label="预计重试">{{ formatTime(snapshot.metrics.nextRetryTs) }}</el-descriptions-item>
          <el-descriptions-item label="任务启动">{{ formatTime(snapshot.startedTs) }}</el-descriptions-item>
          <el-descriptions-item label="周期 / 超时">{{ snapshot.pollIntervalMs }} / {{ snapshot.timeoutMs }} ms</el-descriptions-item>
          <el-descriptions-item label="任务配置版本"><span :title="snapshot.configurationRevision ?? ''">{{ snapshot.configurationRevision?.slice(0, 12) ?? snapshot.configVersion }}</span></el-descriptions-item>
          <el-descriptions-item label="最近通信失败" :span="3">{{ formatTime(snapshot.metrics.lastFailureTs) }} · {{ snapshot.metrics.lastFailure ?? '无' }}</el-descriptions-item>
        </el-descriptions>
        <el-tabs v-model="tab">
          <el-tab-pane label="测点质量" name="points">
            <div class="toolbar">
              <el-input v-model="search" clearable placeholder="搜索测点名称、地址或 ID" aria-label="诊断搜索" style="width: 280px" @change="filter" />
              <el-select v-model="quality" clearable placeholder="全部质量" aria-label="诊断质量" style="width: 160px" @change="filter"><el-option v-for="(label, key) in qualityLabels" :key="key" :label="label" :value="key" /></el-select>
              <el-checkbox v-model="staleOnly" @change="filter">仅看过期</el-checkbox>
              <span class="muted">共 {{ snapshot.pointCount }} 点；异常时工程量保留最近值，须结合质量判断</span>
            </div>
            <el-table :data="snapshot.points" max-height="320" size="small" row-key="tagId" empty-text="没有符合条件的测点">
              <el-table-column type="expand"><template #default="{ row }"><div class="point-detail">源时间：{{ formatTime(row.sourceTs) }}　最近正常：{{ formatTime(row.lastGoodTs) }}　完整地址：{{ row.address }}</div></template></el-table-column>
              <el-table-column prop="tagId" label="ID" width="60" />
              <el-table-column prop="name" label="测点" min-width="120" show-overflow-tooltip />
              <el-table-column prop="address" label="地址" min-width="190" show-overflow-tooltip />
              <el-table-column prop="dataType" label="类型" width="80" />
              <el-table-column label="原始值" width="90"><template #default="{ row }">{{ formatValue(row.rawValue) }}</template></el-table-column>
              <el-table-column label="工程量" width="90"><template #default="{ row }">{{ formatValue(row.value) }}</template></el-table-column>
              <el-table-column label="质量" width="120"><template #default="{ row }"><el-tag size="small" :type="row.stale ? 'danger' : row.quality ? qualityType(row.quality) : 'info'">{{ row.quality ? qualityLabels[row.quality as TagQuality] : '等待采样' }}{{ row.stale ? ' · 过期' : '' }}</el-tag></template></el-table-column>
              <el-table-column label="数据龄期" width="100"><template #default="{ row }">{{ row.ageMs === null ? '-' : `${(row.ageMs / 1000).toFixed(1)} s` }}</template></el-table-column>
              <el-table-column prop="error" label="错误" min-width="180" show-overflow-tooltip />
            </el-table>
            <el-pagination v-model:current-page="page" v-model:page-size="pageSize" :page-sizes="[25, 50, 100, 200]" :total="result?.total ?? 0" layout="total, sizes, prev, pager, next" @current-change="load" @size-change="filter" />
          </el-tab-pane>
          <el-tab-pane label="读取分组" name="groups">
            <el-table :data="snapshot.readGroups" max-height="350" size="small">
              <el-table-column prop="index" label="分组" width="65" /><el-table-column label="区域" width="180"><template #default="{ row }">{{ areaLabels[row.area] ?? row.area }}{{ row.dbNumber !== null ? ` ${row.dbNumber}` : '' }}</template></el-table-column>
              <el-table-column label="起始地址" width="130"><template #default="{ row }">{{ row.startAddress ?? '按节点读取' }}</template></el-table-column>
              <el-table-column label="长度" width="130"><template #default="{ row }">{{ row.length }} {{ row.unit }}</template></el-table-column>
              <el-table-column label="包含测点 ID" min-width="400" show-overflow-tooltip><template #default="{ row }">{{ row.tagIds.join(', ') }}</template></el-table-column>
            </el-table>
          </el-tab-pane>
          <el-tab-pane label="最近采样" name="samples">
            <el-table :data="snapshot.samples" max-height="350" size="small">
              <el-table-column label="时间" width="180"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
              <el-table-column label="读耗时" width="140"><template #default="{ row }">{{ ms(row.durationMs) }} ms</template></el-table-column>
              <el-table-column label="结果" width="130"><template #default="{ row }">{{ row.success ? row.bad ? '质量异常' : '成功' : '通信失败' }}</template></el-table-column>
              <el-table-column prop="good" label="正常点" width="90" /><el-table-column prop="bad" label="异常点" width="90" /><el-table-column prop="error" label="错误" show-overflow-tooltip />
            </el-table>
          </el-tab-pane>
          <el-tab-pane label="最近事件" name="events">
            <el-table :data="snapshot.events" max-height="350" size="small">
              <el-table-column label="时间" width="180"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
              <el-table-column prop="level" label="级别" width="100" /><el-table-column prop="tagId" label="测点 ID" width="90" /><el-table-column prop="message" label="事件" />
            </el-table>
          </el-tab-pane>
        </el-tabs>
        <p class="muted">统计及最近 200 条事件、120 批采样保留在当前采集任务内存中；配置重建或服务重启后重新计数。快照时间 {{ formatTime(snapshot.capturedTs) }}。</p>
      </template>
    </div>
    <template #footer><el-button @click="visible = false">关闭</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import type { Device, TagQuality } from '../types'
import { diagnosticsApi, type DiagnosticsPage } from '../api/diagnostics'
import { errorMessage } from '../api/http'
import { areaLabels, formatTime, formatValue, qualityLabels, qualityType } from '../utils'

const props = defineProps<{ device: Device }>(), visible = defineModel<boolean>({ required: true })
const result = ref<DiagnosticsPage | null>(null), snapshot = computed(() => result.value?.diagnostics)
const busy = ref(false), exporting = ref(false), reconnecting = ref(false), autoRefresh = ref(true), error = ref(''), tab = ref('points')
const page = ref(1), pageSize = ref(100), search = ref(''), quality = ref<TagQuality | ''>(''), staleOnly = ref(false)
const states: Record<string, string> = { Disabled: '停用', NoTags: '未配置测点', Waiting: '等待采集', Online: '正常', Degraded: '部分测点异常', Offline: '离线', Faulted: '任务异常停止', Stopped: '任务已停止' }
const operations: Record<string, string> = { Idle: '空闲', Connecting: '正在连接', Reading: '正在读取', Writing: '正在写入及回读' }
const stateType = (state: string): 'success' | 'warning' | 'danger' | 'info' => state === 'Online' ? 'success' : state === 'Degraded' ? 'warning' : state === 'Offline' || state === 'Faulted' ? 'danger' : 'info'
const ms = (value: number) => value.toFixed(1)
let timer: ReturnType<typeof setInterval> | undefined, controller: AbortController | undefined, generation = 0, requestVersion = 0
function stop(): void { generation++; requestVersion++; if (timer) clearInterval(timer); timer = undefined; controller?.abort(); busy.value = false }
watch(() => [visible.value, props.device.id] as const, ([open]) => {
  stop(); if (!open) return
  result.value = null; error.value = ''; page.value = 1; search.value = ''; quality.value = ''; staleOnly.value = false; tab.value = 'points'; exporting.value = false; reconnecting.value = false
  void load(); timer = setInterval(() => { if (autoRefresh.value && !busy.value) void load() }, 3000)
}, { immediate: true })
onBeforeUnmount(stop)
async function load(): Promise<void> {
  if (!visible.value || busy.value) return
  const current = generation, request = ++requestVersion; controller = new AbortController(); busy.value = true; let firstPage = false
  try {
    const data = await diagnosticsApi.get(props.device.id, { page: page.value, pageSize: pageSize.value, search: search.value.trim() || undefined, quality: quality.value || undefined, staleOnly: staleOnly.value }, controller.signal)
    if (current !== generation || request !== requestVersion) return
    result.value = data; error.value = ''
    if (data.total > 0 && (page.value - 1) * pageSize.value >= data.total) { page.value = 1; firstPage = true }
  } catch (e) { if (current === generation && request === requestVersion) error.value = errorMessage(e, '加载采集诊断失败') }
  finally { if (current === generation && request === requestVersion) { busy.value = false; if (firstPage) void load() } }
}
function filter(): void { page.value = 1; controller?.abort(); requestVersion++; busy.value = false; void load() }
async function reconnect(): Promise<void> {
  const current = generation; reconnecting.value = true
  try { const response = await diagnosticsApi.reconnect(props.device.id); if (current === generation && visible.value) { ElMessage.success(response.message); void load() } }
  catch (e) { if (current === generation && visible.value) ElMessage.error(errorMessage(e)) }
  finally { if (current === generation) reconnecting.value = false }
}
async function download(): Promise<void> {
  const current = generation; exporting.value = true
  try {
    const blob = await diagnosticsApi.export(props.device.id); if (current !== generation || !visible.value) return
    const url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url; link.download = `collector-${props.device.id}.json`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (e) { if (current === generation && visible.value) ElMessage.error(errorMessage(e)) }
  finally { if (current === generation) exporting.value = false }
}
</script>

<style scoped>
.diagnostics { max-height: 78vh; overflow: auto; }
.metrics { display: grid; grid-template-columns: repeat(6, 1fr); gap: 10px; margin: 14px 0; }
.metrics > div { background: #f5f7fa; padding: 12px; border-radius: 6px; display: flex; flex-direction: column; gap: 6px; }
.metrics small { color: #6b7280; font-size: 12px; }
.metrics strong { font-size: 20px; color: #253343; }
.point-detail { padding: 12px 30px; overflow-wrap: anywhere; }
.el-pagination { margin-top: 12px; }
.el-alert { margin: 8px 0; }
</style>

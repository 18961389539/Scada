<template>
  <el-dialog v-model="visible" :title="$t('collectorDiagnosticsDialog.k390778', [device.name])" width="1280px" top="3vh">
    <div class="diagnostics" v-loading="busy && !result">
      <div class="toolbar">
        <el-tag v-if="snapshot" :type="stateType(snapshot.state)">{{ states[snapshot.state] ?? snapshot.state }}</el-tag>
        <el-tag v-if="snapshot?.gatewayId" type="info">{{ $t('collectorDiagnosticsDialog.k7397ce', [snapshot.gatewayId]) }}</el-tag>
        <span v-if="snapshot" class="muted">{{ snapshot.protocol }} · {{ snapshot.connection }}</span>
        <span class="spacer" />
        <el-switch v-model="autoRefresh" :active-text="$t('collectorDiagnosticsDialog.kd8fee6')" />
        <el-button :loading="busy" @click="load()">{{ $t('collectorDiagnosticsDialog.k38108e') }}</el-button>
        <el-button :loading="exporting" @click="download">{{ $t('collectorDiagnosticsDialog.k4d3907') }}</el-button>
        <el-button :loading="reconnecting" :disabled="!snapshot?.enabled || !snapshot.pointCount" @click="reconnect">{{ $t('collectorDiagnosticsDialog.k778dd2') }}</el-button>
      </div>
      <el-alert v-if="error" :title="error" type="error" :closable="false" />
      <template v-if="snapshot">
        <el-alert v-if="snapshot.configurationPending" :title="$t('collectorDiagnosticsDialog.k21e1d2')" type="info" :closable="false" />
        <el-alert v-if="snapshot.currentError" :title="snapshot.currentError" :type="snapshot.state === 'Offline' || snapshot.state === 'Faulted' ? 'error' : 'warning'" :closable="false" />
        <div class="metrics">
          <div><small>{{ $t('collectorDiagnosticsDialog.k1910c5') }}</small><strong>{{ snapshot.metrics.pollSuccesses }} / {{ snapshot.metrics.pollAttempts }}</strong><small>{{ $t('collectorDiagnosticsDialog.k41aeb7', [snapshot.metrics.degradedPolls]) }}</small></div>
          <div><small>{{ $t('collectorDiagnosticsDialog.keb88c7') }}</small><strong>{{ snapshot.metrics.connectFailures }} / {{ snapshot.metrics.connectAttempts }}</strong><small>{{ $t('collectorDiagnosticsDialog.kf2c2cd', [snapshot.metrics.consecutiveFailures]) }}</small></div>
          <div><small>{{ $t('collectorDiagnosticsDialog.kcc68a7') }}</small><strong>{{ ms(snapshot.metrics.windowAvgMs) }} / {{ ms(snapshot.metrics.windowP95Ms) }}</strong><small>{{ $t('collectorDiagnosticsDialog.k8f532e', [snapshot.metrics.windowSamples, ms(snapshot.metrics.windowMaxMs)]) }}</small></div>
          <div><small>{{ $t('collectorDiagnosticsDialog.k25e69c') }}</small><strong>{{ snapshot.quality.good }} / {{ snapshot.quality.bad + snapshot.quality.uncertain + snapshot.quality.commFailure }} / {{ snapshot.quality.unknown }}</strong><small>{{ $t('collectorDiagnosticsDialog.kfad9c5', [snapshot.quality.stale, snapshot.staleAfterMs]) }}</small></div>
          <div><small>{{ $t('collectorDiagnosticsDialog.ka67967') }}</small><strong>{{ snapshot.pendingCommands }} / 128</strong><small>{{ operations[snapshot.operation] ?? snapshot.operation }}{{ snapshot.operationTagId ? ` #${snapshot.operationTagId}` : '' }}</small></div>
          <div><small>{{ $t('collectorDiagnosticsDialog.k2b0eb8') }}</small><strong>{{ snapshot.metrics.commandsSucceeded }} / {{ snapshot.metrics.commandsFailed }} / {{ snapshot.metrics.commandsExpired + snapshot.metrics.commandsRejected }}</strong><small>{{ $t('collectorDiagnosticsDialog.k25e35f') }}</small></div>
        </div>
        <el-descriptions :column="3" size="small" border>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.ka188d4')">{{ formatTime(snapshot.metrics.lastAttemptTs) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.ka84d3d')">{{ formatTime(snapshot.metrics.lastSuccessTs) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.k35ef8d')">{{ formatTime(snapshot.metrics.nextRetryTs) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.k9f5e53')">{{ formatTime(snapshot.startedTs) }}</el-descriptions-item>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.k35e7eb')">{{ snapshot.pollIntervalMs }} / {{ snapshot.timeoutMs }} ms</el-descriptions-item>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.k817587')"><span :title="snapshot.configurationRevision ?? ''">{{ snapshot.configurationRevision?.slice(0, 12) ?? snapshot.configVersion }}</span></el-descriptions-item>
          <el-descriptions-item :label="$t('collectorDiagnosticsDialog.k4885d4')" :span="3">{{ formatTime(snapshot.metrics.lastFailureTs) }} · {{ snapshot.metrics.lastFailure ?? $t('collectorDiagnosticsDialog.k720777') }}</el-descriptions-item>
        </el-descriptions>
        <el-tabs v-model="tab">
          <el-tab-pane :label="$t('collectorDiagnosticsDialog.k8ec829')" name="points">
            <div class="toolbar">
              <el-input v-model="search" clearable :placeholder="$t('collectorDiagnosticsDialog.k5c8441')" :aria-label="$t('collectorDiagnosticsDialog.k87a5e2')" style="width: 280px" @change="filter" />
              <el-select v-model="quality" clearable :placeholder="$t('collectorDiagnosticsDialog.k3ba93a')" :aria-label="$t('collectorDiagnosticsDialog.k07eae2')" style="width: 160px" @change="filter"><el-option v-for="(label, key) in qualityLabels" :key="key" :label="label" :value="key" /></el-select>
              <el-checkbox v-model="staleOnly" @change="filter">{{ $t('collectorDiagnosticsDialog.k98a8e7') }}</el-checkbox>
              <span class="muted">{{ $t('collectorDiagnosticsDialog.k732ae7', [snapshot.pointCount]) }}</span>
            </div>
            <el-table :data="snapshot.points" max-height="320" size="small" row-key="tagId" :empty-text="$t('collectorDiagnosticsDialog.ka1953d')">
              <el-table-column type="expand"><template #default="{ row }"><div class="point-detail">{{ $t('collectorDiagnosticsDialog.kc664ff', [formatTime(row.sourceTs), formatTime(row.lastGoodTs), row.address]) }}</div></template></el-table-column>
              <el-table-column prop="tagId" label="ID" width="60" />
              <el-table-column prop="name" :label="$t('collectorDiagnosticsDialog.k8cff1c')" min-width="120" show-overflow-tooltip />
              <el-table-column prop="address" :label="$t('collectorDiagnosticsDialog.k67d2d7')" min-width="190" show-overflow-tooltip />
              <el-table-column prop="dataType" :label="$t('collectorDiagnosticsDialog.ke4e46c')" width="80" />
              <el-table-column :label="$t('collectorDiagnosticsDialog.k17f869')" width="90"><template #default="{ row }">{{ formatValue(row.rawValue) }}</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.k494eaf')" width="90"><template #default="{ row }">{{ formatValue(row.value) }}</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.k82f516')" width="120"><template #default="{ row }"><el-tag size="small" :type="row.stale ? 'danger' : row.quality ? qualityType(row.quality) : 'info'">{{ row.quality ? qualityLabels[row.quality as TagQuality] : $t('collectorDiagnosticsDialog.kaef833') }}{{ row.stale ? $t('collectorDiagnosticsDialog.kcdba5a') : '' }}</el-tag></template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.kbaf2e6')" width="100"><template #default="{ row }">{{ row.ageMs === null ? '-' : `${(row.ageMs / 1000).toFixed(1)} s` }}</template></el-table-column>
              <el-table-column prop="error" :label="$t('collectorDiagnosticsDialog.kb859c7')" min-width="180" show-overflow-tooltip />
            </el-table>
            <el-pagination v-model:current-page="page" v-model:page-size="pageSize" :page-sizes="[25, 50, 100, 200]" :total="result?.total ?? 0" layout="total, sizes, prev, pager, next" @current-change="load" @size-change="filter" />
          </el-tab-pane>
          <el-tab-pane :label="$t('collectorDiagnosticsDialog.k546344')" name="groups">
            <el-table :data="snapshot.readGroups" max-height="350" size="small">
              <el-table-column prop="index" :label="$t('collectorDiagnosticsDialog.k97d8a6')" width="65" /><el-table-column :label="$t('collectorDiagnosticsDialog.k17fc93')" width="180"><template #default="{ row }">{{ areaLabels[row.area] ?? row.area }}{{ row.dbNumber !== null ? ` ${row.dbNumber}` : '' }}</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.kae79aa')" width="130"><template #default="{ row }">{{ row.startAddress ?? $t('collectorDiagnosticsDialog.k7fb9ec') }}</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.ka53949')" width="130"><template #default="{ row }">{{ row.length }} {{ row.unit }}</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.k4a27e9')" min-width="400" show-overflow-tooltip><template #default="{ row }">{{ row.tagIds.join(', ') }}</template></el-table-column>
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('collectorDiagnosticsDialog.k986e6e')" name="samples">
            <el-table :data="snapshot.samples" max-height="350" size="small">
              <el-table-column :label="$t('collectorDiagnosticsDialog.k89b4aa')" width="180"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.k8862c8')" width="140"><template #default="{ row }">{{ ms(row.durationMs) }} ms</template></el-table-column>
              <el-table-column :label="$t('collectorDiagnosticsDialog.k0a2c91')" width="130"><template #default="{ row }">{{ row.success ? row.bad ? $t('collectorDiagnosticsDialog.k6bceb6') : $t('collectorDiagnosticsDialog.k51991a') : $t('collectorDiagnosticsDialog.k6ac251') }}</template></el-table-column>
              <el-table-column prop="good" :label="$t('collectorDiagnosticsDialog.kb74b15')" width="90" /><el-table-column prop="bad" :label="$t('collectorDiagnosticsDialog.k073fa6')" width="90" /><el-table-column prop="error" :label="$t('collectorDiagnosticsDialog.kb859c7')" show-overflow-tooltip />
            </el-table>
          </el-tab-pane>
          <el-tab-pane :label="$t('collectorDiagnosticsDialog.k8a95b9')" name="events">
            <el-table :data="snapshot.events" max-height="350" size="small">
              <el-table-column :label="$t('collectorDiagnosticsDialog.k89b4aa')" width="180"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
              <el-table-column prop="level" :label="$t('collectorDiagnosticsDialog.k254849')" width="100" /><el-table-column prop="tagId" :label="$t('collectorDiagnosticsDialog.k6b0394')" width="90" /><el-table-column prop="message" :label="$t('collectorDiagnosticsDialog.k550e32')" />
            </el-table>
          </el-tab-pane>
        </el-tabs>
        <p class="muted">{{ $t('collectorDiagnosticsDialog.kf8ec55', [formatTime(snapshot.capturedTs)]) }}</p>
      </template>
    </div>
    <template #footer><el-button @click="visible = false">{{ $t('collectorDiagnosticsDialog.k6c14bd') }}</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import type { Device, TagQuality } from '../types'
import { diagnosticsApi, type DiagnosticsPage } from '../api/diagnostics'
import { errorMessage } from '../api/http'
import { areaLabels, formatTime, formatValue, qualityLabels, qualityType } from '../utils'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'

const props = defineProps<{ device: Device }>(), visible = defineModel<boolean>({ required: true })
const result = ref<DiagnosticsPage | null>(null), snapshot = computed(() => result.value?.diagnostics)
const busy = ref(false), exporting = ref(false), reconnecting = ref(false), autoRefresh = ref(true), error = ref(''), tab = ref('points')
const page = ref(1), pageSize = ref(100), search = ref(''), quality = ref<TagQuality | ''>(''), staleOnly = ref(false)
const states: Record<string, string> = { Disabled: tr('collectorDiagnosticsDialog.kd989e5'), NoTags: tr('collectorDiagnosticsDialog.k6ef0f9'), Waiting: tr('collectorDiagnosticsDialog.k9e5ee8'), Online: tr('collectorDiagnosticsDialog.kf78d03'), Degraded: tr('collectorDiagnosticsDialog.k02f9d1'), Offline: tr('collectorDiagnosticsDialog.k211357'), Faulted: tr('collectorDiagnosticsDialog.k554be3'), Stopped: tr('collectorDiagnosticsDialog.kb5b228') }
const operations: Record<string, string> = { Idle: tr('collectorDiagnosticsDialog.k837e7a'), Connecting: tr('collectorDiagnosticsDialog.k50d957'), Reading: tr('collectorDiagnosticsDialog.k62e515'), Writing: tr('collectorDiagnosticsDialog.k8117fb') }
const stateType = (state: string): 'success' | 'warning' | 'danger' | 'info' => state === 'Online' ? 'success' : state === 'Degraded' ? 'warning' : state === 'Offline' || state === 'Faulted' ? 'danger' : 'info'
const ms = (value: number) => value.toFixed(1)
let controller: AbortController | undefined, generation = 0, requestVersion = 0
function stop(): void { generation++; requestVersion++; controller?.abort(); busy.value = false }
watch(() => [visible.value, props.device.id] as const, ([open]) => {
  stop(); if (!open) return
  result.value = null; error.value = ''; page.value = 1; search.value = ''; quality.value = ''; staleOnly.value = false; tab.value = 'points'; exporting.value = false; reconnecting.value = false
  void load()
}, { immediate: true })
// 诊断弹窗打开时 3s 自动刷新；关闭、暂停开关或前台请求进行中都会跳过本轮。
usePolling(() => load(true), 3000, { enabled: () => visible.value && autoRefresh.value && !busy.value })
onBeforeUnmount(stop)
async function load(background = false): Promise<void> {
  if (!visible.value || (busy.value && !background)) return
  const current = generation, request = ++requestVersion; controller = new AbortController(); if (!background) busy.value = true; let firstPage = false
  try {
    const data = await diagnosticsApi.get(props.device.id, { page: page.value, pageSize: pageSize.value, search: search.value.trim() || undefined, quality: quality.value || undefined, staleOnly: staleOnly.value }, controller.signal)
    if (current !== generation || request !== requestVersion) return
    result.value = data; error.value = ''
    if (data.total > 0 && (page.value - 1) * pageSize.value >= data.total) { page.value = 1; firstPage = true }
  } catch (e) { if (current === generation && request === requestVersion) error.value = errorMessage(e, tr('collectorDiagnosticsDialog.kf9e189')) }
  finally { if (current === generation && request === requestVersion) { if (!background) busy.value = false; if (firstPage) void load() } }
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

<template>
  <div class="page traceability-page">
    <div class="trace-heading"><h2>{{ $t('traceability.title') }}</h2>
      <el-tooltip :content="$t('traceability.refresh')"><el-button :icon="Refresh" :loading="metaLoading" :aria-label="$t('traceability.refresh')" @click="loadMeta" /></el-tooltip>
    </div>
    <el-alert v-if="metaError" :title="metaError" type="error" :closable="false" show-icon />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon />
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('traceability.records')" name="records">
        <form class="toolbar trace-filters" @submit.prevent="query(1)">
          <el-input v-model="snQuery" clearable maxlength="200" :prefix-icon="Search" :placeholder="$t('traceability.snQuery')" :aria-label="$t('traceability.snQuery')" data-test="trace-sn-query" />
          <el-select v-model="queryJobId" clearable filterable :placeholder="$t('traceability.allJobs')" :aria-label="$t('traceability.allJobs')">
            <el-option v-for="job in jobs" :key="job.id" :value="job.id" :label="job.name" />
          </el-select>
          <el-select v-model="queryOutcome" :aria-label="$t('ux.trace.result')" data-test="trace-outcome-filter">
            <el-option value="" :label="$t('ux.trace.allOutcomes')" />
            <el-option v-for="outcome in outcomes" :key="outcome" :value="outcome" :label="outcomeLabel(outcome)" />
          </el-select>
          <el-date-picker v-model="range" type="datetimerange" :start-placeholder="$t('traceability.from')" :end-placeholder="$t('traceability.to')" />
          <el-button native-type="submit" type="primary" :icon="Search" :loading="queryLoading" data-test="trace-query">{{ $t('traceability.query') }}</el-button>
        </form>
        <el-alert v-if="queryError" :title="queryError" type="error" :closable="false" show-icon />
        <el-table :data="records" v-loading="queryLoading" row-key="id" data-test="trace-records" :empty-text="queryError || $t('traceability.empty')">
          <el-table-column type="expand"><template #default="{ row }">
            <el-table :data="row.values" class="trace-values" size="small">
              <el-table-column prop="name" :label="$t('traceability.fieldName')" min-width="140" />
              <el-table-column prop="value" :label="$t('traceability.value')" min-width="120" />
              <el-table-column prop="unit" :label="$t('traceability.unit')" min-width="80" />
              <el-table-column :label="$t('ux.trace.result')" min-width="110"><template #default="{ row: value }"><el-tag v-if="value.outcome" :type="outcomeType(value.outcome)">{{ outcomeLabel(value.outcome) }}</el-tag><span v-else>-</span></template></el-table-column>
            </el-table>
          </template></el-table-column>
          <el-table-column prop="sn" label="SN" min-width="180" show-overflow-tooltip />
          <el-table-column prop="jobName" :label="$t('traceability.job')" min-width="160" />
          <el-table-column :label="$t('ux.trace.result')" width="120"><template #default="{ row }"><el-tag :type="outcomeType(row.outcome)" :data-test="'trace-outcome-' + row.id">{{ outcomeLabel(row.outcome) }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('traceability.time')" min-width="170"><template #default="{ row }"><div>{{ formatTime(Date.parse(row.capturedAt)) }}</div><span class="tiny muted">{{ $t('ux.trace.saved') }}</span></template></el-table-column>
          <el-table-column :label="$t('traceability.ack')" width="140"><template #default="{ row }"><el-tag :type="row.acknowledged ? 'info' : 'warning'">{{ $t(row.acknowledged ? 'traceability.acknowledged' : 'traceability.awaitingReply') }}</el-tag></template></el-table-column>
        </el-table>
        <p class="tiny muted" data-test="trace-outcome-hint">{{ $t('ux.trace.historyHint') }}</p>
        <el-pagination :current-page="page" :page-size="50" :total="total" layout="prev, pager, next, total" @current-change="query" />
      </el-tab-pane>
      <el-tab-pane :label="$t('traceability.scan')" name="scan">
        <div class="trace-scan">
          <el-select v-model="scanJobId" filterable :placeholder="$t('traceability.job')" :aria-label="$t('traceability.job')" data-test="trace-scan-job">
            <el-option v-for="job in scanJobs" :key="job.id" :value="job.id" :label="job.name" />
          </el-select>
          <form v-if="selectedScanJob?.snSource === 'WebScan'" class="trace-scan-input" @submit.prevent="bindScan">
            <el-input ref="scanInput" v-model="scanSn" :disabled="!selectedScanJob || !!pendingSn || scanning" maxlength="200" :placeholder="$t('traceability.scanSn')" :aria-label="$t('traceability.scanSn')" data-test="trace-scan-input" />
            <el-button native-type="submit" :icon="Link" type="primary" :loading="scanning" :disabled="!selectedScanJob || !!pendingSn || !scanSn.trim()" data-test="trace-bind">{{ $t('traceability.bind') }}</el-button>
          </form>
          <el-descriptions v-if="selectedScanJob" :column="1" border>
            <el-descriptions-item :label="$t('traceability.snSource')">{{ sourceLabel(selectedScanJob.snSource) }}</el-descriptions-item>
            <el-descriptions-item v-if="isScanner(selectedScanJob.snSource)" :label="$t('traceability.connection')"><span data-test="trace-scanner-state">{{ scannerStateLabel(selectedScanJob.scannerStatus?.state) }}</span></el-descriptions-item>
            <el-descriptions-item :label="$t('traceability.state')">{{ stateLabel(selectedScanJob.status.state) }}</el-descriptions-item>
            <el-descriptions-item :label="$t('traceability.pendingSn')"><span data-test="trace-pending-sn">{{ pendingSn || '-' }}</span></el-descriptions-item>
            <el-descriptions-item :label="$t('traceability.expires')">{{ pendingSn ? formatTime(selectedScanJob.status.scanExpiresTs) : '-' }}</el-descriptions-item>
            <el-descriptions-item :label="$t('traceability.reply')">{{ selectedScanJob.status.reply ?? '-' }}</el-descriptions-item>
          </el-descriptions>
          <el-alert v-if="selectedScanJob?.status.error" :title="selectedScanJob.status.error" type="warning" :closable="false" show-icon />
          <el-alert v-if="isScanner(selectedScanJob?.snSource) && selectedScanJob?.scannerStatus?.error" :title="selectedScanJob.scannerStatus.error" type="warning" :closable="false" show-icon data-test="trace-scanner-error" />
          <el-button v-if="pendingSn" :icon="Delete" :disabled="scanning" @click="clearScan">{{ $t('traceability.clearScan') }}</el-button>
          <el-empty v-if="!scanJobs.length && !metaError" :description="$t('traceability.noScanJobs')" />
        </div>
      </el-tab-pane>
      <el-tab-pane :label="$t('traceability.config')" name="config">
        <el-button v-if="canConfigure" :icon="Plus" type="primary" data-test="trace-add" @click="edit()">{{ $t('traceability.add') }}</el-button>
        <el-table :data="jobs" data-test="trace-jobs" :empty-text="metaError || $t('traceability.empty')">
          <el-table-column prop="name" :label="$t('traceability.job')" min-width="160" />
          <el-table-column :label="$t('traceability.trigger')" min-width="180"><template #default="{ row }">{{ tagName(row.triggerTagId) }}</template></el-table-column>
          <el-table-column :label="$t('traceability.snSource')" min-width="140"><template #default="{ row }">{{ sourceLabel(row.snSource) }}</template></el-table-column>
          <el-table-column :label="$t('traceability.enabled')" width="100"><template #default="{ row }">{{ $t(row.enabled ? 'traceability.yes' : 'traceability.no') }}</template></el-table-column>
          <el-table-column :label="$t('traceability.state')" min-width="160"><template #default="{ row }"><el-tooltip :content="row.status.error || stateLabel(row.status.state)"><span>{{ stateLabel(row.status.state) }}</span></el-tooltip></template></el-table-column>
          <el-table-column :label="$t('traceability.connection')" min-width="140"><template #default="{ row }"><el-tooltip v-if="isScanner(row.snSource)" :content="row.scannerStatus?.error || scannerStateLabel(row.scannerStatus?.state)"><span>{{ scannerStateLabel(row.scannerStatus?.state) }}</span></el-tooltip><span v-else>-</span></template></el-table-column>
          <el-table-column width="70"><template #default="{ row }"><el-tooltip :content="$t('traceability.edit')"><el-button v-if="row.canConfigure" :icon="Edit" :aria-label="$t('traceability.edit')" data-test="trace-edit" @click="edit(row)" /></el-tooltip></template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
    <el-dialog v-model="dialog" :title="$t(editId ? 'traceability.edit' : 'traceability.add')" width="min(680px, calc(100vw - 24px))" :before-close="editGuard.beforeClose" :close-on-click-modal="false" :close-on-press-escape="!saving" :show-close="!saving" @keydown="editGuard.onKey">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon data-test="trace-form-error" />
      <el-form label-position="top" @submit.prevent="save">
        <div class="trace-form-grid">
          <el-form-item :label="$t('traceability.job')" required><el-input v-model="form.name" maxlength="100" data-test="trace-name" /></el-form-item>
          <el-form-item :label="$t('traceability.enabled')"><el-switch v-model="form.enabled" data-test="trace-enabled" /></el-form-item>
          <el-form-item :label="$t('traceability.device')" required><el-select v-model="formDeviceId" filterable data-test="trace-device" @change="deviceChanged"><el-option v-for="device in configurableDevices" :key="device.id" :label="device.name" :value="device.id" /></el-select></el-form-item>
          <el-form-item :label="$t('traceability.trigger')" required><el-select v-model="form.triggerTagId" filterable data-test="trace-trigger"><el-option v-for="tag in triggerTags" :key="tag.id" :label="tag.name" :value="tag.id" /></el-select></el-form-item>
        </div>
        <el-form-item :label="$t('traceability.snSource')" required>
          <el-radio-group v-model="form.snSource" class="trace-source-options" data-test="trace-source" @change="sourceChanged"><el-radio-button v-for="source in snSources" :key="source" :value="source">{{ sourceLabel(source) }}</el-radio-button></el-radio-group>
        </el-form-item>
        <template v-if="form.snSource === 'Plc'">
          <el-form-item :label="$t('traceability.snFormat')"><el-select v-model="form.snFormat" data-test="trace-sn-format" @change="form.snTagIds = []"><el-option value="Numeric" :label="$t('traceability.numeric')" /><el-option value="AsciiLowByteFirst" :label="$t('traceability.asciiLow')" /><el-option value="AsciiHighByteFirst" :label="$t('traceability.asciiHigh')" /></el-select></el-form-item>
          <el-form-item :label="$t('traceability.snRegisters')" required>
            <el-select v-if="form.snFormat === 'Numeric'" :model-value="form.snTagIds[0]" filterable data-test="trace-sn-registers" @update:model-value="form.snTagIds = [$event]"><el-option v-for="tag in dataTags" :key="tag.id" :label="tag.name" :value="tag.id" /></el-select>
            <el-select v-else v-model="form.snTagIds" multiple filterable data-test="trace-sn-registers"><el-option v-for="tag in asciiTags" :key="tag.id" :label="tag.name" :value="tag.id" /></el-select>
          </el-form-item>
        </template>
        <el-form-item v-else :label="$t('traceability.scanExpiry')"><el-input-number v-model="form.scanExpirySeconds" :min="5" :max="3600" :step="30" /></el-form-item>
        <template v-if="isScanner(form.snSource)">
          <el-form-item v-if="form.snSource === 'Tcp'" :label="$t('traceability.tcpMode')"><el-radio-group v-model="form.scanner.tcpMode" data-test="trace-tcp-mode"><el-radio-button value="Client">{{ $t('traceability.tcpClient') }}</el-radio-button><el-radio-button value="Server">{{ $t('traceability.tcpServer') }}</el-radio-button></el-radio-group></el-form-item>
          <div class="trace-form-grid">
            <template v-if="form.snSource === 'Tcp'">
              <el-form-item :label="$t(form.scanner.tcpMode === 'Client' ? 'traceability.tcpHost' : 'traceability.listenHost')" required><el-input v-model="form.scanner.host" maxlength="253" data-test="trace-tcp-host" /></el-form-item>
              <el-form-item :label="$t('traceability.port')" required><el-input-number v-model="form.scanner.port" :min="1" :max="65535" data-test="trace-tcp-port" /></el-form-item>
            </template>
            <template v-else>
              <el-form-item :label="$t('traceability.serialPort')" required><el-input v-model="form.scanner.serialPort" maxlength="128" data-test="trace-serial-port" /></el-form-item>
              <el-form-item :label="$t('traceability.baudRate')" required><el-input-number v-model="form.scanner.baudRate" :min="300" :max="921600" data-test="trace-baud-rate" /></el-form-item>
              <el-form-item :label="$t('traceability.dataBits')"><el-select v-model="form.scanner.dataBits"><el-option v-for="bits in [5, 6, 7, 8]" :key="bits" :value="bits" :label="String(bits)" /></el-select></el-form-item>
              <el-form-item :label="$t('traceability.parity')"><el-select v-model="form.scanner.parity"><el-option v-for="parity in ['None', 'Odd', 'Even']" :key="parity" :value="parity" :label="$t('traceability.parity' + parity)" /></el-select></el-form-item>
              <el-form-item :label="$t('traceability.stopBits')"><el-select v-model="form.scanner.stopBits"><el-option value="One" label="1" /><el-option value="Two" label="2" /></el-select></el-form-item>
            </template>
            <el-form-item :label="$t('traceability.terminator')"><el-select v-model="form.scanner.terminator" data-test="trace-terminator"><el-option value="CrLf" label="CRLF" /><el-option value="Lf" label="LF" /><el-option value="Cr" label="CR" /></el-select></el-form-item>
            <el-form-item :label="$t('traceability.encoding')"><el-select v-model="form.scanner.encoding"><el-option value="Utf8" label="UTF-8" /><el-option value="Ascii" label="ASCII" /></el-select></el-form-item>
          </div>
        </template>
        <div class="trace-field-heading"><h3>{{ $t('traceability.fields') }}</h3><el-tooltip :content="$t('traceability.addField')"><el-button :icon="Plus" :disabled="form.fields.length >= 64" :aria-label="$t('traceability.addField')" data-test="trace-add-field" @click="form.fields.push({ name: '', tagId: 0 })" /></el-tooltip></div>
        <div v-for="(field, index) in form.fields" :key="index" class="trace-field-row">
          <el-input v-model="field.name" maxlength="100" :placeholder="$t('traceability.fieldName')" :aria-label="$t('traceability.fieldName')" :data-test="'trace-field-name-' + index" />
          <el-select v-model="field.tagId" filterable :placeholder="$t('traceability.tag')" :aria-label="$t('traceability.tag')" :data-test="'trace-field-tag-' + index"><el-option v-for="tag in dataTags" :key="tag.id" :label="tag.name" :value="tag.id" /></el-select>
          <el-tooltip :content="$t('traceability.remove')"><el-button :icon="Delete" :aria-label="$t('traceability.remove')" @click="removeField(index)" /></el-tooltip>
        </div>
        <h3>{{ $t('ux.trace.mappingTitle') }}</h3>
        <p class="tiny muted">{{ $t('ux.trace.mappingHint') }}</p>
        <el-form-item :label="$t('ux.trace.resultField')">
          <el-select v-model="outcomeFieldIndex" clearable :placeholder="$t('ux.trace.noMapping')" :aria-label="$t('ux.trace.resultField')" data-test="trace-outcome-field">
            <el-option v-for="(field, index) in form.fields" :key="index" :value="index" :label="field.name || $t('ux.trace.unnamedField', [index + 1])" :disabled="!field.name.trim() || !field.tagId" />
          </el-select>
        </el-form-item>
        <div v-if="outcomeFieldIndex !== undefined" class="trace-form-grid">
          <el-form-item :label="$t('ux.trace.passValue')" required><el-input-number v-model="outcomePassValue" data-test="trace-pass-value" /></el-form-item>
          <el-form-item :label="$t('ux.trace.failValue')" required><el-input-number v-model="outcomeFailValue" data-test="trace-fail-value" /></el-form-item>
        </div>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="editGuard.close()">{{ $t('traceability.cancel') }}</el-button><el-button :icon="Check" type="primary" :loading="saving" data-test="trace-save" @click="save">{{ $t('traceability.save') }}</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { Check, Delete, Edit, Link, Plus, Refresh, Search } from '@element-plus/icons-vue'
import type { InputInstance } from 'element-plus'
import { deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { useEditDialog } from '../composables/useEditDialog'
import { traceabilityApi, type SnSource, type TraceabilityJob, type TraceabilityJobInput, type TraceabilityRecord, type TraceabilityScannerOptions, type TraceabilityOutcome } from '../api/traceability'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import type { Device, Tag } from '../types'
import { formatTime } from '../utils'
import { can } from '../access/policies'

const auth = useAuthStore()
const tab = ref('records')
const jobs = ref<TraceabilityJob[]>([]), devices = ref<Device[]>([]), tags = ref<Tag[]>([])
const metaError = ref(''), actionError = ref(''), queryError = ref(''), formError = ref('')
const metaLoading = ref(false), queryLoading = ref(false), saving = ref(false), scanning = ref(false)
const snQuery = ref(''), queryJobId = ref<number>(), range = ref<[Date, Date] | null>(null)
const queryOutcome = ref<TraceabilityOutcome | ''>('')
const outcomes: TraceabilityOutcome[] = ['Passed', 'Failed', 'Unknown', 'NotEvaluated']
const outcomeFieldIndex = ref<number>()
const outcomePassValue = ref<number | undefined>(1), outcomeFailValue = ref<number | undefined>(2)
const outcomeLabel = (outcome: TraceabilityOutcome) => tr(`ux.trace.outcomes.${outcome}`)
const outcomeType = (outcome: TraceabilityOutcome) => outcome === 'Passed' ? 'success' : outcome === 'Failed' ? 'danger' : outcome === 'Unknown' ? 'warning' : 'info'
const records = ref<TraceabilityRecord[]>([]), total = ref(0), page = ref(1)
const scanJobId = ref<number>(), scanSn = ref(''), scanInput = ref<InputInstance>()
const clock = ref(Date.now())
const scanJobs = computed(() => jobs.value.filter(job => job.enabled && job.snSource !== 'Plc' && job.canScan))
const selectedScanJob = computed(() => scanJobs.value.find(job => job.id === scanJobId.value))
const pendingSn = computed(() => selectedScanJob.value?.status.scanExpiresTs && selectedScanJob.value.status.scanExpiresTs > clock.value ? selectedScanJob.value.status.pendingSn : null)
const canConfigure = computed(() => can(auth.user, 'Configure') && can(auth.user, 'Control'))
const configurableDevices = computed(() => devices.value.filter(device => !device.gatewayId && ['ModbusTcp', 'ModbusRtu', 'SiemensS7', 'MitsubishiMc', 'OmronFins'].includes(device.protocol) && can(auth.user, 'Configure', device.areaId) && can(auth.user, 'Control', device.areaId)))
const dialog = ref(false), editId = ref<number>(), formDeviceId = ref<number>()
const snSources: SnSource[] = ['Plc', 'WebScan', 'Tcp', 'Serial']
const scannerDefaults = (): TraceabilityScannerOptions => ({ tcpMode: 'Client', host: '127.0.0.1', port: 9004, serialPort: 'COM1', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One', terminator: 'CrLf', encoding: 'Utf8' })
const blank = (): TraceabilityJobInput => ({ name: '', enabled: false, triggerTagId: 0, snSource: 'Plc', snFormat: 'Numeric', snTagIds: [], fields: [{ name: '', tagId: 0 }], scanExpirySeconds: 300, revision: 0, scanner: scannerDefaults() })
const form = reactive<TraceabilityJobInput>(blank())
const physicalTags = computed(() => tags.value.filter(tag => tag.deviceId === formDeviceId.value && (!tag.kind || tag.kind === 'Physical')))
const triggerTags = computed(() => physicalTags.value.filter(tag => tag.writable && tag.dataType !== 'Bool' && tag.scale === 1 && tag.offset === 0))
const dataTags = computed(() => physicalTags.value.filter(tag => tag.id !== form.triggerTagId))
const asciiTags = computed(() => dataTags.value.filter(tag => tag.dataType === 'UInt16' && tag.scale === 1 && tag.offset === 0))
const tagName = (id: number) => tags.value.find(tag => tag.id === id)?.name ?? `#${id}`
const stateLabel = (state: string) => tr(`traceability.states.${state}`)
const sourceLabel = (source: SnSource) => tr(`traceability.${({ Plc: 'plc', WebScan: 'webScan', Tcp: 'tcp', Serial: 'serial' })[source]}`)
const isScanner = (source?: SnSource) => source === 'Tcp' || source === 'Serial'
const scannerStateLabel = (state?: string) => tr(`traceability.scannerStates.${state || 'stopped'}`)
let timer: ReturnType<typeof setTimeout> | undefined
let disposed = false
let queryVersion = 0

async function loadMeta() {
  if (metaLoading.value) return
  metaLoading.value = true
  try {
    const [nextJobs, nextDevices, nextTags] = await Promise.all([traceabilityApi.jobs(), deviceApi.list(), tagApi.list()])
    jobs.value = nextJobs; devices.value = nextDevices; tags.value = nextTags
    metaError.value = ''; clock.value = Date.now()
    if (!scanJobs.value.some(job => job.id === scanJobId.value)) scanJobId.value = scanJobs.value[0]?.id
  } catch (error) { metaError.value = errorMessage(error, tr('traceability.loadFailed')) }
  finally { metaLoading.value = false }
}
async function poll() {
  if (disposed) return
  if (tab.value !== 'records' && !metaLoading.value) {
    try { jobs.value = await traceabilityApi.jobs(); metaError.value = ''; clock.value = Date.now() }
    catch (error) { metaError.value = errorMessage(error, tr('traceability.loadFailed')) }
  }
  if (!disposed) timer = setTimeout(poll, 2000)
}
async function query(nextPage = 1) {
  const version = ++queryVersion
  queryLoading.value = true; queryError.value = ''; page.value = nextPage
  try {
    const result = await traceabilityApi.records({ sn: snQuery.value.trim() || undefined, jobId: queryJobId.value, from: range.value?.[0].getTime(), to: range.value?.[1].getTime(), outcome: queryOutcome.value || undefined, page: nextPage, pageSize: 50 })
    if (version === queryVersion) { records.value = result.items; total.value = result.total }
  } catch (error) { if (version === queryVersion) queryError.value = errorMessage(error, tr('traceability.queryFailed')) }
  finally { if (version === queryVersion) queryLoading.value = false }
}
async function bindScan() {
  const job = selectedScanJob.value
  if (!job || job.snSource !== 'WebScan' || pendingSn.value || !scanSn.value.trim() || scanning.value) return
  scanning.value = true; actionError.value = ''
  try { job.status = await traceabilityApi.scan(job.id, scanSn.value.trim()); scanSn.value = ''; clock.value = Date.now() }
  catch (error) { actionError.value = errorMessage(error, tr('traceability.scanFailed')) }
  finally { scanning.value = false }
}
async function clearScan() {
  const job = selectedScanJob.value
  if (!job || scanning.value) return
  scanning.value = true; actionError.value = ''
  try { await traceabilityApi.clear(job.id); await loadMeta(); await nextTick(); scanInput.value?.focus() }
  catch (error) { actionError.value = errorMessage(error, tr('traceability.scanFailed')) }
  finally { scanning.value = false }
}
function edit(job?: TraceabilityJob) {
  formError.value = ''; editId.value = job?.id
  Object.assign(form, job ? { name: job.name, enabled: job.enabled, triggerTagId: job.triggerTagId, snSource: job.snSource, snFormat: job.snFormat, snTagIds: [...job.snTagIds], fields: job.fields.map(field => ({ ...field })), scanExpirySeconds: job.scanExpirySeconds, revision: job.revision, scanner: { ...scannerDefaults(), ...job.scanner } } : blank())
  const mappedIndex = form.fields.findIndex(field => field.outcomeMapping)
  outcomeFieldIndex.value = mappedIndex < 0 ? undefined : mappedIndex
  outcomePassValue.value = form.fields[mappedIndex]?.outcomeMapping?.passValue ?? 1
  outcomeFailValue.value = form.fields[mappedIndex]?.outcomeMapping?.failValue ?? 2
  formDeviceId.value = job ? tags.value.find(tag => tag.id === job.triggerTagId)?.deviceId ?? undefined : undefined
  dialog.value = true
}
function deviceChanged() { form.triggerTagId = 0; form.snTagIds = []; form.fields = [{ name: '', tagId: 0 }]; outcomeFieldIndex.value = undefined }
function removeField(index: number) {
  form.fields.splice(index, 1)
  if (outcomeFieldIndex.value === index) outcomeFieldIndex.value = undefined
  else if (outcomeFieldIndex.value !== undefined && outcomeFieldIndex.value > index) outcomeFieldIndex.value--
}
function sourceChanged() { form.snTagIds = []; form.snFormat = 'Numeric' }
// 追溯任务字段多，误关弹窗代价高：接入未保存确认与 Ctrl+Enter 保存。
const editGuard = useEditDialog({
  visible: dialog,
  saving,
  snapshot: () => JSON.stringify({ editId: editId.value, deviceId: formDeviceId.value, form, outcomeFieldIndex: outcomeFieldIndex.value, outcomePassValue: outcomePassValue.value, outcomeFailValue: outcomeFailValue.value }),
  save
})
async function save() {
  if (saving.value) return
  formError.value = ''
  if (!form.name.trim() || !form.triggerTagId || !form.fields.length || form.fields.some(field => !field.tagId || !field.name.trim()) || form.snSource === 'Plc' && !form.snTagIds.length) { formError.value = tr('traceability.required'); return }
  const resultIndex = outcomeFieldIndex.value, passValue = outcomePassValue.value, failValue = outcomeFailValue.value
  if (resultIndex !== undefined && (!form.fields[resultIndex] || typeof passValue !== 'number' || typeof failValue !== 'number' || !Number.isFinite(passValue) || !Number.isFinite(failValue) || passValue === failValue)) {
    formError.value = tr('ux.trace.invalidMapping'); return
  }
  const fields = form.fields.map((field, index) => ({ name: field.name.trim(), tagId: field.tagId,
    ...(index === resultIndex && typeof passValue === 'number' && typeof failValue === 'number' ? { outcomeMapping: { passValue, failValue } } : {}) }))
  saving.value = true
  try {
    await traceabilityApi.save(editId.value, { ...form, name: form.name.trim(), snTagIds: form.snSource === 'Plc' ? [...form.snTagIds] : [], fields })
    dialog.value = false; await loadMeta()
  } catch (error) { formError.value = errorMessage(error, tr('traceability.saveFailed')) }
  finally { saving.value = false }
}
watch([tab, scanJobId], async () => { scanSn.value = ''; actionError.value = ''; await nextTick(); if (tab.value === 'scan') scanInput.value?.focus() })
watch(scanJobs, available => { if (!available.some(job => job.id === scanJobId.value)) scanJobId.value = available[0]?.id })
watch(pendingSn, async (value, previous) => { if (!value && previous) { await nextTick(); scanInput.value?.focus() } })
onMounted(async () => { await Promise.all([loadMeta(), query()]); if (!disposed) timer = setTimeout(poll, 2000) })
onBeforeUnmount(() => { disposed = true; if (timer) clearTimeout(timer); queryVersion++ })
</script>

<style scoped>
.traceability-page { min-width: 0; }
.trace-heading, .trace-field-heading { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.trace-heading h2 { margin: 0; font-size: 20px; }
.trace-field-heading h3 { font-size: 15px; }
.trace-filters { display: flex; flex-wrap: wrap; gap: 10px; margin-bottom: 16px; }
.trace-filters > .el-input { width: 260px; }
.trace-filters > .el-select { width: 200px; }
.trace-filters :deep(.el-date-editor) { max-width: 100%; width: 360px; flex-grow: 0; }
.trace-scan { display: grid; gap: 16px; max-width: 640px; padding: 8px 0; }
.trace-scan-input { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 10px; }
.trace-form-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 0 16px; }
.trace-form-grid > * { min-width: 0; }
.trace-form-grid :deep(.el-input-number) { width: 100%; }
.trace-source-options { display: flex; flex-wrap: wrap; row-gap: 8px; }
.trace-field-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) 40px; gap: 8px; margin: 10px 0; }
.trace-values { width: calc(100% - 48px); margin: 8px 24px; }
.el-select { width: 100%; }
.el-pagination { margin-top: 16px; max-width: 100%; overflow-x: auto; }
.el-alert { margin-bottom: 12px; }
@media (max-width: 640px) {
  .trace-filters > .el-input, .trace-filters > .el-select, .trace-filters :deep(.el-date-editor) { width: 100%; }
  .trace-form-grid { grid-template-columns: minmax(0, 1fr); }
  .trace-field-row { grid-template-columns: minmax(0, 1fr) 40px; }
  .trace-field-row > .el-input { grid-column: 1 / -1; }
  .trace-scan :deep(.el-descriptions__content) { overflow-wrap: anywhere; }
}
</style>
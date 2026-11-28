<template>
  <div class="page page--fill">
    <div class="panel">
      <div class="toolbar">
        <h3>{{ $t('devicesView.kc70d1b') }}</h3>
        <el-button v-if="gatewayFilter !== null" link @click="clearFilter">{{ $t('devicesView.k48528b') }}</el-button>
        <span class="spacer" />
        <template v-if="selected.length">
          <el-text data-test="device-batch-count">{{ $t('devicesView.k805f4f', [selected.length]) }}</el-text>
          <el-button data-test="device-batch-enable" :loading="batching" @click="batchSetEnabled(true)">{{ $t('devicesView.kf5e360') }}</el-button>
          <el-button data-test="device-batch-disable" :loading="batching" @click="batchSetEnabled(false)">{{ $t('devicesView.k45f528') }}</el-button>
          <el-button type="danger" plain data-test="device-batch-delete" :loading="batching" @click="batchDelete">{{ $t('devicesView.k362aed') }}</el-button>
          <el-button v-if="batching" type="warning" plain :disabled="batchCancel" data-test="device-batch-stop" @click="batchCancel = true">{{ $t('batchUx.stopBatch') }}</el-button>
        </template>
        <el-dropdown v-if="canOpenCertificates" trigger="click" @command="onMoreCommand">
          <el-button data-test="device-more-actions">{{ $t('uxImprove.more') }}<el-icon class="more-caret"><ArrowDown /></el-icon></el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="certificates" data-test="device-certificates">{{ $t('devicesView.k1c11ad') }}</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-button v-if="configurableAreas.length" type="primary" :icon="Plus" data-test="device-create" @click="openCreate">{{ $t('devicesView.k56ac09') }}</el-button>
      </div>
      <form class="catalog-filters" @submit.prevent="applyQuery">
        <el-input v-model="search" clearable :prefix-icon="Search" :placeholder="$t('devicesView.searchPlaceholder')" :aria-label="$t('devicesView.searchPlaceholder')" class="catalog-search" @keydown.enter="onSearchKey" />
        <el-select v-model="filterProtocol" :aria-label="$t('devicesView.protocolFilter')" class="catalog-select" data-test="device-filter-protocol">
          <el-option :label="$t('devicesView.allProtocols')" value="all" />
          <el-option v-for="protocol in protocols" :key="protocol" :label="protocolLabel(protocol)" :value="protocol" />
        </el-select>
        <el-select v-model="filterEnabled" :aria-label="$t('listFilters.status')" class="catalog-select" data-test="device-filter-enabled">
          <el-option :label="$t('listFilters.allStates')" value="all" />
          <el-option :label="$t('listFilters.enabled')" value="enabled" />
          <el-option :label="$t('listFilters.disabled')" value="disabled" />
        </el-select>
        <el-select v-model="filterHealth" :aria-label="$t('devicesView.statusFilter')" class="catalog-select" data-test="device-filter-health">
          <el-option :label="$t('devicesView.allHealth')" value="all" />
          <el-option :label="$t('devicesView.statusOnline')" value="online" />
          <el-option :label="$t('devicesView.statusOffline')" value="offline" />
          <el-option :label="$t('devicesView.statusFault')" value="fault" />
        </el-select>
        <el-button type="primary" :icon="Search" data-test="device-query" @click="applyQuery">{{ $t('configurationUx.query') }}</el-button>
        <el-button v-if="hasFilters" link type="primary" data-test="device-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <span v-if="!loading || deviceTotal > 0 || devices.length" class="catalog-count muted" role="status" data-test="device-result-count">{{ $t('listFilters.resultCount', [deviceTotal, scopeTotal]) }}</span>
        <el-button :icon="Refresh" :loading="loading" data-test="device-refresh" @click="load">{{ $t('listFilters.refresh') }}</el-button>
        <el-switch v-model="showEngineering" :active-text="$t('ux.tables.engineering')" data-test="device-engineering-columns" />
      </form>
      <el-alert v-if="catalog.pending.value" :title="$t('configurationUx.pendingQuery')" type="info" :closable="false" show-icon data-test="device-query-pending" />
      <p class="tiny muted" role="status" data-test="device-query-context">{{ $t('configurationUx.resultScope', [queryScope, page, deviceTotal]) }}</p>
      <el-alert v-if="batchProgress" :title="batchCancel ? $t('batchUx.stopping') : batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" data-test="device-batch-progress" />
      <PageStatusBar :issues="pageIssues" @action="onStatusAction" />
      <el-alert v-if="gatewayFilter !== null" :title="$t('devicesView.kf60ef3', [gatewayFilter])" type="info" :closable="false" />
      <el-alert v-if="savedDevice" :title="$t('configurationUx.savedNext', [savedDevice.name])" type="success" :closable="true" data-test="device-saved-next" @close="savedDevice = null">
        <el-button v-if="can(auth.user, 'Control', savedDevice.areaId)" link type="primary" @click="openTest(savedDevice)">{{ $t('configurationUx.testConnection') }}</el-button>
        <el-button link @click="router.push({ path: '/config/tags', query: { deviceId: String(savedDevice.id) } })">{{ $t('configurationUx.addTags') }}</el-button>
      </el-alert>
      <el-table ref="tableRef" v-loading="loading" :data="devices" :default-sort="{ prop: sortKey, order: sortOrder }" size="small" height="100%" row-key="id" class="engineering-table" data-test="devices-table" @selection-change="onSelect" @sort-change="onSortChange">
        <template #empty>
          <PageState v-if="loadError" state="error" :description="loadError" :retry-text="$t('common.retry')" compact @retry="load()" />
          <PageState v-else-if="hasFilters" state="empty" :description="$t('listFilters.noMatches')" compact>
            <el-button link type="primary" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
          </PageState>
          <PageState v-else state="empty" :description="$t('devicesView.k2b9379')" compact />
        </template>
        <el-table-column type="selection" width="48" :selectable="canConfigureDevice" />
        <el-table-column v-if="showEngineering" prop="id" label="ID" width="60" sortable="custom" show-overflow-tooltip />
        <el-table-column :label="$t('devicesView.activePath')" min-width="260" data-test="device-acquisition-path">
        <template #default="{ row }">
          <div v-if="acquisitionByDevice[row.id]?.hasStandby || acquisitionByDevice[row.id]?.activePath === 'Standby'">
            <el-tag size="small" :type="acquisitionByDevice[row.id]?.activePath === 'Standby' ? 'warning' : 'success'">{{ acquisitionByDevice[row.id]?.activePath === 'Standby' ? $t('devicesView.pathStandby') : $t('devicesView.pathPrimary') }}</el-tag>
            <el-tag v-if="acquisitionByDevice[row.id]?.manualLock" size="small" type="danger" data-test="device-path-locked">{{ $t('devicesView.pathLocked') }}</el-tag>
            <div v-if="pathChangeText(row)" class="tiny muted" data-test="device-path-reason">{{ pathChangeText(row) }}</div>
            <el-button v-if="acquisitionByDevice[row.id]?.activePath === 'Standby'" link type="primary" :loading="switchingPath === row.id" data-test="device-switch-primary" @click="switchAcquisition(row, 'Primary')">{{ $t('devicesView.switchToPrimary') }}</el-button>
            <el-button v-if="acquisitionByDevice[row.id]?.activePath === 'Standby' && acquisitionByDevice[row.id]?.manualLock" link :loading="switchingPath === row.id" data-test="device-resume-auto" @click="resumeAcquisitionAuto(row)">{{ $t('devicesView.resumeAuto') }}</el-button>
            <el-button v-if="acquisitionByDevice[row.id]?.activePath !== 'Standby'" link type="warning" :loading="switchingPath === row.id" data-test="device-switch-standby" @click="switchAcquisition(row, 'Standby')">{{ $t('devicesView.switchToStandby') }}</el-button>
          </div>
          <span v-else class="muted">—</span>
        </template>
      </el-table-column>
      <el-table-column prop="name" :label="$t('devicesView.k1be7ae')" min-width="180" sortable="custom" show-overflow-tooltip>
          <template #default="{ row }">
            <div>{{ row.name }} <el-tag v-if="row.simulationEnabled" type="warning" size="small" data-test="device-simulation-badge">{{ $t('simulation.badge') }}</el-tag><el-tag v-if="row.channelId != null" type="info" size="small" data-test="device-channel-badge">{{ channelName(row.channelId) }}</el-tag></div>
            <router-link v-if="canConfigureDevice(row)" :to="{ path: '/config/tags', query: { deviceId: String(row.id) } }" class="tiny" data-test="device-tags">{{ $t('devicesView.viewTags') }}</router-link>
          </template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.k17fc93')" width="110" show-overflow-tooltip><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
        <el-table-column :label="$t('devicesView.kb0c431')" min-width="140" show-overflow-tooltip><template #default="{ row }">{{ protocolLabel(row.protocol) }}</template></el-table-column>
        <el-table-column :label="$t('devicesView.k5dfe3e')" width="180" show-overflow-tooltip data-test="device-active-gateway"><template #default="{ row }">
          <router-link v-if="displayGatewayId(row) !== null && globalConfigure" :to="{ path: '/config/gateways', query: { gatewayId: String(displayGatewayId(row)) } }">{{ $t('devicesView.k38b4cc', [displayGatewayId(row)]) }}</router-link><span v-else-if="displayGatewayId(row) !== null">{{ $t('devicesView.k38b4cc', [displayGatewayId(row)]) }}</span><span v-else>{{ $t('devicesView.k3963d7') }}</span>
          <el-tag v-if="row.configuredStandby || isConfiguredStandby(row)" size="small" type="warning" style="margin-left: 4px">{{ $t('devicesView.configuredStandby') }}</el-tag>
        </template></el-table-column>
        <el-table-column :label="$t('devicesView.k67d2d7')" min-width="190" show-overflow-tooltip>
          <template #default="{ row }">{{ activeEndpointText(row) }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" prop="stationNumber" :label="$t('devicesView.k60aa5c')" width="70" sortable="custom" show-overflow-tooltip />
        <el-table-column v-if="showEngineering" prop="byteOrder" :label="$t('devicesView.ka15ac8')" width="90" show-overflow-tooltip />
        <el-table-column v-if="showEngineering" prop="pollIntervalMs" :label="$t('devicesView.k9b415a')" width="95" sortable="custom" show-overflow-tooltip>
          <template #default="{ row }">{{ row.pollIntervalMs }} ms</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" prop="timeoutMs" :label="$t('devicesView.kff06c2')" width="80" show-overflow-tooltip>
          <template #default="{ row }">{{ row.timeoutMs }} ms</template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.runtimeStatus')" width="110" data-test="device-health-column">
          <template #default="{ row }">
            <el-tooltip :content="healthTooltip(row)" placement="top">
              <el-tag :class="{ 'tag-quiet': deviceHealthKind(row.id) === 'online' }" :type="healthTagType(row.id)" size="small" :data-test="`device-health-${row.id}`">{{ healthLabel(row.id) }}</el-tag>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column v-if="showEngineering" :label="$t('devicesView.lastPoll')" width="150" show-overflow-tooltip>
          <template #default="{ row }">{{ deviceHealth(row.id)?.lastPollTs ? formatTime(deviceHealth(row.id)!.lastPollTs) : '--' }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" :label="$t('devicesView.commFailures')" width="100" show-overflow-tooltip>
          <template #default="{ row }">{{ deviceHealth(row.id)?.commFailures ?? '--' }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" :label="$t('devicesView.lastError')" min-width="180" show-overflow-tooltip>
          <template #default="{ row }">{{ deviceHealth(row.id)?.error || '—' }}</template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.kd4e9ca')" width="80" show-overflow-tooltip>
          <template #default="{ row }">
            <el-tag :class="{ 'tag-quiet': row.enabled }" :type="row.enabled ? undefined : 'warning'" size="small">{{ row.enabled ? $t('devicesView.kd4e9ca') : $t('devicesView.kd989e5') }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.kf3ea6d')" width="220" fixed="right">
          <template #default="{ row }"><div class="engineering-actions">
            <el-button link type="primary" :disabled="!can(auth.user, 'Configure', row.areaId)" @click="openEdit(row)">{{ $t('devicesView.ka7f814') }}</el-button>
            <el-button link :disabled="!can(auth.user, 'Control', row.areaId)" @click="openTest(row)">{{ $t('devicesView.k10b7d8') }}</el-button>
            <el-dropdown trigger="click">
              <el-button link data-test="device-more">{{ $t('ux.tables.more') }}</el-button>
              <template #dropdown><el-dropdown-menu>
                <el-dropdown-item @click="diagnosticDevice = row; diagnosticsVisible = true">{{ $t('devicesView.ke7a75c') }}</el-dropdown-item>
                <el-dropdown-item v-if="row.simulationEnabled" :disabled="!can(auth.user, 'Control', row.areaId)" data-test="device-simulation-open" @click="openSimulation(row)">{{ $t('simulation.open') }}</el-dropdown-item>
                <el-dropdown-item v-if="row.protocol === 'OpcUa'" :disabled="!canConfigureDevice(row)" @click="openBrowse(row)">{{ $t('devicesView.kfcad10') }}</el-dropdown-item>
                <el-dropdown-item :disabled="!canConfigureDevice(row)" data-test="device-copy" @click="openCopy(row)">{{ $t('devicesView.k4edd1d') }}</el-dropdown-item>
                <el-dropdown-item v-if="globalConfigure" data-test="device-assign" @click="openAssignment(row.id)">{{ $t('devicesView.k5ad09b') }}</el-dropdown-item>
                <el-dropdown-item divided :disabled="!canConfigureDevice(row)" data-test="device-delete" @click="remove(row)">{{ $t('devicesView.k3755f5') }}</el-dropdown-item>
              </el-dropdown-menu></template>
            </el-dropdown>
          </div></template>
        </el-table-column>
      </el-table>
      <el-pagination :current-page="page" :page-size="PAGE_SIZE" :total="deviceTotal" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="device-pagination" @update:current-page="page = $event" @current-change="onPageChange" />
    </div>

    <DeviceFormDialog ref="formDialog" :areas="configurableAreas" :channels="channels" @saved="onFormSaved" />
    <DeviceTestDialog ref="testDialog" :can-open-certificates="canOpenCertificates" @diagnose="openDiagnostics" @certificates="certificatesVisible = true" />
    <OpcCertificatesDialog v-model="certificatesVisible" :devices="certificateDevices" :include-center="globalConfigure" />
    <SimulationDialog v-model="simulationVisible" :device="simulationDevice" />
    <GatewayAssignmentDialog v-if="assignmentDeviceId" v-model="assignmentVisible" :device-id="assignmentDeviceId" :suggested-gateway-id="assignmentSuggested" @changed="load" />
    <CollectorDiagnosticsDialog v-if="diagnosticDevice" v-model="diagnosticsVisible" :device="diagnosticDevice" />
    <OpcBrowserDialog v-if="browseDevice" v-model="browseVisible" :device="browseDevice" @imported="load" />
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, toRefs, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type TableInstance } from 'element-plus'
import { ArrowDown, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { areaApi, deviceApi, healthApi } from '../api'
import { channelApi, type ChannelInfo } from '../api/channels'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import type { Area, Device, DeviceInput, DeviceStatus } from '../types'
import { can, canGlobal } from '../access/policies'
import { protocolLabel } from '../protocols'
import OpcCertificatesDialog from '../components/OpcCertificatesDialog.vue'
import { opcApi } from '../api/opc'
import { summarizeCertificateExpiry } from '../composables/opcCertificateExpiry'
import OpcBrowserDialog from '../components/OpcBrowserDialog.vue'
import CollectorDiagnosticsDialog from '../components/CollectorDiagnosticsDialog.vue'
import GatewayAssignmentDialog from '../components/GatewayAssignmentDialog.vue'
import SimulationDialog from '../components/SimulationDialog.vue'
import DeviceFormDialog from '../components/devices/DeviceFormDialog.vue'
import PageState from '../components/PageState.vue'
import DeviceTestDialog from '../components/devices/DeviceTestDialog.vue'
import { tr } from '../i18n'
import { formatTime } from '../utils'
import { usePolling } from '../composables/usePolling'
import { deviceRuntimeKind, matchesDeviceRuntimeFilter, type DeviceRuntimeKind } from '../composables/deviceRuntimeStatus'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { useCatalogQuery } from '../composables/useCatalogQuery'
import { positiveQueryId } from '../nav/query'
import PageStatusBar, { type PageIssue } from '../components/PageStatusBar.vue'

const devices = ref<Device[]>([])
const deviceTotal = ref(0)
const scopeTotal = ref(0)
const protocolNames = ref<string[]>([])
const certificateList = ref<Device[]>([])
const route = useRoute(), router = useRouter()
const auth = useAuthStore()
const globalConfigure = computed(() => canGlobal(auth.user, 'Configure'))
const areas = ref<Area[]>([])
const areasError = ref('')
// 通道是全局对象，只有全局配置权限能改；没有权限的用户只能沿用设备上已下发的端点。
const channels = ref<ChannelInfo[]>([])
const configurableAreas = computed(() => areas.value.filter(a => can(auth.user, 'Configure', a.id)))
function canConfigureDevice(row: Device): boolean { return can(auth.user, 'Configure', row.areaId) }
const certificateDevices = computed(() => certificateList.value.filter(d => can(auth.user, 'Configure', d.areaId)))
function areaName(id?: number): string { return areas.value.find(a => a.id === id)?.name ?? (id ? `#${id}` : '-') }
const gatewayFilter = computed(() => typeof route.query.gatewayId === 'string' && /^[1-9]\d*$/.test(route.query.gatewayId) ? Number(route.query.gatewayId) : null)
function displayGatewayId(device: Device): number | null {
  if (device.activeGatewayId !== undefined) return device.activeGatewayId
  return acquisitionByDevice.value[device.id]?.activeGatewayId ?? device.gatewayId
}
function isConfiguredStandby(device: Device): boolean {
  if (gatewayFilter.value == null) return !!device.configuredStandby
  return displayGatewayId(device) === gatewayFilter.value && device.gatewayId !== gatewayFilter.value
}
const catalog = useCatalogQuery({ search: '', protocol: 'all', enabled: 'all' })
const { search, protocol: filterProtocol, enabled: filterEnabled } = toRefs(catalog.draft)
const filterHealth = ref<'all' | DeviceRuntimeKind>('all')
const healthById = ref(new Map<number, DeviceStatus>())
const healthError = ref('')
const showEngineering = ref(false)
const protocols = computed(() => {
  const source = protocolNames.value.length ? protocolNames.value : devices.value.map(device => device.protocol)
  return [...new Set(source)].sort((a, b) => protocolLabel(a).localeCompare(protocolLabel(b)))
})
const canOpenCertificates = computed(() => globalConfigure.value
  || (protocols.value.includes('OpcUa') && configurableAreas.value.length > 0)
  || devices.value.some(d => d.protocol === 'OpcUa' && d.gatewayId != null && canConfigureDevice(d))
  || certificateDevices.value.some(d => d.protocol === 'OpcUa' && d.gatewayId != null))
const hasFilters = computed(() => catalog.pending.value || !!catalog.applied.value.search || catalog.applied.value.protocol !== 'all' || catalog.applied.value.enabled !== 'all' || filterHealth.value !== 'all')
const queryScope = computed(() => [
  catalog.applied.value.search,
  catalog.applied.value.protocol !== 'all' ? protocolLabel(catalog.applied.value.protocol) : '',
  catalog.applied.value.enabled !== 'all' ? tr(catalog.applied.value.enabled === 'enabled' ? 'listFilters.enabled' : 'listFilters.disabled') : '',
  filterHealth.value !== 'all' ? tr(`devicesView.status${filterHealth.value[0].toUpperCase()}${filterHealth.value.slice(1)}`) : '',
  gatewayFilter.value ? tr('devicesView.k38b4cc', [gatewayFilter.value]) : ''
].filter(Boolean).join(' · ') || tr('configurationUx.all'))
const tableRef = ref<TableInstance>()
const PAGE_SIZE = 100
const page = computed({
  get: () => catalog.page.value,
  set: value => { catalog.page.value = value }
})
const sortKey = computed(() => (['id', 'name', 'stationNumber', 'pollIntervalMs', 'timeoutMs'] as const).find(key => key === route.query.sort) ?? '')
const sortOrder = computed(() => route.query.order === 'ascending' || route.query.order === 'descending' ? route.query.order : null)
function onPageChange(): void { selected.value = []; tableRef.value?.clearSelection() }
function onSortChange({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }): void {
  void router.replace({ query: { ...route.query, sort: order ? prop : undefined, order: order ?? undefined, page: undefined } })
  onPageChange()
}
async function applyQuery(): Promise<void> { await catalog.apply(); onPageChange() }
function onSearchKey(event: KeyboardEvent): void {
  event.preventDefault()
  if (!event.isComposing && event.keyCode !== 229) void applyQuery()
}
async function resetFilters(): Promise<void> { filterHealth.value = 'all'; await catalog.apply(true); onPageChange() }
watch([() => JSON.stringify(catalog.applied.value), () => catalog.page.value, sortKey, sortOrder, gatewayFilter, filterHealth], () => { onPageChange(); void loadDevices() })
const assignmentDeviceId = ref<number | null>(null), assignmentVisible = ref(false), assignmentSuggested = ref<number | undefined>()
function clearFilter(): void { const query = { ...route.query }; delete query.gatewayId; void router.replace({ query }) }
function openAssignment(id: number, suggested?: number): void { assignmentDeviceId.value = id; assignmentSuggested.value = suggested; assignmentVisible.value = true }
const acquisitionByDevice = ref<Record<number, import('../types').DeviceAcquisitionInfo>>({})
const switchingPath = ref<number | null>(null)
async function loadAcquisition(): Promise<void> {
  try {
    const rows = await deviceApi.acquisition()
    acquisitionByDevice.value = Object.fromEntries(rows.map(r => [r.deviceId, r]))
  } catch { /* 列表页仍可显示设备本身 */ }
}
function sharesBusWith(left: Device, right: Device): boolean {
  if (left.id === right.id) return false
  if (left.channelId != null && left.channelId === right.channelId) return true
  return left.standbyChannelId != null && left.standbyChannelId === right.standbyChannelId
}
async function busPeerNames(device: Device): Promise<string[]> {
  let catalog = devices.value
  try { catalog = await deviceApi.list() } catch { /* 确认框仍可用当前页已加载的设备 */ }
  return catalog.filter(item => item.enabled && sharesBusWith(device, item)).map(item => item.name)
}
async function confirmPathChange(device: Device, path: 'Primary' | 'Standby' | 'auto'): Promise<boolean> {
  const peers = await busPeerNames(device)
  const lines: string[] = []
  if (path === 'Standby') lines.push(tr('devicesView.switchStandbyConfirm'))
  else if (path === 'Primary') lines.push(tr('devicesView.switchPrimaryConfirm'))
  else lines.push(tr('devicesView.resumeAutoConfirm'))
  if (peers.length) lines.push(tr('devicesView.switchSharedBusConfirm', [peers.join('、')]))
  try {
    await ElMessageBox.confirm(lines.join('\n\n'), tr('devicesView.switchConfirmTitle', [device.name]), {
      type: 'warning',
      confirmButtonText: path === 'auto' ? tr('devicesView.resumeAuto') : path === 'Standby' ? tr('devicesView.switchToStandby') : tr('devicesView.switchToPrimary'),
      cancelButtonText: tr('devicesView.k4d0b46')
    })
    return true
  } catch { return false }
}
function pathChangeText(device: Device): string {
  const acq = acquisitionByDevice.value[device.id]
  if (!acq) return ''
  const parts = [acq.reason, acq.changedBy, acq.changedTs ? formatTime(acq.changedTs) : ''].filter(Boolean)
  return parts.join(' · ')
}
function activeEndpointText(device: Device): string {
  const acq = acquisitionByDevice.value[device.id]
  if (!acq || (!acq.hasStandby && acq.activePath !== 'Standby')) return endpointText(device)
  const rtu = ['ModbusRtu', 'ModbusAscii'].includes(device.protocol) || (softElementProtocol(device.protocol) && /"transport"\s*:\s*"Rtu"/i.test(device.protocolOptions ?? ''))
  const serial = acq.activeSerialPort || device.serialPort
  const host = acq.activeHost || device.host
  const port = acq.activePort ?? device.port
  const address = rtu ? `${serial} · ${device.baudRate}` : `${host}:${port}`
  const path = acq.activePath === 'Standby' ? tr('devicesView.pathStandby') : tr('devicesView.pathPrimary')
  return `${address}（${path}）`
}
async function switchAcquisition(device: Device, path: 'Primary' | 'Standby'): Promise<void> {
  if (!await confirmPathChange(device, path)) return
  switchingPath.value = device.id
  try {
    await deviceApi.switchAcquisitionPath(device.id, path)
    await loadAcquisition()
    ElMessage.success(path === 'Standby' ? tr('devicesView.switchSuccessStandby') : tr('devicesView.switchSuccessPrimary'))
  } catch (e) {
    ElMessage.error(errorMessage(e, tr('devicesView.switchFailed')))
  } finally { switchingPath.value = null }
}
async function resumeAcquisitionAuto(device: Device): Promise<void> {
  if (!await confirmPathChange(device, 'auto')) return
  switchingPath.value = device.id
  try {
    await deviceApi.resumeAcquisitionAuto(device.id)
    await loadAcquisition()
    ElMessage.success(tr('devicesView.resumeAutoSuccess'))
  } catch (e) {
    ElMessage.error(errorMessage(e, tr('devicesView.resumeAutoFailed')))
  } finally { switchingPath.value = null }
}
const loading = ref(false)
const loadError = ref('')
const actionError = ref('')
// 设备、区域与操作错误统一走状态条，避免三条 alert 依次把表格推下去。
const pageIssues = computed<PageIssue[]>(() => {
  const issues: PageIssue[] = []
  if (loadError.value) issues.push({ id: 'load', test: 'devices-load-error', level: 'error', message: loadError.value, detail: tr('configurationUx.loadHelp'), action: tr('common.retry'), actionTest: 'devices-load-retry' })
  if (areasError.value) issues.push({ id: 'areas', test: 'devices-areas-error', level: 'warning', message: areasError.value, action: tr('common.retry'), actionTest: 'devices-areas-retry' })
  if (healthError.value) issues.push({ id: 'health', test: 'devices-health-error', level: 'warning', message: healthError.value, action: tr('common.retry'), actionTest: 'devices-health-retry' })
  if (actionError.value) issues.push({ id: 'action', test: 'devices-action-error', level: 'error', message: actionError.value })
  if (certWarning.value) issues.push({ id: 'certs', test: 'devices-cert-expiry', level: 'warning', message: certWarning.value, action: tr('devicesView.viewCertificates'), actionTest: 'devices-cert-open' })
  return issues
})
function onStatusAction(id: string): void {
  if (id === 'load' || id === 'areas') void load()
  if (id === 'health') void loadHealth()
  if (id === 'certs') certificatesVisible.value = true
}
function onMoreCommand(command: string): void {
  if (command === 'certificates') certificatesVisible.value = true
}
const savedDevice = ref<Device | null>(null)
const selected = ref<Device[]>([])
const batching = ref(false)
const batchCancel = ref(false)
const batchProgress = ref('')
function onSelect(rows: Device[]): void { selected.value = rows }

const certificatesVisible = ref(false), browseVisible = ref(false), browseDevice = ref<Device | null>(null)
const certWarning = ref('')
let certScan = 0
let certController: AbortController | undefined
watch(certificatesVisible, visible => { if (visible) void loadCertificateDevices() })
async function loadCertificateDevices(): Promise<void> {
  try { certificateList.value = await deviceApi.list() } catch { /* 证书对话框保持上次列表；打不开时仍可看中心证书 */ }
}
async function scanCertificates(): Promise<void> {
  if (!canOpenCertificates.value) { certWarning.value = ''; return }
  const current = ++certScan
  certController?.abort()
  const request = certController = new AbortController()
  try {
    const expired: string[] = []
    const expiring: string[] = []
    const collect = (label: string, certs: { subject: string; notAfter: string; store: string }[]) => {
      const summary = summarizeCertificateExpiry(certs)
      if (summary.expired.length) expired.push(`${label}: ${summary.expired.map(x => x.subject || x.notAfter).join('、')}`)
      if (summary.expiring.length) expiring.push(`${label}: ${summary.expiring.map(x => x.subject || x.notAfter).join('、')}`)
    }
    if (globalConfigure.value) {
      try { collect(tr('opcCertificatesDialog.k3963d7'), await opcApi.certificates(undefined, request.signal)) }
      catch { /* 中心证书读失败不挡住设备页 */ }
    }
    const remotes = devices.value.filter(d => d.protocol === 'OpcUa' && d.gatewayId != null && canConfigureDevice(d))
    for (const device of remotes) {
      if (request.signal.aborted || current !== certScan) return
      try {
        collect(tr('opcCertificatesDialog.kc3a1fc', [device.gatewayId, device.name]), await opcApi.certificates({ deviceId: device.id, gatewayId: device.gatewayId! }, request.signal))
      } catch { /* 单台网关失败时继续扫其余证书 */ }
    }
    if (current !== certScan) return
    if (expired.length) certWarning.value = tr('devicesView.certExpired', [expired.join('；')])
    else if (expiring.length) certWarning.value = tr('devicesView.certExpiring', [expiring.join('；')])
    else certWarning.value = ''
  } catch {
    if (current === certScan) certWarning.value = ''
  }
}
const diagnosticsVisible = ref(false), diagnosticDevice = ref<Device | null>(null)
const simulationVisible = ref(false), simulationDevice = ref<Device | null>(null)
function openSimulation(device: Device): void { simulationDevice.value = device; simulationVisible.value = true }
function openBrowse(device: Device): void { browseDevice.value = device; browseVisible.value = true }

function softElementProtocol(protocol: string): boolean {
  return protocol === 'Inovance' || protocol === 'Xinje' || protocol === 'Delta'
}
function endpointText(row: { protocol: string; opcEndpointUrl?: string; serialPort: string; baudRate: number; host: string; port: number; protocolOptions?: string }): string {
  if (row.protocol === 'OpcUa') return row.opcEndpointUrl ?? ''
  const rtu = ['ModbusRtu', 'ModbusAscii'].includes(row.protocol) || (softElementProtocol(row.protocol) && /"transport"\s*:\s*"Rtu"/i.test(row.protocolOptions ?? ''))
  return rtu ? `${row.serialPort} · ${row.baudRate}` : `${row.host}:${row.port}`
}
function channelName(id: number | null): string {
  return id == null ? '' : channels.value.find(c => c.id === id)?.name ?? `#${id}`
}

function deviceHealth(id: number): DeviceStatus | undefined { return healthById.value.get(id) }
function deviceHealthKind(id: number): DeviceRuntimeKind { return deviceRuntimeKind(deviceHealth(id)) }
function healthLabel(id: number): string {
  const kind = deviceHealthKind(id)
  const key = `devicesView.status${kind[0].toUpperCase()}${kind.slice(1)}`
  return tr(key)
}
function healthTagType(id: number): 'success' | 'danger' | 'info' | 'warning' | undefined {
  const kind = deviceHealthKind(id)
  if (kind === 'online') return undefined
  if (kind === 'unknown') return 'info'
  return 'danger'
}
function healthTooltip(device: Device): string {
  const status = deviceHealth(device.id)
  const poll = status?.lastPollTs ? formatTime(status.lastPollTs) : '--'
  const base = tr('devicesView.healthTooltip', [healthLabel(device.id), poll, status?.commFailures ?? '--'])
  return status?.error ? `${base} · ${tr('devicesView.lastError')}: ${status.error}` : base
}
async function loadHealth(): Promise<void> {
  try {
    const health = await healthApi.get()
    healthById.value = new Map(health.devices.map(item => [item.deviceId, item]))
    healthError.value = ''
  } catch (error) {
    healthError.value = errorMessage(error, tr('devicesView.healthFailed'))
  }
}
function matchesCatalogFilters(device: Device): boolean {
  const applied = catalog.applied.value
  if (gatewayFilter.value != null && displayGatewayId(device) !== gatewayFilter.value) return false
  if (applied.protocol !== 'all' && device.protocol !== applied.protocol) return false
  if (applied.enabled === 'enabled' && !device.enabled) return false
  if (applied.enabled === 'disabled' && device.enabled) return false
  const query = applied.search.trim().toLowerCase()
  if (query && !`${device.name} ${device.host} ${device.protocol} ${device.serialPort}`.toLowerCase().includes(query)) return false
  return true
}
function sortDevices(items: Device[]): Device[] {
  const key = sortKey.value
  const order = sortOrder.value
  if (!key || !order) return items
  const sign = order === 'ascending' ? 1 : -1
  return [...items].sort((left, right) => {
    const av = left[key as keyof Device]
    const bv = right[key as keyof Device]
    if (typeof av === 'number' && typeof bv === 'number') return (av - bv) * sign
    return String(av ?? '').localeCompare(String(bv ?? ''), undefined, { numeric: true }) * sign
  })
}

let loadVersion = 0
let catalogVersion = 0
async function loadDevices(): Promise<void> {
  const version = ++loadVersion
  loading.value = true
  try {
    const applied = catalog.applied.value
    const filtered = !!applied.search.trim() || applied.protocol !== 'all' || applied.enabled !== 'all' || filterHealth.value !== 'all'
    if (filterHealth.value !== 'all') {
      if (!healthById.value.size) await loadHealth()
      const items = sortDevices((await deviceApi.list()).filter(device =>
        matchesCatalogFilters(device) && matchesDeviceRuntimeFilter(deviceHealth(device.id), filterHealth.value)))
      if (version !== loadVersion) return
      const maxPage = Math.max(1, Math.ceil(items.length / PAGE_SIZE))
      if (catalog.page.value > maxPage) {
        catalog.page.value = maxPage
        return
      }
      const scope = await deviceApi.page({ gatewayId: gatewayFilter.value, skip: 0, take: 1 })
      if (version !== loadVersion) return
      const skip = Math.max(0, (catalog.page.value - 1) * PAGE_SIZE)
      devices.value = items.slice(skip, skip + PAGE_SIZE)
      deviceTotal.value = items.length
      scopeTotal.value = scope.total
      protocolNames.value = scope.protocols.length ? scope.protocols : [...new Set(items.map(device => device.protocol))]
      selected.value = []
      loadError.value = ''
      return
    }
    const pageRequest = deviceApi.page({
      search: applied.search,
      protocol: applied.protocol,
      enabled: applied.enabled,
      gatewayId: gatewayFilter.value,
      skip: Math.max(0, (catalog.page.value - 1) * PAGE_SIZE),
      take: PAGE_SIZE,
      sort: sortKey.value,
      order: sortOrder.value
    })
    const scopeRequest = filtered
      ? deviceApi.page({ gatewayId: gatewayFilter.value, skip: 0, take: 1 })
      : null
    const result = await pageRequest
    if (version !== loadVersion) return
    const maxPage = Math.max(1, Math.ceil(result.total / PAGE_SIZE))
    if (catalog.page.value > maxPage) {
      catalog.page.value = maxPage
      return
    }
    const scope = scopeRequest ? await scopeRequest : null
    if (version !== loadVersion) return
    devices.value = result.items
    deviceTotal.value = result.total
    scopeTotal.value = scope?.total ?? result.total
    protocolNames.value = result.protocols.length ? result.protocols : (scope?.protocols ?? [])
    selected.value = []
    loadError.value = ''
  } catch (error) {
    if (version === loadVersion) loadError.value = errorMessage(error, tr('devicesView.ka70156'))
  } finally {
    if (version === loadVersion) loading.value = false
  }
}
async function loadAreasAndChannels(): Promise<void> {
  const version = ++catalogVersion
  const [channelList, areaList] = await Promise.all([
    // 通道读取失败不该挡住设备列表：没有通道时只是不能改绑定。
    channelApi.list().catch(() => [] as ChannelInfo[]),
    areaApi.list().then(list => { if (version === catalogVersion) areasError.value = ''; return list }).catch((error) => { if (version === catalogVersion) areasError.value = errorMessage(error, tr('devicesView.areasLoadFailed')); return areas.value })
  ])
  if (version !== catalogVersion) return
  channels.value = channelList
  areas.value = areaList
}
async function load(): Promise<void> {
  const healthTask = loadHealth()
  const catalogTask = loadAreasAndChannels()
  if (filterHealth.value !== 'all') await healthTask
  await Promise.all([loadDevices(), catalogTask, healthTask, loadAcquisition()])
  void scanCertificates()
}
usePolling(loadHealth, 15000)

const formDialog = ref<InstanceType<typeof DeviceFormDialog>>()
const testDialog = ref<InstanceType<typeof DeviceTestDialog>>()

function openCreate(): void {
  if (!configurableAreas.value.length) return
  const gatewayId = gatewayFilter.value
  if (gatewayId !== null) clearFilter()
  formDialog.value?.open(null, { gatewayId })
}

function openEdit(device: Device): void { formDialog.value?.open(device) }
function openCopy(device: Device): void { formDialog.value?.open(device, { copy: true }) }

function openDiagnostics(device: Device): void {
  diagnosticDevice.value = device
  diagnosticsVisible.value = true
}

async function onFormSaved(device: Device, gatewayId: number | null): Promise<void> {
  savedDevice.value = device
  if (gatewayId !== null && globalConfigure.value) openAssignment(device.id, gatewayId)
  await load()
}

function toDeviceInput(device: Device, patch: Partial<DeviceInput> = {}): DeviceInput {
  return {
    name: device.name,
    protocol: device.protocol,
    host: device.host,
    port: device.port,
    stationNumber: device.stationNumber,
    byteOrder: device.byteOrder,
    pollIntervalMs: device.pollIntervalMs,
    timeoutMs: device.timeoutMs,
    enabled: device.enabled,
    areaId: device.areaId ?? 1,
    channelId: device.channelId ?? null,
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: null, clearOpcPassword: false,
    protocolOptions: device.protocolOptions,
    simulationEnabled: device.simulationEnabled ?? false,
    simulationOptions: device.simulationOptions ?? '',
    revision: device.revision,
    ...patch
  }
}

async function reportBatch(report: Awaited<ReturnType<typeof runBatch>>, okText: string): Promise<void> {
  batchProgress.value = ''
  batchCancel.value = false
  const pending = report.total - report.ok - report.failed.length
  if (report.failed.length === 0) {
    if (report.canceled) ElMessage.info(tr('batchUx.stoppedOnly', [report.ok, report.total, pending]))
    else ElMessage.success(okText)
  } else {
    const summary = summarizeBatch(report, tr('devicesView.kdda4f8'))
    const body = report.canceled ? `${summary}\n\n${tr('batchUx.stoppedSuffix', [pending])}` : summary
    await ElMessageBox.alert(body, tr('devicesView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('devicesView.kcb63c6') })
  }
  await load()
}

async function batchSetEnabled(enabled: boolean): Promise<void> {
  const rows = selected.value.filter(d => canConfigureDevice(d))
  if (!rows.length) return
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (d) => `#${d.id} ${d.name}`, async (d) => { await deviceApi.update(d.id, toDeviceInput(d, { enabled })) }, (p) => {
    batchProgress.value = tr('devicesView.k4c5062', [enabled ? tr('devicesView.kd4e9ca') : tr('devicesView.kd989e5'), p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('devicesView.k312a99', [enabled ? tr('devicesView.kd4e9ca') : tr('devicesView.kd989e5'), report.ok]))
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.filter(d => canConfigureDevice(d))
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(`${tr('devicesView.k072151', [rows.length])}\n\n${tr('actionUx.deleteDeviceCascade')}`, tr('devicesView.k362aed'), { type: 'warning', confirmButtonText: tr('devicesView.k3755f5'), cancelButtonText: tr('devicesView.k4d0b46') })
  } catch { return }
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (d) => `#${d.id} ${d.name}`, async (d) => { await deviceApi.remove(d.id) }, (p) => {
    batchProgress.value = tr('devicesView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('devicesView.k9d4fef', [report.ok]))
}

async function remove(device: Device): Promise<void> {
  actionError.value = ''
  try {
    await ElMessageBox.confirm(`${tr('devicesView.kd54ce8')}\n\n${tr('actionUx.deleteDeviceCascade')}`, tr('ux.tables.confirmDelete', [device.name]), { type: 'warning', confirmButtonText: tr('devicesView.k3755f5'), cancelButtonText: tr('devicesView.k4d0b46') })
    await deviceApi.remove(device.id)
    ElMessage.success(tr('devicesView.kfb5fe1'))
    await load()
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    actionError.value = errorMessage(error, tr('devicesView.k72250c'))
  }
}

function openTest(device: Device): void { testDialog.value?.open(device, acquisitionByDevice.value[device.id]) }

watch([() => route.query.diagnoseId, devices], () => { void openDiagnoseFromQuery() })
async function openDiagnoseFromQuery(): Promise<void> {
  const id = positiveQueryId(route.query.diagnoseId)
  if (!id) return
  let device = devices.value.find(item => item.id === id)
  if (!device) {
    try { device = await deviceApi.get(id) } catch { return }
  }
  if (device && canConfigureDevice(device)) { diagnosticDevice.value = device; diagnosticsVisible.value = true }
}
onBeforeUnmount(() => { loadVersion++; catalogVersion++; certScan++; certController?.abort() })
onMounted(load)
</script>

<style scoped>
.more-caret { margin-left: 4px; }
</style>

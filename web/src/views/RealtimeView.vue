<template>
  <div class="page page--fill page--workspace">
    <div class="monitor-controls">
      <div class="toolbar monitor-filters">
        <el-select v-model="filterDevice" clearable filterable :aria-label="$t('realtimeView.kfb9b2c')" :placeholder="$t('realtimeView.kfb9b2c')" class="device-filter" data-test="device-filter">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable :aria-label="$t('realtimeView.kc23497')" :placeholder="$t('realtimeView.kc23497')" class="tag-search" data-test="realtime-search" />
        <el-select v-model="qualityFilter" size="small" :aria-label="$t('realtimeMonitor.quality')" class="quality-filter" data-test="quality-filter">
          <el-option :label="$t('realtimeView.qualityAll')" value="all" />
          <el-option :label="$t('realtimeView.qualityAbnormal')" value="abnormal" />
          <el-option :label="$t('realtimeView.qualityGood')" value="good" />
        </el-select>
        <el-tag v-if="deviceState === 'offline'" type="warning" closable data-test="realtime-offline-devices" @close="deviceState = ''">{{ $t('realtimeMonitor.offlineDevices') }}</el-tag>
        <el-select v-model="sortOrder" size="small" class="sort-filter" :aria-label="$t('realtimeMonitor.sort')" data-test="realtime-sort">
          <el-option :label="$t('realtimeMonitor.configurationOrder')" value="configuration" />
          <el-option :label="$t('realtimeMonitor.abnormalFirst')" value="abnormal" />
        </el-select>
        <el-button v-if="hasFilters" :icon="Close" size="small" @click="resetFilters" data-test="realtime-reset">{{ $t('realtimeMonitor.clearFilters') }}</el-button>
        <span class="spacer" />
        <el-tag :class="{ 'tag-quiet': hubState === 'connected' }" :type="hubState === 'connected' ? undefined : 'warning'" size="small" data-test="realtime-connection">
          {{ connectionLabel }}
        </el-tag>
        <el-button size="small" :icon="frozen ? VideoPlay : VideoPause" :type="frozen ? 'warning' : 'default'" data-test="freeze-toggle" @click="toggleFreeze">
          {{ frozen ? $t('realtimeView.resume') : $t('realtimeView.pause') }}
        </el-button>
        <el-button size="small" :icon="Refresh" :loading="configLoading || snapshotLoading" :disabled="frozen" data-test="snapshot-refresh" @click="refresh">{{ $t(loadError || configError ? 'realtimeMonitor.retry' : 'realtimeView.ka4fa6a') }}</el-button>
      </div>
      <el-alert v-if="frozen" type="warning" :closable="false" show-icon :title="$t('realtimeView.frozenHint', [formatTime(frozenAt)])" data-test="realtime-frozen">
        <el-button link type="primary" @click="toggleFreeze">{{ $t('realtimeView.resume') }}</el-button>
      </el-alert>
      <div class="muted tiny refresh-status" data-test="realtime-verified">{{ $t('realtimeMonitor.verified', [lastSnapshotAt ? formatTime(lastSnapshotAt) : '--']) }}</div>
    </div>
    <PageStatusBar :issues="pageIssues" @action="onStatusAction" />

    <section class="monitor-results">
      <div class="toolbar">
        <strong>{{ $t('realtimeView.k60a3fe') }}</strong>
        <span class="spacer" />
        <el-tag class="tag-quiet" :type="undefined" size="small">{{ $t('realtimeView.keb7bc0', [totalCount]) }}</el-tag>
        <el-tag v-if="abnormalCount" type="danger" size="small" data-test="abnormal-count">{{ $t('realtimeView.abnormalCount', [abnormalCount]) }}</el-tag>
        <el-tag v-if="scopedAlarmCount" type="danger" size="small" data-test="realtime-alarm-count">{{ $t('realtimeMonitor.alarmCount', [scopedAlarmCount]) }}</el-tag>
      </div>
      <el-table v-if="!narrowMonitor" class="monitor-table" :data="tags" row-key="id" size="small" height="100%" :empty-text="emptyText" :row-class-name="rowClass" data-test="realtime-table">
        <el-table-column :label="$t('realtimeView.k01f2c1')" width="120" show-overflow-tooltip>
          <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
        </el-table-column>
        <el-table-column prop="name" :label="$t('realtimeView.k8cff1c')" min-width="130" show-overflow-tooltip />
        <el-table-column :label="$t('realtimeView.k521523')" width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="value-strong" :class="valueClass(row.id)">
              {{ displayTag(row) }} <span class="muted tiny">{{ row.unit }}</span>
            </span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('realtimeMonitor.quality')" width="120" show-overflow-tooltip>
          <template #default="{ row }">
            <el-tag v-if="unread(row.id)" type="info" size="small" data-test="quality-unread">{{ $t('realtimeView.unread') }}</el-tag>
            <el-tag v-else :class="{ 'tag-quiet': qualityOf(row.id) === 'Good' }" :type="qualityType(qualityOf(row.id))" size="small">
              {{ qualityText(row) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('realtimeMonitor.activeAlarm')" min-width="135" show-overflow-tooltip>
          <template #default="{ row }"><el-button v-if="alarmsFor(row.id).length && canViewAlarms" link type="danger" data-test="realtime-alarm" @click="openAlarms(row)">{{ alarmText(row) }}</el-button><span v-else class="muted tiny">{{ alarmText(row) }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('realtimeMonitor.sampleTime')" width="170" show-overflow-tooltip>
          <template #default="{ row }"><div>{{ formatTime(valueOf(row.id)?.ts) }}</div><el-tag v-if="sampleStale(row)" type="warning" size="small">{{ $t('realtimeMonitor.stale') }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('realtimeView.k412f54')" min-width="160" show-overflow-tooltip>
          <template #default="{ row }"><div>{{ row.description }}</div><div v-if="valueOf(row.id)?.error" class="tiny tag-fail" data-test="realtime-point-error">{{ valueOf(row.id)?.error }}</div></template>
        </el-table-column>
        <el-table-column :label="$t('realtimeView.kf3ea6d')" width="220" fixed="right">
          <template #default="{ row }"><div class="row-actions">
            <el-button v-if="canViewScreens" link data-test="realtime-screen" @click="openScreen(row)">{{ $t('alarmUx.openScreen') }}</el-button>
            <router-link v-if="canViewTrends" :to="{ path: '/trends', query: { tagId: String(row.id) } }" data-test="realtime-trend">{{ $t('realtimeView.viewTrend') }}</router-link>
            <el-button v-if="row.writable && canControlTag(row)" link type="primary" :disabled="frozen" data-test="realtime-write" @click="openWrite(row)">
              {{ $t('realtimeView.k2fb871') }}
            </el-button>
            <span v-else class="muted tiny">{{ row.writable ? $t('realtimeView.kb44182') : $t('realtimeView.kffc1d0') }}</span>
          </div></template>
        </el-table-column>
      </el-table>
      <div v-else class="monitor-mobile-list" data-test="realtime-mobile-list">
        <article v-for="tag in tags" :key="tag.id" class="monitor-point" :class="rowClass({ row: tag })">
          <div class="point-heading"><strong>{{ tag.name }}</strong><span class="point-value" :class="valueClass(tag.id)">{{ displayTag(tag) }} <small>{{ tag.unit }}</small></span></div>
          <div class="point-status"><span>{{ deviceName(tag.deviceId) }}</span><span>{{ $t('realtimeMonitor.quality') }}</span><el-tag :class="{ 'tag-quiet': !unread(tag.id) && qualityOf(tag.id) === 'Good' }" :type="unread(tag.id) ? 'info' : qualityType(qualityOf(tag.id))" size="small">{{ unread(tag.id) ? $t('realtimeView.unread') : qualityText(tag) }}</el-tag><el-tag v-if="sampleStale(tag)" type="warning" size="small">{{ $t('realtimeMonitor.stale') }}</el-tag></div>
          <div class="point-alarm"><span class="muted tiny">{{ $t('realtimeMonitor.activeAlarm') }}</span><el-button v-if="alarmsFor(tag.id).length && canViewAlarms" link type="danger" @click="openAlarms(tag)">{{ alarmText(tag) }}</el-button><span v-else class="muted tiny">{{ alarmText(tag) }}</span></div>
          <p v-if="valueOf(tag.id)?.error" class="tiny tag-fail" data-test="realtime-mobile-error">{{ valueOf(tag.id)?.error }}</p>
          <p v-else-if="tag.description !== tag.name" class="muted tiny">{{ tag.description }}</p>
          <div class="point-footer"><span class="muted tiny">{{ $t('realtimeMonitor.sampleTime') }} {{ formatTime(valueOf(tag.id)?.ts) }}</span><el-button v-if="canViewScreens" link @click="openScreen(tag)">{{ $t('alarmUx.openScreen') }}</el-button><router-link v-if="canViewTrends" :to="{ path: '/trends', query: { tagId: String(tag.id) } }" class="tiny">{{ $t('realtimeView.viewTrend') }}</router-link><el-button v-if="tag.writable && canControlTag(tag)" link type="primary" :disabled="frozen" @click="openWrite(tag)">{{ $t('realtimeView.k2fb871') }}</el-button></div>
        </article>
        <p v-if="!tags.length" class="muted">{{ emptyText }}</p>
      </div>
      <el-pagination :current-page="page" :page-size="100" :total="totalCount" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="tag-pagination" @update:current-page="page = $event" />
    </section>

    <CommandReceipt v-if="receiptId && !writeVisible" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <el-dialog v-model="writeVisible" :title="$t('realtimeView.k859e7c', [writeTag?.name ?? ''])" width="420px" :close-on-click-modal="!writing" :close-on-press-escape="!writing" :show-close="!writing" data-test="realtime-write-dialog">
      <el-form label-width="90px">
        <el-form-item :label="$t('realtimeView.k8b0db6')">
          <span class="value-strong">{{ writeTag ? displayTag(writeTag) : '--' }}{{ writeTag && writeTag.dataType !== 'Bool' && writeTag.dataType !== 'String' ? ` ${writeTag.unit ?? ''}` : '' }}</span>
          <div class="muted tiny">{{ $t('realtimeMonitor.sampleTime') }} {{ formatTime(writeTag ? valueOf(writeTag.id)?.ts : null) }}</div>
        </el-form-item>
        <el-form-item :label="$t('realtimeView.kba60a3')">
          <el-input v-if="writeTag?.dataType === 'String'" v-model="writeText" :disabled="writing || !!writeRequestId" :aria-label="$t('realtimeView.kba60a3')" style="width: 100%" data-test="write-text" />
          <el-switch v-else-if="writeTag?.dataType === 'Bool'" v-model="writeValue" :active-value="1" :inactive-value="0" :disabled="writing || !!writeRequestId" :active-text="$t('realtimeMonitor.bitOne')" :inactive-text="$t('realtimeMonitor.bitZero')" data-test="write-boolean" />
          <el-input-number v-else v-model="writeValue" :disabled="writing || !!writeRequestId" :min="writeTag?.minValue ?? undefined" :max="writeTag?.maxValue ?? undefined" :aria-label="$t('realtimeView.kba60a3')" style="width: 100%" data-test="write-number" />
          <div v-if="writeTag?.dataType === 'String'" class="tiny muted" style="width: 100%; margin-top: 4px" data-test="write-text-hint">
            {{ $t('realtimeView.writeTextHint', [writeTag?.textLength ?? 0]) }} · {{ $t('realtimeView.writeCurrent', [writeCurrentValue, '']) }}
          </div>
          <div v-else-if="writeTag?.dataType !== 'Bool'" class="tiny muted" style="width: 100%; margin-top: 4px" data-test="write-hints">
            {{ $t('realtimeView.writeRange', [writeTag?.minValue ?? '-', writeTag?.maxValue ?? '-', writeTag?.unit ?? '']) }}
            · {{ $t('realtimeView.writeCurrent', [writeCurrentValue, writeTag?.unit ?? '']) }}
          </div>
        </el-form-item>
        <el-alert v-if="writeTag && !unread(writeTag.id) && (sampleStale(writeTag) || qualityOf(writeTag.id) !== 'Good')" type="warning" :closable="false" show-icon :title="$t('realtimeMonitor.writeReferenceWarning')" data-test="write-reference-warning" />
        <el-alert type="warning" :closable="false" show-icon :title="writeRequestId ? $t('realtimeView.ke50c4d') : $t('realtimeView.k7868f7')" />
        <el-alert v-if="writeError" type="error" :closable="false" show-icon :title="writeError" style="margin-top: 8px" data-test="realtime-write-error" />
        <CommandReceipt v-if="receiptId && writeVisible" :id="receiptId" />
      </el-form>
      <template #footer>
        <el-button :disabled="writing" @click="writeVisible = false">{{ $t('realtimeView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="writing" @click="submitWrite">{{ $t('realtimeView.ka1468f') }}</el-button>
      </template>
    </el-dialog>
    <el-dialog v-model="screenPicker" :title="$t('alarmUx.pickScreen')" width="420px" data-test="realtime-screen-picker">
      <div class="screen-picker">
        <el-button v-for="screen in screenMatches" :key="screen.id" data-test="realtime-screen-choice" @click="goScreen(screen.id)">{{ screen.name }}</el-button>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import CommandReceipt from '../components/CommandReceipt.vue'
import { createCommandId } from '../api/commandTypes'
import { Close, Refresh, VideoPause, VideoPlay } from '@element-plus/icons-vue'
import { hubState as realtimeHubState, onHubReconnected, subscribeTags, type TagSubscription } from '../realtime/hub'
import { alarmApi, commandApi, deviceApi, healthApi, realtimeApi, tagApi } from '../api'
import { screenApi } from '../api/screens'
import { collectScreenTags } from '../screens/screenTags'
import ControlConfirmDialog from '../components/ControlConfirmDialog.vue'
import PageStatusBar, { type PageIssue } from '../components/PageStatusBar.vue'
import { controlApi, type ControlCheck } from '../api/control'
import { commandErrorMessage, errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmEvent, Device, Tag, TagQuality, TagValue } from '../types'
import {
  canActOnDevice,
  formatTime,
  formatValue,
  qualityLabels,
  qualityType
} from '../utils'
import { tr } from '../i18n'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { visibleNavItems } from '../nav/catalog'
import { positiveQueryId } from '../nav/query'

const auth = useAuthStore()
const router = useRouter()
// 7 列表格在 769–960px 的窄桌面会被压到不可读；比全局 isMobile 提前切换为卡片列表。
const narrowQuery = window.matchMedia('(max-width: 960px), (pointer: coarse) and (max-width: 1024px)')
const narrowMonitor = ref(narrowQuery.matches)
function onNarrowChange(event: MediaQueryListEvent): void { narrowMonitor.value = event.matches }
const canViewAlarms = computed(() => visibleNavItems(auth.user).some(item => item.path === '/alarms'))
const canViewTrends = computed(() => visibleNavItems(auth.user).some(item => item.path === '/trends'))
const canViewScreens = computed(() => visibleNavItems(auth.user).some(item => item.path === '/screens'))
const alarms = ref<AlarmEvent[]>([]), alarmError = ref(''), alarmsLoaded = ref(false), alarmsCapped = ref(false)
const alarmIndex = computed(() => {
  const index = new Map<number, AlarmEvent[]>()
  for (const alarm of alarms.value) {
    if (!alarm.tagId || alarm.endTs) continue
    index.set(alarm.tagId, [...(index.get(alarm.tagId) ?? []), alarm])
  }
  return index
})
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const totalCount = ref(0)
const abnormalTotal = ref(0)
const deviceIndex = computed(() => new Map(devices.value.map(device => [device.id, device])))
const loadError = ref('')
const configError = ref('')
const snapshotError = ref('')
const configReady = ref(false), configLoading = ref(false), lastSnapshotAt = ref(0), lastConfigAt = ref(0)
const { now } = useNowTicker()
function canControlTag(tag: Tag): boolean {
  return canActOnDevice(auth.user, 'Control', devices.value, tag.deviceId)
}
const route = useRoute()
const initialQuery = route.query
const filterDevice = ref<number | undefined>(positiveQueryId(initialQuery.deviceId))
const deviceState = ref<'offline' | ''>(initialQuery.deviceState === 'offline' ? 'offline' : '')
const attentionDeviceIds = ref<Set<number>>(new Set())
const attentionReady = ref(initialQuery.deviceState !== 'offline')
const attentionError = ref('')
const search = ref(typeof initialQuery.search === 'string' ? initialQuery.search : '')
// 现场排查时先看坏质量：按质量筛选 + 异常行高亮，比逐个扫表快得多
const qualityFilter = ref<'all' | 'abnormal' | 'good'>(
  initialQuery.quality === 'abnormal' || initialQuery.quality === 'good' ? initialQuery.quality : 'all'
)
const sortOrder = ref<'configuration' | 'abnormal'>(initialQuery.sort === 'abnormal' ? 'abnormal' : 'configuration')
watch(() => route.query.deviceId, value => {
  filterDevice.value = positiveQueryId(value)
}, { immediate: true })
watch(() => route.query.deviceState, value => {
  deviceState.value = value === 'offline' ? 'offline' : ''
})
watch(deviceState, () => { if (deviceState.value === 'offline') attentionReady.value = false })
const hasFilters = computed(() => !!filterDevice.value || !!search.value || qualityFilter.value !== 'all' || deviceState.value === 'offline')
function resetFilters(): void { filterDevice.value = undefined; search.value = ''; qualityFilter.value = 'all'; deviceState.value = '' }
// 暂停刷新：数值不断变化时冻结表格，便于读数与截图
const frozen = ref(false)
const frozenAt = ref<number | null>(null), snapshotLoading = ref(false)
let snapshotSeq = 0, disposed = false
const values = reactive<Record<number, TagValue>>({})
// 连接状态由共享连接模块统一维护（disconnected / connecting / connected / reconnecting）
const hubState = realtimeHubState()
const connectionLabel = computed(() => ({
  connected: tr('realtimeMonitor.connection.connected'),
  connecting: tr('realtimeMonitor.connection.connecting'),
  reconnecting: tr('realtimeMonitor.connection.reconnecting'),
  disconnected: tr('realtimeMonitor.connection.disconnected')
})[hubState.value])

let subscription: TagSubscription | undefined
let disposeReconnected: (() => void) | undefined

const page = ref(positiveQueryId(initialQuery.page) ?? 1)
// 筛选、排序和页码都是排查上下文：下钻返回、刷新或分享链接后应回到同一张列表。
function syncUrl(): void {
  if (route.path !== '/realtime') return
  const query = {
    ...route.query,
    deviceId: filterDevice.value ? String(filterDevice.value) : undefined,
    search: search.value || undefined,
    quality: qualityFilter.value === 'all' ? undefined : qualityFilter.value,
    sort: sortOrder.value === 'configuration' ? undefined : sortOrder.value,
    deviceState: deviceState.value === 'offline' ? 'offline' : undefined,
    page: page.value > 1 ? String(page.value) : undefined
  }
  const keys = ['deviceId', 'search', 'quality', 'sort', 'deviceState', 'page'] as const
  if (keys.every(key => query[key] === route.query[key])) return
  void router.replace({ query })
}
watch([filterDevice, search, qualityFilter, sortOrder, deviceState], () => { page.value = 1 }, { flush: 'sync' })
watch([filterDevice, search, qualityFilter, sortOrder, deviceState, page], syncUrl)
watch(totalCount, total => {
  const last = Math.max(1, Math.ceil(total / 100))
  if (page.value > last) page.value = last
})

// 当前筛选范围内质量非良好的测点数（未采集到值的测点也算），用于顶部一眼可见
// 快照整体失败时所有测点都会显示“未读取”，此时异常计数没有参考意义，直接隐藏。
const abnormalCount = computed(() => snapshotError.value ? 0 : abnormalTotal.value)
const staleCount = computed(() => tags.value.filter(sampleStale).length)
// 实时页同时可能出现配置、快照、报警、画面索引和过期数据五类提示：统一交给状态条折叠展示。
const pageIssues = computed<PageIssue[]>(() => {
  const issues: PageIssue[] = []
  if (loadError.value) issues.push({ id: 'load', test: 'realtime-load-error', level: 'error', message: loadError.value, action: tr('realtimeMonitor.retry'), actionTest: 'realtime-load-retry' })
  if (configError.value) issues.push({ id: 'config', test: 'realtime-config-error', level: 'warning', message: configError.value, action: tr('realtimeMonitor.retry'), actionTest: 'realtime-config-retry' })
  if (snapshotError.value) issues.push({ id: 'snapshot', test: 'realtime-snapshot-error', level: 'error', message: snapshotError.value, action: tr('realtimeMonitor.retry'), actionTest: 'realtime-snapshot-retry' })
  if (alarmError.value) issues.push({ id: 'alarms', test: 'realtime-alarm-error', level: 'warning', message: alarmError.value })
  if (alarmsCapped.value && !alarmError.value) issues.push({ id: 'capped', test: 'realtime-alarm-capped', level: 'warning', message: tr('realtimeMonitor.alarmsLimited') })
  if (screenError.value) issues.push({ id: 'screen', test: 'realtime-screen-error', level: 'warning', message: screenError.value })
  if (staleCount.value && !frozen.value) issues.push({ id: 'stale', test: 'realtime-stale', level: 'warning', message: tr('realtimeMonitor.staleCount', [staleCount.value]), action: tr('realtimeMonitor.retry'), actionTest: 'realtime-stale-retry' })
  return issues
})
function onStatusAction(): void { void refresh() }
const scopedAlarmCount = computed(() => tags.value.reduce((total, tag) => total + alarmsFor(tag.id).length, 0))
const emptyText = computed(() => loadError.value || configError.value || attentionError.value || (!configReady.value || deviceState.value === 'offline' && !attentionReady.value ? tr('realtimeMonitor.loading') : hasFilters.value ? tr('realtimeMonitor.noMatch') : tr('realtimeView.k1b4bde')))

function valueOf(tagId: number): TagValue | undefined {
  return values[tagId]
}

function displayTag(tag: Tag): string {
  const value = values[tag.id]
  if (!value) return '--'
  if (tag.dataType === 'String') return value.text ?? '--'
  if (tag.dataType === 'Bool') return tr(value.value !== 0 ? 'realtimeMonitor.bitOne' : 'realtimeMonitor.bitZero')
  if (['Int16', 'UInt16', 'Int32', 'UInt32'].includes(tag.dataType)) return value.value.toLocaleString('en-US', { useGrouping: false, maximumFractionDigits: 0 })
  return formatValue(value.value, 2)
}
function sampleStale(tag: Tag): boolean {
  const value = values[tag.id]
  if (!value) return false
  if (snapshotError.value || lastSnapshotAt.value && now.value - lastSnapshotAt.value > 30_000) return true
  const interval = tag.kind === 'Calculated' ? tag.calculationIntervalMs : deviceIndex.value.get(tag.deviceId ?? 0)?.pollIntervalMs
  return !!interval && now.value - value.ts > Math.max(30_000, interval * 3)
}
function qualityText(tag: Tag): string {
  const quality = qualityOf(tag.id)
  return quality === 'Good' ? tr('realtimeMonitor.good') : qualityLabels[quality]
}
function alarmsFor(tagId: number): AlarmEvent[] { return alarmIndex.value.get(tagId) ?? [] }
function alarmText(tag: Tag): string {
  if (!canViewAlarms.value) return tr('realtimeMonitor.alarmUnavailable')
  if (alarmError.value || !alarmsLoaded.value) return tr('realtimeMonitor.alarmUnknown')
  const events = alarmsFor(tag.id)
  if (!events.length) return alarmsCapped.value ? tr('realtimeMonitor.alarmUnlisted') : tr('realtimeMonitor.noAlarmObserved')
  return tr('realtimeMonitor.alarmCount', [events.length]) + (events.some(alarm => alarm.suppressed || alarm.shelved) ? ` · ${tr('realtimeMonitor.protectedAlarm')}` : '')
}
function openAlarms(tag: Tag): void {
  if (!canViewAlarms.value) return
  const events = alarmsFor(tag.id)
  const state = events.some(alarm => !alarm.suppressed && !alarm.shelved) ? 'active' : events.some(alarm => alarm.suppressed) ? 'suppressed' : 'shelved'
  void router.push({ path: '/alarms', query: { tagId: String(tag.id), state } })
}
const screenPicker = ref(false)
const screenMatches = ref<{ id: number; name: string }[]>([])
const screenError = ref('')
let screenIndex: Map<number, { id: number; name: string }[]> | null = null
function goScreen(id: number): void {
  screenPicker.value = false
  void router.push(`/screens/${id}`)
}
async function openScreen(tag: Tag): Promise<void> {
  screenError.value = ''
  if (!canViewScreens.value) return
  try {
    if (!screenIndex) {
      const published = (await screenApi.list()).filter(screen => screen.publishedRevision != null)
      const loaded = await Promise.all(published.map(async screen => {
        try { return { screen, detail: await screenApi.get(screen.id) } }
        catch { return null }
      }))
      if (published.length && loaded.every(item => item == null)) throw new Error('screen-index')
      const index = new Map<number, { id: number; name: string }[]>()
      for (const item of loaded) {
        if (!item) continue
        const tagsOnScreen = new Set<number>()
        for (const node of item.detail.document.nodes ?? []) collectScreenTags(node, tagsOnScreen)
        for (const tagId of tagsOnScreen) {
          const rows = index.get(tagId) ?? []
          rows.push({ id: item.screen.id, name: item.screen.name })
          index.set(tagId, rows)
        }
      }
      screenIndex = index
    }
  } catch (error) {
    screenIndex = null
    screenError.value = errorMessage(error, tr('alarmUx.screenLookupFailed'))
    return
  }
  const matches = screenIndex.get(tag.id) ?? []
  if (!matches.length) { screenError.value = tr('alarmUx.noScreen'); return }
  if (matches.length === 1) { await router.push(`/screens/${matches[0].id}`); return }
  screenMatches.value = matches
  screenPicker.value = true
}
async function loadAlarms(): Promise<void> {
  if (!canViewAlarms.value) return
  try {
    const result = await alarmApi.query({ activeOnly: true, priorityFirst: true, limit: 2000 })
    if (disposed || frozen.value) return
    alarms.value = result
    alarmsLoaded.value = true
    alarmsCapped.value = result.length >= 2000
    alarmError.value = ''
  } catch (error) {
    if (!disposed) alarmError.value = errorMessage(error, tr('realtimeMonitor.alarmsFailed'))
  }
}

function qualityOf(tagId: number): TagQuality {
  return values[tagId]?.quality ?? 'Bad'
}

function unread(tagId: number): boolean {
  return values[tagId] == null
}

function valueClass(tagId: number): string {
  if (unread(tagId)) return ''
  const quality = qualityOf(tagId)
  if (quality === 'Good') return ''
  if (quality === 'CommFailure') return 'tag-fail'
  return 'tag-bad'
}

// 异常质量整行着色，避免只靠数值颜色在长表里漏看
function rowClass({ row }: { row: Tag }): string {
  if (unread(row.id)) return ''
  const quality = qualityOf(row.id)
  if (quality === 'Good') return ''
  return quality === 'Uncertain' ? 'row-quality-warn' : 'row-quality-bad'
}

function deviceName(id: number | null): string {
  if (id === null) return tr('realtimeView.k4e0387')
  return deviceIndex.value.get(id)?.name ?? `#${id}`
}

async function loadSnapshot(): Promise<void> {
  if (frozen.value || disposed || !configReady.value) return
  const seq = ++snapshotSeq
  snapshotLoading.value = true
  try {
    const ids = tags.value.map(tag => tag.id)
    const snapshot = ids.length ? await realtimeApi.get(ids) : []
    if (seq !== snapshotSeq || disposed || frozen.value) return
    for (const item of snapshot) {
      if (!values[item.tagId] || item.ts >= values[item.tagId].ts) values[item.tagId] = item
    }
    snapshotError.value = ''
    lastSnapshotAt.value = Date.now()

  } catch (error) {
    if (seq !== snapshotSeq || disposed || frozen.value) return
    snapshotError.value = errorMessage(error, tr('realtimeView.k990c88'))
  } finally {
    if (seq === snapshotSeq) snapshotLoading.value = false
  }
}

function toggleFreeze(): void {
  frozen.value = !frozen.value
  if (frozen.value) {
    frozenAt.value = Date.now()
    snapshotSeq++
    snapshotLoading.value = false
  } else frozenAt.value = null
  // 恢复时补一次快照，冻结期间的变化不会丢
  if (!frozen.value) void refresh()
}

function applyValue(value: TagValue): void {
  // 暂停期间丢弃推送，恢复时统一拉快照，避免读数时表格跳动
  if (frozen.value || disposed) return
  if (values[value.tagId] && value.ts < values[value.tagId].ts) return
  values[value.tagId] = value
}

// 共享连接按 tagId 只回传本页订阅的测点，无需再自行过滤；
// 订阅走引用计数，离开页面时释放，不影响其他页面订阅的同一测点。
async function startSubscription(): Promise<void> {
  const next = await subscribeTags(tags.value.map((x) => x.id), applyValue)
  if (disposed) { next.dispose(); return }
  // 先订阅新的再释放旧的：重叠测点不会出现取消/重订抖动
  subscription?.dispose()
  subscription = next
}

const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
const writeVisible = ref(false)
const writing = ref(false)
const writeError = ref('')
const writeTag = ref<Tag | null>(null)
const writeValue = ref<number | undefined>(0)
const writeText = ref('')
const writeRequestId = ref<string | null>(null), receiptId = ref<string | null>(null)

// 写值前给出当前值参考：避免操作人凭记忆改数，也便于判断写入是否真的变化
const writeCurrentValue = computed(() => {
  const tag = writeTag.value
  if (!tag) return '--'
  const value = values[tag.id]
  if (!value) return '--'
  return tag.dataType === 'String' ? (value.text ?? '--')
    : tag.dataType === 'Bool' ? (value.value !== 0 ? '1' : '0')
    : formatValue(value.value, 2)
})

function openWrite(tag: Tag): void {
  if (frozen.value) return
  writeRequestId.value = null; receiptId.value = null; writeError.value = ''
  writeTag.value = tag
  writeValue.value = values[tag.id]?.value ?? tag.minValue ?? 0
  writeText.value = values[tag.id]?.text ?? ''
  writeVisible.value = true
}

async function submitWrite(): Promise<void> {
  const tag = writeTag.value, target = writeValue.value
  if (!tag || frozen.value || writing.value || disposed) return
  if (tag.dataType === 'String') {
    const text = writeText.value
    const bytes = new TextEncoder().encode(text).length
    if (!text || bytes > tag.textLength) {
      writeError.value = tr('realtimeView.writeTextTooLong', [tag.textLength])
      return
    }
    writing.value = true
    writeError.value = ''
    try {
      const requestId = createCommandId()
      const result = await commandApi.write(tag.id, 0, requestId, true, text)
      receiptId.value = result.commandId ?? null
      if (result.success) {
        ElMessage.success(tr('realtimeView.kf2b186', [result.readBackText ?? text]))
        writeVisible.value = false
        await loadSnapshot()
      } else {
        writeError.value = result.message || tr('realtimeView.ke92dc2')
      }
    } catch (error) {
      if (!disposed) writeError.value = commandErrorMessage(error, tr('realtimeView.ke92dc2'))
    } finally {
      writing.value = false
    }
    return
  }
  if (typeof target !== 'number' || !Number.isFinite(target)
    || (['Int16', 'UInt16', 'Int32', 'UInt32'].includes(tag.dataType) && !Number.isInteger(target))
    || (tag.dataType === 'Bool' && target !== 0 && target !== 1)
    || (tag.minValue != null && target < tag.minValue) || (tag.maxValue != null && target > tag.maxValue)) {
    writeError.value = tr('realtimeMonitor.invalidWrite')
    return
  }
  writing.value = true
  writeError.value = ''
  try {
    // 默认静默预检查后直接下发，结果用非阻塞通知呈现；仅控制点显式要求“操作确认”时才弹确认框。
    let check: ControlCheck
    try {
      check = await controlApi.check(tag.id, target)
    } catch (error) {
      // 预检查失败说明命令还没下发，明确说出来，操作员才敢直接重试
      if (!disposed) writeError.value = tr('commandUx.checkFailed', [errorMessage(error, tr('controlConfirmDialog.k831e7c'))])
      return
    }
    if (check.refusals.length) { writeError.value = check.refusals.join('；'); return }
    const needsConfirm = !!check.requireConfirm
    if (needsConfirm && !writeRequestId.value && !await confirmDialog.value?.open(tag.id, target, tr('realtimeView.k71fb1d', [tag.name]), check)) return
    if (disposed || frozen.value) return
    writeRequestId.value ??= createCommandId()
    const result = await commandApi.write(tag.id, target, writeRequestId.value, needsConfirm)
    receiptId.value = result.commandId ?? null
    if (result.pending) {
      ElMessage.warning(tr('realtimeView.kf1787c')); writeVisible.value = false
    } else if (result.success) {
      ElMessage.success(tr('realtimeView.kf2b186', [formatValue(result.readBack)]))
      writeVisible.value = false
      await loadSnapshot()
    } else {
      writeError.value = result.message || tr('realtimeView.ke92dc2')
    }
  } catch (error) {
    if (disposed) return
    if (devices.value.find(x => x.id === tag.deviceId)?.gatewayId != null) receiptId.value = writeRequestId.value
    // 超时/断网时命令可能已经下发，不能只说「写入失败，请重试」
    writeError.value = commandErrorMessage(error, tr('realtimeView.ke92dc2'))
  } finally {
    writing.value = false
  }
}

async function loadAttention(): Promise<void> {
  if (deviceState.value !== 'offline') { attentionReady.value = true; attentionError.value = ''; return }
  attentionReady.value = false
  try {
    const health = await healthApi.get()
    if (disposed) return
    attentionDeviceIds.value = new Set(health.devices.filter(device => !device.online || !!device.error).map(device => device.deviceId))
    attentionError.value = ''
  } catch (error) {
    if (disposed) return
    attentionDeviceIds.value = new Set()
    attentionError.value = errorMessage(error, tr('realtimeMonitor.offlineDevicesFailed'))
  } finally {
    if (!disposed) attentionReady.value = true
  }
}

function handleConfigFailure(error: unknown, first: boolean, fallback: string): void {
  if (disposed) return
  const detail = errorMessage(error, fallback)
  if (first) {
    loadError.value = detail
    configError.value = ''
    return
  }
  configError.value = tr('realtimeMonitor.configRefreshFailed', [
    detail,
    lastConfigAt.value ? formatTime(lastConfigAt.value) : '--'
  ])
  loadError.value = ''
}

async function refreshData(): Promise<void> {
  if (disposed || frozen.value) return
  const first = !configReady.value
  if (first) configLoading.value = true
  try {
    if (!devices.value.length) {
      try { devices.value = await deviceApi.list() }
      catch (error) { handleConfigFailure(error, first, tr('realtimeView.k9060ce')); return }
    }
    await loadAttention()
    if (disposed || frozen.value) return
    if (deviceState.value === 'offline' && !attentionReady.value) return
    const offlineIds = deviceState.value === 'offline' ? [...attentionDeviceIds.value] : undefined
    let result: Awaited<ReturnType<typeof tagApi.page>>
    try {
      result = await tagApi.page({
        deviceId: filterDevice.value,
        search: search.value.trim() || undefined,
        quality: qualityFilter.value === 'all' ? undefined : qualityFilter.value,
        sort: sortOrder.value === 'abnormal' ? 'abnormal' : undefined,
        deviceIds: offlineIds ? (offlineIds.length ? offlineIds : [0]) : undefined,
        skip: (page.value - 1) * 100,
        take: 100
      })
    } catch (error) {
      handleConfigFailure(error, first, tr('realtimeView.k9060ce'))
      return
    }
    if (disposed || frozen.value) return
    tags.value = result.items
    totalCount.value = result.total
    abnormalTotal.value = result.abnormal
    configReady.value = true
    lastConfigAt.value = Date.now()
    configError.value = ''
    loadError.value = ''
    await startSubscription()
    await Promise.all([loadSnapshot(), loadAlarms()])
  } finally { if (first) configLoading.value = false }
}

const { run: refresh } = usePolling(refreshData, 10_000, { immediate: true, enabled: () => !frozen.value })
let searchTimer: number | undefined
watch([filterDevice, qualityFilter, sortOrder, deviceState, page], () => {
  if (!frozen.value && configReady.value) void refresh()
})
// 逐字输入不要每个字符都打一次 /tags/page；在途请求由 usePolling 排队补跑，最终条件一定生效。
watch(search, () => {
  if (searchTimer) window.clearTimeout(searchTimer)
  searchTimer = window.setTimeout(() => {
    searchTimer = undefined
    if (!frozen.value && configReady.value) void refresh()
  }, 250)
})

onMounted(() => {
  narrowQuery.addEventListener('change', onNarrowChange)
  // 重连后补拉快照，补齐断线期间缺失的值（重新订阅由共享连接模块负责）
  disposeReconnected = onHubReconnected(() => {
    void refresh()
  })
})

onBeforeUnmount(() => {
  disposed = true
  snapshotSeq++
  narrowQuery.removeEventListener('change', onNarrowChange)
  if (searchTimer) window.clearTimeout(searchTimer)
  subscription?.dispose()
  disposeReconnected?.()
})
</script>

<style scoped>
.screen-picker { display: flex; flex-direction: column; align-items: stretch; gap: 8px; }
.refresh-status { margin-top: 8px; overflow-wrap: anywhere; }
.monitor-controls { border-bottom: 1px solid var(--scada-border); padding-bottom: 14px; margin-bottom: 14px; }
.monitor-results { min-width: 0; flex: 1; min-height: 0; display: flex; flex-direction: column; }
.monitor-results > .el-table { flex: 1; min-height: 0; }
.device-filter { width: 180px; }
.tag-search { width: 200px; }
.quality-filter, .sort-filter { width: 130px; }
.row-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; }
.row-actions .el-button { margin-left: 0; }
.monitor-mobile-list { display: block; }
.monitor-point { padding: 14px 0; border-bottom: 1px solid var(--scada-border); }
.point-heading, .point-status, .point-footer, .point-alarm { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; min-width: 0; }
.point-heading { justify-content: space-between; }
.point-heading strong { font-size: 13px; overflow-wrap: anywhere; }
.point-value { font-family: var(--scada-font-mono); font-size: 18px; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.point-value small { font-size: 12px; color: var(--scada-text-muted); }
.point-status, .point-footer, .point-alarm { margin-top: 8px; font-size: 12px; }
.point-status > span:first-child, .point-footer > span:first-child { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.monitor-point p { margin: 8px 0; overflow-wrap: anywhere; }
.monitor-point .el-button { white-space: normal; height: auto; }
@media (max-width: 767px) {
  .monitor-filters { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
  .device-filter, .tag-search, .quality-filter, .sort-filter { width: 100%; min-width: 0; }
  .monitor-filters .spacer { display: none; }
  .monitor-filters .el-button { margin-left: 0; height: auto; min-height: 32px; white-space: normal; }
  .monitor-filters [data-test='realtime-connection'] { justify-self: start; max-width: 100%; }
  .point-footer .el-button { margin-left: 0; }
}

/* 异常质量的整行着色：长表里比只看数值颜色更容易发现 */
:deep(.row-quality-bad) {
  background: rgba(245, 108, 108, 0.10);
}
:deep(.row-quality-warn) {
  background: rgba(230, 162, 60, 0.10);
}
</style>

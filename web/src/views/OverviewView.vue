<template>
  <div class="page">
    <div class="overview-refresh" data-test="overview-freshness" :class="{ 'tag-bad': dataStale }">
      <span>{{ $t('overviewStatus.updated', [lastUpdated ? formatTime(lastUpdated) : $t('overviewStatus.unknown')]) }}</span>
      <el-tag v-if="dataStale" type="warning" size="small" data-test="overview-stale">{{ $t('overviewStatus.stale') }}</el-tag>
      <span v-else class="muted tiny">{{ $t('overviewView.kc2d817') }}</span>
      <span class="spacer" />
      <el-tooltip :content="$t('overviewView.k38108e')">
        <el-button :icon="Refresh" :loading="loading" :aria-label="$t('overviewView.k38108e')" circle size="small" data-test="overview-refresh" @click="refresh" />
      </el-tooltip>
    </div>
    <el-alert v-if="loadError" type="error" :closable="false" show-icon :title="loadError" data-test="overview-load-error" />
    <el-alert v-if="alarmListError" type="error" :closable="false" show-icon :title="alarmListError" data-test="overview-list-error" />
    <el-alert
      v-if="summaryError"
      type="warning"
      :closable="false"
      show-icon
      :title="summaryError"
      :description="$t('overviewView.summaryFallback')"
      data-test="overview-summary-error"
    />
    <AlarmFloodBanner :summary="alarmSummary" compact :review="canGo('/alarms')" data-test="overview-flood" @review="go('/alarms')" />
    <el-alert v-if="health?.warnings?.length" type="warning" :closable="false" show-icon :title="health.warnings.map(warning => warning.message).join(' · ')" />
    <SetupGuide v-if="can(auth.user, 'Configure')" />
    <div class="card-grid operational-grid">
      <div
        class="stat-card"
        :class="{ clickable: canGo('/realtime'), 'is-warn': devWarn && !devAlarm, 'is-alarm': devAlarm }"
        v-bind="clickableAttrs('/realtime')"
        data-test="stat-devices"
        @click="go('/realtime')"
        @keydown.enter="go('/realtime')"
      >
        <div class="label">{{ $t('overviewView.k99828c') }}</div>
        <div class="value" :class="devAlarm ? 'tag-fail' : devWarn ? 'tag-bad' : ''">{{ health?.onlineDevices ?? '--' }} / {{ health?.totalDevices ?? '--' }}</div>
        <div class="sub">{{ !health ? $t('overviewStatus.unknown') : loadError ? $t('overviewStatus.stale') : offlineCount > 0 ? $t('overviewView.offlineCount', [offlineCount]) : $t('overviewView.kc64267') }}</div>
      </div>
      <div
        class="stat-card"
        :class="{ clickable: canGo('/alarms'), 'is-alarm': (unackCount ?? 0) > 0 }"
        v-bind="clickableAttrs('/alarms')"
        data-test="stat-alarms"
        @click="openUnackAlarms"
        @keydown.enter="openUnackAlarms"
      >
        <div class="label">{{ $t('overviewStatus.activeUnack') }}</div>
        <div class="value" :class="(unackCount ?? 0) > 0 ? 'tag-fail' : ''">{{ unackCount ?? '--' }}</div>
        <div class="sub">{{ $t('overviewView.ka84899', [activeAlarmTotal ?? '--']) }}<span v-if="summaryError"> · {{ $t('overviewStatus.loadedOnly') }}</span></div>
      </div>
      <div class="stat-card" :class="{ 'is-warn': (alarmSummary?.rtnUnack ?? 0) > 0 }" data-test="stat-returned">
        <div class="label">{{ $t('overviewStatus.returned') }}</div>
        <div class="value">{{ alarmSummary?.rtnUnack ?? '--' }}</div>
        <el-button v-if="canGo('/alarms')" link type="primary" @click="router.push({ path: '/alarms', query: { state: 'rtnUnack' } })">{{ $t('overviewStatus.review') }}</el-button>
      </div>
      <div class="stat-card" :class="{ 'is-warn': floodActive || (alarmSummary?.suppressed ?? 0) > 0 }" data-test="stat-protection">
        <div class="label">{{ $t('overviewStatus.protection') }}</div>
        <div class="value">{{ alarmSummary?.suppressed ?? '--' }} / {{ alarmSummary?.shelved ?? '--' }}</div>
        <el-button v-if="canGo('/alarms')" link type="primary" @click="router.push({ path: '/alarms', query: { state: 'suppressed' } })">{{ floodActive ? $t('overviewStatus.flood') : $t('overviewStatus.review') }}</el-button>
      </div>
    </div>
    <p class="tiny muted alarm-scope" data-test="overview-alarm-scope">{{ $t('overviewStatus.alarmScope') }}</p>

    <section class="overview-section" data-test="overview-alarm-section">
      <div class="toolbar">
        <strong>{{ $t('overviewView.k855fec') }}</strong>
        <span class="muted tiny">{{ $t('overviewStatus.listed', [activeAlarms.length, activeAlarmTotal ?? '--']) }}</span>
        <span class="spacer" />
        <el-button v-if="canGo('/alarms')" size="small" :icon="ArrowRight" @click="go('/alarms')">{{ $t('overviewView.ked2172') }}</el-button>
      </div>
      <el-table class="desktop-table" :data="activeAlarms" size="small" :empty-text="alarmEmptyText" data-test="overview-alarms">
        <el-table-column :label="$t('overviewView.k254849')" width="90">
          <template #default="{ row }"><el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.kf3dd64')" min-width="200">
          <template #default="{ row }"><div>{{ row.message }}</div><span class="muted tiny">{{ alarmSource(row) }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('overviewStatus.triggerValue')" width="130">
          <template #default="{ row }">{{ alarmValue(row) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.ke8868a')" width="165">
          <template #default="{ row }">{{ formatTime(row.startTs) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewStatus.state')" width="95">
          <template #default="{ row }"><el-tag :type="row.ackTs ? 'info' : 'danger'" size="small">{{ $t(row.ackTs ? 'overviewStatus.acked' : 'overviewView.k0642fa') }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('overviewStatus.locate')" width="85">
          <template #default="{ row }"><el-button v-if="row.tagId && canGo('/trends')" link type="primary" :icon="TrendCharts" @click="openTrend(row)">{{ $t('overviewStatus.trend') }}</el-button></template>
        </el-table-column>
      </el-table>
      <div class="mobile-list" data-test="overview-alarm-cards">
        <div v-for="alarm in activeAlarms" :key="alarm.id" class="mobile-row">
          <div class="row-heading"><el-tag :type="levelType(alarm.level)" size="small">{{ levelLabel(alarm.level) }}</el-tag><strong>{{ alarm.message }}</strong></div>
          <div class="row-detail"><span>{{ alarmSource(alarm) }}</span><span>{{ alarmValue(alarm) }}</span></div>
          <div class="row-detail"><span>{{ formatTime(alarm.startTs) }}</span><el-tag :type="alarm.ackTs ? 'info' : 'danger'" size="small">{{ $t(alarm.ackTs ? 'overviewStatus.acked' : 'overviewView.k0642fa') }}</el-tag><el-button v-if="alarm.tagId && canGo('/trends')" link type="primary" :icon="TrendCharts" @click="openTrend(alarm)">{{ $t('overviewStatus.trend') }}</el-button></div>
        </div>
        <p v-if="!activeAlarms.length" class="muted tiny">{{ alarmEmptyText }}</p>
      </div>
    </section>

    <section class="overview-section" data-test="overview-device-section">
      <div class="toolbar">
        <strong>{{ $t('overviewView.kd97d61') }}</strong>
        <span class="muted tiny">{{ healthUpdatedAt ? formatTime(healthUpdatedAt) : $t('overviewStatus.unknown') }}</span>
        <span class="spacer" />
        <el-switch v-model="abnormalOnly" :active-text="$t('overviewStatus.abnormalOnly')" data-test="overview-device-filter" />
      </div>
      <el-table class="desktop-table" :data="visibleDevices" size="small" max-height="300" :empty-text="deviceEmptyText" data-test="overview-devices">
        <el-table-column :label="$t('overviewView.k01f2c1')" min-width="150">
          <template #default="{ row }"><el-button v-if="canGo('/realtime')" link type="primary" @click="openDevice(row.deviceId)">{{ row.name }}</el-button><span v-else>{{ row.name }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k62e951')" width="110">
          <template #default="{ row }"><el-tag :type="loadError ? 'info' : row.online ? 'success' : 'danger'" size="small">{{ loadError ? $t('overviewStatus.stale') : $t(row.online ? 'overviewView.k0373ff' : 'overviewView.k211357') }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k014935')" min-width="165"><template #default="{ row }">{{ row.lastPollTs ? formatTime(row.lastPollTs) : '--' }}</template></el-table-column>
        <el-table-column :label="$t('overviewView.k8a12b1')" width="100"><template #default="{ row }">{{ row.lastCycleMs != null ? row.lastCycleMs.toFixed(2) + ' ms' : '--' }}</template></el-table-column>
        <el-table-column prop="commFailures" :label="$t('overviewView.k6ac251')" width="100" />
        <el-table-column :label="$t('overviewView.ka38a81')" min-width="180"><template #default="{ row }"><span class="tiny">{{ row.error ?? '--' }}</span></template></el-table-column>
      </el-table>
      <div class="mobile-list" data-test="overview-device-cards">
        <div v-for="device in visibleDevices" :key="device.deviceId" class="mobile-row">
          <div class="row-heading"><el-button v-if="canGo('/realtime')" link type="primary" @click="openDevice(device.deviceId)">{{ device.name }}</el-button><strong v-else>{{ device.name }}</strong><el-tag :type="loadError ? 'info' : device.online ? 'success' : 'danger'" size="small">{{ loadError ? $t('overviewStatus.stale') : $t(device.online ? 'overviewView.k0373ff' : 'overviewView.k211357') }}</el-tag></div>
          <p v-if="device.error" class="tiny tag-fail">{{ device.error }}</p>
          <div class="muted tiny">{{ $t('overviewView.k014935') }} {{ device.lastPollTs ? formatTime(device.lastPollTs) : '--' }}</div>
        </div>
        <p v-if="!visibleDevices.length" class="muted tiny">{{ deviceEmptyText }}</p>
      </div>
    </section>

    <details class="overview-section" data-test="overview-maintenance">
      <summary>{{ $t('overviewStatus.maintenance') }}</summary>
      <div class="card-grid maintenance-grid">
      <div
        class="stat-card"
        :class="{ clickable: canGo('/realtime'), 'is-warn': backlogWarn }"
        v-bind="clickableAttrs('/realtime')"
        data-test="stat-tags"
        @click="go('/realtime')"
        @keydown.enter="go('/realtime')"
      >
        <div class="label">{{ $t('overviewView.k674e52') }}</div>
        <div class="value">{{ health?.configuredTags ?? '--' }}</div>
        <div class="sub">{{ $t('overviewView.k8e8615', [health?.busBacklog ?? '--']) }}</div>
      </div>
      <div
        class="stat-card"
        :class="{ clickable: canGo('/system'), 'is-alarm': diskWarn }"
        v-bind="clickableAttrs('/system')"
        data-test="stat-storage"
        @click="go('/system')"
        @keydown.enter="go('/system')"
      >
        <div class="label">{{ $t('overviewView.k4d5c56') }}</div>
        <div class="value">{{ health ? formatBytes(health.storage.historyBytes) : '--' }}</div>
        <div class="sub" :class="diskWarn ? 'tag-fail' : ''">{{ $t('overviewView.kcc8821', [health?.disk.freeGb ?? '-']) }}</div>
      </div>
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k66836c') }}</div>
        <div class="value">{{ uptime }}</div>
        <div class="sub">{{ health?.environment ?? '-' }}</div>
      </div>
      </div>
    </details>

    <details class="overview-section quick-panel" data-test="overview-quick">
      <summary>{{ $t('nav.quickEntries') }}</summary>
      <div class="toolbar">
        <span class="spacer" />
        <el-button size="small" data-test="overview-open-palette" @click="openPalette">
          <el-icon><Search /></el-icon>
          {{ $t('nav.search') }}
          <kbd class="mini-kbd">Ctrl+K</kbd>
        </el-button>
      </div>
      <div class="quick-grid">
        <div
          v-for="item in quickItems"
          :key="item.path"
          class="quick-card"
        >
          <button type="button" class="quick-link" :data-test="'overview-quick-' + item.id" @click="router.push(item.path)">
            <el-icon :size="20"><component :is="item.icon" /></el-icon>
            <span>{{ $t(item.titleKey) }}</span>
          </button>
          <el-button
            text
            size="small"
            class="pin"
            :class="{ on: favSet.has(item.path) }"
            :aria-label="$t(favSet.has(item.path) ? 'nav.removeFavorite' : 'nav.addFavorite', [$t(item.titleKey)])"
            :title="$t(favSet.has(item.path) ? 'nav.removeFavorite' : 'nav.addFavorite', [$t(item.titleKey)])"
            :data-test="'overview-pin-' + item.id"
            @click.stop="togglePin(item.path)"
          >
            <el-icon><StarFilled v-if="favSet.has(item.path)" /><Star v-else /></el-icon>
          </el-button>
        </div>
      </div>
      <div v-if="favoriteItems.length" class="sub-section">
        <div class="sub-title">{{ $t('nav.favorites') }}</div>
        <div class="chip-row">
          <el-button
            v-for="item in favoriteItems"
            :key="'fav-' + item.path"
            size="small"
            round
            :data-test="'overview-fav-' + item.id"
            @click="router.push(item.path)"
          >{{ $t(item.titleKey) }}</el-button>
        </div>
      </div>
      <div v-if="recentItems.length" class="sub-section">
        <div class="sub-title">{{ $t('nav.recent') }}</div>
        <div class="chip-row">
          <el-button
            v-for="item in recentItems"
            :key="'r-' + item.path"
            size="small"
            round
            plain
            :data-test="'overview-recent-' + item.id"
            @click="router.push(item.path)"
          >{{ $t(item.titleKey) }}</el-button>
        </div>
      </div>
    </details>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ArrowRight, Refresh, Search, Star, StarFilled, TrendCharts } from '@element-plus/icons-vue'
import { alarmApi, healthApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmEvent, AlarmSummary, Health, Tag } from '../types'
import { formatBytes, formatTime, formatValue, levelLabel, levelType } from '../utils'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { findNavByPath, visibleNavItems, type NavItemDef } from '../nav/catalog'
import { loadFavorites, toggleFavorite } from '../nav/favorites'
import { loadRecents } from '../nav/recents'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { can } from '../access/policies'
import AlarmFloodBanner from '../components/AlarmFloodBanner.vue'
import SetupGuide from '../components/SetupGuide.vue'

const router = useRouter()
const auth = useAuthStore()
const health = ref<Health | null>(null)
const alarms = ref<AlarmEvent[]>([])
const loadError = ref('')
const alarmListError = ref('')
const loading = ref(true)
const healthUpdatedAt = ref(0), alarmsUpdatedAt = ref(0), summaryUpdatedAt = ref(0)
const { now } = useNowTicker()
const abnormalOnly = ref(true)
const tags = ref<Tag[]>([])
const tagIndex = computed(() => new Map(tags.value.map(tag => [tag.id, tag])))
const favPaths = ref<string[]>([])
const recentPaths = ref<string[]>([])

const allowed = computed(() => visibleNavItems(auth.user))

// 卡片跳转只允许进入当前用户可见的页面，无权限时保持纯展示（不加光标、不响应键盘）
function canGo(path: string): boolean {
  return allowed.value.some((item) => item.path === path)
}
function go(path: string): void {
  if (canGo(path)) void router.push(path)
}
function openUnackAlarms(): void {
  if (canGo('/alarms')) void router.push({ path: '/alarms', query: { state: 'unack' } })
}
function clickableAttrs(path: string): Record<string, string | number> {
  return canGo(path) ? { role: 'button', tabindex: 0 } : {}
}
const favSet = computed(() => new Set(favPaths.value))
const quickItems = computed(() => allowed.value.filter((item) => item.quick && item.path !== '/overview'))
const favoriteItems = computed(() =>
  favPaths.value.map((p) => findNavByPath(p)).filter((x): x is NavItemDef => !!x && x.path !== '/overview' && allowed.value.some((a) => a.path === x.path))
)
const recentItems = computed(() =>
  recentPaths.value
    .map((p) => findNavByPath(p))
    .filter((x): x is NavItemDef => !!x && allowed.value.some((a) => a.path === x.path))
    .filter((x) => x.path !== '/overview' && !favSet.value.has(x.path))
    .slice(0, 6)
)

function refreshNavLists(): void {
  const name = auth.user?.userName
  favPaths.value = loadFavorites(name)
  recentPaths.value = loadRecents(name)
}
function togglePin(path: string): void {
  favPaths.value = toggleFavorite(auth.user?.userName, path)
}
function openPalette(): void {
  window.dispatchEvent(new Event('scada-open-palette'))
}

const levelRank: Record<string, number> = { Critical: 0, Warning: 1, Info: 2 }
const activeAlarms = computed(() =>
  [...alarms.value.filter((alarm) => !alarm.endTs && !alarm.shelved && !alarm.suppressed)]
    .sort((a, b) => (levelRank[a.level] ?? 9) - (levelRank[b.level] ?? 9) || b.startTs - a.startTs)
    .slice(0, 10)
)
// 数字取服务端全量统计；summary 拉取失败时退回「已加载这部分」的估算，至少不会显示 0
const alarmSummary = ref<AlarmSummary | null>(null)
const summaryError = ref('')
// The table only shows the first 10. A failed summary must not count that slice as the plant total.
const loadedActive = computed(() => alarms.value.filter((alarm) => !alarm.endTs && !alarm.shelved && !alarm.suppressed))
const unackCount = computed(() => {
  const summary = alarmSummary.value
  if (!summaryError.value && summary) return summary.unack
  return alarmsUpdatedAt.value ? loadedActive.value.filter((alarm) => !alarm.ackTs).length : null
})
const activeAlarmTotal = computed(() => {
  const summary = alarmSummary.value
  if (!summaryError.value && summary) return summary.unack + summary.acked
  return alarmsUpdatedAt.value ? loadedActive.value.length : null
})
const floodActive = computed(() => !!(alarmSummary.value?.floodGuard?.anyInFlood || alarmSummary.value?.flood?.inFlood))
const lastUpdated = computed(() => Math.min(healthUpdatedAt.value, alarmsUpdatedAt.value, summaryUpdatedAt.value))
const dataStale = computed(() => !!(loadError.value || alarmListError.value || summaryError.value) || !!lastUpdated.value && now.value - lastUpdated.value > 15_000)
const alarmEmptyText = computed(() => alarmListError.value || loadError.value || (!alarmsUpdatedAt.value ? tr('overviewStatus.unknown') : tr('overviewView.k996b9d')))
const deviceEmptyText = computed(() => loadError.value || (!health.value ? tr('overviewStatus.unknown') : tr(abnormalOnly.value ? 'overviewStatus.noDeviceIssues' : 'overviewView.k2b9379')))
const visibleDevices = computed(() => [...(health.value?.devices ?? [])]
  .filter(device => !abnormalOnly.value || !device.online || !!device.error)
  .sort((left, right) => Number(left.online) - Number(right.online) || Number(!!right.error) - Number(!!left.error) || left.name.localeCompare(right.name)))
function alarmSource(alarm: AlarmEvent): string {
  const tag = alarm.tagId ? tagIndex.value.get(alarm.tagId) : undefined
  const deviceId = alarm.deviceId ?? tag?.deviceId
  const device = health.value?.devices.find(item => item.deviceId === deviceId)
  return [device?.name ?? (deviceId ? `#${deviceId}` : ''), tag?.name ?? (alarm.tagId ? `#${alarm.tagId}` : '')].filter(Boolean).join(' / ') || tr('overviewStatus.systemEvent')
}
function alarmValue(alarm: AlarmEvent): string {
  return [formatValue(alarm.value), alarm.tagId ? tagIndex.value.get(alarm.tagId)?.unit : ''].filter(Boolean).join(' ')
}
function openDevice(deviceId: number): void {
  if (canGo('/realtime')) void router.push({ path: '/realtime', query: { deviceId: String(deviceId) } })
}
function openTrend(alarm: AlarmEvent): void {
  if (alarm.tagId && canGo('/trends')) void router.push({ path: '/trends', query: { tagId: String(alarm.tagId) } })
}
const offlineCount = computed(() => {
  const h = health.value
  if (!h) return 0
  return Math.max(0, h.totalDevices - h.onlineDevices)
})
// 单台离线在现场很常见，只有全部设备离线才算严重
const devWarn = computed(() => offlineCount.value > 0)
const devAlarm = computed(() => {
  const h = health.value
  return !!h && h.totalDevices > 0 && h.onlineDevices === 0
})
const backlogWarn = computed(() => (health.value?.busBacklog ?? 0) > 0)
const diskWarn = computed(() => {
  const free = health.value?.disk.freeGb
  return free != null && free < 5
})
const uptime = computed(() => {
  if (!health.value) return '--'
  const seconds = health.value.uptimeSeconds
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days > 0) return tr('overviewView.k4ed72e', [days, hours])
  if (hours > 0) return tr('overviewView.k165ffa', [hours, minutes])
  return tr('overviewView.uptimeMinutes', [minutes])
})

async function load(): Promise<void> {
  loading.value = true
  try {
    await Promise.all([
      healthApi.get().then(value => {
        health.value = value
        healthUpdatedAt.value = Date.now()
        loadError.value = ''
      }, error => { loadError.value = errorMessage(error, tr('overviewView.k6ac251')) }),
      alarmApi.query({ activeOnly: true, priorityFirst: true, includeShelved: false, limit: 200 }).then(value => {
        alarms.value = value
        alarmsUpdatedAt.value = Date.now()
        alarmListError.value = ''
      }, error => { alarmListError.value = errorMessage(error, tr('overviewView.alarmListFailed')) }),
      alarmApi.summary().then(
        value => {
          alarmSummary.value = value
          summaryUpdatedAt.value = Date.now()
          summaryError.value = ''
        },
        error => {
          alarmSummary.value = null
          summaryError.value = errorMessage(error, tr('overviewView.summaryFailed'))
        }
      )
    ])
  } finally {
    loading.value = false

  }
}

// 概览是常驻大屏页：5s 兜底轮询，切到后台标签时暂停，回到前台立刻补一次。
const { run: refresh } = usePolling(load, 5000, { immediate: true })

onMounted(async () => {
  refreshNavLists()
  window.addEventListener('scada-nav-lists-changed', refreshNavLists)
  try { tags.value = await tagApi.list() } catch { tags.value = [] }
})

onBeforeUnmount(() => {
  window.removeEventListener('scada-nav-lists-changed', refreshNavLists)
})
</script>

<style scoped>
.overview-refresh { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; font-size: 12px; }
.operational-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.alarm-scope { margin: 8px 0 12px; line-height: 1.6; overflow-wrap: anywhere; }
.overview-section { min-width: 0; border-top: 1px solid var(--scada-border); padding: 14px 0; }
.overview-section summary { cursor: pointer; font-size: 13px; font-weight: 600; padding: 4px 0; }
.maintenance-grid { margin-top: 12px; }
.mobile-list { display: none; }
.stat-card { min-width: 0; }
.stat-card .label, .stat-card .value, .stat-card .sub { letter-spacing: 0; overflow-wrap: anywhere; }
.stat-card .value { font-size: 24px; }
.stat-card > .el-button { margin-top: 5px; }
.toolbar { flex-wrap: wrap; }
.row-heading { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; }
.row-heading strong { flex: 1; min-width: 0; overflow-wrap: anywhere; font-size: 13px; }
.row-detail { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-top: 8px; font-size: 12px; }
.row-detail > span:first-child { flex: 1; min-width: 0; overflow-wrap: anywhere; }
.mobile-row { padding: 12px 0; border-bottom: 1px solid var(--scada-border); }
.mobile-row:last-child { border-bottom: 0; }
.mobile-row .el-button { white-space: normal; height: auto; text-align: left; overflow-wrap: anywhere; }
.mobile-row p { overflow-wrap: anywhere; }
.quick-panel { margin-top: 0; }
.quick-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(140px, 1fr));
  gap: 10px;
  padding: 4px 0 8px;
}
.quick-card {
  position: relative;
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 8px;
  padding: 14px 14px 12px;
  border-radius: 6px;
  border: 1px solid var(--scada-border);
  background: var(--scada-panel-muted);
  color: var(--scada-text);
  cursor: pointer;
  text-align: left;
  font-size: 13px;
  font-weight: 500;
  transition: border-color 0.15s, box-shadow 0.15s;
}
.quick-card:hover {
  border-color: var(--scada-accent);
  box-shadow: var(--scada-shadow-sm);
}
.quick-card .el-icon { color: var(--scada-accent); }
.quick-link { display: flex; align-items: center; gap: 8px; width: 100%; padding: 0 18px 0 0; background: none; border: 0; color: inherit; font: inherit; cursor: pointer; text-align: left; }
.quick-card .pin {
  position: absolute;
  top: 4px;
  right: 4px;
  color: var(--scada-text-muted);
}
.quick-card .pin.on { color: #f5a623; }
.sub-section { margin-top: 10px; }
.sub-title {
  font-size: 12px;
  color: var(--scada-text-muted);
  margin-bottom: 6px;
  font-weight: 600;
}
.chip-row { display: flex; flex-wrap: wrap; gap: 8px; }
.mini-kbd {
  margin-left: 6px;
  font: 10px/1.2 ui-monospace, SFMono-Regular, Menlo, monospace;
  padding: 1px 4px;
  border-radius: 3px;
  border: 1px solid var(--scada-border);
  color: var(--scada-text-muted);
}
/* 可点击卡片：给出可操作提示，避免用户以为只是展示 */
.stat-card.clickable {
  cursor: pointer;
  transition: border-color 0.15s, box-shadow 0.15s;
}
.stat-card.clickable:hover,
.stat-card.clickable:focus-visible {
  border-color: var(--scada-accent);
  box-shadow: var(--scada-shadow-sm);
  outline: none;
}
/* 异常阈值：左侧色条 + 数字着色，一眼看出哪块有问题 */
.stat-card.is-warn {
  border-left: 3px solid #d48806;
}
.stat-card.is-alarm {
  border-left: 3px solid #d4380d;
}
@media (max-width: 767px) {
  .operational-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
  .stat-card { padding: 12px; }
  .stat-card .value { font-size: 22px; }
  .desktop-table { display: none; }
  .mobile-list { display: block; }
  .overview-refresh > span:first-child { flex-basis: calc(100% - 44px); }
  .overview-refresh .spacer { display: none; }
  .quick-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .quick-card { padding: 12px 8px; }
  .quick-link { flex-direction: column; align-items: flex-start; }
}
</style>

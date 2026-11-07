<template>
  <div class="page">
    <div class="overview-refresh" data-test="overview-freshness" :class="{ 'tag-bad': dataStale }">
      <span>{{ freshnessText }}</span>
      <el-tag v-if="dataStale" type="warning" size="small" data-test="overview-stale">{{ $t('overviewStatus.stale') }}</el-tag>
      <span v-else class="muted tiny">{{ $t('overviewView.kc2d817') }}</span>
      <span class="spacer" />
      <el-tooltip :content="$t('overviewView.k38108e')">
        <el-button :icon="Refresh" :loading="loading" :aria-label="$t('overviewView.k38108e')" circle size="small" data-test="overview-refresh" @click="refresh" />
      </el-tooltip>
    </div>
    <PageStatusBar :issues="pageIssues" />
    <AlarmFloodBanner :summary="alarmSummary" compact :review="canGo('/alarms')" data-test="overview-flood" @review="go('/alarms')" />
    <SetupGuide v-if="can(auth.user, 'Configure') && !setupComplete" @status="onSetupStatus" />
    <div class="card-grid operational-grid">
      <KpiCard
        :class="{ clickable: canGo('/realtime') }"
        v-bind="clickableAttrs('/realtime')"
        data-test="stat-devices"
        :label="$t('overviewView.k99828c')"
        :value="`${health?.onlineDevices ?? '--'} / ${health?.totalDevices ?? '--'}`"
        :sub="!health ? $t('overviewStatus.unknown') : loadError ? $t('overviewStatus.stale') : offlineCount > 0 ? $t('overviewView.offlineCount', [offlineCount]) : $t('overviewView.kc64267')"
        :tone="devAlarm ? 'alarm' : devWarn ? 'warn' : undefined"
        :series="series.online"
        :target="health?.totalDevices"
        @click="openDeviceCard"
        @keydown.enter="openDeviceCard"
      />
      <KpiCard
        :class="{ clickable: canGo('/alarms') }"
        v-bind="clickableAttrs('/alarms')"
        data-test="stat-alarms"
        :label="$t('overviewStatus.activeUnack')"
        :value="`${unackCount ?? '--'}`"
        :sub="$t('overviewView.ka84899', [activeAlarmTotal ?? '--']) + (summaryError ? ` · ${$t('overviewStatus.loadedOnly')}` : '')"
        :tone="(unackCount ?? 0) > 0 ? 'alarm' : undefined"
        :series="series.unack"
        @click="openUnackAlarms"
        @keydown.enter="openUnackAlarms"
      />
      <KpiCard
        :class="{ clickable: canGo('/alarms') }"
        v-bind="clickableAttrs('/alarms')"
        data-test="stat-returned"
        :label="$t('overviewStatus.returned')"
        :value="`${alarmSummary?.rtnUnack ?? '--'}`"
        :sub="$t('overviewUx.returnedHint')"
        :tone="(alarmSummary?.rtnUnack ?? 0) > 0 ? 'warn' : undefined"
        :series="series.returned"
        @click="openReturned"
        @keydown.enter="openReturned"
      >
      </KpiCard>
      <KpiCard
        :class="{ clickable: canGo('/alarms') }"
        v-bind="clickableAttrs('/alarms')"
        data-test="stat-protection"
        :label="$t('overviewStatus.protection')"
        :value="`${alarmSummary?.suppressed ?? '--'} / ${alarmSummary?.shelved ?? '--'}`"
        :tone="floodActive || (alarmSummary?.suppressed ?? 0) > 0 || (alarmSummary?.shelved ?? 0) > 0 ? 'warn' : undefined"
        :series="series.protection"
        @click="openSuppressed"
        @keydown.enter="openSuppressed"
      >
        <div v-if="canGo('/alarms')" class="card-links" @click.stop @keydown.stop>
          <el-button link :type="floodActive ? undefined : 'primary'" data-test="overview-review-suppressed" @click="openSuppressed">{{ $t('overviewUx.reviewSuppressed') }}</el-button>
          <el-button link data-test="overview-review-shelved" @click="openShelved">{{ $t('overviewUx.reviewShelved') }}</el-button>
          <el-button v-if="floodActive" link type="primary" data-test="overview-flood-review" @click="go('/alarms')">{{ $t('overviewStatus.flood') }}</el-button>
        </div>
      </KpiCard>
    </div>
    <p class="tiny muted alarm-scope" data-test="overview-alarm-scope">{{ $t('overviewStatus.alarmScope') }}</p>

    <section class="overview-section" data-test="overview-alarm-section">
      <div class="toolbar">
        <strong>{{ $t('overviewUx.attentionList') }}</strong>
        <span class="muted tiny">{{ $t('overviewStatus.listed', [attentionAlarms.length, attentionTotal ?? '--']) }}</span>
        <span class="spacer" />
        <el-button v-if="canGo('/console')" size="small" data-test="overview-open-console" @click="openConsole">{{ $t('overviewUx.openConsole') }}</el-button>
        <el-button v-if="canGo('/alarms')" size="small" :icon="ArrowRight" @click="go('/alarms')">{{ $t('overviewView.ked2172') }}</el-button>
      </div>
      <el-table class="desktop-table" :data="attentionAlarms" size="small" :empty-text="alarmEmptyText" data-test="overview-alarms">
        <el-table-column :label="$t('overviewView.k254849')" width="90" show-overflow-tooltip>
          <template #default="{ row }"><el-tag class="alarm-level-tag" :data-level="row.level" :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.kf3dd64')" min-width="200" show-overflow-tooltip>
          <template #default="{ row }"><div>{{ alarmMessage(row) }}</div><span class="muted tiny">{{ alarmSource(row) }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('overviewStatus.triggerValue')" width="130" show-overflow-tooltip>
          <template #default="{ row }">{{ alarmValue(row) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.ke8868a')" width="165" show-overflow-tooltip>
          <template #default="{ row }">{{ formatTime(row.startTs) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewStatus.state')" width="120" show-overflow-tooltip>
          <template #default="{ row }"><el-tag :type="alarmStateType(row)" size="small">{{ $t(alarmStateKey(row)) }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('overviewStatus.locate')" min-width="160">
          <template #default="{ row }">
            <div class="alarm-actions">
              <el-button v-if="row.tagId && canGo('/screens')" link type="primary" data-test="overview-open-screen" @click="openScreen(row)">{{ $t('alarmUx.openScreen') }}</el-button>
              <el-button v-else-if="canGo('/alarms')" link type="primary" data-test="overview-open-alarm" @click="openAlarm(row)">{{ $t('overviewUx.openAlarm') }}</el-button>
              <el-dropdown v-if="!isMobile && overviewOverflow(row)" trigger="click">
                <el-button link data-test="overview-row-more" :aria-label="$t('overviewUx.moreActions')" @click.stop>{{ $t('alarmUx.rowMore') }}</el-button>
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item v-if="row.tagId && canGo('/screens') && canGo('/alarms')" data-test="overview-open-alarm" @click="openAlarm(row)">{{ $t('overviewUx.openAlarm') }}</el-dropdown-item>
                    <el-dropdown-item v-if="row.tagId && canGo('/trends')" data-test="overview-open-trend" @click="openTrend(row)">{{ $t('overviewStatus.trend') }}</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </template>
        </el-table-column>
      </el-table>
      <div class="mobile-list" data-test="overview-alarm-cards">
        <div v-for="alarm in attentionAlarms" :key="alarm.id" class="mobile-row">
          <div class="row-heading"><el-tag class="alarm-level-tag" :data-level="alarm.level" :type="levelType(alarm.level)" size="small">{{ levelLabel(alarm.level) }}</el-tag><strong>{{ alarmMessage(alarm) }}</strong></div>
          <div class="row-detail"><span>{{ alarmSource(alarm) }}</span><span>{{ alarmValue(alarm) }}</span></div>
          <div class="row-detail">
            <span>{{ formatTime(alarm.startTs) }}</span>
            <el-tag :type="alarmStateType(alarm)" size="small">{{ $t(alarmStateKey(alarm)) }}</el-tag>
            <el-button v-if="alarm.tagId && canGo('/screens')" link type="primary" data-test="overview-open-screen" @click="openScreen(alarm)">{{ $t('alarmUx.openScreen') }}</el-button>
            <el-button v-else-if="canGo('/alarms')" link type="primary" data-test="overview-open-alarm" @click="openAlarm(alarm)">{{ $t('overviewUx.openAlarm') }}</el-button>
            <el-dropdown v-if="isMobile && overviewOverflow(alarm)" trigger="click">
              <el-button link data-test="overview-row-more" :aria-label="$t('overviewUx.moreActions')" @click.stop>{{ $t('alarmUx.rowMore') }}</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item v-if="alarm.tagId && canGo('/screens') && canGo('/alarms')" data-test="overview-open-alarm" @click="openAlarm(alarm)">{{ $t('overviewUx.openAlarm') }}</el-dropdown-item>
                  <el-dropdown-item v-if="alarm.tagId && canGo('/trends')" data-test="overview-open-trend" @click="openTrend(alarm)">{{ $t('overviewStatus.trend') }}</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>
        </div>
        <p v-if="!attentionAlarms.length" class="muted tiny">{{ alarmEmptyText }}</p>
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
        <el-table-column :label="$t('overviewView.k01f2c1')" min-width="150" show-overflow-tooltip>
          <template #default="{ row }"><el-button v-if="canGo('/realtime')" link type="primary" @click="openDevice(row.deviceId)">{{ row.name }}</el-button><span v-else>{{ row.name }}</span></template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k62e951')" width="110" show-overflow-tooltip>
          <template #default="{ row }"><el-tag :class="{ 'tag-quiet': !loadError && row.online }" :type="loadError ? 'info' : row.online ? undefined : 'danger'" effect="plain" size="small">{{ loadError ? $t('overviewStatus.stale') : $t(row.online ? 'overviewView.k0373ff' : 'overviewView.k211357') }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k014935')" min-width="165" show-overflow-tooltip><template #default="{ row }">{{ row.lastPollTs ? formatTime(row.lastPollTs) : '--' }}</template></el-table-column>
        <el-table-column :label="$t('overviewView.k8a12b1')" width="100" show-overflow-tooltip><template #default="{ row }">{{ row.lastCycleMs != null ? row.lastCycleMs.toFixed(2) + ' ms' : '--' }}</template></el-table-column>
        <el-table-column prop="commFailures" :label="$t('overviewView.k6ac251')" width="100" show-overflow-tooltip />
        <el-table-column :label="$t('overviewView.ka38a81')" min-width="180" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ row.error ?? '--' }}</span></template></el-table-column>
      </el-table>
      <div class="mobile-list" data-test="overview-device-cards">
        <div v-for="device in visibleDevices" :key="device.deviceId" class="mobile-row">
          <div class="row-heading"><el-button v-if="canGo('/realtime')" link type="primary" @click="openDevice(device.deviceId)">{{ device.name }}</el-button><strong v-else>{{ device.name }}</strong><el-tag :class="{ 'tag-quiet': !loadError && device.online }" :type="loadError ? 'info' : device.online ? undefined : 'danger'" effect="plain" size="small">{{ loadError ? $t('overviewStatus.stale') : $t(device.online ? 'overviewView.k0373ff' : 'overviewView.k211357') }}</el-tag></div>
          <p v-if="device.error" class="tiny tag-fail">{{ device.error }}</p>
          <div class="muted tiny">{{ $t('overviewView.k014935') }} {{ device.lastPollTs ? formatTime(device.lastPollTs) : '--' }}</div>
        </div>
        <p v-if="!visibleDevices.length" class="muted tiny">{{ deviceEmptyText }}</p>
      </div>
    </section>

    <details class="overview-section" data-test="overview-maintenance">
      <summary>{{ $t('overviewStatus.maintenance') }}</summary>
      <SetupGuide v-if="can(auth.user, 'Configure') && setupComplete" @status="onSetupStatus" />
      <div class="card-grid maintenance-grid">
      <KpiCard
        :class="{ clickable: canGo('/realtime') }"
        v-bind="clickableAttrs('/realtime')"
        data-test="stat-tags"
        :label="$t('overviewView.k674e52')"
        :value="`${health?.configuredTags ?? '--'}`"
        :sub="$t('overviewView.k8e8615', [health?.busBacklog ?? '--'])"
        :tone="backlogWarn ? 'warn' : undefined"
        @click="go('/realtime')"
        @keydown.enter="go('/realtime')"
      />
      <KpiCard
        :class="{ clickable: canGo('/system') }"
        v-bind="clickableAttrs('/system')"
        data-test="stat-storage"
        :label="$t('overviewView.k4d5c56')"
        :value="health?.storage ? formatBytes(health.storage.historyBytes) : '--'"
        :sub="$t('overviewView.kcc8821', [health?.disk?.freeGb ?? '-'])"
        :tone="diskWarn ? 'alarm' : undefined"
        @click="go('/system')"
        @keydown.enter="go('/system')"
      />
      <KpiCard
        data-test="stat-uptime"
        :label="$t('overviewView.k66836c')"
        :value="uptime"
        :sub="health?.environment ?? '-'"
      />
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
    </details>
    <el-dialog v-model="screenPicker" :title="$t('alarmUx.pickScreen')" width="420px" data-test="overview-screen-picker">
      <div class="screen-picker">
        <el-button v-for="screen in screenMatches" :key="screen.id" data-test="overview-screen-choice" @click="goScreen(screen.id)">{{ screen.name }}</el-button>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowRight, Refresh, Search, Star, StarFilled } from '@element-plus/icons-vue'
import { alarmApi, healthApi, tagApi } from '../api'
import { hmiLayoutApi } from '../api/hmiLayouts'
import { errorMessage } from '../api/http'
import { screenApi } from '../api/screens'
import type { AlarmEvent, AlarmListState, AlarmSummary, Health, Tag } from '../types'
import { collectScreenTags } from '../screens/screenTags'
import { alarmMessage, alarmTrendQuery, formatBytes, formatTime, formatValue, levelLabel, levelType } from '../utils'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { findNavByPath, visibleNavItems, type NavItemDef } from '../nav/catalog'
import { loadFavorites, toggleFavorite } from '../nav/favorites'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { can } from '../access/policies'
import { isMobile } from '../mobile'
import AlarmFloodBanner from '../components/AlarmFloodBanner.vue'
import KpiCard from '../components/KpiCard.vue'
import PageStatusBar, { type PageIssue } from '../components/PageStatusBar.vue'
import SetupGuide from '../components/SetupGuide.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const health = ref<Health | null>(null)
// 火花线用现有 5s 轮询在内存里累积，不额外增加接口；最多保留 40 个采样点。
const series = reactive({
  online: [] as number[],
  unack: [] as number[],
  returned: [] as number[],
  protection: [] as number[]
})
function pushSample(key: keyof typeof series, value: number | null | undefined): void {
  if (typeof value !== 'number' || !Number.isFinite(value)) return
  const list = series[key]
  list.push(value)
  if (list.length > 40) list.shift()
}
const alarms = ref<AlarmEvent[]>([])
const loadError = ref('')
const alarmListError = ref('')
const loading = ref(true)
const healthUpdatedAt = ref(0), alarmsUpdatedAt = ref(0), summaryUpdatedAt = ref(0)
const { now } = useNowTicker()
const abnormalOnly = computed({
  get: () => route.query.devices !== 'all',
  set: (value: boolean) => {
    const query = { ...route.query }
    if (value) delete query.devices
    else query.devices = 'all'
    void router.replace({ query })
  }
})
const setupComplete = ref(false)
const returnedError = ref('')
const screenError = ref('')
const screenPicker = ref(false)
const screenMatches = ref<{ id: number; name: string }[]>([])
let screenIndex: Map<number, { id: number; name: string }[]> | null = null
const tags = ref<Tag[]>([])
const tagError = ref(''), tagsReady = ref(false), tagsLoading = ref(false)
const tagIndex = computed(() => new Map(tags.value.map(tag => [tag.id, tag])))
const favPaths = ref<string[]>([])

const allowed = computed(() => visibleNavItems(auth.user))

// 卡片跳转只允许进入当前用户可见的页面，无权限时保持纯展示（不加光标、不响应键盘）
function canGo(path: string): boolean {
  return allowed.value.some((item) => item.path === path)
}
function go(path: string): void {
  if (canGo(path)) void router.push(path)
}

// 后端各接口的失败合并成一条状态条：逐条铺开会把整页内容推下去，
// 但每条错误仍保留原来的 data-test，自动化与读屏都能取到。
const pageIssues = computed<PageIssue[]>(() => {
  const issues: PageIssue[] = []
  if (loadError.value) issues.push({ id: 'health', test: 'overview-load-error', level: 'error', message: loadError.value })
  if (tagError.value) issues.push({ id: 'tags', test: 'overview-tags-error', level: 'warning', message: tagError.value, detail: tr('overviewUx.tagsRecovery') })
  if (alarmListError.value) issues.push({ id: 'alarms', test: 'overview-list-error', level: 'error', message: alarmListError.value })
  if (summaryError.value) issues.push({ id: 'summary', test: 'overview-summary-error', level: 'warning', message: summaryError.value, detail: tr('overviewView.summaryFallback') })
  if (health.value?.warnings?.length) issues.push({ id: 'health-warnings', test: 'overview-health-warning', level: 'warning', message: health.value.warnings.map(warning => warning.message).join(' · ') })
  if (returnedError.value) issues.push({ id: 'returned', test: 'overview-returned-error', level: 'warning', message: returnedError.value })
  if (screenError.value) issues.push({ id: 'screens', test: 'overview-screen-error', level: 'warning', message: screenError.value })
  return issues
})
function openUnackAlarms(): void {
  if (canGo('/alarms')) void router.push({ path: '/alarms', query: { state: 'unack' } })
}
function openReturned(): void {
  if (canGo('/alarms')) void router.push({ path: '/alarms', query: { state: 'rtnUnack' } })
}
function openSuppressed(): void {
  if (canGo('/alarms')) void router.push({ path: '/alarms', query: { state: 'suppressed' } })
}
function openShelved(): void {
  if (canGo('/alarms')) void router.push({ path: '/alarms', query: { state: 'shelved' } })
}
function onSetupStatus(complete: boolean): void {
  if (complete) setupComplete.value = true
}
async function openConsole(): Promise<void> {
  if (!canGo('/console')) return
  try {
    const resolved = await hmiLayoutApi.resolveLocal()
    const layoutId = resolved?.workstation.layoutId
    if (layoutId) { await router.push(`/console/${layoutId}`); return }
  } catch {
    // 工位解析失败时仍进入运行工作台，由那边说明未绑定的原因。
  }
  await router.push('/console')
}
function clickableAttrs(path: string): Record<string, string | number> {
  return canGo(path) ? { role: 'button', tabindex: 0 } : {}
}
const favSet = computed(() => new Set(favPaths.value))
const quickItems = computed(() => allowed.value.filter((item) => item.quick && item.path !== '/overview'))
const favoriteItems = computed(() =>
  favPaths.value.map((p) => findNavByPath(p)).filter((x): x is NavItemDef => !!x && x.path !== '/overview' && allowed.value.some((a) => a.path === x.path))
)

function refreshNavLists(): void {
  const name = auth.user?.userName
  favPaths.value = loadFavorites(name)
}
function togglePin(path: string): void {
  favPaths.value = toggleFavorite(auth.user?.userName, path)
}
function openPalette(): void {
  window.dispatchEvent(new Event('scada-open-palette'))
}

const levelRank: Record<string, number> = { Critical: 0, Warning: 1, Info: 2 }
function needsAttention(alarm: AlarmEvent): boolean {
  return !alarm.shelved && !alarm.suppressed && (!alarm.endTs || !alarm.ackTs)
}
const attentionAlarms = computed(() =>
  [...alarms.value.filter(needsAttention)]
    .sort((a, b) => Number(!!a.ackTs) - Number(!!b.ackTs) || (levelRank[a.level] ?? 9) - (levelRank[b.level] ?? 9) || b.startTs - a.startTs)
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
const attentionTotal = computed(() => {
  const summary = alarmSummary.value
  if (!summaryError.value && summary) return summary.unack + summary.acked + summary.rtnUnack
  return alarmsUpdatedAt.value ? alarms.value.filter(needsAttention).length : null
})
const floodActive = computed(() => !!(alarmSummary.value?.floodGuard?.anyInFlood || alarmSummary.value?.flood?.inFlood))
function sourceStamp(at: number, failed: boolean): string {
  if (!at) return failed ? tr('overviewUx.sourceFailed') : tr('overviewStatus.unknown')
  return failed ? `${formatTime(at)} · ${tr('overviewUx.sourceFailed')}` : formatTime(at)
}
const freshnessText = computed(() => {
  const healthAt = healthUpdatedAt.value, alarmAt = alarmsUpdatedAt.value, summaryAt = summaryUpdatedAt.value
  const failed = !!(loadError.value || alarmListError.value || returnedError.value || summaryError.value)
  if (healthAt && alarmAt && summaryAt && !failed) return tr('overviewStatus.updated', [formatTime(Math.min(healthAt, alarmAt, summaryAt))])
  if (healthAt || alarmAt || summaryAt || failed) {
    return tr('overviewUx.freshnessPartial', [
      sourceStamp(healthAt, !!loadError.value),
      sourceStamp(alarmAt, !!(alarmListError.value || returnedError.value)),
      sourceStamp(summaryAt, !!summaryError.value)
    ])
  }
  return tr('overviewStatus.updated', [tr('overviewStatus.unknown')])
})
const lastSuccess = computed(() => {
  const times = [healthUpdatedAt.value, alarmsUpdatedAt.value, summaryUpdatedAt.value].filter(at => at > 0)
  return times.length ? Math.min(...times) : 0
})
const dataStale = computed(() => !!(loadError.value || tagError.value || alarmListError.value || returnedError.value || summaryError.value) || !!lastSuccess.value && now.value - lastSuccess.value > 15_000)
const alarmEmptyText = computed(() => alarmListError.value || loadError.value || (!alarmsUpdatedAt.value ? tr('overviewStatus.unknown') : tr('overviewUx.listEmpty')))
const deviceEmptyText = computed(() => loadError.value || (!health.value ? tr('overviewStatus.unknown') : tr(abnormalOnly.value ? 'overviewStatus.noDeviceIssues' : 'overviewView.k2b9379')))
const visibleDevices = computed(() => [...(health.value?.devices ?? [])]
  .filter(device => !abnormalOnly.value || !device.online || !!device.error)
  .sort((left, right) => Number(left.online) - Number(right.online) || Number(!!right.error) - Number(!!left.error) || left.name.localeCompare(right.name)))
function alarmSource(alarm: AlarmEvent): string {
  const tag = alarm.tagId ? tagIndex.value.get(alarm.tagId) : undefined
  const deviceId = alarm.deviceId ?? tag?.deviceId
  const device = health.value?.devices?.find(item => item.deviceId === deviceId)
  return [device?.name ?? (deviceId ? `#${deviceId}` : ''), tag?.name ?? (alarm.tagId ? `#${alarm.tagId}` : '')].filter(Boolean).join(' / ') || tr('overviewStatus.systemEvent')
}
function alarmValue(alarm: AlarmEvent): string {
  return [formatValue(alarm.value), alarm.tagId ? tagIndex.value.get(alarm.tagId)?.unit : ''].filter(Boolean).join(' ')
}
function alarmStateKey(alarm: AlarmEvent): string {
  if (alarm.endTs && !alarm.ackTs) return 'overviewStatus.returned'
  return alarm.ackTs ? 'overviewStatus.acked' : 'overviewView.k0642fa'
}
function alarmStateType(alarm: AlarmEvent): 'warning' | 'info' | 'danger' {
  if (alarm.endTs && !alarm.ackTs) return 'warning'
  return alarm.ackTs ? 'info' : 'danger'
}
function alarmListState(alarm: AlarmEvent): AlarmListState {
  if (alarm.endTs && !alarm.ackTs) return 'rtnUnack'
  return alarm.ackTs ? 'acked' : 'unack'
}
function overviewOverflow(alarm: AlarmEvent): boolean {
  const screen = !!alarm.tagId && canGo('/screens')
  const trend = !!alarm.tagId && canGo('/trends')
  if (screen) return canGo('/alarms') || trend
  return trend
}
function openAlarm(alarm: AlarmEvent): void {
  if (canGo('/alarms')) void router.push({ path: '/alarms', query: { state: alarmListState(alarm), focus: String(alarm.id) } })
}
function openDevice(deviceId: number): void {
  if (canGo('/realtime')) void router.push({ path: '/realtime', query: { deviceId: String(deviceId) } })
}
function openDeviceCard(): void {
  if (!canGo('/realtime')) return
  if (offlineCount.value > 0) void router.push({ path: '/realtime', query: { deviceState: 'offline' } })
  else void router.push('/realtime')
}
function goScreen(id: number): void {
  screenPicker.value = false
  void router.push(`/screens/${id}`)
}
async function openScreen(alarm: AlarmEvent): Promise<void> {
  screenError.value = ''
  if (!alarm.tagId || !canGo('/screens')) return
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
  const matches = screenIndex.get(alarm.tagId) ?? []
  if (!matches.length) { screenError.value = tr('alarmUx.noScreen'); return }
  if (matches.length === 1) { await router.push(`/screens/${matches[0].id}`); return }
  screenMatches.value = matches
  screenPicker.value = true
}
function openTrend(alarm: AlarmEvent): void {
  // 带上报警前后那一段：默认的最近 1 小时对几小时前的报警是空图（与报警页同源）
  if (alarm.tagId && canGo('/trends')) void router.push(alarmTrendQuery(alarm))
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
  const free = health.value?.disk?.freeGb
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

async function loadTags(): Promise<void> {
  // 目录只在尚未成功加载时获取；失败后随页面刷新/轮询重试，不让名称和单位永久缺失。
  if (tagsReady.value || tagsLoading.value) return
  tagsLoading.value = true
  try {
    tags.value = await tagApi.list()
    tagsReady.value = true
    tagError.value = ''
  } catch (error) {
    tags.value = []
    tagError.value = errorMessage(error, tr('overviewUx.tagsFailed'))
  } finally {
    tagsLoading.value = false
  }
}

async function load(): Promise<void> {
  loading.value = true
  try {
    await Promise.all([
      loadTags(),
      healthApi.get().then(value => {
        health.value = value
        healthUpdatedAt.value = Date.now()
        loadError.value = ''
        pushSample('online', value.onlineDevices)
      }, error => { loadError.value = errorMessage(error, tr('overviewView.k6ac251')) }),
      Promise.allSettled([
        alarmApi.query({ live: true, priorityFirst: true, includeShelved: false, limit: 200 }),
        alarmApi.queryPage({ live: true, state: 'rtnUnack', priorityFirst: true, limit: 50 })
      ]).then(([liveResult, returnedResult]) => {
        if (liveResult.status === 'fulfilled') {
          const merged = new Map<number, AlarmEvent>()
          if (returnedResult.status === 'fulfilled') {
            for (const alarm of returnedResult.value.items) merged.set(alarm.id, alarm)
            returnedError.value = ''
          } else {
            returnedError.value = errorMessage(returnedResult.reason, tr('overviewUx.returnedFailed'))
          }
          for (const alarm of liveResult.value) merged.set(alarm.id, alarm)
          alarms.value = [...merged.values()]
          alarmsUpdatedAt.value = Date.now()
          alarmListError.value = ''
        } else {
          alarmListError.value = errorMessage(liveResult.reason, tr('overviewView.alarmListFailed'))
          returnedError.value = ''
        }
      }),
      alarmApi.summary().then(
        value => {
          alarmSummary.value = value
          summaryUpdatedAt.value = Date.now()
          summaryError.value = ''
          pushSample('unack', value.unack)
          pushSample('returned', value.rtnUnack)
          pushSample('protection', (value.suppressed ?? 0) + (value.shelved ?? 0))
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

onMounted(() => {
  refreshNavLists()
  window.addEventListener('scada-nav-lists-changed', refreshNavLists)
})

onBeforeUnmount(() => {
  window.removeEventListener('scada-nav-lists-changed', refreshNavLists)
})
</script>

<style scoped>
.overview-refresh { display: flex; align-items: center; flex-wrap: wrap; gap: 8px; margin-bottom: 12px; font-size: 12px; }
.operational-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); }
.card-links { display: flex; flex-wrap: wrap; gap: 0 8px; }
.card-links :deep(.el-button) { margin: 0; height: 22px; }
.alarm-actions { display: flex; flex-wrap: wrap; gap: 2px 8px; }
.screen-picker { display: flex; flex-direction: column; align-items: stretch; gap: 8px; }
.alarm-scope { margin: 4px 0 8px; line-height: 1.5; overflow-wrap: anywhere; }
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
  border-color: var(--scada-accent-ink);
  box-shadow: var(--scada-shadow-sm);
}
.quick-card .el-icon { color: var(--scada-accent-ink); }
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
  border-color: var(--scada-accent-ink);
  box-shadow: var(--scada-shadow-sm);
  outline: none;
}
/* 异常阈值：左侧色条 + 数字着色，一眼看出哪块有问题 */
.stat-card.is-warn {
  border-left: 3px solid var(--scada-alarm-warning);
}
.stat-card.is-alarm {
  border-left: 3px solid var(--scada-alarm-critical);
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

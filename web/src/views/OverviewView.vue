<template>
  <div class="page">
    <div class="card-grid">
      <!-- 卡片可点击：从「看到数字」直接到「能处理」；无权限的页面自动降级为纯展示 -->
      <div
        class="stat-card"
        :class="{ clickable: canGo('/realtime'), 'is-warn': devWarn && !devAlarm, 'is-alarm': devAlarm }"
        v-bind="clickableAttrs('/realtime')"
        data-test="stat-devices"
        @click="go('/realtime')"
        @keydown.enter="go('/realtime')"
      >
        <div class="label">{{ $t('overviewView.k99828c') }}</div>
        <div class="value" :class="devAlarm ? 'tag-fail' : devWarn ? 'tag-bad' : ''">{{ health?.onlineDevices ?? 0 }} / {{ health?.totalDevices ?? 0 }}</div>
        <div class="sub">{{ offlineCount > 0 ? $t('overviewView.offlineCount', [offlineCount]) : $t('overviewView.kc64267') }}</div>
      </div>
      <div
        class="stat-card"
        :class="{ clickable: canGo('/alarms'), 'is-alarm': unackCount > 0 }"
        v-bind="clickableAttrs('/alarms')"
        data-test="stat-alarms"
        @click="go('/alarms')"
        @keydown.enter="go('/alarms')"
      >
        <div class="label">{{ $t('overviewView.k9710b5') }}</div>
        <div class="value" :class="unackCount > 0 ? 'tag-fail' : ''">{{ unackCount }}</div>
        <div class="sub">{{ $t('overviewView.ka84899', [activeAlarmTotal]) }}</div>
      </div>
      <div
        class="stat-card"
        :class="{ clickable: canGo('/realtime'), 'is-warn': backlogWarn }"
        v-bind="clickableAttrs('/realtime')"
        data-test="stat-tags"
        @click="go('/realtime')"
        @keydown.enter="go('/realtime')"
      >
        <div class="label">{{ $t('overviewView.k674e52') }}</div>
        <div class="value">{{ health?.configuredTags ?? 0 }}</div>
        <div class="sub">{{ $t('overviewView.k8e8615', [health?.busBacklog ?? 0]) }}</div>
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
        <div class="value">{{ formatBytes(health?.storage.historyBytes ?? 0) }}</div>
        <div class="sub" :class="diskWarn ? 'tag-fail' : ''">{{ $t('overviewView.kcc8821', [health?.disk.freeGb ?? '-']) }}</div>
      </div>
      <div class="stat-card">
        <div class="label">{{ $t('overviewView.k66836c') }}</div>
        <div class="value">{{ uptime }}</div>
        <div class="sub">{{ health?.environment ?? '-' }}</div>
      </div>
    </div>

    <div class="panel quick-panel" data-test="overview-quick">
      <div class="toolbar">
        <h3>{{ $t('nav.quickEntries') }}</h3>
        <span class="spacer" />
        <el-button size="small" data-test="overview-open-palette" @click="openPalette">
          <el-icon><Search /></el-icon>
          {{ $t('nav.search') }}
          <kbd class="mini-kbd">Ctrl+K</kbd>
        </el-button>
      </div>
      <div class="quick-grid">
        <button
          v-for="item in quickItems"
          :key="item.path"
          type="button"
          class="quick-card"
          :data-test="'overview-quick-' + item.id"
          @click="router.push(item.path)"
        >
          <el-icon :size="22"><component :is="item.icon" /></el-icon>
          <span>{{ $t(item.titleKey) }}</span>
          <el-button
            text
            size="small"
            class="pin"
            :class="{ on: favSet.has(item.path) }"
            :data-test="'overview-pin-' + item.id"
            @click.stop="togglePin(item.path)"
          >
            <el-icon><StarFilled v-if="favSet.has(item.path)" /><Star v-else /></el-icon>
          </el-button>
        </button>
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
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>{{ $t('overviewView.kd97d61') }}</strong>
        <span class="spacer" />
        <span class="muted tiny">{{ $t('overviewView.kc2d817') }}</span>
        <el-button size="small" :icon="Refresh" @click="load">{{ $t('overviewView.k38108e') }}</el-button>
      </div>
      <el-table :data="health?.devices ?? []" size="small" :empty-text="$t('overviewView.k2b9379')">
        <el-table-column prop="name" :label="$t('overviewView.k01f2c1')" min-width="140" />
        <el-table-column :label="$t('overviewView.k62e951')" width="110">
          <template #default="{ row }">
            <el-tag :type="row.online ? 'success' : 'danger'" effect="light" size="small">
              {{ row.online ? $t('overviewView.k0373ff') : $t('overviewView.k211357') }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k014935')" min-width="150">
          <template #default="{ row }">{{ row.lastPollTs ? formatTime(row.lastPollTs) : '-' }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.k8a12b1')" width="100">
          <template #default="{ row }">{{ row.lastCycleMs ? row.lastCycleMs.toFixed(2) + ' ms' : '-' }}</template>
        </el-table-column>
        <el-table-column prop="tagCount" :label="$t('overviewView.k217dc2')" width="90" />
        <el-table-column prop="commFailures" :label="$t('overviewView.k6ac251')" width="100" />
        <el-table-column :label="$t('overviewView.ka38a81')" min-width="200">
          <template #default="{ row }"><span class="tiny muted">{{ row.error ?? '-' }}</span></template>
        </el-table-column>
      </el-table>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>{{ $t('overviewView.k855fec') }}</strong>
        <span class="spacer" />
        <el-button size="small" @click="router.push('/alarms')">{{ $t('overviewView.ked2172') }}</el-button>
      </div>
      <el-table :data="activeAlarms" size="small" :empty-text="$t('overviewView.k996b9d')">
        <el-table-column :label="$t('overviewView.k254849')" width="90">
          <template #default="{ row }">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('overviewView.kf3dd64')" min-width="220" />
        <el-table-column :label="$t('overviewView.k521523')" width="110">
          <template #default="{ row }">{{ formatValue(row.value) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.ke8868a')" width="170">
          <template #default="{ row }">{{ formatTime(row.startTs) }}</template>
        </el-table-column>
        <el-table-column :label="$t('overviewView.kb56d9a')" width="120">
          <template #default="{ row }">
            <el-tag v-if="row.ackTs" type="info" size="small">{{ row.ackUser }}</el-tag>
            <span v-else class="muted tiny">{{ $t('overviewView.k0642fa') }}</span>
          </template>
        </el-table-column>
      </el-table>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { Refresh, Search, Star, StarFilled } from '@element-plus/icons-vue'
import { alarmApi, healthApi } from '../api'
import type { AlarmEvent, AlarmSummary, Health } from '../types'
import { formatBytes, formatTime, formatValue, levelLabel, levelType } from '../utils'
import { tr } from '../i18n'
import { useAuthStore } from '../stores/auth'
import { findNavByPath, visibleNavItems, type NavItemDef } from '../nav/catalog'
import { loadFavorites, toggleFavorite } from '../nav/favorites'
import { loadRecents } from '../nav/recents'
import { usePolling } from '../composables/usePolling'

const router = useRouter()
const auth = useAuthStore()
const health = ref<Health | null>(null)
const alarms = ref<AlarmEvent[]>([])
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
function clickableAttrs(path: string): Record<string, string | number> {
  return canGo(path) ? { role: 'button', tabindex: 0 } : {}
}
const favSet = computed(() => new Set(favPaths.value))
const quickItems = computed(() => allowed.value.filter((i) => i.quick))
const favoriteItems = computed(() =>
  favPaths.value.map((p) => findNavByPath(p)).filter((x): x is NavItemDef => !!x && allowed.value.some((a) => a.path === x.path))
)
const recentItems = computed(() =>
  recentPaths.value
    .map((p) => findNavByPath(p))
    .filter((x): x is NavItemDef => !!x && allowed.value.some((a) => a.path === x.path))
    .filter((x) => !favSet.value.has(x.path))
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

const activeAlarms = computed(() => alarms.value.filter((x) => !x.endTs).slice(0, 10))
// 数字取服务端全量统计；summary 拉取失败时退回「已加载这部分」的估算，至少不会显示 0
const alarmSummary = ref<AlarmSummary | null>(null)
const unackCount = computed(() =>
  alarmSummary.value ? alarmSummary.value.unack : activeAlarms.value.filter((x) => !x.ackTs).length
)
const activeAlarmTotal = computed(() =>
  alarmSummary.value
    ? alarmSummary.value.unack + alarmSummary.value.acked + alarmSummary.value.rtnUnack
    : activeAlarms.value.length
)
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
  const seconds = health.value?.uptimeSeconds ?? 0
  const days = Math.floor(seconds / 86400)
  const hours = Math.floor((seconds % 86400) / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  if (days > 0) return tr('overviewView.k4ed72e', [days, hours])
  if (hours > 0) return tr('overviewView.k165ffa', [hours, minutes])
  return tr('overviewView.uptimeMinutes', [minutes])
})

async function load(): Promise<void> {
  try {
    const [h, a, s] = await Promise.all([
      healthApi.get(),
      alarmApi.query({ activeOnly: true, limit: 200 }),
      // summary 是服务端按全量活动报警统计的，概览卡片的数字必须用它；
      // 用列表前 N 条自己数，超过 N 就会少报（曾经就把它截在前 10 条里）。
      alarmApi.summary().catch(() => null)
    ])
    health.value = h
    alarms.value = a
    if (s) alarmSummary.value = s
  } catch {
    // next refresh will retry
  }
}

// 概览是常驻大屏页：5s 兜底轮询，切到后台标签时暂停，回到前台立刻补一次。
usePolling(load, 5000)

onMounted(async () => {
  refreshNavLists()
  window.addEventListener('scada-nav-lists-changed', refreshNavLists)
  await load()
})

onBeforeUnmount(() => {
  window.removeEventListener('scada-nav-lists-changed', refreshNavLists)
})
</script>

<style scoped>
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
  border-radius: 10px;
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
</style>

<template>
  <div class="page page--fill page--workspace screen-workspace" data-test="screen-workspace">
    <div class="ws-toolbar">
      <div class="ws-heading">
        <strong>{{ $t('workspace.title') }}</strong>
        <span class="muted" data-test="workspace-count">{{ $t('workspace.tabCount', [tabs.length, MAX_TABS]) }}</span>
      </div>
      <div class="ws-actions">
        <el-radio-group :model-value="layoutMode" size="small" :aria-label="$t('workspace.layoutLabel')" data-test="workspace-layout" @change="onLayoutChange">
          <el-radio-button value="single" data-test="workspace-layout-single">{{ $t('workspace.layoutSingle') }}</el-radio-button>
          <el-radio-button value="grid2" data-test="workspace-layout-grid2">2×2</el-radio-button>
          <el-radio-button value="grid3" data-test="workspace-layout-grid3">3×3</el-radio-button>
        </el-radio-group>
        <el-tooltip :content="$t('workspace.cacheHint')" placement="bottom">
          <el-checkbox v-model="cachePages" :disabled="layoutMode !== 'single'" data-test="workspace-cache" @change="onCacheChange">{{ $t('workspace.cache') }}</el-checkbox>
        </el-tooltip>
        <el-button-group>
          <el-button :icon="ArrowLeft" :disabled="!canGoBack" :title="$t('workspace.back')" :aria-label="$t('workspace.back')" data-test="workspace-back" @click="goBack" />
          <el-button :icon="ArrowRight" :disabled="!canGoForward" :title="$t('workspace.forward')" :aria-label="$t('workspace.forward')" data-test="workspace-forward" @click="goForward" />
        </el-button-group>
        <el-dropdown v-if="tabs.length" trigger="click" popper-class="ws-all-tabs-popper" @command="activateFromList">
          <el-button :icon="List" :title="$t('workspace.allTabs')" :aria-label="$t('workspace.allTabs')" data-test="workspace-all-tabs" />
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item v-for="tab in tabs" :key="tab.key" :command="tab.key" :class="{ 'ws-all-active': tab.key === activeKey }" :data-test="`workspace-all-tab-${tab.key}`">
                <span class="ws-all-name">{{ tab.name }}</span>
                <span class="ws-all-id">#{{ tab.screenId }}</span>
                <button v-if="tabAlarmBadgeText(tabAlarmSummary(tab))" type="button" class="ws-all-alarm is-button" :class="{ 'is-incomplete': tabAlarmSummary(tab).incomplete }" :title="tabAlarmTitle(tab)" :aria-label="tabAlarmTitle(tab)" data-test="workspace-all-alarm" @click.stop="openTabAlarms(tab, $event)">{{ tabAlarmBadgeText(tabAlarmSummary(tab)) }}</button>
              </el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-dropdown trigger="click" @command="onTabCommand">
          <el-button :icon="MoreFilled" :title="$t('workspace.tabActions')" :aria-label="$t('workspace.tabActions')" data-test="workspace-tab-actions" />
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="reopen" :disabled="!closedStack.length" data-test="workspace-reopen">{{ $t('workspace.reopenTab') }}</el-dropdown-item>
              <el-dropdown-item command="copyLink" :disabled="!tabs.length" data-test="workspace-copy-link">{{ $t('workspace.copyLink') }}</el-dropdown-item>
              <el-dropdown-item divided command="closeOthers" :disabled="tabs.length <= 1" data-test="workspace-close-others">{{ $t('workspace.closeOthers') }}</el-dropdown-item>
              <el-dropdown-item command="closeAll" :disabled="!tabs.length" data-test="workspace-close-all">{{ $t('workspace.closeAll') }}</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-button type="primary" :icon="Plus" data-test="workspace-open" @click="openCatalog">{{ $t('workspace.openScreen') }}</el-button>
        <span class="muted tiny ws-shortcuts">{{ $t('workspace.shortcuts') }}</span>
      </div>
    </div>

    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="workspace-load-error">
      <el-button link type="primary" @click="loadScreens">{{ $t('workspace.retry') }}</el-button>
    </el-alert>
    <el-alert v-if="notice" :title="notice" type="warning" closable show-icon data-test="workspace-notice" @close="notice = ''" />

    <div ref="tabStrip" class="ws-tabs" role="tablist" :aria-label="$t('workspace.title')" data-test="workspace-tabs" @wheel="onTabsWheel" @dblclick.self="openCatalog">
      <div
        v-for="(tab, index) in tabs"
        :key="tab.key"
        :id="`ws-tab-${tab.key}`"
        class="ws-tab"
        :class="{ active: tab.key === activeKey, dragging: dragIndex === index, 'drag-over': dragOverIndex === index && dragIndex !== index }"
        role="tab"
        :tabindex="tab.key === activeKey ? 0 : -1"
        draggable="true"
        :aria-selected="tab.key === activeKey"
        :aria-controls="`ws-pane-${tab.key}`"
        :data-test="`workspace-tab-${tab.key}`"
        :data-screen-id="tab.screenId"
        @click="activate(tab.key)"
        @keydown="onTabKeydown($event, index)"
        @mousedown.middle.prevent
        @auxclick="onAuxClick($event, tab.key)"
        @contextmenu.prevent.stop="openMenu($event, tab.key)"
        @dragstart="startDrag(index, $event)"
        @dragover.prevent="dragOverIndex = index"
        @drop.prevent="dropTab(index)"
        @dragend="endDrag"
      >
        <span v-if="tab.pinned" class="ws-tab-pin" data-test="workspace-pin-icon" :title="$t('workspace.pinned')"><el-icon><StarFilled /></el-icon></span>
        <span class="ws-tab-name" :title="tab.name">{{ tab.name }}</span>
        <span class="ws-tab-id">#{{ tab.screenId }}</span>
        <button v-if="tabAlarmBadgeText(tabAlarmSummary(tab))" type="button" class="ws-tab-alarm is-button" :class="{ 'is-incomplete': tabAlarmSummary(tab).incomplete }" :title="tabAlarmTitle(tab)" :aria-label="tabAlarmTitle(tab)" data-test="workspace-tab-alarm" @click.stop="openTabAlarms(tab, $event)">{{ tabAlarmBadgeText(tabAlarmSummary(tab)) }}</button>
        <span v-if="tab.key !== activeKey && cachePages" class="ws-tab-paused" :title="$t('workspace.backgroundPaused')" aria-hidden="true">Ⅱ</span>
        <span
          v-if="!tab.pinned"
          class="ws-tab-close"
          role="button"
          tabindex="0"
          :aria-label="$t('workspace.close', [tab.name])"
          :title="$t('workspace.close', [tab.name])"
          @click.stop="closeTab(tab.key)"
          @keydown.enter.stop="closeTab(tab.key)"
          @keydown.space.stop.prevent="closeTab(tab.key)"
        ><el-icon><Close /></el-icon></span>
      </div>
      <span v-if="!tabs.length" class="muted ws-tabs-empty">{{ $t('workspace.noTabs') }}</span>
    </div>

    <nav v-if="activeTab && activeTab.history.length > 1" class="ws-trail" :aria-label="$t('workspace.trail')" data-test="workspace-trail">
      <button
        v-for="(entry, index) in activeTab.history"
        :key="`${entry.screenId}-${index}`"
        type="button"
        :class="{ current: index === activeTab.index }"
        :aria-current="index === activeTab.index ? 'page' : undefined"
        @click="jumpTrail(index)"
      >{{ entry.name }}</button>
    </nav>

    <div class="ws-body">
      <el-empty v-if="!tabs.length" :description="$t('workspace.emptyHint')" :image-size="90" data-test="workspace-empty">
        <el-button type="primary" :icon="Plus" @click="openCatalog">{{ $t('workspace.openScreen') }}</el-button>
      </el-empty>
      <template v-else-if="layoutMode === 'single'">
        <div v-for="tab in tabs" v-show="tab.key === activeKey" :id="`ws-pane-${tab.key}`" :key="tab.key" class="ws-pane" role="tabpanel" :aria-labelledby="`ws-tab-${tab.key}`" :data-test="`workspace-pane-${tab.key}`">
          <ScreenRuntime
            v-if="tab.mounted"
            :key="`${tab.key}-${tab.reloadNonce}`"
            :screen-id="tab.screenId"
            fill
            initial-zoom="fit"
            navigate-mode="tabs"
            toolbar-mode="embedded"
            :suspended="tab.key !== activeKey"
            :highlight-tag-ids="highlightTagsFor(tab.key)"
            @navigate="onNavigate"
            @screen-loaded="screen => onScreenLoaded(tab.key, screen)"
          />
        </div>
      </template>
      <div v-else class="ws-grid" :class="`is-${layoutMode}`" data-test="workspace-grid">
        <section
          v-for="tab in gridTabs"
          :key="`${tab.key}-${tab.reloadNonce}`"
          class="ws-cell"
          :class="{ active: tab.key === activeKey }"
          :data-screen-id="tab.screenId"
          data-test="workspace-cell"
          @click.self="activate(tab.key)"
        >
          <header class="ws-cell-head" @click="activate(tab.key)">
            <span class="ws-cell-name" :title="tab.name">{{ tab.name }}</span>
            <span class="ws-tab-id">#{{ tab.screenId }}</span>
            <button v-if="tabAlarmBadgeText(tabAlarmSummary(tab))" type="button" class="ws-tab-alarm is-button" :class="{ 'is-incomplete': tabAlarmSummary(tab).incomplete }" :title="tabAlarmTitle(tab)" :aria-label="tabAlarmTitle(tab)" data-test="workspace-cell-alarm" @click.stop="openTabAlarms(tab, $event)">{{ tabAlarmBadgeText(tabAlarmSummary(tab)) }}</button>
            <button v-if="!tab.pinned" type="button" class="ws-cell-close" :aria-label="$t('workspace.close', [tab.name])" data-test="workspace-cell-close" @click.stop="closeTab(tab.key)"><el-icon><Close /></el-icon></button>
          </header>
          <ScreenRuntime
            :screen-id="tab.screenId"
            fill
            initial-zoom="fit"
            navigate-mode="tabs"
            toolbar-mode="embedded"
            :highlight-tag-ids="highlightTagsFor(tab.key)"
            @navigate="onNavigate"
            @screen-loaded="screen => onScreenLoaded(tab.key, screen)"
          />
        </section>
        <button v-if="gridTabs.length < gridSize" type="button" class="ws-cell is-empty" data-test="workspace-grid-empty" @click="openCatalog">
          <el-icon><Plus /></el-icon>
          <span>{{ $t('workspace.gridEmpty') }}</span>
        </button>
      </div>
    </div>

    <el-dialog v-model="catalogVisible" :title="$t('workspace.openScreen')" width="760px" destroy-on-close data-test="workspace-catalog">
      <div class="catalog-toolbar">
        <el-input
          v-model="search"
          clearable
          :prefix-icon="Search"
          :placeholder="$t('workspace.searchPlaceholder')"
          :aria-label="$t('workspace.searchPlaceholder')"
          data-test="workspace-search"
        />
        <el-button :icon="Refresh" :loading="loading" :title="$t('workspace.refresh')" :aria-label="$t('workspace.refresh')" data-test="workspace-refresh" @click="loadScreens" />
      </div>
      <p class="muted tiny">{{ $t('workspace.publishedHint') }}</p>
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon>
        <el-button link type="primary" @click="loadScreens">{{ $t('workspace.retry') }}</el-button>
      </el-alert>
      <div v-else v-loading="loading" class="catalog-list">
        <div v-for="screen in filteredScreens" :key="screen.id" class="catalog-row-wrap">
          <el-checkbox
            :model-value="catalogSelection.includes(screen.id)"
            :aria-label="$t('workspace.selectScreen', [screen.name])"
            :data-test="`workspace-catalog-check-${screen.id}`"
            @change="toggleCatalogSelection(screen.id)"
          />
          <button type="button" class="catalog-row" :data-test="`workspace-catalog-${screen.id}`" @click="openScreen(screen.id)">
            <span class="catalog-name">{{ screen.name }}</span>
            <span class="catalog-meta">#{{ screen.id }} · r{{ screen.publishedRevision }}</span>
            <el-tag v-if="isOpen(screen.id)" size="small" class="tag-quiet" :type="undefined">{{ $t('workspace.opened') }}</el-tag>
            <el-icon class="catalog-open"><Plus /></el-icon>
          </button>
        </div>
        <el-empty
          v-if="!loading && !filteredScreens.length"
          :description="search ? $t('workspace.noMatches') : $t('workspace.noScreens')"
          :image-size="70"
        />
      </div>
      <template #footer>
        <el-button type="primary" :disabled="!catalogSelection.length" data-test="workspace-catalog-add" @click="addSelectedScreens">{{ $t('workspace.batchAdd', [catalogSelection.length]) }}</el-button>
        <el-button @click="catalogVisible = false">{{ $t('workspace.closeDialog') }}</el-button>
      </template>
    </el-dialog>

    <div
      v-if="menu && menuTab"
      class="ws-menu"
      :style="{ left: `${menu.x}px`, top: `${menu.y}px` }"
      role="menu"
      :aria-label="$t('workspace.tabMenu')"
      data-test="workspace-context-menu"
      @click.stop
    >
      <button type="button" role="menuitem" data-test="workspace-menu-reload" @click="onMenuCommand('reload')">{{ $t('workspace.reloadTab') }}</button>
      <button type="button" role="menuitem" data-test="workspace-menu-duplicate" @click="onMenuCommand('duplicate')">{{ $t('workspace.duplicateTab') }}</button>
      <button type="button" role="menuitem" data-test="workspace-menu-pin" @click="onMenuCommand('pin')">{{ menuTab.pinned ? $t('workspace.unpinTab') : $t('workspace.pinTab') }}</button>
      <div class="ws-menu-sep"></div>
      <button type="button" role="menuitem" data-test="workspace-menu-close" :disabled="menuTab.pinned" @click="onMenuCommand('close')">{{ $t('workspace.closeTab') }}</button>
      <button type="button" role="menuitem" data-test="workspace-menu-close-right" @click="onMenuCommand('closeRight')">{{ $t('workspace.closeRight') }}</button>
      <button type="button" role="menuitem" data-test="workspace-menu-close-others" @click="onMenuCommand('closeOthers')">{{ $t('workspace.closeOthers') }}</button>
      <button type="button" role="menuitem" data-test="workspace-menu-close-all" @click="onMenuCommand('closeAll')">{{ $t('workspace.closeAll') }}</button>
    </div>
  </div>

    <el-dialog v-model="alarmPopoverVisible" :title="alarmPopoverTab ? tabAlarmTitle(alarmPopoverTab) : $t('workspaceAlarms.openList')" width="420px" data-test="workspace-tab-alarms-dialog">
      <template v-if="alarmPopoverTab">
        <p class="tiny muted">{{ tabAlarmTitle(alarmPopoverTab) }}</p>
        <ul v-if="tabAlarmSummary(alarmPopoverTab).alarms.length" class="ws-alarm-list" data-test="workspace-tab-alarms">
          <li v-for="alarm in tabAlarmSummary(alarmPopoverTab).alarms" :key="alarm.id">
            <button type="button" class="ws-alarm-item" :data-test="`workspace-tab-alarm-item-${alarm.id}`" @click="focusTabAlarm(alarmPopoverTab, alarm)">{{ alarm.message }}</button>
          </li>
        </ul>
        <p v-else class="muted">{{ $t('workspaceAlarms.empty') }}</p>
      </template>
      <template #footer>
        <el-button data-test="workspace-tab-alarms-open" @click="openFilteredAlarms(alarmPopoverTab!)">{{ $t('workspaceAlarms.viewAlarms') }}</el-button>
      </template>
    </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter, type LocationQuery } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowLeft, ArrowRight, Close, List, MoreFilled, Plus, Refresh, Search, StarFilled } from '@element-plus/icons-vue'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import { screenApi } from '../api/screens'

import { useWorkspaceAlarms } from '../composables/useWorkspaceAlarms'
import type { ScreenSummary } from '../screens/model'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'

interface TabHistoryEntry { screenId: number; name: string }
interface WorkspaceTab {
  key: number
  screenId: number
  name: string
  mounted: boolean
  pinned: boolean
  reloadNonce: number
  history: TabHistoryEntry[]
  index: number
  /** 用于"标签达上限时替换最久未使用"；不持久化 */
  lastActiveAt: number
}
interface StoredTab {
  screenId: number
  name: string
  pinned?: boolean
  history?: TabHistoryEntry[]
  index?: number
}
interface StoredState { tabs: StoredTab[]; activeIndex: number; cache: boolean; layout?: WorkspaceLayout }
type WorkspaceLayout = 'single' | 'grid2' | 'grid3'

const MAX_TABS = 8
const MAX_HISTORY = 20
const MAX_CLOSED = 10
const route = useRoute(), router = useRouter(), auth = useAuthStore()
const screens = ref<ScreenSummary[]>([])
const tabs = ref<WorkspaceTab[]>([])
const activeKey = ref<number | null>(null)
const cachePages = ref(true)
// 单页 = 现有标签切换；grid2/grid3 = 同时显示 4/9 格（第 9 格在 8 标签上限下可作为空位）。
const layoutMode = ref<WorkspaceLayout>('single')
const gridSize = computed(() => layoutMode.value === 'grid2' ? 4 : layoutMode.value === 'grid3' ? 9 : 0)
const gridTabs = computed(() => layoutMode.value === 'single' ? [] : tabs.value.slice(0, gridSize.value))
const catalogVisible = ref(false)
const search = ref('')
const loading = ref(false)
const loadError = ref('')
const notice = ref('')
const dragIndex = ref<number | null>(null)
const dragOverIndex = ref<number | null>(null)
const tabStrip = ref<HTMLElement | null>(null)
const menu = ref<{ x: number; y: number; tabKey: number } | null>(null)
const closedStack = ref<StoredTab[]>([])
let nextKey = 1
// 报警摘要（标签红点）与绑定测点预取拆到 composable：视图只负责展示与交互。
const { tabAlarmCount, tabAlarmSummary, tabAlarmBadgeText, setTabTags } = useWorkspaceAlarms(tabs)

const alarmPopoverVisible = ref(false)
const alarmPopoverRef = ref<HTMLElement | null>(null)
const alarmPopoverTab = ref<typeof tabs.value[number] | null>(null)
const highlightTagMap = ref<Record<number, number[]>>({})
function highlightTagsFor(key: number): number[] { return highlightTagMap.value[key] ?? [] }
function tabAlarmTitle(tab: Parameters<typeof tabAlarmSummary>[0]): string {
  const summary = tabAlarmSummary(tab)
  if (summary.reason === 'capped') return tr('workspaceAlarms.incompleteCapped')
  if (summary.reason === 'fetch-failed') return tr('workspaceAlarms.incompleteFetch')
  if (summary.reason === 'prefetch-failed') return tr('workspaceAlarms.incompletePrefetch')
  return tr('workspace.tabAlarms', [summary.count])
}
function openTabAlarms(tab: typeof tabs.value[number], event: Event): void {
  activate(tab.key)
  alarmPopoverTab.value = tab
  alarmPopoverRef.value = event.currentTarget as HTMLElement
  alarmPopoverVisible.value = true
  const tags = [...new Set(tabAlarmSummary(tab).alarms.map(item => item.tagId).filter((id): id is number => !!id))]
  highlightTagMap.value = { ...highlightTagMap.value, [tab.key]: tags }
}
function focusTabAlarm(tab: typeof tabs.value[number], alarm: import('../types').AlarmEvent): void {
  if (alarm.tagId) highlightTagMap.value = { ...highlightTagMap.value, [tab.key]: [alarm.tagId] }
  activate(tab.key)
}
function openFilteredAlarms(tab: typeof tabs.value[number]): void {
  const tagId = tabAlarmSummary(tab).alarms.find(item => item.tagId)?.tagId
  alarmPopoverVisible.value = false
  void router.push({ path: '/alarms', query: tagId ? { state: 'unack', tagId: String(tagId) } : { state: 'unack' } })
}

const storageKey = computed(() => `scada-screen-workspace:${auth.user?.id ?? auth.user?.userName ?? 'anonymous'}`)
const activeTab = computed(() => tabs.value.find(tab => tab.key === activeKey.value) ?? null)
const menuTab = computed(() => tabs.value.find(tab => tab.key === menu.value?.tabKey) ?? null)
const activeIndex = computed(() => tabs.value.findIndex(tab => tab.key === activeKey.value))
const canGoBack = computed(() => !!activeTab.value && activeTab.value.index > 0)
const canGoForward = computed(() => !!activeTab.value && activeTab.value.index < activeTab.value.history.length - 1)
const filteredScreens = computed(() => {
  const keyword = search.value.trim().toLowerCase()
  return screens.value
    .filter(screen => screen.publishedRevision != null)
    .filter(screen => !keyword || screen.name.toLowerCase().includes(keyword) || String(screen.id).includes(keyword))
    .sort((a, b) => a.name.localeCompare(b.name, 'zh-CN'))
})

function parseIds(value: unknown): number[] {
  if (typeof value !== 'string') return []
  return value.split(',').map(item => Number(item)).filter(id => Number.isSafeInteger(id) && id > 0)
}

function screenName(id: number, fallback?: string): string {
  return screens.value.find(screen => screen.id === id)?.name ?? fallback ?? `#${id}`
}

function normalizeHistory(history: unknown, screenId: number, name: string): { history: TabHistoryEntry[]; index: number } {
  const entries: TabHistoryEntry[] = []
  if (Array.isArray(history)) {
    for (const item of history) {
      if (!item || typeof item !== 'object') continue
      const id = Number((item as { screenId?: unknown }).screenId)
      const label = (item as { name?: unknown }).name
      if (!Number.isSafeInteger(id) || id <= 0 || typeof label !== 'string') continue
      entries.push({ screenId: id, name: label })
      if (entries.length >= MAX_HISTORY) break
    }
  }
  if (!entries.length) return { history: [{ screenId, name }], index: 0 }
  let index = entries.findIndex(entry => entry.screenId === screenId)
  if (index < 0) {
    entries.push({ screenId, name })
    index = entries.length - 1
  }
  return { history: entries, index }
}

function makeTab(screenId: number, name: string, history?: TabHistoryEntry[], index?: number, pinned = false): WorkspaceTab {
  const entries = history?.length ? history.slice(-MAX_HISTORY) : [{ screenId, name }]
  const safeIndex = index != null && Number.isSafeInteger(index) && index >= 0 && index < entries.length ? index : entries.length - 1
  const current = entries[safeIndex] ?? { screenId, name }
  return { key: nextKey++, screenId: current.screenId, name: current.name, mounted: false, pinned, reloadNonce: 0, history: entries, index: safeIndex, lastActiveAt: Date.now() }
}

function readStored(): StoredState | null {
  try {
    // sessionStorage 按浏览器窗口/标签隔离：同一窗口刷新能恢复，新窗口各自一组，不会互相覆盖。
    // 首次升级时从旧的 localStorage 迁移一次，避免已打开的窗口丢掉标签组。
    let raw = sessionStorage.getItem(storageKey.value)
    if (raw == null) {
      const legacy = localStorage.getItem(storageKey.value)
      if (legacy != null) {
        sessionStorage.setItem(storageKey.value, legacy)
        localStorage.removeItem(storageKey.value)
        raw = legacy
      }
    }
    if (!raw) return null
    const parsed = JSON.parse(raw) as { tabs?: unknown; activeIndex?: unknown; cache?: unknown; layout?: unknown }
    if (!Array.isArray(parsed.tabs)) return null
    const storedTabs: StoredTab[] = []
    for (const item of parsed.tabs) {
      if (!item || typeof item !== 'object') continue
      const screenId = Number((item as { screenId?: unknown }).screenId)
      const name = (item as { name?: unknown }).name
      if (!Number.isSafeInteger(screenId) || screenId <= 0 || typeof name !== 'string') continue
      const normalized = normalizeHistory((item as { history?: unknown }).history, screenId, name)
      const rawIndex = Number((item as { index?: unknown }).index)
      storedTabs.push({
        screenId,
        name,
        pinned: Boolean((item as { pinned?: unknown }).pinned),
        history: normalized.history,
        index: Number.isSafeInteger(rawIndex) && rawIndex >= 0 ? rawIndex : normalized.index
      })
      if (storedTabs.length >= MAX_TABS) break
    }
    const active = Number(parsed.activeIndex)
    const layout: WorkspaceLayout | undefined = parsed.layout === 'grid2' || parsed.layout === 'grid3' ? parsed.layout : undefined
    return { tabs: storedTabs, activeIndex: Number.isSafeInteger(active) && active >= 0 ? active : 0, cache: parsed.cache !== false, layout }
  } catch {
    return null
  }
}

function persist(): void {
  try {
    sessionStorage.setItem(storageKey.value, JSON.stringify({
      tabs: tabs.value.map(tab => ({ screenId: tab.screenId, name: tab.name, pinned: tab.pinned, history: tab.history, index: tab.index })),
      activeIndex: activeIndex.value,
      cache: cachePages.value,
      layout: layoutMode.value
    }))
  } catch {
    // 隐私模式或存储已满时不影响当前会话。
  }
  syncRoute()
}

function syncRoute(): void {
  if (route.path !== '/workspace') return
  const nextScreens = tabs.value.map(tab => tab.screenId).join(',')
  const nextActive = activeIndex.value >= 0 ? String(activeIndex.value) : ''
  const nextLayout = layoutMode.value === 'single' ? '' : layoutMode.value
  const currentScreens = typeof route.query.screens === 'string' ? route.query.screens : ''
  const currentActive = typeof route.query.active === 'string' ? route.query.active : ''
  const currentLayout = typeof route.query.layout === 'string' ? route.query.layout : ''
  if (currentScreens === nextScreens && currentActive === nextActive && currentLayout === nextLayout && !('screen' in route.query)) return
  const query: LocationQuery = { ...route.query }
  delete query.screen
  if (nextLayout) query.layout = nextLayout
  else delete query.layout
  if (nextScreens) {
    query.screens = nextScreens
    query.active = nextActive
  } else {
    delete query.screens
    delete query.active
  }
  void router.replace({ path: '/workspace', query })
}

async function loadScreens(): Promise<void> {
  loading.value = true
  loadError.value = ''
  try {
    screens.value = await screenApi.list(false)
    for (const tab of tabs.value) {
      const current = tab.history[tab.index]
      tab.name = screenName(tab.screenId, tab.name)
      if (current) current.name = screenName(current.screenId, current.name)
      for (const entry of tab.history) entry.name = screenName(entry.screenId, entry.name)
    }
    pruneTabs()
  } catch (error) {
    loadError.value = error instanceof Error && error.message ? error.message : tr('workspace.loadFailed')
  } finally {
    loading.value = false
  }
}

function pruneTabs(): void {
  const visible = new Set(screens.value.filter(screen => screen.publishedRevision != null).map(screen => screen.id))
  const next: WorkspaceTab[] = []
  let changed = false
  for (const tab of tabs.value) {
    const history = tab.history.filter(entry => visible.has(entry.screenId))
    if (history.length !== tab.history.length) changed = true
    if (!history.length) {
      changed = true
      continue
    }
    let index = Math.min(tab.index, history.length - 1)
    if (!visible.has(tab.screenId) || history[index]?.screenId !== tab.screenId) {
      const found = history.findIndex(entry => entry.screenId === tab.screenId)
      index = found >= 0 ? found : Math.max(0, index)
      changed = true
    }
    tab.history = history
    tab.index = index
    tab.screenId = history[index].screenId
    tab.name = history[index].name
    next.push(tab)
  }
  if (!changed) return
  tabs.value = next
  if (!tabs.value.some(tab => tab.key === activeKey.value)) {
    const fallback = tabs.value[0] ?? null
    activeKey.value = fallback?.key ?? null
    if (fallback) fallback.mounted = true
  }
  notice.value = tr('workspace.removed')
  persist()
}

function activate(key: number): void {
  const tab = tabs.value.find(item => item.key === key)
  if (!tab || activeKey.value === key) return
  const previous = activeTab.value
  if (previous && !cachePages.value) previous.mounted = false
  tab.mounted = true
  tab.lastActiveAt = Date.now()
  activeKey.value = key
  persist()
}

/** "全部标签"下拉把命令值原样回传；只接受数字 key，字符串/对象一律忽略。 */
function activateFromList(key: number | string | object): void {
  if (typeof key === 'number') activate(key)
}

/** 平铺模式：网格里的画面全部保持实时（不像标签切换那样暂停非活动标签）。 */
function setLayout(mode: WorkspaceLayout): void {
  if (layoutMode.value === mode) return
  layoutMode.value = mode
  // 没有画面时直接打开选择器，避免用户切到一张空白网格。
  if (mode !== 'single' && !tabs.value.length) void openCatalog()
  persist()
}

/** el-radio-group 的 change 回传 string|number|boolean，先收窄再交给 setLayout。 */
function onLayoutChange(value: string | number | boolean): void {
  if (value === 'single' || value === 'grid2' || value === 'grid3') setLayout(value)
}

/** 达上限时替换"最久未激活"的非固定、非当前标签；由调用方确认后才关闭。 */
async function makeRoomForNewTab(): Promise<boolean> {
  const candidates = tabs.value.filter(tab => !tab.pinned && tab.key !== activeKey.value)
  if (!candidates.length) {
    ElMessage.warning(tr('workspace.maxTabs', [MAX_TABS]))
    return false
  }
  const oldest = candidates.reduce((left, right) => (left.lastActiveAt <= right.lastActiveAt ? left : right))
  try {
    await ElMessageBox.confirm(tr('workspace.replaceOldest', [MAX_TABS, oldest.name]), tr('workspace.replaceTitle'), {
      type: 'warning',
      confirmButtonText: tr('workspace.replaceConfirm'),
      cancelButtonText: tr('common.cancel')
    })
  } catch {
    return false
  }
  closeTab(oldest.key)
  return true
}

async function openScreen(id: number): Promise<void> {
  const summary = screens.value.find(screen => screen.id === id)
  if (!summary || summary.publishedRevision == null) {
    ElMessage.warning(tr('workspace.openFailed'))
    return
  }
  const existing = tabs.value.find(tab => tab.screenId === id)
  if (existing) {
    activate(existing.key)
    catalogVisible.value = false
    search.value = ''
    return
  }
  if (tabs.value.length >= MAX_TABS && !await makeRoomForNewTab()) return
  const tab = makeTab(id, summary.name)
  tabs.value.push(tab)
  activate(tab.key)
  catalogVisible.value = false
  search.value = ''
}

async function openCatalog(): Promise<void> {
  catalogVisible.value = true
  search.value = ''
  catalogSelection.value = []
  await loadScreens()
}

function applyCurrent(tab: WorkspaceTab): void {
  const entry = tab.history[tab.index] ?? { screenId: tab.screenId, name: tab.name }
  tab.screenId = entry.screenId
  tab.name = entry.name
  tab.mounted = true
}

async function onNavigate(id: number): Promise<void> {
  const tab = activeTab.value
  if (!tab) {
    if (!screens.value.some(screen => screen.id === id)) await loadScreens()
    openScreen(id)
    return
  }
  if (!screens.value.some(screen => screen.id === id)) await loadScreens()
  if (!tabs.value.includes(tab) || activeKey.value !== tab.key) return
  const summary = screens.value.find(screen => screen.id === id)
  if (!summary || summary.publishedRevision == null) {
    ElMessage.warning(tr('workspace.openFailed'))
    return
  }
  if (tab.screenId === id) return
  tab.history = [...tab.history.slice(0, tab.index + 1), { screenId: id, name: summary.name }].slice(-MAX_HISTORY)
  tab.index = tab.history.length - 1
  applyCurrent(tab)
  persist()
}

function goBack(): void {
  const tab = activeTab.value
  if (!tab || tab.index <= 0) return
  tab.index -= 1
  applyCurrent(tab)
  persist()
}

function goForward(): void {
  const tab = activeTab.value
  if (!tab || tab.index >= tab.history.length - 1) return
  tab.index += 1
  applyCurrent(tab)
  persist()
}

function jumpTrail(index: number): void {
  const tab = activeTab.value
  if (!tab || index < 0 || index >= tab.history.length || index === tab.index) return
  tab.history = tab.history.slice(0, index + 1)
  tab.index = index
  applyCurrent(tab)
  persist()
}

function rememberClosed(tab: WorkspaceTab): void {
  closedStack.value.unshift({
    screenId: tab.screenId,
    name: tab.name,
    history: tab.history.map(entry => ({ ...entry })),
    index: tab.index
  })
  if (closedStack.value.length > MAX_CLOSED) closedStack.value.length = MAX_CLOSED
}

function rememberClosedTabs(list: WorkspaceTab[]): void {
  for (const tab of list) rememberClosed(tab)
}

function reopenClosed(): void {
  while (closedStack.value.length) {
    const item = closedStack.value.shift()!
    if (screens.value.length && !screens.value.some(screen => screen.id === item.screenId && screen.publishedRevision != null)) continue
    if (tabs.value.length >= MAX_TABS) {
      closedStack.value.unshift(item)
      ElMessage.warning(tr('workspace.maxTabs', [MAX_TABS]))
      return
    }
    const normalized = normalizeHistory(item.history, item.screenId, screenName(item.screenId, item.name))
    const tab = makeTab(item.screenId, screenName(item.screenId, item.name), normalized.history, item.index ?? normalized.index)
    tabs.value.push(tab)
    activate(tab.key)
    return
  }
  ElMessage.info(tr('workspace.noClosedTabs'))
}

function closeTab(key: number): void {
  const index = tabs.value.findIndex(tab => tab.key === key)
  if (index < 0) return
  if (tabs.value[index].pinned) {
    ElMessage.warning(tr('workspace.pinBlocked'))
    return
  }
  const tab = tabs.value[index]
  rememberClosed(tab)
  const wasActive = key === activeKey.value
  tabs.value.splice(index, 1)
  if (wasActive) {
    const next = tabs.value[index] ?? tabs.value[index - 1] ?? null
    activeKey.value = null
    if (next) activate(next.key)
    else persist()
  } else {
    persist()
  }
}

function closeOthers(keepKey = activeKey.value): void {
  const keep = tabs.value.find(tab => tab.key === keepKey) ?? activeTab.value
  if (!keep) return
  rememberClosedTabs(tabs.value.filter(tab => !tab.pinned && tab.key !== keep.key))
  tabs.value = tabs.value.filter(tab => tab.pinned || tab.key === keep.key)
  keep.mounted = true
  activeKey.value = keep.key
  persist()
}

function closeAll(): void {
  const kept = tabs.value.filter(tab => tab.pinned)
  rememberClosedTabs(tabs.value.filter(tab => !tab.pinned))
  tabs.value = kept
  if (!kept.some(tab => tab.key === activeKey.value)) {
    const next = kept[0] ?? null
    activeKey.value = next?.key ?? null
    if (next) next.mounted = true
  }
  persist()
}

function closeRight(key: number): void {
  const index = tabs.value.findIndex(tab => tab.key === key)
  if (index < 0) return
  const removed = tabs.value.slice(index + 1).filter(tab => !tab.pinned)
  if (!removed.length) return
  rememberClosedTabs([...removed].reverse())
  tabs.value = tabs.value.filter((tab, at) => at <= index || tab.pinned)
  if (!tabs.value.some(tab => tab.key === activeKey.value)) activate(key)
  else persist()
}

function reorderTabs(): void {
  tabs.value = [...tabs.value.filter(tab => tab.pinned), ...tabs.value.filter(tab => !tab.pinned)]
}

function togglePin(key: number): void {
  const tab = tabs.value.find(item => item.key === key)
  if (!tab) return
  tab.pinned = !tab.pinned
  reorderTabs()
  persist()
}

function duplicateTab(key: number): void {
  const source = tabs.value.find(tab => tab.key === key)
  if (!source) return
  if (tabs.value.length >= MAX_TABS) {
    ElMessage.warning(tr('workspace.maxTabs', [MAX_TABS]))
    return
  }
  const duplicate = makeTab(source.screenId, source.name)
  tabs.value.push(duplicate)
  activate(duplicate.key)
}

function reloadTab(key: number): void {
  const tab = tabs.value.find(item => item.key === key)
  if (!tab) return
  tab.reloadNonce += 1
}

function openMenu(event: MouseEvent, key: number): void {
  activate(key)
  menu.value = {
    x: Math.max(8, Math.min(event.clientX, window.innerWidth - 208)),
    y: Math.max(8, Math.min(event.clientY, window.innerHeight - 320)),
    tabKey: key
  }
}

function closeMenu(): void {
  menu.value = null
}

function onMenuCommand(command: string): void {
  const tab = menuTab.value
  closeMenu()
  if (!tab) return
  if (command === 'reload') reloadTab(tab.key)
  else if (command === 'duplicate') duplicateTab(tab.key)
  else if (command === 'pin') togglePin(tab.key)
  else if (command === 'close') closeTab(tab.key)
  else if (command === 'closeRight') closeRight(tab.key)
  else if (command === 'closeOthers') closeOthers(tab.key)
  else if (command === 'closeAll') closeAll()
}

function onTabsWheel(event: WheelEvent): void {
  const strip = tabStrip.value
  if (!strip || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return
  strip.scrollLeft += event.deltaY
  event.preventDefault()
}

function onTabCommand(command: string | number | object): void {
  if (command === 'reopen') reopenClosed()
  else if (command === 'copyLink') void copyGroupLink()
  else if (command === 'closeOthers') closeOthers()
  else if (command === 'closeAll') closeAll()
}

/** 只把画面列表与当前激活页写进链接：固定状态与每标签历史属于本机会话，不随链接分享。 */
async function copyGroupLink(): Promise<void> {
  if (!tabs.value.length) return
  const screens = tabs.value.map(tab => tab.screenId).join(',')
  const active = Math.max(0, tabs.value.findIndex(tab => tab.key === activeKey.value))
  const layout = layoutMode.value === 'single' ? '' : `&layout=${layoutMode.value}`
  const link = `${location.origin}/workspace?screens=${screens}&active=${active}${layout}`
  try {
    await navigator.clipboard.writeText(link)
    ElMessage.success(tr('workspace.copyLinkDone'))
  } catch {
    // 非安全上下文（http + 内网 IP）没有 clipboard 权限：把链接直接显示出来，允许手动复制。
    ElMessage.warning(tr('workspace.copyLinkFailed', [link]))
  }
}

function nextTab(): void {
  if (!tabs.value.length) return
  const index = Math.max(0, activeIndex.value)
  activate(tabs.value[(index + 1) % tabs.value.length].key)
}

function previousTab(): void {
  if (!tabs.value.length) return
  const index = Math.max(0, activeIndex.value)
  activate(tabs.value[(index - 1 + tabs.value.length) % tabs.value.length].key)
}

function onAuxClick(event: MouseEvent, key: number): void {
  if (event.button !== 1) return
  event.preventDefault()
  closeTab(key)
}

function startDrag(index: number, event: DragEvent): void {
  dragIndex.value = index
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = 'move'
    event.dataTransfer.setData('text/plain', String(index))
  }
}

function dropTab(index: number): void {
  const from = dragIndex.value
  dragIndex.value = null
  dragOverIndex.value = null
  if (from == null || from === index || from < 0 || from >= tabs.value.length) return
  if (tabs.value[from].pinned !== tabs.value[index]?.pinned) return
  const [moved] = tabs.value.splice(from, 1)
  if (moved) tabs.value.splice(index, 0, moved)
  persist()
}

function endDrag(): void {
  dragIndex.value = null
  dragOverIndex.value = null
}

function onScreenLoaded(key: number, screen: { id: number; name: string; tagIds: number[] } | null): void {
  if (!screen) return
  const tab = tabs.value.find(item => item.key === key)
  if (!tab || tab.screenId !== screen.id) return
  // 绑定测点随画面一起更新：标签报警徽标依赖这份映射，缺失时标签不会亮红点。
  setTabTags(key, screen.tagIds)
  let changed = tab.name !== screen.name
  tab.name = screen.name
  for (const entry of tab.history) {
    if (entry.screenId === screen.id && entry.name !== screen.name) {
      entry.name = screen.name
      changed = true
    }
  }
  if (changed) persist()
}

function onCacheChange(): void {
  if (!cachePages.value) {
    for (const tab of tabs.value) if (tab.key !== activeKey.value) tab.mounted = false
  }
  persist()
}

function isOpen(id: number): boolean {
  return tabs.value.some(tab => tab.screenId === id)
}

/** 目录多选：勾选后一次性加入；单个点行仍是即时打开（保持原有快捷路径）。 */
const catalogSelection = ref<number[]>([])
function toggleCatalogSelection(id: number): void {
  catalogSelection.value = catalogSelection.value.includes(id)
    ? catalogSelection.value.filter(item => item !== id)
    : [...catalogSelection.value, id]
}

function addSelectedScreens(): void {
  const ids = catalogSelection.value
  if (!ids.length) return
  const room = Math.max(0, MAX_TABS - tabs.value.length)
  const accepted: number[] = []
  let skipped = 0
  for (const id of ids) {
    if (tabs.value.some(tab => tab.screenId === id) || accepted.length >= room) { skipped++; continue }
    const summary = screens.value.find(screen => screen.id === id)
    if (!summary || summary.publishedRevision == null) { skipped++; continue }
    tabs.value.push(makeTab(id, summary.name))
    accepted.push(id)
  }
  if (accepted.length) {
    const last = tabs.value[tabs.value.length - 1]
    if (last) activate(last.key)
    persist()
    catalogVisible.value = false
    search.value = ''
  }
  // 已打开与超上限的画面不静默丢弃：明确告诉用户有多少个没加进来。
  if (skipped) ElMessage.warning(tr('workspace.batchSkipped', [skipped]))
  catalogSelection.value = []
}

function onTabKeydown(event: KeyboardEvent, index: number): void {
  let nextIndex: number | null = null
  if (event.key === 'ArrowRight') nextIndex = (index + 1) % tabs.value.length
  else if (event.key === 'ArrowLeft') nextIndex = (index - 1 + tabs.value.length) % tabs.value.length
  else if (event.key === 'Home') nextIndex = 0
  else if (event.key === 'End') nextIndex = tabs.value.length - 1
  if (nextIndex == null) return
  event.preventDefault()
  const tab = tabs.value[nextIndex]
  if (!tab) return
  activate(tab.key)
  void nextTick(() => tabStrip.value?.querySelector<HTMLElement>(`[data-test="workspace-tab-${tab.key}"]`)?.focus())
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape' && menu.value) {
    event.preventDefault()
    closeMenu()
    return
  }
  if (catalogVisible.value) return
  const target = event.target as HTMLElement | null
  if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.tagName === 'SELECT' || target.isContentEditable)) return
  if (event.ctrlKey && event.shiftKey && !event.altKey && event.key.toLowerCase() === 't') {
    event.preventDefault()
    reopenClosed()
    return
  }
  if (event.ctrlKey && !event.altKey && event.key === 'Tab') {
    if (!tabs.value.length) return
    event.preventDefault()
    if (event.shiftKey) previousTab()
    else nextTab()
    return
  }
  if (event.ctrlKey && !event.shiftKey && !event.altKey && event.key.toLowerCase() === 'w') {
    if (activeKey.value == null) return
    event.preventDefault()
    closeTab(activeKey.value)
    return
  }
  if (event.altKey && !event.ctrlKey && event.key === 'ArrowLeft') {
    event.preventDefault()
    goBack()
    return
  }
  if (event.altKey && !event.ctrlKey && event.key === 'ArrowRight') {
    event.preventDefault()
    goForward()
  }
}

onMounted(async () => {
  const stored = readStored()
  if (stored) {
    cachePages.value = stored.cache
    if (stored.layout) layoutMode.value = stored.layout
  }
  // URL 优先于本窗口快照：分享链接里带了布局就按链接打开。
  if (route.query.layout === 'grid2' || route.query.layout === 'grid3') layoutMode.value = route.query.layout
  await loadScreens()
  const queryIds = parseIds(route.query.screens)
  const directId = Number(route.query.screen)
  const hasDirect = Number.isSafeInteger(directId) && directId > 0
  let preferredIndex = 0
  if (queryIds.length) {
    const storedMatches = stored && stored.tabs.length === queryIds.length
      && stored.tabs.every((tab, index) => tab.screenId === queryIds[index])
    const source: StoredTab[] = storedMatches
      ? stored!.tabs
      : queryIds.map(id => ({ screenId: id, name: screenName(id) }))
    for (const item of source) {
      if (tabs.value.length >= MAX_TABS) break
      const normalized = normalizeHistory(item.history, item.screenId, screenName(item.screenId, item.name))
      tabs.value.push(makeTab(item.screenId, screenName(item.screenId, item.name), normalized.history, item.index ?? normalized.index, Boolean(item.pinned)))
    }
    const requested = Number(route.query.active)
    preferredIndex = Number.isSafeInteger(requested) && requested >= 0 && requested < tabs.value.length
      ? requested
      : Math.min(stored?.activeIndex ?? 0, Math.max(0, tabs.value.length - 1))
  } else if (stored && stored.tabs.length) {
    for (const item of stored.tabs) {
      if (tabs.value.length >= MAX_TABS) break
      const normalized = normalizeHistory(item.history, item.screenId, screenName(item.screenId, item.name))
      tabs.value.push(makeTab(item.screenId, screenName(item.screenId, item.name), normalized.history, item.index ?? normalized.index, Boolean(item.pinned)))
    }
    preferredIndex = Math.min(stored.activeIndex, Math.max(0, tabs.value.length - 1))
  }
  if (hasDirect) {
    const summary = screens.value.find(screen => screen.id === directId)
    if (summary?.publishedRevision != null) {
      const existing = tabs.value.find(tab => tab.screenId === directId)
      if (existing) preferredIndex = tabs.value.indexOf(existing)
      else if (tabs.value.length < MAX_TABS) {
        const tab = makeTab(directId, summary.name)
        tabs.value.push(tab)
        preferredIndex = tabs.value.length - 1
      }
    }
  }
  if (tabs.value.length && !loadError.value) pruneTabs()
  const preferred = tabs.value[preferredIndex] ?? tabs.value[0] ?? null
  if (preferred) activate(preferred.key)
  else persist()
  window.addEventListener('keydown', onKeydown)
  document.addEventListener('click', closeMenu)
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  document.removeEventListener('click', closeMenu)
})

watch(activeKey, async () => {
  await nextTick()
  tabStrip.value?.querySelector<HTMLElement>('.ws-tab.active')?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
})
</script>

<style scoped>
.screen-workspace { gap: var(--scada-space-3); }
.ws-toolbar { display: flex; align-items: center; gap: var(--scada-space-3); flex-wrap: wrap; }
.ws-heading { display: flex; align-items: baseline; gap: var(--scada-space-2); min-width: 0; }
.ws-heading strong { font-size: 16px; }
.ws-actions { margin-left: auto; display: flex; align-items: center; gap: var(--scada-space-2); flex-wrap: wrap; }
.ws-shortcuts { white-space: nowrap; }
.ws-tabs { display: flex; align-items: stretch; gap: 2px; overflow-x: auto; border-bottom: 1px solid var(--scada-border); flex: none; }
.ws-tab {
  display: flex;
  align-items: center;
  gap: var(--scada-space-2);
  padding: 7px 6px 7px 12px;
  border: 1px solid transparent;
  border-bottom: 0;
  border-radius: 6px 6px 0 0;
  background: transparent;
  color: var(--scada-text-muted);
  cursor: pointer;
  white-space: nowrap;
  font: inherit;
  font-size: 13px;
  user-select: none;
}
.ws-tab:hover { background: var(--scada-panel-hover); color: var(--scada-text); }
.ws-tab.active { background: var(--scada-panel); color: var(--scada-text); border-color: var(--scada-border); box-shadow: inset 0 2px 0 var(--scada-primary); }
.ws-tab.dragging { opacity: 0.5; }
.ws-tab.drag-over { box-shadow: inset 2px 0 0 var(--scada-primary); }
.ws-tab-id { font-size: 11px; opacity: 0.7; font-variant-numeric: tabular-nums; }
/* 后台标签的未确认报警徽标：与顶栏报警计数同色，切换前就能看到哪个画面有未确认报警。 */
.ws-tab-alarm,
.ws-all-alarm {
  min-width: 18px;
  padding: 0 5px;
  border-radius: 9px;
  background: var(--el-color-danger);
  color: var(--el-color-white);
  font-size: 11px;
  line-height: 18px;
  text-align: center;
  font-variant-numeric: tabular-nums;
}
.ws-all-name { flex: 1; margin-right: var(--scada-space-2); }
.ws-all-id { color: var(--scada-text-muted); font-size: 11px; }
.ws-tab-name { max-width: 180px; overflow: hidden; text-overflow: ellipsis; }
.ws-tab-paused { font-size: 10px; color: var(--scada-text-muted); opacity: 0.7; letter-spacing: -1px; }
.ws-tab-pin { display: inline-flex; align-items: center; color: var(--scada-primary); font-size: 12px; }
.ws-tab-close { display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; border-radius: 4px; }
.ws-tab-close:hover { background: color-mix(in srgb, var(--el-color-danger) 14%, transparent); color: var(--el-color-danger); }
.ws-tabs-empty { padding: 8px 4px; font-size: 12px; pointer-events: none; }
.ws-trail { display: flex; align-items: center; gap: 4px; flex-wrap: wrap; font-size: 12px; min-height: 20px; }
.ws-trail button { background: transparent; border: 0; color: var(--scada-primary); cursor: pointer; padding: 0; font: inherit; }
.ws-trail button:hover { text-decoration: underline; }
.ws-trail button.current { color: var(--scada-text); cursor: default; text-decoration: none; }
.ws-trail button + button::before { content: '/'; margin-right: 4px; color: var(--scada-text-muted); }
.ws-body { flex: 1; min-height: 0; position: relative; }
.ws-pane { height: 100%; min-height: 0; }
/* 平铺模式：每格独立挂载运行时并保持实时（不像标签切换那样暂停非活动页）。 */
.ws-grid { height: 100%; min-height: 0; display: grid; gap: var(--scada-space-2); padding: var(--scada-space-1); }
.ws-grid.is-grid2 { grid-template-columns: repeat(2, minmax(0, 1fr)); grid-template-rows: repeat(2, minmax(0, 1fr)); }
.ws-grid.is-grid3 { grid-template-columns: repeat(3, minmax(0, 1fr)); grid-template-rows: repeat(3, minmax(0, 1fr)); }
.ws-cell { min-width: 0; min-height: 0; display: flex; flex-direction: column; overflow: hidden; border: 1px solid var(--scada-border); border-radius: 6px; background: var(--scada-panel); cursor: pointer; }
.ws-cell.active { border-color: var(--scada-primary); }
.ws-cell-head { display: flex; align-items: center; gap: var(--scada-space-2); padding: 4px 8px; font-size: 12px; background: var(--scada-panel-muted); }
.ws-cell-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.ws-cell :deep(.runtime) { flex: 1; min-height: 0; }
.ws-cell.is-empty { display: flex; align-items: center; justify-content: center; gap: var(--scada-space-2); color: var(--scada-text-muted); border-style: dashed; background: transparent; font: inherit; font-size: 13px; }
.ws-cell.is-empty:hover { border-color: var(--scada-primary); color: var(--scada-primary); }
.ws-cell-close { display: inline-flex; align-items: center; justify-content: center; width: 18px; height: 18px; border: 0; border-radius: 4px; background: transparent; color: var(--scada-text-muted); cursor: pointer; }
.ws-cell-close:hover { background: color-mix(in srgb, var(--el-color-danger) 14%, transparent); color: var(--el-color-danger); }
.catalog-toolbar { display: flex; align-items: center; gap: var(--scada-space-2); }
.catalog-toolbar .el-input { flex: 1; }
.catalog-list { max-height: 56vh; overflow: auto; margin-top: var(--scada-space-2); display: flex; flex-direction: column; gap: var(--scada-space-1); }
/* 勾选框与行按钮并列：不能把 checkbox 嵌进 <button>，否则是非法交互嵌套。 */
.catalog-row-wrap { display: flex; align-items: center; gap: var(--scada-space-2); }
.catalog-row-wrap .catalog-row { flex: 1; }
.catalog-row {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto auto 18px;
  gap: var(--scada-space-3);
  align-items: center;
  width: 100%;
  padding: 10px 12px;
  background: transparent;
  border: 1px solid var(--scada-border);
  border-radius: 6px;
  color: inherit;
  cursor: pointer;
  text-align: left;
  font: inherit;
}
.catalog-row:hover { border-color: var(--scada-primary); background: var(--scada-panel-hover); }
.catalog-name { font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.catalog-meta { font-size: 12px; color: var(--scada-text-muted); font-variant-numeric: tabular-nums; }
.catalog-open { color: var(--scada-text-muted); }
.ws-menu {
  position: fixed;
  z-index: 80;
  min-width: 190px;
  padding: 4px;
  display: flex;
  flex-direction: column;
  background: var(--scada-panel);
  border: 1px solid var(--scada-border);
  border-radius: 6px;
  box-shadow: 0 12px 32px rgba(0, 0, 0, 0.28);
}
.ws-menu button {
  padding: 7px 10px;
  background: transparent;
  border: 0;
  border-radius: 4px;
  color: var(--scada-text);
  cursor: pointer;
  font: inherit;
  font-size: 12px;
  text-align: left;
}
.ws-menu button:hover { background: var(--scada-panel-hover); }
.ws-menu button:disabled { opacity: 0.45; cursor: default; }
.ws-menu-sep { height: 1px; margin: 4px 2px; background: var(--scada-border); }
@media (max-width: 768px) {
  .ws-actions { margin-left: 0; }
  .ws-shortcuts { display: none; }
  .catalog-row { grid-template-columns: minmax(0, 1fr) auto 18px; }
  .catalog-row .el-tag { display: none; }
}
.ws-alarm-list { margin: 0; padding: 0; list-style: none; display: flex; flex-direction: column; gap: 6px; max-height: 50vh; overflow: auto; }
.ws-alarm-item { width: 100%; text-align: left; padding: 6px 8px; border: 1px solid var(--scada-border, var(--el-border-color)); border-radius: 4px; background: transparent; color: inherit; cursor: pointer; }
.ws-alarm-item:hover, .ws-alarm-item:focus-visible { border-color: var(--scada-accent-ink); }
</style>

<template>
  <div ref="navRef" class="nav-wrap" :class="{ collapsed }">
    <div v-if="!collapsed" class="nav-filter">
      <el-input
        v-model="filter"
        size="small"
        clearable
        data-test="nav-filter"
        :placeholder="$t('nav.menuFilter')"
        :prefix-icon="Search"
        @keydown="onFilterKey"
      />
    </div>
    <div
      v-if="!collapsed && !filter.trim() && (favorites.length || recents.length)"
      class="nav-pins"
      data-test="nav-pins"
    >
      <template v-if="favorites.length">
        <div class="nav-pin-title">{{ $t('nav.favorites') }}</div>
        <button
          v-for="item in favorites"
          :key="'fav-' + item.path"
          type="button"
          class="nav-pin"
          :class="{ active: isCurrent(item) }"
          :data-test="'nav-fav-' + item.id"
          @click="go(item)"
        >
          <el-icon><StarFilled /></el-icon>
          <span class="nav-pin-label">{{ $t(item.titleKey) }}</span>
        </button>
      </template>
      <template v-if="recents.length">
        <div class="nav-pin-title">{{ $t('nav.recent') }}</div>
        <button
          v-for="item in recents"
          :key="'recent-' + item.path"
          type="button"
          class="nav-pin"
          :class="{ active: isCurrent(item) }"
          :data-test="'nav-recent-' + item.id"
          @click="go(item)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span class="nav-pin-label">{{ $t(item.titleKey) }}</span>
        </button>
      </template>
    </div>
    <el-menu
      ref="menuRef"
      :default-active="route.path"
      router
      class="nav-menu"
      background-color="transparent"
      text-color="var(--scada-dark-muted)"
      active-text-color="var(--scada-accent)"
      :default-openeds="initialOpenKeys"
      :collapse="collapsed"
      :collapse-transition="false"
      @open="handleOpen"
      @close="handleClose"
    >
      <el-sub-menu v-for="group in groups" :key="group.id" :index="group.id" :data-test="'nav-' + group.id + '-group'">
        <template #title>
          <el-icon><component :is="group.icon" /></el-icon>
          <span>{{ $t(group.titleKey) }}</span>
        </template>
        <el-menu-item
          v-for="item in group.items"
          :key="item.id"
          :index="item.path"
          :class="{ 'is-kbd': item.path === kbPath }"
          :data-test="'nav-item-' + item.id"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span>{{ $t(item.titleKey) }}</span>
          <template v-if="item.id === 'alarms' && alarmCount > 0">
            <i v-if="collapsed" class="nav-dot" :class="{ flood: alarmFlood }" data-test="nav-alarm-dot" />
            <span v-else class="nav-count" :class="{ flood: alarmFlood }" data-test="nav-alarm-count">
              {{ alarmCount > 99 ? '99+' : alarmCount }}
            </span>
          </template>
        </el-menu-item>
      </el-sub-menu>
      <div v-if="!groups.length" class="nav-empty" data-test="nav-filter-empty">
        {{ $t('nav.noResults') }}
      </div>
    </el-menu>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { MenuInstance } from 'element-plus'
import { useRoute, useRouter } from 'vue-router'
import { Search, StarFilled } from '@element-plus/icons-vue'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'
import {
  NAV_GROUPS,
  findNavByPath,
  fuzzyMatch,
  navSearchText,
  visibleNavItems,
  type NavGroupId,
  type NavItemDef
} from '../nav/catalog'
import { loadFavorites } from '../nav/favorites'
import { loadRecents } from '../nav/recents'

const props = withDefaults(defineProps<{
  alarmCount?: number
  alarmFlood?: boolean
  collapsed?: boolean
}>(), { alarmCount: 0, alarmFlood: false, collapsed: false })

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const filter = ref('')
const navRef = ref<HTMLElement | null>(null)
const menuRef = ref<MenuInstance>()
const kbIndex = ref(-1)
const favPaths = ref<string[]>([])
const recentPaths = ref<string[]>([])

const alarmCount = computed(() => props.alarmCount)
const alarmFlood = computed(() => props.alarmFlood)
const collapsed = computed(() => props.collapsed)

function menuEl(): HTMLElement | null {
  return navRef.value?.querySelector<HTMLElement>('.nav-menu') ?? null
}

const allowed = computed(() => visibleNavItems(auth.user))
const barePath = computed(() => route.path.replace(/\/$/, '') || '/')

function groupOfPath(path: string): NavGroupId {
  const hit = allowed.value.find(item => path === item.path || path.startsWith(item.path + '/'))
  if (hit) return hit.group
  return path.startsWith('/config/') ? 'config' : 'monitoring'
}

const OPEN_KEY = (userName: string | null | undefined): string => `scada_nav_open_${userName || 'anon'}`
function readOpenKeys(userName: string | null | undefined): string[] {
  try {
    const raw = localStorage.getItem(OPEN_KEY(userName))
    if (!raw) return []
    const parsed = JSON.parse(raw) as unknown
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}
function persistOpenKeys(): void {
  try {
    localStorage.setItem(OPEN_KEY(auth.user?.userName), JSON.stringify(openKeys.value))
  } catch {
    /* quota */
  }
}

const storedOpenKeys = readOpenKeys(auth.user?.userName)
const openKeys = ref<string[]>([...storedOpenKeys])
const initialOpenKeys = Array.from(new Set([...storedOpenKeys, groupOfPath(route.path)]))

// 展开状态以用户为准：路由切换只补充当前分组，不再覆盖用户手动展开的分组。
let syncing = false
async function syncOpen(keys: string[]): Promise<void> {
  syncing = true
  await nextTick()
  for (const group of groups.value) {
    if (keys.includes(group.id)) menuRef.value?.open(group.id)
    else menuRef.value?.close(group.id)
  }
  if (filter.value.trim()) menuEl()?.scrollTo({ top: 0 })
  await nextTick()
  syncing = false
}

function handleOpen(index: string): void {
  scrollGroupIntoView(index)
  if (syncing || filter.value.trim()) return
  if (!openKeys.value.includes(index)) {
    openKeys.value = [...openKeys.value, index]
    persistOpenKeys()
  }
}

function handleClose(index: string): void {
  if (syncing || filter.value.trim()) return
  if (openKeys.value.includes(index)) {
    openKeys.value = openKeys.value.filter(key => key !== index)
    persistOpenKeys()
  }
}

// 展开的分组可能落在可视区外：把整组滚进菜单视口，避免用户点了却看不到内容。
function scrollGroupIntoView(index: string): void {
  void nextTick(() => {
    requestAnimationFrame(() => {
      const el = menuEl()
      const groupEl = el?.querySelector<HTMLElement>(`[data-test="nav-${index}-group"]`)
      if (!el || !groupEl) return
      const menuRect = el.getBoundingClientRect()
      const groupRect = groupEl.getBoundingClientRect()
      const overflow = Math.min(groupRect.bottom, groupRect.top + menuRect.height) - menuRect.bottom
      if (overflow > 0) {
        el.scrollTo({ top: el.scrollTop + overflow + 8, behavior: 'smooth' })
      }
    })
  })
}

// 直接打开组态子页时，菜单会在初始化时自动展开父级但不触发 open；
// 这里补滚动。菜单高度要等布局与展开动画结束才稳定，所以用 ResizeObserver 跟随尺寸变化。
let revealObserver: ResizeObserver | undefined
function revealActive(): void {
  void nextTick(() => {
    requestAnimationFrame(() => {
      scrollActiveIntoView()
      if (revealObserver || !menuEl()) return
      revealObserver = new ResizeObserver(() => scrollActiveIntoView())
      revealObserver.observe(menuEl()!)
    })
  })
}

function scrollActiveIntoView(): void {
  const el = menuEl()
  const active = el?.querySelector<HTMLElement>('.el-menu-item.is-active')
  if (!el || !active || !active.offsetHeight) return
  const menuRect = el.getBoundingClientRect()
  const itemRect = active.getBoundingClientRect()
  if (itemRect.bottom > menuRect.bottom) {
    el.scrollTop += itemRect.bottom - menuRect.bottom + 8
  } else if (itemRect.top < menuRect.top) {
    el.scrollTop -= menuRect.top - itemRect.top + 8
  }
}

onMounted(() => {
  revealActive()
  refreshLists()
  window.addEventListener('scada-nav-lists-changed', refreshLists)
})

onBeforeUnmount(() => {
  revealObserver?.disconnect()
  revealObserver = undefined
  window.removeEventListener('scada-nav-lists-changed', refreshLists)
})

function refreshLists(): void {
  const name = auth.user?.userName
  favPaths.value = loadFavorites(name)
  recentPaths.value = loadRecents(name)
}

watch(() => route.fullPath, () => {
  refreshLists()
  const groupId = groupOfPath(route.path)
  if (!filter.value.trim() && !openKeys.value.includes(groupId)) {
    openKeys.value = [...openKeys.value, groupId]
    persistOpenKeys()
    void nextTick(() => menuRef.value?.open(groupId))
  }
  revealActive()
})

watch(() => auth.user?.userName, (userName) => {
  const keys = readOpenKeys(userName)
  openKeys.value = [...keys]
  refreshLists()
  void syncOpen(Array.from(new Set([...keys, groupOfPath(route.path)])))
})

const allowedPaths = computed(() => new Set(allowed.value.map(item => item.path)))
const favSet = computed(() => new Set(favPaths.value))

function toItems(paths: string[]): NavItemDef[] {
  return paths
    .map(path => findNavByPath(path))
    .filter((item): item is NavItemDef => !!item && allowedPaths.value.has(item.path))
}

const favorites = computed(() => toItems(favPaths.value).slice(0, 6))
// 当前页不进「最近」，否则同一页面在收藏区与分组里重复出现，视觉与自动化选择器都容易混淆。
const recents = computed(() => toItems(recentPaths.value)
  .filter(item => item.path !== barePath.value && !favSet.value.has(item.path))
  .slice(0, 4))

function isCurrent(item: NavItemDef): boolean {
  return route.path === item.path || route.path.startsWith(item.path + '/')
}

function go(item: NavItemDef): void {
  if (isCurrent(item)) return
  void router.push(item.path)
}

function matches(item: NavItemDef): boolean {
  const q = filter.value.trim()
  if (!q) return true
  return fuzzyMatch(navSearchText(item, tr), q)
}

const groups = computed(() => Object.entries(NAV_GROUPS).map(([id, group]) => ({
  id, ...group, items: allowed.value.filter(item => item.group === id && matches(item))
})).filter(group => group.items.length))

const flatItems = computed(() => groups.value.flatMap(group => group.items))
const kbPath = computed(() => flatItems.value[kbIndex.value]?.path ?? '')

watch(filter, (value) => {
  kbIndex.value = -1
  const keys = value.trim() ? groups.value.map(group => group.id) : openKeys.value
  void syncOpen([...keys])
})

watch(flatItems, () => { kbIndex.value = -1 })

function onFilterKey(event: KeyboardEvent): void {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    const total = flatItems.value.length
    if (!total) return
    event.preventDefault()
    kbIndex.value = event.key === 'ArrowDown'
      ? (kbIndex.value + 1) % total
      : (kbIndex.value - 1 + total) % total
    void nextTick(() => {
      const el = menuEl()
      const row = el?.querySelector<HTMLElement>('.el-menu-item.is-kbd')
      if (!el || !row) return
      const menuRect = el.getBoundingClientRect()
      const rowRect = row.getBoundingClientRect()
      if (rowRect.bottom > menuRect.bottom) el.scrollTop += rowRect.bottom - menuRect.bottom + 8
      else if (rowRect.top < menuRect.top) el.scrollTop -= menuRect.top - rowRect.top + 8
    })
    return
  }
  if (event.key === 'Enter') {
    const item = flatItems.value[kbIndex.value]
    if (!item) return
    event.preventDefault()
    void router.push(item.path)
    kbIndex.value = -1
    return
  }
  if (event.key === 'Escape' && filter.value) {
    event.preventDefault()
    filter.value = ''
    kbIndex.value = -1
  }
}
</script>

<style scoped>
.nav-wrap {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}
.nav-filter {
  padding: 10px 12px 6px;
  flex-shrink: 0;
}
.nav-filter :deep(.el-input__wrapper) {
  background: rgba(255, 255, 255, 0.06);
  box-shadow: 0 0 0 1px var(--scada-dark-border) inset;
}
.nav-filter :deep(.el-input__inner) {
  color: var(--scada-dark-text);
}
.nav-filter :deep(.el-input__inner::placeholder) {
  color: var(--scada-dark-faint);
}
.nav-filter :deep(.el-input__prefix),
.nav-filter :deep(.el-input__suffix) {
  color: var(--scada-dark-muted);
}
.nav-pins {
  flex-shrink: 0;
  max-height: 34%;
  overflow: auto;
  padding: 2px 10px 6px;
  border-bottom: 1px solid var(--scada-dark-border);
}
.nav-pin-title {
  padding: 6px 8px 4px;
  font-size: 11px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--scada-dark-faint);
}
.nav-pin {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  height: 34px;
  padding: 0 10px;
  border: none;
  border-radius: var(--scada-radius-sm);
  background: transparent;
  color: var(--scada-dark-muted);
  font-family: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}
.nav-pin:hover {
  background: rgba(99, 230, 190, 0.08);
  color: var(--scada-dark-text);
}
.nav-pin.active {
  background: rgba(99, 230, 190, 0.14);
  color: var(--scada-accent);
  font-weight: 600;
}
.nav-pin-label {
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.nav-menu {
  flex: 1;
  overflow: auto;
  border-right: none;
}
.nav-empty {
  padding: 16px 18px;
  color: var(--scada-dark-faint);
  font-size: 12px;
}
.nav-menu :deep(.el-sub-menu.is-active > .el-sub-menu__title) {
  color: var(--scada-accent) !important;
}
.nav-menu :deep(.el-menu-item .el-icon),
.nav-menu :deep(.el-sub-menu__title .el-icon) {
  color: inherit;
  margin-right: 8px;
}
.nav-menu :deep(.el-menu-item.is-kbd) {
  background: rgba(99, 230, 190, 0.1) !important;
  outline: 1px solid var(--scada-accent);
  outline-offset: -1px;
}
.nav-menu :deep(.el-menu-item) {
  position: relative;
}
.nav-count {
  margin-left: auto;
  min-width: 18px;
  height: 18px;
  padding: 0 5px;
  border-radius: 9px;
  background: rgba(240, 120, 120, 0.22);
  color: #ff9b9b;
  font-size: 11px;
  font-weight: 600;
  line-height: 18px;
  text-align: center;
}
.nav-count.flood {
  background: var(--el-color-danger);
  color: #fff;
}
.nav-dot {
  position: absolute;
  top: 9px;
  left: 36px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: #ff9b9b;
}
.nav-dot.flood {
  background: var(--el-color-danger);
  box-shadow: 0 0 0 3px rgba(245, 108, 108, 0.2);
}
.nav-wrap.collapsed .nav-menu {
  padding-top: 6px;
}
</style>
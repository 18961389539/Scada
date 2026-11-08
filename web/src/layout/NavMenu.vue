<template>
  <nav ref="navRef" class="nav-wrap" :class="{ collapsed, 'can-drag': canDrag, 'is-sorting': drag }" :aria-label="$t('nav.primary')">
    <div v-if="!collapsed" class="nav-filter">
      <el-input
        v-model="filter"
        size="small"
        clearable
        data-test="nav-filter"
        :placeholder="$t('nav.menuFilter')"
        :aria-label="$t('nav.menuFilter')"
        :prefix-icon="Search"
        @compositionstart="composing = true"
        @compositionend="composing = false"
        @keydown="onFilterKey"
      />
      <button type="button" class="nav-adjust" data-test="nav-adjust" :aria-pressed="adjusting" @click="adjusting = !adjusting">{{ $t(adjusting ? 'navUx.adjustDone' : 'navUx.adjustMenu') }}</button>
    </div>
    <div
      v-if="!collapsed && !filter.trim() && favorites.length"
      class="nav-pins"
      data-test="nav-pins"
    >
      <div class="nav-pin-title"><el-icon><StarFilled /></el-icon>{{ $t('nav.favorites') }}</div>
      <div class="nav-pin-grid">
        <button
          v-for="item in favorites"
          :key="'fav-' + item.path"
          type="button"
          class="nav-pin"
          :class="[{ active: isCurrent(item), 'is-dragging': isDragging('fav', item.path) }, dropClass('fav', item.path)]"
          :aria-current="isCurrent(item) ? 'page' : undefined"
          :title="$t(item.titleKey)"
          :data-nav-path="item.path"
          :data-test="'nav-fav-' + item.id"
          @pointerdown="onPointerDown({ kind: 'fav', id: item.path }, $event)"
          @pointermove="onPointerMove"
          @pointerup="onPointerUp"
          @pointercancel="onPointerUp"
          @click.capture="swallowClick"
          @click="go(item)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span class="nav-pin-label">{{ $t(item.titleKey) }}</span>
        </button>
      </div>
    </div>
    <el-menu
      ref="menuRef"
      :default-active="activePath"
      router
      class="nav-menu"
      background-color="transparent"
      text-color="var(--scada-text-secondary)"
      active-text-color="var(--scada-accent-ink)"
      :default-openeds="initialOpenKeys"
      :collapse="collapsed"
      :collapse-transition="false"
      @open="handleOpen"
      @close="handleClose"
      @select="emit('navigate')"
    >
      <el-sub-menu
        v-for="group in groups"
        :key="group.id"
        :index="group.id"
        :class="[{ 'is-current-group': group.id === currentGroup, 'is-dragging': isDragging('group', group.id) }, dropClass('group', group.id)]"
        :data-test="'nav-' + group.id + '-group'"
        popper-class="nav-group-popup"
      >
        <template #title>
          <button
            v-if="canDrag"
            type="button"
            class="nav-grip"
            data-test="nav-drag"
            :title="$t('nav.dragReorder')"
            :aria-label="$t('nav.dragReorder')"
            @pointerdown.stop="onPointerDown({ kind: 'group', id: group.id }, $event)"
            @pointermove="onPointerMove"
            @pointerup="onPointerUp"
            @pointercancel="onPointerUp"
            @click.stop.prevent="finishGripClick"
            @keydown.stop
          ><el-icon><Rank /></el-icon></button>
          <el-icon><component :is="group.icon" /></el-icon>
          <span class="nav-group-label">{{ $t(group.titleKey) }}</span>
        </template>
        <el-menu-item
          v-for="item in group.items"
          :key="item.id"
          :index="item.path"
          :class="[{ 'is-kbd': item.path === kbPath, 'is-dragging': isDragging('item', item.id) }, dropClass('item', item.id)]"
          :aria-label="$t(item.titleKey)"
          :aria-current="isCurrent(item) ? 'page' : undefined"
          :data-test="'nav-item-' + item.id"
          @click.capture="swallowClick"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span class="nav-item-label">{{ $t(item.titleKey) }}</span>
          <template v-if="item.id === 'alarms' && alarmCount > 0">
            <i v-if="collapsed" class="nav-dot" :class="{ flood: alarmFlood }" data-test="nav-alarm-dot" />
            <span v-else class="nav-count" :class="{ flood: alarmFlood }" data-test="nav-alarm-count">
              {{ alarmCount > 99 ? '99+' : alarmCount }}
            </span>
          </template>
          <button
            v-if="canDrag"
            type="button"
            class="nav-grip"
            data-test="nav-drag"
            :title="$t('nav.dragReorder')"
            :aria-label="$t('nav.dragReorder')"
            @pointerdown.stop="onPointerDown({ kind: 'item', groupId: group.id, id: item.id }, $event)"
            @pointermove="onPointerMove"
            @pointerup="onPointerUp"
            @pointercancel="onPointerUp"
            @click.stop.prevent="finishGripClick"
            @keydown.stop
          ><el-icon><Rank /></el-icon></button>
          <button
            v-if="!collapsed"
            type="button"
            class="nav-star"
            :class="{ on: favSet.has(item.path) }"
            :title="$t(favSet.has(item.path) ? 'nav.removeFavorite' : 'nav.addFavorite', [$t(item.titleKey)])"
            :aria-label="$t(favSet.has(item.path) ? 'nav.removeFavorite' : 'nav.addFavorite', [$t(item.titleKey)])"
            :aria-pressed="favSet.has(item.path)"
            :data-test="'nav-star-' + item.id"
            @click.stop.prevent="starItem(item)"
            @mousedown.stop
            @keydown.stop
          >
            <el-icon><StarFilled v-if="favSet.has(item.path)" /><Star v-else /></el-icon>
          </button>
        </el-menu-item>
      </el-sub-menu>
      <div v-if="!groups.length" class="nav-empty" data-test="nav-filter-empty">
        {{ $t('nav.noResults') }}
      </div>
    </el-menu>
  </nav>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { MenuInstance } from 'element-plus'
import { useRoute, useRouter } from 'vue-router'
import { Rank, Search, Star, StarFilled } from '@element-plus/icons-vue'
import { can } from '../access/policies'
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
import { loadFavorites, saveFavorites, toggleFavorite } from '../nav/favorites'
import { displayOrder, loadNavOrder, moveRelative, reorderVisible, saveNavOrder, type NavOrder } from '../nav/order'

const props = withDefaults(defineProps<{
  alarmCount?: number
  alarmFlood?: boolean
  collapsed?: boolean
}>(), { alarmCount: 0, alarmFlood: false, collapsed: false })
const emit = defineEmits<{ navigate: [] }>()

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const filter = ref('')
const composing = ref(false)
const navRef = ref<HTMLElement | null>(null)
const menuRef = ref<MenuInstance>()
const kbIndex = ref(-1)
const favPaths = ref<string[]>([])
const navOrder = ref<NavOrder>(loadNavOrder(auth.user?.userName))

const alarmCount = computed(() => props.alarmCount)
const alarmFlood = computed(() => props.alarmFlood)
const collapsed = computed(() => props.collapsed)
const adjusting = ref(false)
const canDrag = computed(() => adjusting.value && !collapsed.value && !filter.value.trim())

function menuEl(): HTMLElement | null {
  return navRef.value?.querySelector<HTMLElement>('.nav-menu') ?? null
}

const allowed = computed(() => visibleNavItems(auth.user))
const activePath = computed(() => findNavByPath(route.path)?.path ?? route.path)

function groupOfPath(path: string): NavGroupId {
  const hit = findNavByPath(path)
  if (hit) return hit.group
  return path.startsWith('/config/') ? 'config' : 'monitoring'
}
const currentGroup = computed(() => groupOfPath(route.path))

const OPEN_KEY = (userName: string | null | undefined): string => `scada_nav_open_${userName || 'anon'}`
function readOpenKeys(userName: string | null | undefined): string[] | null {
  try {
    const raw = localStorage.getItem(OPEN_KEY(userName))
    if (raw == null) return null
    const parsed = JSON.parse(raw) as unknown
    return Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === 'string') : []
  } catch {
    return []
  }
}
function fallbackOpenKeys(): string[] {
  const current = groupOfPath(route.path)
  // 没有组态权限的账号默认只展开监视；当前页在别的分组时把那一组也打开。
  if (!can(auth.user, 'Configure')) return current === 'monitoring' ? ['monitoring'] : ['monitoring', current]
  return [current]
}
function persistOpenKeys(): void {
  try {
    localStorage.setItem(OPEN_KEY(auth.user?.userName), JSON.stringify(openKeys.value))
  } catch {
    /* quota */
  }
}

const storedOpenKeys = readOpenKeys(auth.user?.userName)
const openKeys = ref<string[]>([...(storedOpenKeys ?? fallbackOpenKeys())])
const initialOpenKeys = Array.from(new Set([...openKeys.value, groupOfPath(route.path)]))

// 展开状态以用户为准：路由切换只补充当前分组，不再覆盖用户手动展开的分组。
let syncing = false
async function syncOpen(keys: string[]): Promise<void> {
  if (collapsed.value) return
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
  if (collapsed.value) return
  scrollGroupIntoView(index)
  if (syncing || filter.value.trim()) return
  if (!openKeys.value.includes(index)) {
    openKeys.value = [...openKeys.value, index]
    persistOpenKeys()
  }
}

function handleClose(index: string): void {
  if (collapsed.value || syncing || filter.value.trim()) return
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
  navOrder.value = loadNavOrder(name)
}

// 侧栏星标：只切换收藏，不触发菜单导航；收藏区与命令面板通过 localStorage 事件同步刷新。
function starItem(item: NavItemDef): void {
  toggleFavorite(auth.user?.userName, item.path)
}

watch(() => route.fullPath, () => {
  refreshLists()
  const groupId = groupOfPath(route.path)
  if (!filter.value.trim() && !openKeys.value.includes(groupId)) {
    openKeys.value = [...openKeys.value, groupId]
    persistOpenKeys()
    if (!collapsed.value) void nextTick(() => menuRef.value?.open(groupId))
  }
  revealActive()
})

watch(() => auth.user?.userName, (userName) => {
  const keys = readOpenKeys(userName) ?? fallbackOpenKeys()
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

function isCurrent(item: NavItemDef): boolean {
  return activePath.value === item.path
}

function go(item: NavItemDef): void {
  emit('navigate')
  if (isCurrent(item)) return
  void router.push(item.path)
}

// 拖动手柄超过几像素才算排序，普通点击仍然跳转。筛选和收起时不排序，避免把不完整列表写进去。
type DragKind = 'item' | 'group' | 'fav'
type DragPayload = { kind: 'item'; groupId: string; id: string } | { kind: 'group' | 'fav'; id: string }
interface DropHint { kind: DragKind; id: string; groupId?: string; after: boolean; axis: 'x' | 'y' }
const drag = ref<DragPayload | null>(null)
const dropHint = ref<DropHint | null>(null)
let gesture: { id: number; x: number; y: number; payload: DragPayload; handle: HTMLElement } | null = null
let suppressClick = false
const DRAG_START = 6

function sameOrder(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((id, index) => id === b[index])
}
function isDragging(kind: DragKind, id: string): boolean {
  return drag.value?.kind === kind && drag.value.id === id
}
function dropClass(kind: DragKind, id: string): string[] {
  const hint = dropHint.value
  if (!hint || hint.kind !== kind || hint.id !== id) return []
  return [hint.after ? 'is-drop-after' : 'is-drop-before', hint.axis === 'x' ? 'is-drop-x' : 'is-drop-y']
}
function groupIdFrom(el: Element | null): string | null {
  return /^nav-(.+)-group$/.exec(el?.getAttribute('data-test') ?? '')?.[1] ?? null
}
function place(el: HTMLElement, x: number, y: number, grid: boolean): { after: boolean; axis: 'x' | 'y' } {
  const rect = el.getBoundingClientRect()
  if (!grid) return { after: y > rect.top + rect.height / 2, axis: 'y' }
  const dx = x - (rect.left + rect.width / 2)
  const dy = y - (rect.top + rect.height / 2)
  const axis = Math.abs(dx) > Math.abs(dy) ? 'x' as const : 'y' as const
  return { after: axis === 'x' ? dx > 0 : dy > 0, axis }
}
function hintAt(x: number, y: number, payload: DragPayload): DropHint | null {
  const el = document.elementFromPoint(x, y)
  if (!el || !navRef.value?.contains(el)) return null
  if (payload.kind === 'fav') {
    const pin = el.closest<HTMLElement>('[data-nav-path]')
    const path = pin?.getAttribute('data-nav-path')
    if (!pin || !path || path === payload.id) return null
    return { kind: 'fav', id: path, ...place(pin, x, y, true) }
  }
  if (payload.kind === 'item') {
    const item = el.closest<HTMLElement>('[data-test^="nav-item-"]')
    const groupId = groupIdFrom(item?.closest('[data-test$="-group"]') ?? null)
    const id = item?.getAttribute('data-test')?.slice('nav-item-'.length)
    if (!item || !id || !groupId || groupId !== payload.groupId || id === payload.id) return null
    return { kind: 'item', id, groupId, ...place(item, x, y, false) }
  }
  const group = el.closest<HTMLElement>('[data-test$="-group"]')
  const id = groupIdFrom(group)
  if (!group || !id || id === payload.id) return null
  const title = group.querySelector<HTMLElement>(':scope > .el-sub-menu__title')
  if (title && y > title.getBoundingClientRect().bottom) return { kind: 'group', id, after: true, axis: 'y' }
  return { kind: 'group', id, ...place(title ?? group, x, y, false) }
}
function commitDrop(payload: DragPayload, hint: DropHint): void {
  if (payload.kind !== hint.kind) return
  const name = auth.user?.userName
  if (payload.kind === 'fav' && hint.kind === 'fav') {
    const next = moveRelative(favPaths.value, payload.id, hint.id, hint.after)
    if (sameOrder(next, favPaths.value)) return
    favPaths.value = next
    saveFavorites(name, next)
    return
  }
  if (payload.kind === 'group' && hint.kind === 'group') {
    const catalog = Object.keys(NAV_GROUPS)
    const before = displayOrder(catalog, navOrder.value.groups)
    const saved = reorderVisible(navOrder.value.groups, catalog, payload.id, hint.id, hint.after)
    if (sameOrder(before, displayOrder(catalog, saved))) return
    navOrder.value = { ...navOrder.value, groups: saved }
    saveNavOrder(name, navOrder.value)
    return
  }
  if (payload.kind === 'item' && hint.kind === 'item' && hint.groupId === payload.groupId) {
    const catalog = allowed.value.filter(item => item.group === payload.groupId).map(item => item.id)
    const before = displayOrder(catalog, navOrder.value.items[payload.groupId])
    const saved = reorderVisible(navOrder.value.items[payload.groupId], catalog, payload.id, hint.id, hint.after)
    if (sameOrder(before, displayOrder(catalog, saved))) return
    navOrder.value = { ...navOrder.value, items: { ...navOrder.value.items, [payload.groupId]: saved } }
    saveNavOrder(name, navOrder.value)
  }
}
function onPointerDown(payload: DragPayload, event: PointerEvent): void {
  if (!canDrag.value || event.button !== 0 || gesture) return
  const handle = event.currentTarget
  if (!(handle instanceof HTMLElement)) return
  suppressClick = false
  gesture = { id: event.pointerId, x: event.clientX, y: event.clientY, payload, handle }
  handle.setPointerCapture(event.pointerId)
}
function onPointerMove(event: PointerEvent): void {
  const state = gesture
  if (!state || event.pointerId !== state.id) return
  if (!drag.value) {
    if (Math.hypot(event.clientX - state.x, event.clientY - state.y) < DRAG_START) return
    drag.value = state.payload
  }
  if (!canDrag.value) return
  dropHint.value = hintAt(event.clientX, event.clientY, state.payload)
  event.preventDefault()
}
function onPointerUp(event: PointerEvent): void {
  const state = gesture
  if (!state || event.pointerId !== state.id) return
  if (drag.value && canDrag.value) {
    const hint = dropHint.value ?? hintAt(event.clientX, event.clientY, state.payload)
    if (hint) commitDrop(state.payload, hint)
    suppressClick = true
  }
  drag.value = null
  dropHint.value = null
  gesture = null
  if (state.handle.hasPointerCapture(event.pointerId)) state.handle.releasePointerCapture(event.pointerId)
}
function swallowClick(event: MouseEvent): void {
  if (!suppressClick) return
  event.preventDefault()
  event.stopPropagation()
  suppressClick = false
}
function finishGripClick(event: MouseEvent): void {
  event.preventDefault()
  event.stopPropagation()
  suppressClick = false
}

function matches(item: NavItemDef): boolean {
  const q = filter.value.trim()
  if (!q) return true
  return fuzzyMatch(navSearchText(item, tr), q)
}

const groups = computed(() => {
  const built = Object.entries(NAV_GROUPS).flatMap(([id, group]) => {
    const visible = allowed.value.filter(item => item.group === id && matches(item))
    if (!visible.length) return []
    const byId = new Map(visible.map(item => [item.id, item]))
    const items = displayOrder(visible.map(item => item.id), navOrder.value.items[id]).flatMap(itemId => {
      const item = byId.get(itemId)
      return item ? [item] : []
    })
    return [{ id, ...group, items }]
  })
  const byId = new Map(built.map(group => [group.id, group]))
  return displayOrder(built.map(group => group.id), navOrder.value.groups).flatMap(id => {
    const group = byId.get(id)
    return group ? [group] : []
  })
})

const flatItems = computed(() => groups.value.flatMap(group => group.items))
const kbPath = computed(() => flatItems.value[kbIndex.value]?.path ?? '')

watch(filter, (value) => {
  kbIndex.value = -1
  const keys = value.trim() ? groups.value.map(group => group.id) : openKeys.value
  void syncOpen([...keys])
})

watch(collapsed, (value) => {
  kbIndex.value = -1
  if (value) {
    filter.value = ''
    return
  }
  void syncOpen(Array.from(new Set([...openKeys.value, currentGroup.value])))
  revealActive()
})

watch(flatItems, () => { kbIndex.value = -1 })

function onFilterKey(event: KeyboardEvent): void {
  if (event.isComposing || composing.value) return
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
    go(item)
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
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 10px 12px 6px;
  flex-shrink: 0;
}
.nav-filter :deep(.el-input) { flex: 1; min-width: 0; }
.nav-adjust {
  flex-shrink: 0;
  margin: 0;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--scada-text-secondary);
  font: inherit;
  font-size: 12px;
  line-height: 1.2;
  cursor: pointer;
}
.nav-adjust[aria-pressed='true'] { color: var(--scada-accent-ink); }
.nav-adjust:focus-visible { outline: 2px solid var(--scada-accent-ink); outline-offset: 2px; }
.nav-filter :deep(.el-input__wrapper) {
  background: var(--scada-panel-muted);
  box-shadow: 0 0 0 1px var(--scada-border) inset;
}
.nav-filter :deep(.el-input__inner) {
  color: var(--scada-text);
}
.nav-filter :deep(.el-input__inner::placeholder) {
  color: var(--scada-text-muted);
}
.nav-filter :deep(.el-input__prefix),
.nav-filter :deep(.el-input__suffix) {
  color: var(--scada-text-secondary);
}
.nav-pins {
  flex-shrink: 0;
  max-height: 34%;
  overflow: auto;
  padding: 2px 10px 6px;
  border-bottom: 1px solid var(--scada-border);
}
.nav-pin-title {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px;
  font-size: 11px;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--scada-text-muted);
}
.nav-pin-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 4px;
}
.nav-pin {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  min-width: 0;
  height: 30px;
  padding: 0 6px;
  border: none;
  border-radius: var(--scada-radius-sm);
  background: transparent;
  color: var(--scada-text-secondary);
  font-family: inherit;
  font-size: 12px;
  text-align: left;
  cursor: pointer;
}
.nav-pin:hover {
  background: color-mix(in srgb, var(--scada-accent-ink) 8%, transparent);
  color: var(--scada-text);
}
.nav-pin:focus-visible {
  outline: 2px solid var(--scada-accent-ink);
  outline-offset: -2px;
}
.nav-pin .el-icon { flex-shrink: 0; }
.nav-pin.active {
  background: color-mix(in srgb, var(--scada-accent-ink) 14%, transparent);
  color: var(--scada-accent-ink);
  font-weight: 600;
}
.nav-pin-label {
  min-width: 0;
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
  color: var(--scada-text-muted);
  font-size: 12px;
}
.nav-menu :deep(.el-sub-menu.is-current-group > .el-sub-menu__title) {
  color: var(--scada-accent-ink) !important;
}
.nav-item-label,
.nav-group-label {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}
.nav-item-label { flex: 1; }
.nav-group-label { padding-right: 12px; }
.nav-menu :deep(.el-menu-item .el-icon),
.nav-menu :deep(.el-sub-menu__title .el-icon) {
  color: inherit;
  margin-right: 8px;
}
.nav-menu :deep(.el-menu-item.is-kbd) {
  background: color-mix(in srgb, var(--scada-accent-ink) 10%, transparent) !important;
  outline: 1px solid var(--scada-accent-ink);
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
  background: var(--scada-alarm-critical-bg);
  color: var(--scada-alarm-critical);
  font-size: 11px;
  font-weight: 600;
  line-height: 18px;
  text-align: center;
}
.nav-count.flood {
  background: var(--el-color-danger);
  color: var(--scada-on-solid);
}
/* 星标默认悬停显现；已收藏时常驻，避免侧栏过密。 */
.nav-star {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  margin-left: 4px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--scada-text-muted);
  cursor: pointer;
  opacity: 0;
}
.nav-menu :deep(.el-menu-item:hover) .nav-star,
.nav-menu :deep(.el-menu-item:focus-within) .nav-star,
.nav-star.on { opacity: 1; }
.nav-star:hover { background: color-mix(in srgb, var(--scada-accent-ink) 12%, transparent); color: var(--scada-accent-ink); }
.nav-star.on { color: var(--scada-accent-ink); }
.nav-star:focus-visible { outline: 2px solid var(--scada-accent-ink); outline-offset: -2px; }
.nav-grip {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 24px;
  margin-left: 2px;
  padding: 0;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--scada-text-muted);
  cursor: grab;
  touch-action: none;
}
.nav-menu :deep(.el-sub-menu__title) .nav-grip { margin-left: 0; margin-right: 4px; }
.nav-grip:hover { background: color-mix(in srgb, var(--scada-accent-ink) 12%, transparent); color: var(--scada-accent-ink); }
.nav-grip:focus-visible { outline: 2px solid var(--scada-accent-ink); outline-offset: -2px; }
.nav-wrap.can-drag .nav-pin { cursor: grab; }
.nav-wrap.is-sorting { user-select: none; }
.nav-wrap.is-sorting .nav-pin,
.nav-wrap.is-sorting .nav-grip { cursor: grabbing; }
.nav-menu :deep(.el-menu-item.is-dragging),
.nav-menu :deep(.el-sub-menu.is-dragging > .el-sub-menu__title),
.nav-pin.is-dragging { opacity: 0.45; }
.nav-menu :deep(.el-menu-item.is-drop-before),
.nav-menu :deep(.el-sub-menu.is-drop-before > .el-sub-menu__title),
.nav-pin.is-drop-before.is-drop-y { box-shadow: inset 0 2px 0 var(--scada-accent-ink); }
.nav-menu :deep(.el-menu-item.is-drop-after),
.nav-menu :deep(.el-sub-menu.is-drop-after > .el-sub-menu__title),
.nav-pin.is-drop-after.is-drop-y { box-shadow: inset 0 -2px 0 var(--scada-accent-ink); }
.nav-pin.is-drop-before.is-drop-x { box-shadow: inset 2px 0 0 var(--scada-accent-ink); }
.nav-pin.is-drop-after.is-drop-x { box-shadow: inset -2px 0 0 var(--scada-accent-ink); }
.nav-dot {
  position: absolute;
  top: 9px;
  left: 36px;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--scada-alarm-critical);
}
.nav-dot.flood {
  background: var(--el-color-danger);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--el-color-danger) 20%, transparent);
}
.nav-wrap.collapsed .nav-menu {
  padding-top: 6px;
}
:global(.nav-group-popup.el-popper) {
  background: var(--scada-panel) !important;
  border-color: var(--scada-border) !important;
}
:global(.nav-group-popup .el-menu--popup) {
  max-height: calc(100vh - 24px);
  overflow-y: auto;
}
:global(.nav-group-popup .el-menu-item) {
  height: 42px;
  line-height: 42px;
}
:global(.nav-group-popup .el-menu-item:hover) {
  background: color-mix(in srgb, var(--scada-accent-ink) 8%, transparent) !important;
}
:global(.nav-group-popup .el-menu-item.is-active) {
  background: color-mix(in srgb, var(--scada-accent-ink) 14%, transparent) !important;
  color: var(--scada-accent-ink) !important;
  font-weight: 600;
}
</style>
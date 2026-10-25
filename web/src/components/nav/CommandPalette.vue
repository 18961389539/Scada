<template>
  <el-dialog
    v-model="open"
    :show-close="false"
    width="560px"
    align-center
    append-to-body
    class="cmd-palette-dialog"
    data-test="command-palette"
    @opened="onOpened"
    @closed="query = ''; active = 0"
  >
    <div class="cmd-palette" @keydown="onKey">
      <div class="cmd-input-row">
        <el-icon :size="18" class="cmd-search-icon"><Search /></el-icon>
        <input
          ref="inputEl"
          v-model="query"
          class="cmd-input"
          data-test="command-palette-input"
          :placeholder="$t('nav.palettePlaceholder')"
          autocomplete="off"
          spellcheck="false"
          @input="active = 0"
        />
        <kbd class="cmd-kbd">Esc</kbd>
      </div>

      <div v-if="!query.trim() && favorites.length" class="cmd-section">
        <div class="cmd-section-title">{{ $t('nav.favorites') }}</div>
        <button
          v-for="(item, i) in favorites"
          :key="'f-' + item.path"
          type="button"
          class="cmd-row"
          :class="{ active: flatIndex(i, 'fav') === active }"
          :data-test="'command-palette-item-' + item.id"
          @mouseenter="active = flatIndex(i, 'fav')"
          @click="go(item)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span class="cmd-title">{{ $t(item.titleKey) }}</span>
          <span class="cmd-path">{{ item.path }}</span>
          <el-button text size="small" class="cmd-star on" data-test="command-palette-star" @click.stop="star(item)">
            <el-icon><StarFilled /></el-icon>
          </el-button>
        </button>
      </div>

      <div v-if="!query.trim() && recents.length" class="cmd-section">
        <div class="cmd-section-title">{{ $t('nav.recent') }}</div>
        <button
          v-for="(item, i) in recents"
          :key="'r-' + item.path"
          type="button"
          class="cmd-row"
          :class="{ active: flatIndex(i, 'recent') === active }"
          :data-test="'command-palette-item-' + item.id"
          @mouseenter="active = flatIndex(i, 'recent')"
          @click="go(item)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span class="cmd-title">{{ $t(item.titleKey) }}</span>
          <span class="cmd-path">{{ item.path }}</span>
          <el-button text size="small" class="cmd-star" :class="{ on: favSet.has(item.path) }" @click.stop="star(item)">
            <el-icon><StarFilled v-if="favSet.has(item.path)" /><Star v-else /></el-icon>
          </el-button>
        </button>
      </div>

      <div v-if="results.length" class="cmd-section">
        <div class="cmd-section-title">{{ query.trim() ? $t('nav.results') : $t('nav.allPages') }}</div>
        <button
          v-for="(item, i) in results"
          :key="'s-' + item.path"
          type="button"
          class="cmd-row"
          :class="{ active: flatIndex(i, 'result') === active }"
          :data-test="'command-palette-item-' + item.id"
          @mouseenter="active = flatIndex(i, 'result')"
          @click="go(item)"
        >
          <el-icon><component :is="item.icon" /></el-icon>
          <span class="cmd-title">{{ $t(item.titleKey) }}</span>
          <span v-if="item.group === 'config'" class="cmd-group">{{ $t(CONFIG_GROUP_KEY) }}</span>
          <span class="cmd-path">{{ item.path }}</span>
          <el-button text size="small" class="cmd-star" :class="{ on: favSet.has(item.path) }" data-test="command-palette-star" @click.stop="star(item)">
            <el-icon><StarFilled v-if="favSet.has(item.path)" /><Star v-else /></el-icon>
          </el-button>
        </button>
      </div>

      <div v-else-if="query.trim()" class="cmd-empty" data-test="command-palette-empty">{{ $t('nav.noResults') }}</div>

      <div class="cmd-hint">
        <span><kbd>↑</kbd><kbd>↓</kbd> {{ $t('nav.hintNav') }}</span>
        <span><kbd>Enter</kbd> {{ $t('nav.hintOpen') }}</span>
        <span><kbd>Ctrl</kbd><kbd>K</kbd> {{ $t('nav.hintToggle') }}</span>
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { Search, Star, StarFilled } from '@element-plus/icons-vue'
import { useAuthStore } from '../../stores/auth'
import { tr } from '../../i18n'
import {
  CONFIG_GROUP_KEY,
  findNavByPath,
  fuzzyMatch,
  navSearchText,
  visibleNavItems,
  type NavItemDef
} from '../../nav/catalog'
import { loadFavorites, toggleFavorite } from '../../nav/favorites'
import { loadRecents } from '../../nav/recents'

const open = defineModel<boolean>({ default: false })
const router = useRouter()
const auth = useAuthStore()
const query = ref('')
const active = ref(0)
const inputEl = ref<HTMLInputElement | null>(null)
const favPaths = ref<string[]>([])
const recentPaths = ref<string[]>([])

const allowed = computed(() => visibleNavItems(auth.user))
const favSet = computed(() => new Set(favPaths.value))

const favorites = computed(() =>
  favPaths.value.map((p) => findNavByPath(p)).filter((x): x is NavItemDef => !!x && allowed.value.some((a) => a.path === x.path))
)

const recents = computed(() =>
  recentPaths.value
    .map((p) => findNavByPath(p))
    .filter((x): x is NavItemDef => !!x && allowed.value.some((a) => a.path === x.path))
    .filter((x) => !favSet.value.has(x.path))
)

const results = computed(() => {
  const q = query.value.trim()
  const list = allowed.value
  if (!q) {
    // 无搜索词：若没有收藏/最近，展示全部便于浏览；否则只靠上方分区。
    if (favorites.value.length || recents.value.length) return []
    return list
  }
  return list.filter((item) => fuzzyMatch(navSearchText(item, tr), q))
})

function flatList(): NavItemDef[] {
  if (query.value.trim()) return results.value
  return [...favorites.value, ...recents.value, ...results.value]
}

type Section = 'fav' | 'recent' | 'result'
function flatIndex(i: number, section: Section): number {
  if (query.value.trim()) return i
  let offset = 0
  if (section === 'fav') return i
  offset += favorites.value.length
  if (section === 'recent') return offset + i
  offset += recents.value.length
  return offset + i
}

function refreshLists(): void {
  const name = auth.user?.userName
  favPaths.value = loadFavorites(name)
  recentPaths.value = loadRecents(name)
}

watch(open, (v) => { if (v) refreshLists() })

async function onOpened(): Promise<void> {
  refreshLists()
  active.value = 0
  await nextTick()
  inputEl.value?.focus()
  inputEl.value?.select()
}

function go(item: NavItemDef): void {
  open.value = false
  void router.push(item.path)
}

function star(item: NavItemDef): void {
  favPaths.value = toggleFavorite(auth.user?.userName, item.path)
}

function onKey(e: KeyboardEvent): void {
  const list = flatList()
  if (e.key === 'ArrowDown') {
    e.preventDefault()
    active.value = list.length ? (active.value + 1) % list.length : 0
  } else if (e.key === 'ArrowUp') {
    e.preventDefault()
    active.value = list.length ? (active.value - 1 + list.length) % list.length : 0
  } else if (e.key === 'Enter') {
    e.preventDefault()
    const item = list[active.value]
    if (item) go(item)
  }
}
</script>

<style scoped>
.cmd-palette { display: flex; flex-direction: column; gap: 4px; margin: -8px -4px; }
.cmd-input-row {
  display: flex; align-items: center; gap: 10px;
  padding: 4px 8px 12px; border-bottom: 1px solid var(--scada-border);
}
.cmd-search-icon { color: var(--scada-text-muted); flex-shrink: 0; }
.cmd-input {
  flex: 1; border: none; outline: none; background: transparent;
  font-size: 16px; color: var(--scada-text); padding: 6px 0;
}
.cmd-input::placeholder { color: var(--scada-text-muted); }
.cmd-kbd {
  font: 11px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace;
  padding: 2px 6px; border-radius: 4px;
  background: var(--scada-panel-muted); color: var(--scada-text-muted);
  border: 1px solid var(--scada-border);
}
.cmd-section { padding: 8px 0 4px; max-height: 360px; overflow: auto; }
.cmd-section-title {
  font-size: 11px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase;
  color: var(--scada-text-muted); padding: 4px 10px 6px;
}
.cmd-row {
  display: flex; align-items: center; gap: 10px; width: 100%;
  border: none; background: transparent; text-align: left;
  padding: 8px 10px; border-radius: 8px; cursor: pointer;
  color: var(--scada-text); font-size: 14px;
}
.cmd-row:hover, .cmd-row.active { background: var(--scada-panel-muted); }
.cmd-row .el-icon { color: var(--scada-text-secondary); flex-shrink: 0; }
.cmd-title { font-weight: 500; }
.cmd-group {
  font-size: 11px; color: var(--scada-text-muted);
  padding: 1px 6px; border-radius: 4px; background: var(--scada-panel-muted);
}
.cmd-path { margin-left: auto; font-size: 12px; color: var(--scada-text-muted); font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.cmd-star { margin-left: 4px; color: var(--scada-text-muted); }
.cmd-star.on { color: #f5a623; }
.cmd-empty { padding: 28px 12px; text-align: center; color: var(--scada-text-muted); }
.cmd-hint {
  display: flex; flex-wrap: wrap; gap: 12px; padding: 10px 8px 2px;
  border-top: 1px solid var(--scada-border); font-size: 12px; color: var(--scada-text-muted);
}
.cmd-hint kbd {
  font: 10px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace;
  padding: 1px 4px; margin-right: 2px; border-radius: 3px;
  background: var(--scada-panel-muted); border: 1px solid var(--scada-border);
}
</style>

<style>
.cmd-palette-dialog.el-dialog { border-radius: 12px; overflow: hidden; }
.cmd-palette-dialog .el-dialog__header { display: none; }
.cmd-palette-dialog .el-dialog__body { padding: 16px 18px 12px; }
</style>

<template>
  <el-dialog
    v-model="open"
    :title="$t('nav.search')"
    :show-close="false"
    width="560px"
    align-center
    append-to-body
    class="cmd-palette-dialog"
    data-test="command-palette"
    @opened="onOpened"
    @closed="query = ''; active = 0"
  >
    <div class="cmd-palette">
      <div class="cmd-input-row">
        <el-icon :size="18" class="cmd-search-icon"><Search /></el-icon>
        <input
          ref="inputEl"
          v-model="query"
          class="cmd-input"
          data-test="command-palette-input"
          :placeholder="$t('nav.palettePlaceholder')"
          :aria-label="$t('nav.search')"
          aria-describedby="command-palette-hint"
          autocomplete="off"
          spellcheck="false"
          @input="active = 0"
          @keydown="onKey"
          @compositionstart="composing = true"
          @compositionend="composing = false"
        />
        <el-button v-if="query" text circle :aria-label="$t('nav.clearSearch')" data-test="command-palette-clear" @click="clearSearch"><el-icon><Close /></el-icon></el-button>
        <kbd class="cmd-kbd">Esc</kbd>
      </div>

      <div ref="resultsEl" class="cmd-results" data-test="command-palette-results">
        <section v-for="section in sections" :key="section.id" class="cmd-section" :aria-label="$t(section.titleKey)">
          <div class="cmd-section-title">{{ $t(section.titleKey) }}</div>
          <div
            v-for="(item, i) in section.items"
            :key="item.path"
            class="cmd-row"
            :class="{ active: section.start + i === active }"
            :data-test="'command-palette-item-' + item.id"
            @mouseenter="active = section.start + i"
          >
            <button type="button" class="cmd-open" @focus="active = section.start + i" @click="go(item)">
              <el-icon><component :is="item.icon" /></el-icon>
              <span class="cmd-title">{{ $t(item.titleKey) }}</span>
              <span v-if="item.group === 'config'" class="cmd-group">{{ $t(CONFIG_GROUP_KEY) }}</span>
              <span class="cmd-path">{{ item.path }}</span>
            </button>
            <el-button
              text size="small" class="cmd-star" :class="{ on: favSet.has(item.path) }"
              :aria-label="$t(favSet.has(item.path) ? 'nav.removeFavorite' : 'nav.addFavorite', [$t(item.titleKey)])"
              :aria-pressed="favSet.has(item.path)"
              data-test="command-palette-star"
              @click="star(item)"
            >
              <el-icon><StarFilled v-if="favSet.has(item.path)" /><Star v-else /></el-icon>
            </el-button>
          </div>
        </section>
        <div v-if="query.trim() && !results.length" class="cmd-empty" data-test="command-palette-empty">
          <p>{{ $t('nav.noResults') }}</p>
          <el-button link type="primary" @click="clearSearch">{{ $t('nav.clearSearch') }}</el-button>
        </div>
      </div>

      <div id="command-palette-hint" class="cmd-hint">
        <span><kbd>↑</kbd><kbd>↓</kbd> {{ $t('nav.hintNav') }}</span>
        <span><kbd>Enter</kbd> {{ $t('nav.hintOpen') }}</span>
        <span><kbd>{{ modKey }}</kbd><kbd>K</kbd> {{ $t('nav.hintToggle') }}</span>
        <span class="cmd-count" role="status" data-test="command-palette-count">{{ $t('nav.resultCount', [flatItems.length]) }}</span>
      </div>
    </div>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { Close, Search, Star, StarFilled } from '@element-plus/icons-vue'
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
const composing = ref(false)
const inputEl = ref<HTMLInputElement | null>(null)
const resultsEl = ref<HTMLElement | null>(null)
const modKey = /Mac|iPhone|iPod|iPad/i.test(navigator.platform) ? '⌘' : 'Ctrl'
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
    const shown = new Set([...favorites.value, ...recents.value].map(item => item.path))
    return list.filter(item => !shown.has(item.path))
  }
  return list.filter((item) => fuzzyMatch(navSearchText(item, tr), q))
})

const sections = computed(() => {
  const groups = query.value.trim()
    ? [{ id: 'result', titleKey: 'nav.results', items: results.value }]
    : [
        { id: 'fav', titleKey: 'nav.favorites', items: favorites.value },
        { id: 'recent', titleKey: 'nav.recent', items: recents.value },
        { id: 'result', titleKey: favorites.value.length || recents.value.length ? 'nav.otherPages' : 'nav.allPages', items: results.value }
      ]
  let start = 0
  return groups.filter(group => group.items.length).map(group => {
    const section = { ...group, start }
    start += group.items.length
    return section
  })
})
const flatItems = computed(() => sections.value.flatMap(section => section.items))

// Keep the keyboard selection inside the one scrollable result area.
function revealActive(): void {
  const container = resultsEl.value
  const row = container?.querySelector<HTMLElement>('.cmd-row.active')
  if (!container || !row) return
  if (active.value === 0) { container.scrollTop = 0; return }
  const bounds = container.getBoundingClientRect(), item = row.getBoundingClientRect()
  if (item.bottom > bounds.bottom) container.scrollTop += item.bottom - bounds.bottom
  else if (item.top < bounds.top) container.scrollTop -= bounds.top - item.top
}
watch(active, () => { void nextTick(revealActive) })
watch(flatItems, () => {
  active.value = Math.min(active.value, Math.max(0, flatItems.value.length - 1))
  void nextTick(revealActive)
})

function refreshLists(): void {
  const name = auth.user?.userName
  favPaths.value = [...new Set(loadFavorites(name))]
  recentPaths.value = [...new Set(loadRecents(name))]
}

watch(open, (v) => { if (v) refreshLists() })

async function onOpened(): Promise<void> {
  refreshLists()
  active.value = 0
  composing.value = false
  await nextTick()
  inputEl.value?.focus()
  inputEl.value?.select()
  if (resultsEl.value) resultsEl.value.scrollTop = 0
  revealActive()
}

function go(item: NavItemDef): void {
  open.value = false
  void router.push(item.path)
}

async function star(item: NavItemDef): Promise<void> {
  const selector = `[data-test="command-palette-item-${item.id}"] .cmd-star`
  const restoreFocus = resultsEl.value?.querySelector(selector) === document.activeElement
  favPaths.value = toggleFavorite(auth.user?.userName, item.path)
  const index = flatItems.value.findIndex(entry => entry.path === item.path)
  active.value = Math.max(0, index)
  await nextTick()
  if (restoreFocus) resultsEl.value?.querySelector<HTMLElement>(selector)?.focus({ preventScroll: true })
}

function clearSearch(): void {
  query.value = ''
  active.value = 0
  inputEl.value?.focus()
}

function onKey(e: KeyboardEvent): void {
  if (composing.value || e.isComposing || e.keyCode === 229) return
  const list = flatItems.value
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
  flex: 1; min-width: 0; border: none; outline: none; background: transparent;
  font-size: 16px; color: var(--scada-text); padding: 6px 0;
}
.cmd-input::placeholder { color: var(--scada-text-muted); }
.cmd-kbd {
  font: 11px/1.4 ui-monospace, SFMono-Regular, Menlo, monospace;
  padding: 2px 6px; border-radius: 4px;
  background: var(--scada-panel-muted); color: var(--scada-text-muted);
  border: 1px solid var(--scada-border);
}
.cmd-results { max-height: min(55dvh, 420px); overflow-y: auto; overscroll-behavior: contain; }
.cmd-section { padding: 8px 0 4px; }
.cmd-section-title {
  font-size: 11px; font-weight: 600; letter-spacing: 0.04em; text-transform: uppercase;
  color: var(--scada-text-muted); padding: 4px 10px 6px;
}
.cmd-row { display: flex; align-items: center; width: 100%; border-radius: 8px; }
.cmd-open {
  display: flex; align-items: center; gap: 10px; flex: 1; min-width: 0;
  border: none; background: transparent; text-align: left;
  padding: 10px; border-radius: 8px; cursor: pointer; font-family: inherit;
  color: var(--scada-text); font-size: 14px;
}
.cmd-row:hover, .cmd-row.active { background: var(--scada-panel-muted); }
.cmd-open:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.cmd-row .el-icon { color: var(--scada-text-secondary); flex-shrink: 0; }
.cmd-title { font-weight: 500; overflow-wrap: anywhere; }
.cmd-group {
  font-size: 11px; color: var(--scada-text-muted);
  padding: 1px 6px; border-radius: 4px; background: var(--scada-panel-muted);
}
.cmd-path { margin-left: auto; font-size: 12px; color: var(--scada-text-muted); font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
.cmd-star { margin: 0 4px; flex-shrink: 0; color: var(--scada-text-muted); }
.cmd-star.on { color: #f5a623; }
.cmd-star .el-icon { color: inherit; }
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
.cmd-count { margin-left: auto; }
@media (max-width: 600px) {
  .cmd-path, .cmd-group { display: none; }
  .cmd-open { min-height: 44px; }
}
</style>

<style>
.cmd-palette-dialog.el-dialog { max-width: calc(100vw - 24px); border-radius: 12px; overflow: hidden; }
.cmd-palette-dialog .el-dialog__header { position: absolute; width: 1px; height: 1px; padding: 0; overflow: hidden; clip-path: inset(50%); }
.cmd-palette-dialog .el-dialog__body { padding: 16px 18px 12px; }
</style>

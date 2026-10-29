<template>
  <nav class="screen-directory" :aria-label="$t('runtimeImprovements.screenDirectory')" data-test="nav-list">
    <div class="directory-heading">{{ $t('runtimeImprovements.screenDirectory') }}</div>
    <div class="directory-search"><el-input v-model="search" size="small" clearable :placeholder="$t('runtimeImprovements.searchScreens')" :aria-label="$t('runtimeImprovements.searchScreens')" data-test="directory-search" /></div>
    <div class="directory-filters" :aria-label="$t('runtimeImprovements.screenFilter')">
      <button v-for="option in filters" :key="option.value" type="button" :aria-pressed="filter === option.value" :data-test="`directory-filter-${option.value}`" @click="filter = option.value">{{ $t(option.label) }}</button>
    </div>
    <div class="directory-results">
      <section v-for="group in groups" :key="group.name" class="directory-group">
        <h3 v-if="group.name">{{ group.name }}</h3>
        <div v-for="screen in group.screens" :key="screen.id" class="directory-row" :class="{ current: screen.id === activeScreenId }">
          <button type="button" class="directory-screen" :data-test="`nav-${screen.id}`" :aria-current="screen.id === activeScreenId ? 'page' : undefined" :title="screen.name" @click="navigate(screen.id)">{{ screen.name }}</button>
          <button type="button" class="directory-favorite" :class="{ selected: favoriteIds.includes(screen.id) }" :aria-pressed="favoriteIds.includes(screen.id)" :aria-label="$t(favoriteIds.includes(screen.id) ? 'runtimeImprovements.removeFavorite' : 'runtimeImprovements.addFavorite', [screen.name])" :title="$t(favoriteIds.includes(screen.id) ? 'runtimeImprovements.removeFavorite' : 'runtimeImprovements.addFavorite', [screen.name])" :data-test="`directory-favorite-${screen.id}`" @click="toggleFavorite(screen.id)">{{ favoriteIds.includes(screen.id) ? '★' : '☆' }}</button>
        </div>
      </section>
      <p v-if="!groups.length" class="directory-empty" data-test="directory-empty">{{ $t(search ? 'runtimeImprovements.noMatchingScreens' : filter === 'favorites' ? 'runtimeImprovements.noFavoriteScreens' : filter === 'recent' ? 'runtimeImprovements.noRecentScreens' : 'runtimeImprovements.noScreens') }}</p>
    </div>
  </nav>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ScreenSummary } from '../../screens/model'
import { tr } from '../../i18n'

const props = defineProps<{ screens: ScreenSummary[]; activeScreenId?: number; storageKey: string }>()
const emit = defineEmits<{ navigate: [screenId: number] }>()
const search = ref(''), filter = ref<'all' | 'favorites' | 'recent'>('all')
const favoriteIds = ref<number[]>([]), recentIds = ref<number[]>([])
const key = computed(() => `scada-console-directory:${encodeURIComponent(props.storageKey)}`)
const changeEvent = 'scada-console-directory-change'
const filters = [
  { value: 'all', label: 'runtimeImprovements.allScreens' },
  { value: 'favorites', label: 'runtimeImprovements.favorites' },
  { value: 'recent', label: 'runtimeImprovements.recent' }
] as const
const ids = (value: unknown): number[] => Array.isArray(value) ? [...new Set(value.filter((id): id is number => Number.isSafeInteger(id) && id > 0))].slice(0, 1000) : []
function load(): void {
  try {
    const stored = JSON.parse(localStorage.getItem(key.value) ?? '{}')
    favoriteIds.value = ids(stored?.favorites)
    recentIds.value = ids(stored?.recent).slice(0, 20)
  } catch { favoriteIds.value = []; recentIds.value = [] }
}
function persist(): void {
  try { localStorage.setItem(key.value, JSON.stringify({ favorites: favoriteIds.value, recent: recentIds.value })) } catch { /* Preferences remain available in this window. */ }
  window.dispatchEvent(new CustomEvent(changeEvent, { detail: key.value }))
}
function remember(id: number): void {
  if (!props.screens.some(screen => screen.id === id && screen.publishedRevision != null) || recentIds.value[0] === id) return
  recentIds.value = [id, ...recentIds.value.filter(previous => previous !== id)].slice(0, 20)
  persist()
}
function navigate(id: number): void { remember(id); emit('navigate', id) }
function toggleFavorite(id: number): void {
  favoriteIds.value = favoriteIds.value.includes(id) ? favoriteIds.value.filter(previous => previous !== id) : [...favoriteIds.value, id]
  persist()
}
watch(key, () => { search.value = ''; filter.value = 'all'; load() }, { immediate: true })
watch([() => props.activeScreenId, () => props.screens, key], ([id]) => { if (id != null) remember(id) }, { immediate: true })
function sync(event: Event): void {
  if ((event instanceof StorageEvent && event.key === key.value) || (event instanceof CustomEvent && event.detail === key.value)) load()
}
onMounted(() => { window.addEventListener('storage', sync); window.addEventListener(changeEvent, sync) })
onBeforeUnmount(() => { window.removeEventListener('storage', sync); window.removeEventListener(changeEvent, sync) })
const groups = computed(() => {
  const query = search.value.trim().toLocaleLowerCase()
  let screens = props.screens.filter(screen => screen.publishedRevision != null && (!query || screen.name.toLocaleLowerCase().includes(query)))
  if (filter.value === 'favorites') screens = screens.filter(screen => favoriteIds.value.includes(screen.id))
  if (filter.value === 'recent') {
    const byId = new Map(screens.map(screen => [screen.id, screen]))
    screens = recentIds.value.flatMap(id => byId.has(id) ? [byId.get(id)!] : [])
    return screens.length ? [{ name: '', screens }] : []
  }
  const grouped = new Map<string, ScreenSummary[]>()
  for (const screen of [...screens].sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }))) {
    const slash = screen.name.indexOf('/')
    const name = slash > 0 ? screen.name.slice(0, slash).trim() : ''
    const groupName = name || (screens.some(item => item.name.indexOf('/') > 0) ? tr('runtimeImprovements.otherScreens') : '')
    if (!grouped.has(groupName)) grouped.set(groupName, [])
    grouped.get(groupName)!.push(screen)
  }
  return [...grouped].map(([name, entries]) => ({ name, screens: entries }))
})
</script>

<style scoped>
.screen-directory { display: flex; flex-direction: column; height: 100%; min-height: 0; color: #dce6f0; font-size: 12px; }
.directory-heading { padding: 6px 8px; font-weight: 600; background: #1b2838; }
.directory-search { padding: 8px; }
.directory-filters { display: flex; flex-wrap: wrap; gap: 4px; padding: 0 8px 8px; }
.directory-filters button { flex: 1; min-height: 28px; border: 1px solid #3b5878; border-radius: 4px; background: transparent; color: inherit; cursor: pointer; font-size: 12px; padding: 3px 6px; }
.directory-filters button[aria-pressed="true"] { background: #244d46; color: #aaf0d0; border-color: #61ddaa; }
.directory-results { overflow: auto; min-height: 0; flex: 1; }
.directory-group h3 { font-size: 11px; color: #a7bacd; background: #172438; padding: 6px 8px; margin: 0; overflow-wrap: anywhere; }
.directory-row { display: flex; align-items: stretch; border-bottom: 1px solid #1f2d3f; }
.directory-screen, .directory-favorite { border: 0; background: transparent; color: inherit; cursor: pointer; }
.directory-screen { min-width: 0; flex: 1; text-align: left; padding: 9px 8px; overflow-wrap: anywhere; }
.directory-favorite { flex-shrink: 0; width: 30px; min-height: 34px; font-size: 17px; color: #a7bacd; }
.directory-favorite.selected { color: #f4cd72; }
.directory-row.current { background: #203c3a; box-shadow: inset 3px 0 #61ddaa; }
.directory-row:hover { background: #1b2838; }
.directory-screen[aria-current="page"] { color: #aaf0d0; font-weight: 600; }
button:focus-visible { outline: 2px solid #61ddaa; outline-offset: -2px; }
.directory-empty { margin: 0; padding: 12px 8px; color: #a7bacd; overflow-wrap: anywhere; }
</style>

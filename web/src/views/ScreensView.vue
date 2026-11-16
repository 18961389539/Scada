<template>
  <div class="page">
    <div class="toolbar">
      <h3>{{ $t('screensView.listTitle') }}</h3>
      <el-tooltip :content="admin ? $t('screensView.draftVisibilityHint') : $t('screensView.publishedRuntimeScreens')" placement="bottom-start" :show-after="200">
        <el-icon class="info-icon"><InfoFilled /></el-icon>
      </el-tooltip>
      <span class="spacer" />
      <el-button v-if="admin" type="primary" :icon="Plus" data-test="screen-create" @click="create">{{ $t('screensView.newScreen') }}</el-button><el-button :icon="Refresh" :loading="loading" @click="load">{{ $t('screensView.refresh') }}</el-button></div>
    <div class="screen-filters">
      <el-input v-model="search" clearable :prefix-icon="Search" :placeholder="$t('screensView.searchPlaceholder')" :aria-label="$t('screensView.searchPlaceholder')" class="screen-search" data-test="screens-search" />
      <el-select v-if="admin" v-model="status" :aria-label="$t('screensView.statusLabel')" class="screen-select" data-test="screens-status">
        <el-option :label="$t('screensView.allStatuses')" value="all" />
        <el-option :label="$t('screensView.published')" value="published" />
        <el-option :label="$t('screensView.unpublished')" value="draft" />
        <el-option :label="$t('screensView.pendingChanges')" value="changed" />
      </el-select>
      <el-select v-model="sort" :aria-label="$t('screensView.sortLabel')" class="screen-select" data-test="screens-sort">
        <el-option :label="$t('screensView.sortUpdated')" value="updated" />
        <el-option :label="$t('screensView.sortName')" value="name" />
      </el-select>
      <el-select v-model="areaFilter" :aria-label="$t('screensView.area')" class="screen-select" data-test="screens-area">
        <el-option :label="$t('screensView.allAreas')" value="" />
        <el-option v-for="area in areas" :key="area.id" :label="area.name" :value="area.id" />
        <el-option :label="$t('screensView.crossArea')" :value="-1" />
      </el-select>
      <el-button v-if="hasFilters" link type="primary" data-test="screens-reset" @click="resetFilters">{{ $t('screensView.resetFilters') }}</el-button>
      <span v-if="!loading || screens.length" class="screen-count muted" role="status" data-test="screens-count">{{ $t('screensView.resultCount', [filteredScreens.length, screens.length]) }}</span>
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="screens-load-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="screens-action-error" />
    <div class="screen-list" ref="screenListRef" v-loading="loading">
      <template v-for="section in screenSections" :key="section.key">
      <h4 v-if="section.title" class="screen-section" :data-test="`screens-section-${section.key}`">{{ section.title }}</h4>
      <article v-for="screen in section.screens" :key="screen.id" class="screen-card" :data-test="`screen-card-${screen.id}`">
        <div class="screen-preview" :data-screen-thumb="screen.id">
          <ScreenThumbnail v-if="thumbnails[screen.id]" :document="thumbnails[screen.id] ?? emptyThumbnail" />
          <div v-else class="screen-preview-empty" aria-hidden="true" />
        </div>
        <div class="screen-info">
          <div class="screen-heading">
            <button type="button" class="screen-favorite" :class="{ selected: favoriteIds.includes(screen.id) }" :aria-pressed="favoriteIds.includes(screen.id)" :aria-label="$t(favoriteIds.includes(screen.id) ? 'screenListUx.unfavorite' : 'screenListUx.favorite', [screen.name])" :data-test="`screen-favorite-${screen.id}`" @click="toggleFavorite(screen.id)">{{ favoriteIds.includes(screen.id) ? '★' : '☆' }}</button>
            <h3>{{ screen.name }}</h3>
            <el-tag :disable-transitions="true" size="small" :class="{ 'tag-quiet': screen.publishedRevision && !screen.hasDraftChanges }" :type="screen.publishedRevision && screen.hasDraftChanges ? 'warning' : screen.publishedRevision ? undefined : 'info'">{{ screen.publishedRevision && screen.hasDraftChanges ? $t('screensView.pendingChanges') : screen.publishedRevision ? $t('screensView.published') : $t('screensView.draft') }}</el-tag>
          </div>
          <p class="screen-meta muted"><span v-if="admin && screen.hasDraftChanges">{{ $t('screenMonitor.draftName') }}</span><span data-test="screen-area">{{ areaLabel(screen) }}</span><span>{{ screen.updatedBy }}</span><span>{{ formatTime(screen.updatedTs) }}</span></p>
        </div>
        <div class="screen-versions">
          <span data-test="screen-published-version">{{ screen.publishedRevision ? $t('screenMonitor.publishedVersion', [screen.publishedRevision]) : $t('screensView.unpublished') }}</span>
          <span v-if="admin" class="muted" data-test="screen-draft-version">{{ $t('screenMonitor.draftVersion', [screen.revision]) }}</span>
          <span v-if="layoutLabel(screen)" data-test="screen-layouts">{{ layoutLabel(screen) }}</span>
          <el-tag v-else-if="screen.layoutCount" class="tag-quiet" :type="undefined" size="small" data-test="screen-layout-count">{{ $t('screensView.layoutUsage', [screen.layoutCount]) }}</el-tag>
        </div>
        <div class="card-actions">
          <el-button v-if="admin || screen.publishedRevision" :icon="View" :aria-label="$t('screensView.preview')" :data-test="`screen-preview-${screen.id}`" @click="preview(screen)">{{ $t('screensView.preview') }}</el-button>
          <el-button v-if="screen.publishedRevision" type="primary" :icon="VideoPlay" :aria-label="$t('screenMonitor.runVersion', [screen.publishedRevision])" :data-test="`screen-run-${screen.id}`" @click="router.push({ path: `/screens/${screen.id}`, query: route.query })">{{ $t('screensView.run') }}</el-button>
          <WorkbenchTooltip v-if="screen.publishedRevision" :content="$t('workspace.openInWorkspace')"><el-button :icon="Collection" :aria-label="$t('workspace.openInWorkspace')" :data-test="`screen-workspace-${screen.id}`" @click="openInWorkspace(screen)" /></WorkbenchTooltip>
          <WorkbenchTooltip v-if="admin && screen.publishedRevision" :content="$t('workbenchTips.quickCreate')"><el-button :data-test="`screen-create-workbench-${screen.id}`" @click="quickCreate?.open(screen.id)">{{ $t('workbench.createFromScreen') }}</el-button></WorkbenchTooltip>
          <el-dropdown v-if="canEdit(screen)" trigger="click" @command="screenAction($event, screen)">
            <el-button :icon="MoreFilled" :title="$t('screenMonitor.more')" :aria-label="$t('screenMonitor.moreFor', [screen.name])" :data-test="`screen-more-${screen.id}`" circle />
            <template #dropdown><el-dropdown-menu>
              <el-dropdown-item command="edit" :icon="Edit">{{ $t('screensView.edit') }}</el-dropdown-item>
              <el-dropdown-item command="duplicate" :icon="CopyDocument">{{ $t('screensView.duplicate') }}</el-dropdown-item>
              <el-dropdown-item command="delete" :icon="Delete" divided>{{ $t('screensView.delete') }}</el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
        </div>
      </article>
      </template>
    </div>
    <el-empty v-if="!loading && !screens.length" :description="loadError || (admin ? $t('screensView.firstScreenHint') : $t('screensView.noPublishedScreensYet'))" :image-size="80">
      <el-button v-if="loadError" @click="load">{{ $t('screensView.retry') }}</el-button>
      <el-button v-if="admin && !loadError" type="primary" @click="create">{{ $t('screensView.newScreen') }}</el-button>
    </el-empty>
    <el-empty v-else-if="!loading && screens.length && !filteredScreens.length" :description="$t('screensView.noMatches')" :image-size="80" data-test="screens-no-matches">
      <el-button @click="resetFilters">{{ $t('screensView.resetFilters') }}</el-button>
    </el-empty>
    <QuickLayoutCreate v-if="admin" ref="quickCreate" @published="id => router.push(`/console/${id}`)" />
    <el-dialog v-model="previewVisible" :title="$t('screensView.previewTitle', [previewName])" width="94%" destroy-on-close data-test="screen-preview-dialog">
      <ScreenRuntime v-if="previewVisible && previewDoc" :preview-document="previewDoc" :preview-name="previewName" />
    </el-dialog>
    <el-dialog v-model="createVisible" :title="$t('screensView.newProcessScreen')" width="420px" :close-on-click-modal="false">
      <el-form label-width="90px">
        <el-form-item :label="$t('screensView.screenName')"><el-input v-model="createName" maxlength="100" data-test="screen-create-name" /></el-form-item>
        <el-form-item :label="$t('screensView.area')">
          <el-select v-model="createAreaId" :clearable="isGlobal" :placeholder="isGlobal ? $t('screensView.crossArea') : $t('screensView.areaRequired')" style="width: 100%" data-test="screen-create-area">
            <el-option v-for="area in configurableAreas" :key="area.id" :label="area.name" :value="area.id" />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="createVisible = false">{{ $t('screensView.cancel') }}</el-button>
        <el-button type="primary" data-test="screen-create-confirm" @click="confirmCreate">{{ $t('screensView.confirm') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>
<script setup lang="ts">
import WorkbenchTooltip from '../components/console/WorkbenchTooltip.vue'
import ScreenThumbnail from '../components/ScreenThumbnail.vue'
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { CopyDocument, Collection, Delete, Edit, InfoFilled, MoreFilled, Plus, Refresh, Search, VideoPlay, View } from '@element-plus/icons-vue'
import { screenApi } from '../api/screens'
import type { ScreenThumbnail as ScreenThumbnailData } from '../api/screens'
import { areaApi } from '../api'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import { formatTime } from '../utils'
import { can, canGlobal } from '../access/policies'
import type { ScreenDocument, ScreenSummary } from '../screens/model'
import { readCatalogMemory, toggleScreenFavorite } from '../screens/screenMemory'
import { tr } from '../i18n'
import QuickLayoutCreate from '../components/console/QuickLayoutCreate.vue'
import ScreenRuntime from '../components/ScreenRuntime.vue'
const auth = useAuthStore(), route = useRoute(), router = useRouter()
const admin = computed(() => can(auth.user, 'Configure'))
const isGlobal = computed(() => canGlobal(auth.user, 'Configure'))
const configurableAreas = ref<{ id: number; name: string }[]>([])
const createVisible = ref(false), createName = ref(''), createAreaId = ref<number | null>(null)
function canEdit(screen: ScreenSummary): boolean {
  return screen.areaId == null ? isGlobal.value : can(auth.user, 'Configure', screen.areaId)
}
const quickCreate = ref<InstanceType<typeof QuickLayoutCreate>>()
const previewVisible = ref(false), previewDoc = ref<ScreenDocument | null>(null), previewName = ref('')
const screens = ref<ScreenSummary[]>([]), loading = ref(false), loadError = ref(''), actionError = ref('')
const areas = ref<{ id: number; name: string }[]>([])
const areaFilter = ref<number | ''>('')
const screenListRef = ref<HTMLElement>()
const thumbnails = ref<Record<number, ScreenThumbnailData | undefined>>({})
const emptyThumbnail: ScreenThumbnailData = { width: 16, height: 9, background: '#101b2d', truncated: false, nodes: [] }
const thumbnailKeys = new Map<number, string>()
const thumbnailLoading = new Set<number>()
let thumbObserver: IntersectionObserver | undefined
const search = ref(''), status = ref<'all' | 'published' | 'draft' | 'changed'>('all'), sort = ref<'updated' | 'name'>('updated')
const favoriteIds = ref<number[]>([]), recentIds = ref<number[]>([])
const catalogUser = computed(() => auth.user?.id ?? 0)
function reloadMemory(): void {
  const memory = readCatalogMemory(catalogUser.value)
  favoriteIds.value = memory.favorites
  recentIds.value = memory.recent
}
function toggleFavorite(id: number): void { favoriteIds.value = toggleScreenFavorite(catalogUser.value, id) }
function layoutLabel(screen: ScreenSummary): string {
  const names = (screen.layoutNames ?? []).map(name => name.trim()).filter(Boolean)
  return names.length ? tr('screenListUx.layouts', [names.join('\u3001')]) : ''
}
function readFilters(): void {
  if (route.path !== '/screens') return
  search.value = typeof route.query.q === 'string' ? route.query.q : ''
  status.value = route.query.status === 'published' || route.query.status === 'draft' || route.query.status === 'changed' ? route.query.status : 'all'
  sort.value = route.query.sort === 'name' ? 'name' : 'updated'
  const area = Number(route.query.area)
  areaFilter.value = Number.isSafeInteger(area) && area !== 0 ? area : ''
}
readFilters()
watch(() => route.fullPath, readFilters)
watch([search, status, sort, areaFilter], () => {
  if (route.path !== '/screens') return
  const query = { ...route.query, q: search.value || undefined, status: status.value === 'all' ? undefined : status.value,
    sort: sort.value === 'updated' ? undefined : sort.value, area: areaFilter.value === '' ? undefined : String(areaFilter.value) }
  if (route.query.q !== query.q || route.query.status !== query.status || route.query.sort !== query.sort || route.query.area !== query.area) void router.replace({ query })
})
const hasFilters = computed(() => !!search.value || status.value !== 'all' || sort.value !== 'updated' || areaFilter.value !== '')
function areaLabel(screen: ScreenSummary): string {
  if (screen.areaId == null) return tr('screensView.crossArea')
  return areas.value.find(area => area.id === screen.areaId)?.name ?? `#${screen.areaId}`
}
function compareScreens(a: ScreenSummary, b: ScreenSummary): number {
  return sort.value === 'name'
    ? a.name.localeCompare(b.name, undefined, { numeric: true }) || a.id - b.id
    : b.updatedTs - a.updatedTs || b.id - a.id
}
const screenSections = computed(() => {
  const terms = search.value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean)
  const matched = screens.value.filter(screen => {
    if (admin.value && status.value === 'published' && !screen.publishedRevision) return false
    if (admin.value && status.value === 'draft' && screen.publishedRevision) return false
    if (admin.value && status.value === 'changed' && (!screen.publishedRevision || !screen.hasDraftChanges)) return false
    if (areaFilter.value !== '' && (areaFilter.value === -1 ? screen.areaId != null : screen.areaId !== areaFilter.value)) return false
    const text = `${screen.name} ${screen.updatedBy} ${(screen.layoutNames ?? []).join(' ')}`.toLocaleLowerCase()
    return terms.every(term => text.includes(term))
  })
  const byId = new Map(matched.map(screen => [screen.id, screen]))
  const favorites = favoriteIds.value.flatMap(id => byId.has(id) ? [byId.get(id)!] : [])
  const favoriteSet = new Set(favorites.map(screen => screen.id))
  const recent = recentIds.value.filter(id => !favoriteSet.has(id)).flatMap(id => byId.has(id) ? [byId.get(id)!] : [])
  const pinned = new Set([...favorites, ...recent].map(screen => screen.id))
  const rest = matched.filter(screen => !pinned.has(screen.id)).sort(compareScreens)
  const titled = favorites.length + recent.length > 0
  return [
    { key: 'favorites', title: titled ? tr('screenListUx.favorites') : '', screens: favorites },
    { key: 'recent', title: titled ? tr('screenListUx.recent') : '', screens: recent },
    { key: 'rest', title: titled && rest.length ? tr('screenListUx.others') : '', screens: rest }
  ].filter(section => section.screens.length)
})
const filteredScreens = computed(() => screenSections.value.flatMap(section => section.screens))
function resetFilters(): void { search.value = ''; status.value = 'all'; sort.value = 'updated'; areaFilter.value = '' }
function screenAction(command: string, screen: ScreenSummary): void {
  if (command === 'edit') void router.push(`/config/screens/${screen.id}`)
  else if (command === 'duplicate') void duplicate(screen)
  else if (command === 'delete') void remove(screen)
}
function openInWorkspace(screen: ScreenSummary): void {
  void router.push({ path: '/workspace', query: { screen: String(screen.id) } })
}
async function preview(screen: ScreenSummary): Promise<void> {
  actionError.value = ''
  try {
    const detail = await screenApi.get(screen.id, canEdit(screen))
    previewDoc.value = detail.document; previewName.value = detail.name; previewVisible.value = true
  } catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
async function duplicate(screen: ScreenSummary): Promise<void> {
  actionError.value = ''
  let created = false
  try {
    const detail = await screenApi.get(screen.id, true)
    const name = tr('screensView.duplicateName', [screen.name]).slice(0, 100)
    const row = await screenApi.create(name, screen.areaId ?? null)
    created = true
    await screenApi.save(row.id, name, row.revision, detail.document, screen.areaId ?? null, true)
    await load(); ElMessage.success(tr('screensView.duplicated', [name]))
  } catch (e) {
    // create 已成功但 save 的回执没回来时，画面可能已经写进去了：刷新列表让用户核对，
    // 不能只报「操作失败」，否则会留下一个没有提示的孤儿空画面。
    await load()
    actionError.value = created
      ? tr('actionUx.duplicateUnknown')
      : errorMessage(e, tr('screensView.actionFailed'))
  }
}
function thumbnailKey(screen: ScreenSummary): string {
  return `${screen.id}:${canEdit(screen) ? screen.revision : screen.publishedRevision ?? 0}`
}
async function ensureThumbnail(screen: ScreenSummary): Promise<void> {
  const key = thumbnailKey(screen)
  if (thumbnailKeys.get(screen.id) === key || thumbnailLoading.has(screen.id)) return
  thumbnailLoading.add(screen.id)
  try {
    const thumbnail = await screenApi.thumbnail(screen.id, canEdit(screen))
    if (!screens.value.some(item => item.id === screen.id)) return
    thumbnails.value[screen.id] = thumbnail
    thumbnailKeys.set(screen.id, key)
  } catch { /* 缩略图失败保留占位，不影响列表使用 */ }
  finally { thumbnailLoading.delete(screen.id) }
}
// 只给进入视口附近的卡片拉缩略图，避免画面多时首屏发起几十个文档请求。
function observeThumbnails(): void {
  const container = screenListRef.value
  if (!container) return
  const elements = [...container.querySelectorAll<HTMLElement>('[data-screen-thumb]')]
  if (!elements.length) return
  if (typeof IntersectionObserver === 'undefined') {
    for (const element of elements) {
      const screen = screens.value.find(item => item.id === Number(element.dataset.screenThumb))
      if (screen) void ensureThumbnail(screen)
    }
    return
  }
  if (!thumbObserver) {
    thumbObserver = new IntersectionObserver(entries => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue
        const id = Number((entry.target as HTMLElement).dataset.screenThumb)
        const screen = screens.value.find(item => item.id === id)
        if (screen) void ensureThumbnail(screen)
        thumbObserver?.unobserve(entry.target)
      }
    }, { rootMargin: '160px' })
  }
  for (const element of elements) thumbObserver.observe(element)
}
async function loadAreas(): Promise<void> {
  try { areas.value = (await areaApi.list()).map(area => ({ id: area.id, name: area.name })) } catch { /* 区域名不可用时仍显示编号 */ }
}
async function load(): Promise<void> {
  if (loading.value) return
  loading.value = true
  try {
    screens.value = await screenApi.list(admin.value)
    loadError.value = ''
    for (const screen of screens.value) {
      if (thumbnailKeys.get(screen.id) === thumbnailKey(screen)) continue
      thumbnailKeys.delete(screen.id)
      delete thumbnails.value[screen.id]
    }
    await nextTick()
    observeThumbnails()
  }
  catch (e) { loadError.value = errorMessage(e, tr('screensView.actionFailed')) }
  finally { loading.value = false }
}
async function create(): Promise<void> {
  if (!configurableAreas.value.length) {
    try { configurableAreas.value = (await areaApi.list()).filter(area => isGlobal.value || can(auth.user, 'Configure', area.id)).map(area => ({ id: area.id, name: area.name })) }
    catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')); return }
  }
  createName.value = ''
  createAreaId.value = isGlobal.value ? null : configurableAreas.value[0]?.id ?? null
  createVisible.value = true
}
async function confirmCreate(): Promise<void> {
  const name = createName.value.trim()
  if (!name || name.length > 100) { actionError.value = tr('screensView.enter1100Characters'); return }
  if (!isGlobal.value && createAreaId.value == null) { actionError.value = tr('screensView.areaRequired'); return }
  actionError.value = ''
  try {
    const screen = await screenApi.create(name, createAreaId.value)
    createVisible.value = false
    await router.push(`/config/screens/${screen.id}`)
  } catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
async function remove(screen: ScreenSummary): Promise<void> {
  let extra = ''
  try {
    const usage = await screenApi.usage(screen.id)
    const layouts = usage.layouts.filter(item => item.inDraft || item.inPublished).map(item => item.publishedName || item.name)
    const stations = usage.workstations.map(item => item.name)
    const instances = (usage.objectInstances ?? []).map(item => item.name)
    const referenced = (usage.referencedBy ?? []).map(item => item.name)
    const blocked = layouts.length + stations.length + instances.length + referenced.length > 0
    if (blocked) {
      const parts = [tr('designReview2.screenDeleteBlocked', [screen.name])]
      if (layouts.length || stations.length) parts.push(tr('designReview2.screenDeleteUsage', [layouts.join('、') || tr('designReview2.none'), stations.join('、') || tr('designReview2.none')]))
      if (instances.length) parts.push(tr('designReview2.screenDeleteInstances', [instances.join('、')]))
      if (referenced.length) parts.push(tr('designReview2.screenDeleteScreens', [referenced.join('、')]))
      if (usage.unreadable) parts.push(tr('designReview2.screenDeleteUnreadable'))
      await ElMessageBox.alert(parts.join(''), tr('screensView.deleteScreen'), { type: 'warning' })
      return
    }
    extra = usage.unreadable ? ` ${tr('designReview2.screenDeleteUnreadable')}` : ''
  } catch { extra = tr('designReview2.screenDeleteUsageUnknown') }
  try { await ElMessageBox.confirm(tr('screensView.deleteItIsAlsoRemovedFromRuntimeScreens', [screen.name]) + extra, tr('screensView.deleteScreen'), { type: 'warning' }) }
  catch { return }
  actionError.value = ''
  try { await screenApi.remove(screen.id, screen.revision); actionError.value = ''; await load() }
  catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
onMounted(() => { reloadMemory(); void loadAreas(); void load() })
onBeforeUnmount(() => thumbObserver?.disconnect())
</script>
<style scoped>
.screen-filters { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-bottom: 16px; }
.screen-search { width: 280px; }
.screen-select { width: 160px; }
.screen-count { margin-left: auto; font-size: 13px; }
.screen-list { border-top: 1px solid var(--scada-border); }
.screen-card { display: grid; grid-template-columns: 128px minmax(0, 1fr) minmax(160px, .4fr) auto; align-items: center; gap: 16px; min-width: 0; padding: 14px 0; border-bottom: 1px solid var(--scada-border); }
.screen-preview { width: 128px; align-self: center; }
.screen-preview-empty { aspect-ratio: 16 / 9; border: 1px solid var(--scada-border); border-radius: 4px; background: var(--scada-panel-muted); }
.screen-info { min-width: 0; }
.screen-section { margin: 14px 0 0; font-size: 12px; font-weight: 600; color: var(--scada-text-muted); }
.screen-favorite { border: 0; background: transparent; color: var(--scada-text-muted); cursor: pointer; font-size: 16px; line-height: 1; padding: 0; }
.screen-favorite.selected { color: var(--el-color-warning); }
.screen-heading, .screen-meta, .card-actions { display: flex; align-items: center; gap: 8px; }
.screen-heading, .screen-meta { flex-wrap: wrap; }
.screen-heading .el-tag { flex-shrink: 0; }
.screen-card h3 { margin: 0; font-size: 15px; line-height: 1.5; }
.screen-meta { margin: 4px 0 0; font-size: 12px; }
.screen-versions { display: flex; flex-wrap: wrap; gap: 4px 16px; font-size: 13px; }
.card-actions { justify-content: flex-end; flex-wrap: wrap; }
.card-actions :deep(.el-button + .el-button) { margin-left: 0; }
.screen-card h3, .screen-card p { overflow-wrap: anywhere; }
.toolbar h3 { margin: 0; }
.info-icon { color: var(--scada-text-muted); cursor: help; }
@media (max-width: 760px) {
  .screen-search { width: 100%; }
  .screen-select { flex: 1; min-width: 120px; }
  .screen-count { width: 100%; margin-left: 0; }
  .screen-card { grid-template-columns: minmax(0, 1fr) auto; align-items: start; gap: 8px 12px; padding: 12px 0; }
  .screen-preview { display: none; }
  .screen-versions { grid-column: 1 / -1; grid-row: 2; }
  .card-actions { grid-column: 1 / -1; grid-row: 3; justify-content: flex-start; }
  .screen-heading { gap: 4px 8px; }
}
</style>

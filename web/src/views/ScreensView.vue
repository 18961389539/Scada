<template>
  <div class="page">
    <div class="toolbar">
      <h3>{{ $t('screensView.listTitle') }}</h3>
      <el-tooltip :content="admin ? $t('screensView.draftVisibilityHint') : $t('screensView.publishedRuntimeScreens')" placement="bottom-start" :show-after="200">
        <el-icon class="info-icon"><InfoFilled /></el-icon>
      </el-tooltip>
      <span class="spacer" />
      <el-button v-if="admin" type="primary" :icon="Plus" @click="create">{{ $t('screensView.newScreen') }}</el-button><el-button :icon="Refresh" :loading="loading" @click="load">{{ $t('screensView.refresh') }}</el-button></div>
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
      <el-button v-if="hasFilters" link type="primary" data-test="screens-reset" @click="resetFilters">{{ $t('screensView.resetFilters') }}</el-button>
      <span v-if="!loading || screens.length" class="screen-count muted" role="status" data-test="screens-count">{{ $t('screensView.resultCount', [filteredScreens.length, screens.length]) }}</span>
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="screens-load-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="screens-action-error" />
    <div class="screen-list" v-loading="loading">
      <article v-for="screen in filteredScreens" :key="screen.id" class="screen-card" :data-test="`screen-card-${screen.id}`">
        <div class="screen-info">
          <div class="screen-heading">
            <h3>{{ screen.name }}</h3>
            <el-tag :disable-transitions="true" size="small" :type="screen.publishedRevision && screen.hasDraftChanges ? 'warning' : screen.publishedRevision ? 'success' : 'info'">{{ screen.publishedRevision && screen.hasDraftChanges ? $t('screensView.pendingChanges') : screen.publishedRevision ? $t('screensView.published') : $t('screensView.draft') }}</el-tag>
          </div>
          <p class="screen-meta muted"><span v-if="admin && screen.hasDraftChanges">{{ $t('screenMonitor.draftName') }}</span><span>{{ screen.updatedBy }}</span><span>{{ formatTime(screen.updatedTs) }}</span></p>
        </div>
        <div class="screen-versions">
          <span data-test="screen-published-version">{{ screen.publishedRevision ? $t('screenMonitor.publishedVersion', [screen.publishedRevision]) : $t('screensView.unpublished') }}</span>
          <span v-if="admin" class="muted" data-test="screen-draft-version">{{ $t('screenMonitor.draftVersion', [screen.revision]) }}</span>
          <el-tag v-if="screen.layoutCount" size="small" type="info" data-test="screen-layout-count">{{ $t('screensView.layoutUsage', [screen.layoutCount]) }}</el-tag>
        </div>
        <div class="card-actions">
          <el-button v-if="admin || screen.publishedRevision" :icon="View" :aria-label="$t('screensView.preview')" :data-test="`screen-preview-${screen.id}`" @click="preview(screen)">{{ $t('screensView.preview') }}</el-button>
          <el-button v-if="screen.publishedRevision" type="primary" :icon="VideoPlay" :aria-label="$t('screenMonitor.runVersion', [screen.publishedRevision])" :data-test="`screen-run-${screen.id}`" @click="router.push({ path: `/screens/${screen.id}`, query: route.query })">{{ $t('screensView.run') }}</el-button>
          <WorkbenchTooltip v-if="admin && screen.publishedRevision" :content="$t('workbenchTips.quickCreate')"><el-button :data-test="`screen-create-workbench-${screen.id}`" @click="quickCreate?.open(screen.id)">{{ $t('workbench.createFromScreen') }}</el-button></WorkbenchTooltip>
          <el-dropdown v-if="admin" trigger="click" @command="screenAction($event, screen)">
            <el-button :icon="MoreFilled" :title="$t('screenMonitor.more')" :aria-label="$t('screenMonitor.moreFor', [screen.name])" :data-test="`screen-more-${screen.id}`" circle />
            <template #dropdown><el-dropdown-menu>
              <el-dropdown-item command="edit" :icon="Edit">{{ $t('screensView.edit') }}</el-dropdown-item>
              <el-dropdown-item command="duplicate" :icon="CopyDocument">{{ $t('screensView.duplicate') }}</el-dropdown-item>
              <el-dropdown-item command="delete" :icon="Delete" divided>{{ $t('screensView.delete') }}</el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
        </div>
      </article>
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
  </div>
</template>
<script setup lang="ts">
import WorkbenchTooltip from '../components/console/WorkbenchTooltip.vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { CopyDocument, Delete, Edit, InfoFilled, MoreFilled, Plus, Refresh, Search, VideoPlay, View } from '@element-plus/icons-vue'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import { formatTime } from '../utils'
import { canGlobal } from '../access/policies'
import type { ScreenDocument, ScreenSummary } from '../screens/model'
import { tr } from '../i18n'
import QuickLayoutCreate from '../components/console/QuickLayoutCreate.vue'
import ScreenRuntime from '../components/ScreenRuntime.vue'
const auth = useAuthStore(), route = useRoute(), router = useRouter(), admin = computed(() => canGlobal(auth.user, 'Configure'))
const quickCreate = ref<InstanceType<typeof QuickLayoutCreate>>()
const previewVisible = ref(false), previewDoc = ref<ScreenDocument | null>(null), previewName = ref('')
const screens = ref<ScreenSummary[]>([]), loading = ref(false), loadError = ref(''), actionError = ref('')
const search = ref(''), status = ref<'all' | 'published' | 'draft' | 'changed'>('all'), sort = ref<'updated' | 'name'>('updated')
function readFilters(): void {
  if (route.path !== '/screens') return
  search.value = typeof route.query.q === 'string' ? route.query.q : ''
  status.value = route.query.status === 'published' || route.query.status === 'draft' || route.query.status === 'changed' ? route.query.status : 'all'
  sort.value = route.query.sort === 'name' ? 'name' : 'updated'
}
readFilters()
watch(() => route.fullPath, readFilters)
watch([search, status, sort], () => {
  if (route.path !== '/screens') return
  const query = { ...route.query, q: search.value || undefined, status: status.value === 'all' ? undefined : status.value, sort: sort.value === 'updated' ? undefined : sort.value }
  if (route.query.q !== query.q || route.query.status !== query.status || route.query.sort !== query.sort) void router.replace({ query })
})
const hasFilters = computed(() => !!search.value || status.value !== 'all' || sort.value !== 'updated')
const filteredScreens = computed(() => {
  const terms = search.value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean)
  return screens.value.filter(screen => {
    if (admin.value && status.value === 'published' && !screen.publishedRevision) return false
    if (admin.value && status.value === 'draft' && screen.publishedRevision) return false
    if (admin.value && status.value === 'changed' && (!screen.publishedRevision || !screen.hasDraftChanges)) return false
    const text = `${screen.name} ${screen.updatedBy}`.toLocaleLowerCase()
    return terms.every(term => text.includes(term))
  }).sort((a, b) => sort.value === 'name'
    ? a.name.localeCompare(b.name, undefined, { numeric: true }) || a.id - b.id
    : b.updatedTs - a.updatedTs || b.id - a.id)
})
function resetFilters(): void { search.value = ''; status.value = 'all'; sort.value = 'updated' }
function screenAction(command: string, screen: ScreenSummary): void {
  if (command === 'edit') void router.push(`/config/screens/${screen.id}`)
  else if (command === 'duplicate') void duplicate(screen)
  else if (command === 'delete') void remove(screen)
}
async function preview(screen: ScreenSummary): Promise<void> {
  actionError.value = ''
  try {
    const detail = await screenApi.get(screen.id, admin.value)
    previewDoc.value = detail.document; previewName.value = detail.name; previewVisible.value = true
  } catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
async function duplicate(screen: ScreenSummary): Promise<void> {
  actionError.value = ''
  try {
    const detail = await screenApi.get(screen.id, true)
    const name = tr('screensView.duplicateName', [screen.name]).slice(0, 100)
    const created = await screenApi.create(name)
    await screenApi.save(created.id, name, created.revision, detail.document)
    await load(); ElMessage.success(tr('screensView.duplicated', [name]))
  } catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
async function load(): Promise<void> {
  if (loading.value) return
  loading.value = true
  try { screens.value = await screenApi.list(admin.value); loadError.value = '' }
  catch (e) { loadError.value = errorMessage(e, tr('screensView.actionFailed')) }
  finally { loading.value = false }
}
async function create(): Promise<void> {
  let name = ''
  try { name = (await ElMessageBox.prompt(tr('screensView.enterScreenName'), tr('screensView.newProcessScreen'), { inputValidator: (v: string) => !!v?.trim() && v.trim().length <= 100 || tr('screensView.enter1100Characters') })).value }
  catch { return }
  actionError.value = ''
  try { const screen = await screenApi.create(name.trim()); actionError.value = ''; await router.push(`/config/screens/${screen.id}`) }
  catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
async function remove(screen: ScreenSummary): Promise<void> {
  try { await ElMessageBox.confirm(tr('screensView.deleteItIsAlsoRemovedFromRuntimeScreens', [screen.name]), tr('screensView.deleteScreen'), { type: 'warning' }) }
  catch { return }
  actionError.value = ''
  try { await screenApi.remove(screen.id, screen.revision); actionError.value = ''; await load() }
  catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
onMounted(load)
</script>
<style scoped>
.screen-filters { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-bottom: 16px; }
.screen-search { width: 280px; }
.screen-select { width: 160px; }
.screen-count { margin-left: auto; font-size: 13px; }
.screen-list { border-top: 1px solid var(--scada-border); }
.screen-card { display: grid; grid-template-columns: minmax(0, 1fr) minmax(160px, .4fr) auto; align-items: center; gap: 16px; min-width: 0; padding: 14px 0; border-bottom: 1px solid var(--scada-border); }
.screen-info { min-width: 0; }
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
  .screen-versions { grid-column: 1 / -1; grid-row: 2; }
  .card-actions { grid-column: 1 / -1; grid-row: 3; justify-content: flex-start; }
  .screen-heading { gap: 4px 8px; }
}
</style>

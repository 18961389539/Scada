<template>
  <div class="page">
    <div class="panel">
    <div class="toolbar">
      <h3>{{ $t('screensView.listTitle') }}</h3>
      <el-tooltip :content="admin ? $t('screensView.k2c00f4') : $t('screensView.k05a21f')" placement="bottom-start" :show-after="200">
        <el-icon class="info-icon"><InfoFilled /></el-icon>
      </el-tooltip>
      <span class="spacer" />
      <el-button v-if="admin" type="primary" @click="create">{{ $t('screensView.kdc834f') }}</el-button><el-button :loading="loading" @click="load">{{ $t('screensView.k38108e') }}</el-button></div>
    <div class="screen-filters">
      <el-input v-model="search" clearable :prefix-icon="Search" :placeholder="$t('screensView.searchPlaceholder')" :aria-label="$t('screensView.searchPlaceholder')" class="screen-search" data-test="screens-search" />
      <el-select v-if="admin" v-model="status" :aria-label="$t('screensView.statusLabel')" class="screen-select" data-test="screens-status">
        <el-option :label="$t('screensView.allStatuses')" value="all" />
        <el-option :label="$t('screensView.k176a2e')" value="published" />
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
        <div class="card-heading"><span class="screen-symbol" aria-hidden="true">▧</span><el-tag :type="screen.publishedRevision && screen.hasDraftChanges ? 'warning' : screen.publishedRevision ? 'success' : 'info'">{{ screen.publishedRevision && screen.hasDraftChanges ? $t('screensView.pendingChanges') : screen.publishedRevision ? $t('screensView.k176a2e') : $t('screensView.k0f4368') }}</el-tag></div>
        <h3>{{ screen.name }}</h3><p class="muted">{{ screen.updatedBy }} · {{ formatTime(screen.updatedTs) }}</p>
        <p class="muted">{{ $t('screensView.k55d3d8', [screen.revision]) }}</p>
        <div class="card-actions"><el-button v-if="screen.publishedRevision" type="primary" @click="router.push(`/screens/${screen.id}`)">{{ $t('screensView.k0c3acd') }}</el-button>
          <el-button v-if="admin" @click="router.push(`/config/screens/${screen.id}`)">{{ $t('screensView.ka7f814') }}</el-button><el-button v-if="admin" type="danger" link @click="remove(screen)">{{ $t('screensView.k3755f5') }}</el-button></div>
      </article>
    </div>
    <el-empty v-if="!loading && !screens.length" :description="loadError || (admin ? $t('screensView.k433980') : $t('screensView.k0a44b0'))" :image-size="80">
      <el-button v-if="loadError" @click="load">{{ $t('screensView.retry') }}</el-button>
      <el-button v-if="admin && !loadError" type="primary" @click="create">{{ $t('screensView.kdc834f') }}</el-button>
    </el-empty>
    <el-empty v-else-if="!loading && screens.length && !filteredScreens.length" :description="$t('screensView.noMatches')" :image-size="80" data-test="screens-no-matches">
      <el-button @click="resetFilters">{{ $t('screensView.resetFilters') }}</el-button>
    </el-empty>
    </div>
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { InfoFilled, Search } from '@element-plus/icons-vue'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import { formatTime , hasGlobal } from '../utils'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'
const auth = useAuthStore(), router = useRouter(), admin = computed(() => hasGlobal(auth.user, 'Configure'))
const screens = ref<ScreenSummary[]>([]), loading = ref(false), loadError = ref(''), actionError = ref('')
const search = ref(''), status = ref<'all' | 'published' | 'draft' | 'changed'>('all'), sort = ref<'updated' | 'name'>('updated')
const hasFilters = computed(() => !!search.value || status.value !== 'all')
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
function resetFilters(): void { search.value = ''; status.value = 'all' }
async function load(): Promise<void> {
  if (loading.value) return
  loading.value = true
  try { screens.value = await screenApi.list(admin.value); loadError.value = '' }
  catch (e) { loadError.value = errorMessage(e, tr('screensView.actionFailed')) }
  finally { loading.value = false }
}
async function create(): Promise<void> {
  let name = ''
  try { name = (await ElMessageBox.prompt(tr('screensView.kaab7c5'), tr('screensView.kf95f8f'), { inputValidator: (v: string) => !!v?.trim() && v.trim().length <= 100 || tr('screensView.kaa0b13') })).value }
  catch { return }
  actionError.value = ''
  try { const screen = await screenApi.create(name.trim()); actionError.value = ''; await router.push(`/config/screens/${screen.id}`) }
  catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
async function remove(screen: ScreenSummary): Promise<void> {
  try { await ElMessageBox.confirm(tr('screensView.k56f5f9', [screen.name]), tr('screensView.k783512'), { type: 'warning' }) }
  catch { return }
  actionError.value = ''
  try { await screenApi.remove(screen.id, screen.revision); actionError.value = ''; await load() }
  catch (e) { actionError.value = errorMessage(e, tr('screensView.actionFailed')) }
}
onMounted(load)
</script>
<style scoped>
.screen-filters { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin-bottom: 20px; }
.screen-search { width: 280px; }
.screen-select { width: 160px; }
.screen-count { margin-left: auto; font-size: 13px; }
.screen-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(min(280px, 100%), 1fr)); gap: 20px; }
.screen-card { min-width: 0; padding: 24px; background: var(--scada-panel); border: 1px solid var(--scada-border); border-radius: 12px; }
.card-heading, .card-actions { display: flex; align-items: center; gap: 8px; } .card-heading { justify-content: space-between; }
.card-actions { flex-wrap: wrap; }
.card-actions :deep(.el-button + .el-button) { margin-left: 0; }
.screen-card h3, .screen-card p { overflow-wrap: anywhere; }
.screen-symbol { font-size: 38px; color: var(--scada-primary); } h3 { margin: 16px 0 8px; }
.info-icon { color: var(--scada-text-muted); cursor: help; }
@media (max-width: 600px) {
  .screen-search { width: 100%; }
  .screen-select { flex: 1; min-width: 120px; }
  .screen-count { width: 100%; margin-left: 0; }
  .screen-card { padding: 16px; }
}
</style>

<template>
  <div class="page" data-test="kiosk-playlists-page">
    <header class="page-header">
      <div>
        <h1>{{ $t('kioskPlaylists.title') }}</h1>
        <p class="help">{{ $t('kioskPlaylists.subtitle') }}</p>
      </div>
      <div class="header-actions">
        <el-button :loading="loading" data-test="kiosk-playlists-refresh" @click="reload">{{ $t('kioskPlaylists.refresh') }}</el-button>
        <el-button type="primary" data-test="kiosk-playlists-add" @click="openEditor()">{{ $t('kioskPlaylists.add') }}</el-button>
      </div>
    </header>
    <el-alert v-if="loadError" :title="loadError" type="error" show-icon :closable="false" data-test="kiosk-playlists-error" />
    <el-table v-loading="loading" :data="playlists" stripe empty-text="" data-test="kiosk-playlists-table">
      <el-table-column :label="$t('kioskPlaylists.name')" prop="name" min-width="160" />
      <el-table-column :label="$t('kioskPlaylists.screens')" min-width="220">
        <template #default="{ row }">{{ screenLabels(row.screenIds) }}</template>
      </el-table-column>
      <el-table-column :label="$t('kioskPlaylists.interval')" width="120">
        <template #default="{ row }">{{ $t('kioskPlaylists.intervalValue', [row.intervalSeconds]) }}</template>
      </el-table-column>
      <el-table-column :label="$t('kioskPlaylists.mode')" width="120">
        <template #default="{ row }">{{ $t(row.monitorOnly ? 'kioskPlaylists.monitorOnly' : 'kioskPlaylists.controlAllowed') }}</template>
      </el-table-column>
      <el-table-column :label="$t('kioskPlaylists.updated')" min-width="180">
        <template #default="{ row }">{{ row.updatedBy }} · {{ formatTime(row.updatedTs) }}</template>
      </el-table-column>
      <el-table-column :label="$t('kioskPlaylists.actions')" width="260" fixed="right">
        <template #default="{ row }">
          <el-button size="small" data-test="kiosk-playlist-start" @click="startKiosk(row)">{{ $t('kioskPlaylists.start') }}</el-button>
          <el-button size="small" data-test="kiosk-playlist-edit" @click="openEditor(row)">{{ $t('kioskPlaylists.edit') }}</el-button>
          <el-button size="small" type="danger" plain data-test="kiosk-playlist-delete" @click="remove(row)">{{ $t('kioskPlaylists.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>
    <el-empty v-if="!loading && !playlists.length && !loadError" :description="$t('kioskPlaylists.empty')" data-test="kiosk-playlists-empty" />

    <el-dialog v-model="editorVisible" :title="$t(editing ? 'kioskPlaylists.editTitle' : 'kioskPlaylists.addTitle')" class="playlist-dialog" data-test="kiosk-playlist-editor" @closed="resetEditor">
      <el-form label-position="top" @submit.prevent>
        <el-form-item :label="$t('kioskPlaylists.name')">
          <el-input v-model="form.name" maxlength="100" show-word-limit data-test="kiosk-playlist-name" />
        </el-form-item>
        <el-form-item :label="$t('kioskPlaylists.screenOrder')">
          <p class="help">{{ $t('kioskPlaylists.screenOrderHelp') }}</p>
          <el-alert v-if="screenError" :title="screenError" type="warning" show-icon :closable="false" />
          <div class="screen-picker">
            <el-checkbox-group v-model="form.screenIds" data-test="kiosk-playlist-screens">
              <el-checkbox v-for="screen in publishedScreens" :key="screen.id" :value="screen.id">{{ screen.name }}</el-checkbox>
            </el-checkbox-group>
          </div>
          <div v-if="form.screenIds.length" class="screen-order" data-test="kiosk-playlist-order">
            <div v-for="(id, index) in form.screenIds" :key="id" class="screen-order-row">
              <span>{{ index + 1 }}. {{ screenName(id) }}</span>
              <el-button size="small" :disabled="index === 0" @click="moveScreen(index, -1)">{{ $t('kioskPlaylists.moveUp') }}</el-button>
              <el-button size="small" :disabled="index === form.screenIds.length - 1" @click="moveScreen(index, 1)">{{ $t('kioskPlaylists.moveDown') }}</el-button>
            </div>
          </div>
        </el-form-item>
        <el-form-item :label="$t('kioskPlaylists.interval')">
          <el-input-number v-model="form.intervalSeconds" :min="2" :max="600" data-test="kiosk-playlist-interval" />
        </el-form-item>
        <el-form-item>
          <el-checkbox v-model="form.monitorOnly" data-test="kiosk-playlist-monitor-only">{{ $t('kioskPlaylists.monitorOnly') }}</el-checkbox>
          <p class="hint" data-test="kiosk-playlist-monitor-hint">{{ $t('kioskPlaylists.monitorOnlyHint') }}</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="editorVisible = false">{{ $t('kioskPlaylists.cancel') }}</el-button>
        <el-button type="primary" :loading="saving" data-test="kiosk-playlist-save" @click="save">{{ $t('kioskPlaylists.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { errorMessage } from '../api/http'
import { kioskPlaylistApi, type KioskPlaylist } from '../api/kioskPlaylists'
import { screenApi } from '../api/screens'
import { tr } from '../i18n'
import { formatTime } from '../utils'
import type { ScreenSummary } from '../screens/model'

const loading = ref(false), saving = ref(false), loadError = ref(''), screenError = ref('')
const playlists = ref<KioskPlaylist[]>([])
const publishedScreens = ref<ScreenSummary[]>([])
const editorVisible = ref(false)
const editing = ref<KioskPlaylist | null>(null)
const form = reactive({ name: '', screenIds: [] as number[], intervalSeconds: 20, monitorOnly: false })

function screenName(id: number): string {
  return publishedScreens.value.find(item => item.id === id)?.name ?? `#${id}`
}
function screenLabels(ids: number[]): string {
  return ids.map(id => screenName(id)).join(' → ')
}
async function reload(): Promise<void> {
  loading.value = true
  loadError.value = ''
  try {
    const [list, screens] = await Promise.all([
      kioskPlaylistApi.list(),
      screenApi.list().catch(reason => {
        screenError.value = errorMessage(reason, tr('kioskPlaylists.screensFailed'))
        return [] as ScreenSummary[]
      })
    ])
    playlists.value = list
    publishedScreens.value = screens.filter(item => item.publishedRevision != null)
    if (!screenError.value) screenError.value = ''
  } catch (reason) {
    loadError.value = errorMessage(reason, tr('kioskPlaylists.loadFailed'))
  } finally {
    loading.value = false
  }
}
function openEditor(row?: KioskPlaylist): void {
  editing.value = row ?? null
  form.name = row?.name ?? ''
  form.screenIds = [...(row?.screenIds ?? [])]
  form.intervalSeconds = row?.intervalSeconds ?? 20
  form.monitorOnly = row?.monitorOnly ?? false
  editorVisible.value = true
}
function resetEditor(): void {
  editing.value = null
}
function moveScreen(index: number, delta: number): void {
  const next = index + delta
  if (next < 0 || next >= form.screenIds.length) return
  const copy = [...form.screenIds]
  const [item] = copy.splice(index, 1)
  copy.splice(next, 0, item!)
  form.screenIds = copy
}
async function save(): Promise<void> {
  const name = form.name.trim()
  if (!name) { ElMessage.warning(tr('kioskPlaylists.nameRequired')); return }
  if (form.screenIds.length < 1) { ElMessage.warning(tr('kioskPlaylists.screensRequired')); return }
  saving.value = true
  try {
    const payload = { name, screenIds: form.screenIds, intervalSeconds: form.intervalSeconds, monitorOnly: form.monitorOnly, revision: editing.value?.revision }
    if (editing.value) await kioskPlaylistApi.save(editing.value.id, payload)
    else await kioskPlaylistApi.create(payload)
    ElMessage.success(tr('kioskPlaylists.saved'))
    editorVisible.value = false
    await reload()
  } catch (reason) {
    ElMessage.error(errorMessage(reason, tr('kioskPlaylists.saveFailed')))
  } finally {
    saving.value = false
  }
}
async function remove(row: KioskPlaylist): Promise<void> {
  try {
    await ElMessageBox.confirm(tr('kioskPlaylists.deleteConfirm', [row.name]), tr('kioskPlaylists.delete'), { type: 'warning' })
  } catch { return }
  try {
    await kioskPlaylistApi.remove(row.id, row.revision)
    ElMessage.success(tr('kioskPlaylists.deleted'))
    await reload()
  } catch (reason) {
    ElMessage.error(errorMessage(reason, tr('kioskPlaylists.deleteFailed')))
  }
}
function startKiosk(row: KioskPlaylist): void {
  const url = `/kiosk/playlists/${row.id}`
  window.open(url, `scada-kiosk-playlist-${row.id}`, 'noopener')
}
onMounted(() => { void reload() })
</script>

<style scoped>
.page { display: flex; flex-direction: column; gap: 16px; }
.page-header { display: flex; justify-content: space-between; gap: 16px; flex-wrap: wrap; align-items: flex-start; }
.page-header h1 { margin: 0 0 6px; }
.help { margin: 0; color: var(--scada-text-muted); font-size: 13px; line-height: 1.6; }
.header-actions { display: flex; gap: 8px; }
.screen-picker { max-height: 220px; overflow: auto; padding: 8px 0; }
.screen-picker .el-checkbox { display: flex; margin: 4px 0; }
.screen-order { display: flex; flex-direction: column; gap: 6px; margin-top: 8px; }
.screen-order-row { display: flex; align-items: center; gap: 8px; }
.screen-order-row span { flex: 1; min-width: 0; }
:global(.playlist-dialog) { width: min(680px, calc(100vw - 24px)); }
</style>

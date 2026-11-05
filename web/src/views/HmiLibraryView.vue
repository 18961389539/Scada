<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><span class="spacer" />
      <el-button data-test="library-import-svg" :disabled="!!loadError" @click="svgInput?.click()">{{ $t('hmiLibraryView.ke94e6a') }}</el-button>
      <el-button data-test="library-import" :disabled="!!loadError" @click="jsonInput?.click()">{{ $t('hmiLibraryView.ke6f963') }}</el-button>
      <el-button data-test="library-export" :disabled="!library.items.length" @click="exportLibrary()">{{ $t('hmiLibraryView.k10d52a') }}</el-button>
      <input ref="svgInput" type="file" accept=".svg,image/svg+xml" multiple hidden data-test="library-svg-file" @change="importSvg" />
      <input ref="jsonInput" type="file" accept=".json,application/json" hidden data-test="library-json-file" @change="importJson" />
    </div>
    <el-dialog v-model="compareVisible" :title="$t('designReview2.libraryCompareTitle')" width="760px" data-test="library-import-compare" @close="cancelCompare">
      <p>{{ $t('hmiLibraryView.k9e2089', [compareRows.map(row => row.key).join('、')]) }}</p>
      <div v-for="row in compareRows" :key="row.key" class="compare-row" data-test="library-import-compare-row">
        <div>
          <div class="tiny">{{ $t('designReview2.libraryCompareCurrent', [row.revision]) }}</div>
          <div v-if="row.oldSvg" class="preview" data-test="library-import-current" v-html="row.oldSvg" />
          <p v-else class="tiny">{{ $t('designReview2.libraryCompareTemplate') }}</p>
        </div>
        <div>
          <div class="tiny">{{ $t('designReview2.libraryCompareIncoming') }}</div>
          <div v-if="row.newSvg" class="preview" data-test="library-import-incoming" v-html="row.newSvg" />
          <p v-else class="tiny">{{ $t('designReview2.libraryCompareTemplate') }}</p>
          <p v-if="row.fileRevision != null && row.fileRevision !== row.revision" class="tiny muted">{{ $t('designReview2.libraryCompareRevision', [row.fileRevision, row.revision]) }}</p>
        </div>
      </div>
      <template #footer>
        <el-button @click="cancelCompare()">{{ $t('designReview2.licenseCancel') }}</el-button>
        <el-button type="primary" data-test="library-import-confirm" @click="acceptCompare()">{{ $t('designReview2.libraryCompareConfirm') }}</el-button>
      </template>
    </el-dialog>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="library-load-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="library-action-error" />
    <p class="page-desc">{{ $t('hmiTemplateEditing.description') }}</p>
    <div v-if="loadError" class="toolbar"><el-button data-test="library-retry" @click="reload()">{{ $t('common.retry') }}</el-button></div>
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('hmiLibraryView.k6eaa88')" name="builtin">
        <div class="gallery" data-test="builtin-gallery">
          <figure v-for="s in builtinSymbols" :key="s.key" :data-symbol="s.key"><div class="preview" :style="{ color: previewColor }" v-html="s.svg" /><figcaption>{{ locale === 'en-US' ? s.nameEn : s.name }}<small>{{ s.key }}</small></figcaption></figure>
        </div>
        <div class="toolbar"><span class="tiny">{{ $t('hmiLibraryView.k2a9a07') }}</span><el-radio-group v-model="previewState" size="small" :aria-label="$t('hmiLibraryView.k2a9a07')"><el-radio-button value="stopped">{{ $t('hmiLibraryView.ka17f70') }}</el-radio-button><el-radio-button value="running">{{ $t('hmiLibraryView.k0c3acd') }}</el-radio-button><el-radio-button value="alarm">{{ $t('hmiLibraryView.k9f45b3') }}</el-radio-button><el-radio-button value="unknown">{{ $t('hmiLibraryView.kd9c32a') }}</el-radio-button></el-radio-group></div>
      </el-tab-pane>
      <el-tab-pane :label="$t('hmiLibraryView.k0d94c6', [library.items.length])" name="user">
        <el-table v-loading="loading" :data="library.items" size="small" data-test="library-items" :empty-text="loadError && !library.items.length ? loadError : $t('hmiLibraryView.k4827ec')">
          <el-table-column :label="$t('hmiLibraryView.kde61aa')" width="90" show-overflow-tooltip><template #default="{ row }"><div v-if="row.kind === 'symbol'" class="preview small" v-html="row.svg" /><el-tag v-else class="tag-quiet" :type="undefined" size="small">{{ $t('hmiLibraryView.k06d0f3') }}</el-tag></template></el-table-column>
          <el-table-column prop="key" :label="$t('hmiLibraryView.k5a8c89')" min-width="140" show-overflow-tooltip />
          <el-table-column prop="name" :label="$t('hmiLibraryView.k1be7ae')" min-width="140" show-overflow-tooltip />
          <el-table-column prop="category" :label="$t('hmiLibraryView.k435c52')" width="110" show-overflow-tooltip />
          <el-table-column :label="$t('hmiLibraryView.ka6ea64')" min-width="160" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ row.placeholders.join('、') }}</span></template></el-table-column>
          <el-table-column :label="$t('hmiLibraryView.kc9c775')" min-width="150" show-overflow-tooltip><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}</span></template></el-table-column>
          <el-table-column :label="$t('hmiLibraryView.usedBy')" min-width="150" show-overflow-tooltip><template #default="{ row }"><span class="tiny" :class="{ muted: !row.usedBy?.length }" :data-used-by="row.usedBy?.length ?? 0">{{ row.usedBy?.length ? row.usedBy.join('、') : $t('hmiLibraryView.unused') }}</span></template></el-table-column>
          <el-table-column :label="$t('hmiLibraryView.kf3ea6d')" width="160"><template #default="{ row }">
            <el-button v-if="row.kind === 'template'" size="small" :data-test="`library-edit-${row.key}`" @click="editTemplate(row)">{{ $t('hmiLibraryView.ka7f814') }}</el-button>
            <el-button size="small" type="danger" :data-test="`library-delete-${row.key}`" @click="remove(row)">{{ $t('hmiLibraryView.k3755f5') }}</el-button>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>
  <el-dialog v-model="editVisible" :title="$t('hmiLibraryView.k80efab', [editing?.name ?? ''])" width="96%" top="2vh" destroy-on-close :show-close="!editSaving" :close-on-click-modal="!editSaving" :close-on-press-escape="!editSaving" data-test="library-template-dialog">
    <el-alert v-if="editError" :title="editError" type="error" :closable="false" show-icon data-test="library-template-error" />
    <div class="toolbar" :inert="editSaving">
      <span class="tiny">{{ $t('hmiLibraryView.ka6ea64') }}</span>
      <el-tag v-for="p in placeholders" :key="p.tagId" closable size="small" :data-test="`placeholder-${p.key}`" @close="removePlaceholder(p)">{{ p.key }}</el-tag>
      <el-input v-model="newPlaceholder" size="small" style="width: 140px" :placeholder="$t('hmiLibraryView.k8b95a0')" data-test="placeholder-new" @keyup.enter="addPlaceholder()" />
      <el-button size="small" @click="addPlaceholder()">{{ $t('hmiLibraryView.k94191c') }}</el-button>
      <span class="tiny muted">{{ $t('hmiTemplateEditing.bindingHint') }}</span>
    </div>
    <ScreenDesignerView v-if="editVisible && editDoc" :key="editKey" embedded :model-value="editDoc" :binding-tags="placeholderTags" :disabled="editSaving" @update:model-value="d => (editDoc = d)" @save="saveTemplate()" />
    <template #footer><el-button :disabled="editSaving" @click="editVisible = false">{{ $t('hmiLibraryView.k4d0b46') }}</el-button><el-button type="primary" :loading="editSaving" data-test="library-template-save" @click="saveTemplate()">{{ $t('hmiLibraryView.kfbfc58') }}</el-button></template>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import ScreenDesignerView from './ScreenDesignerView.vue'
import { hmiLibraryApi, type LibraryInput } from '../api/hmiLibrary'
import { errorMessage } from '../api/http'
import { useLibraryStore } from '../stores/library'
import { builtinSymbols, isaColors, sanitizeSvg } from '../screens/symbols'
import { cleanNode, type LibraryItem, type ScreenDocument } from '../screens/model'
import { TemplateBindingError, templateFromEditor, templatePlaceholderInUse, templateToEditor, type TemplatePlaceholder } from '../screens/templateBindings'
import type { Tag } from '../types'
import { tr } from '../i18n'

const { locale } = useI18n()
const library = useLibraryStore()
const tab = ref('builtin'), previewState = ref<keyof typeof isaColors>('running'), loadError = ref(''), actionError = ref(''), loading = ref(true)
const previewColor = computed(() => isaColors[previewState.value])
const svgInput = ref<HTMLInputElement>(), jsonInput = ref<HTMLInputElement>()

async function guard(fn: () => Promise<unknown>, fallback: string, target: { value: string } = actionError): Promise<boolean> {
  target.value = ''
  try { await fn(); target.value = ''; return true } catch (e) { target.value = errorMessage(e, fallback); return false }
}
function toInput(x: LibraryItem): LibraryInput { return { key: x.key, name: x.name, category: x.category, kind: x.kind, svg: x.svg, document: x.document, placeholders: x.placeholders, width: x.width, height: x.height } }

async function importSvg(ev: Event): Promise<void> {
  const files = [...((ev.target as HTMLInputElement).files ?? [])]; (ev.target as HTMLInputElement).value = ''; actionError.value = ''
  if (loadError.value) { actionError.value = tr('designReview2.libraryMustReload'); return }
  const items: LibraryInput[] = []
  for (const f of files) {
    try {
      const svg = sanitizeSvg(await f.text())
      const vb = /viewBox="([\d.\s-]+)"/.exec(svg)?.[1]?.split(/\s+/).map(Number)
      const base = (f.name.replace(/\.svg$/i, '').toLowerCase().replace(/[^a-z0-9_-]/g, '-').replace(/^[^a-z]+/, '') || 'symbol').slice(0, 60)
      // 文件名归一化后可能撞键（Pump 1.svg 与 pump-1.svg）：同一批内加序号，不让后一个顶掉前一个
      let key = base
      for (let n = 2; items.some(x => x.key === key); n++) key = `${base}-${n}`
      items.push({ key, name: f.name.replace(/\.svg$/i, '').slice(0, 100), category: tr('hmiLibraryView.kc49333'), kind: 'symbol', svg,
        width: Math.min(400, Math.max(16, vb?.[2] ?? 100)), height: Math.min(400, Math.max(16, vb?.[3] ?? 100)) })
    } catch (e) { actionError.value = `${f.name}：${(e as Error).message}`; return }
  }
  const accepted = await confirmOverwrite(items)
  if (accepted?.length && await guard(async () => { const r = await hmiLibraryApi.import(accepted); ElMessage.success(tr('hmiLibraryView.k1228b3', [r.added, r.updated])) }, tr('hmiLibraryView.ka01a8d'))) await showImported()
}
async function importJson(ev: Event): Promise<void> {
  const file = (ev.target as HTMLInputElement).files?.[0]; (ev.target as HTMLInputElement).value = ''; if (!file) return
  actionError.value = ''
  if (loadError.value) { actionError.value = tr('designReview2.libraryMustReload'); return }
  let items: LibraryInput[]
  try { const parsed = JSON.parse(await file.text()); items = (Array.isArray(parsed) ? parsed : parsed.items) as LibraryInput[]; if (!Array.isArray(items)) throw new Error(tr('hmiLibraryView.kf42b5e')) }
  catch (e) { actionError.value = tr('hmiLibraryView.k04c9f0', [(e as Error).message]); return }
  for (const x of items) if (x.kind === 'symbol' && x.svg) { try { x.svg = sanitizeSvg(x.svg) } catch { actionError.value = tr('hmiLibraryView.k448e4e', [x.key]); return } }
  const accepted = await confirmOverwrite(items)
  if (accepted && await guard(async () => { const r = await hmiLibraryApi.import(accepted); ElMessage.success(tr('hmiLibraryView.k1228b3', [r.added, r.updated])) }, tr('hmiLibraryView.ka01a8d'))) await showImported()
}
async function showImported(): Promise<void> {
  try { await library.reload(); tab.value = 'user' } catch (e) { actionError.value = errorMessage(e, tr('hmiLibraryView.listFailed')) }
}
const compareVisible = ref(false)
const compareRows = ref<{ key: string; revision: number; fileRevision?: number; oldSvg: string | null; newSvg: string | null }[]>([])
let compareResolve: ((ok: boolean) => void) | null = null
function stampRevision(item: LibraryInput): LibraryInput {
  const current = library.items.find(row => row.key === item.key)
  return { ...item, revision: current?.revision ?? 0 }
}
function confirmOverwrite(items: LibraryInput[]): Promise<LibraryInput[] | null> {
  const rows = items.flatMap(item => {
    const current = library.items.find(row => row.key === item.key)
    if (!current) return []
    return [{ key: item.key, revision: current.revision, fileRevision: item.revision, oldSvg: current.kind === 'symbol' ? current.svg : null, newSvg: item.kind === 'symbol' ? item.svg ?? null : null }]
  })
  if (!rows.length) return Promise.resolve(items.map(stampRevision))
  compareRows.value = rows
  compareVisible.value = true
  return new Promise(resolve => { compareResolve = ok => resolve(ok ? items.map(stampRevision) : null) })
}
function acceptCompare(): void {
  const resolve = compareResolve
  compareResolve = null
  compareVisible.value = false
  resolve?.(true)
}
function cancelCompare(): void {
  const resolve = compareResolve
  compareResolve = null
  compareVisible.value = false
  resolve?.(false)
}
function exportLibrary(): void {
  const blob = new Blob([JSON.stringify({ format: 1, items: library.items.map(item => ({ ...toInput(item), revision: item.revision })) }, null, 2)], { type: 'application/json' })
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'scada-hmi-library.json'; a.click(); URL.revokeObjectURL(a.href)
}
async function remove(row: LibraryItem): Promise<void> {
  try { await ElMessageBox.confirm(tr('hmiLibraryView.k5c9bb5', [row.name]), tr('hmiLibraryView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => hmiLibraryApi.remove(row.id), tr('hmiLibraryView.k72250c'))) {
    try { await library.reload() } catch (e) { actionError.value = errorMessage(e, tr('hmiLibraryView.listFailed')) }
  }
}

// IDs are allocated once, never reused: removing a placeholder must not retarget bindings or undo history.
const editVisible = ref(false), editSaving = ref(false), editError = ref(''), editing = ref<LibraryItem | null>(null), editDoc = ref<ScreenDocument | null>(null), editKey = ref(0)
const placeholders = ref<TemplatePlaceholder[]>([]), newPlaceholder = ref('')
let nextPlaceholderId = -1
const allocatePlaceholder = (key: string): TemplatePlaceholder => ({ key, tagId: nextPlaceholderId-- })
const placeholderTags = computed<Tag[]>(() => placeholders.value.map(p => ({
  id: p.tagId, name: p.key, writable: true, unit: '', deviceId: null, kind: 'Physical', expression: '', calculationEnabled: false,
  calculationIntervalMs: 1000, inputMaxAgeMs: 1000, calculationRevision: 0, area: 'HoldingRegister', address: 0, dbNumber: 0,
  bitOffset: 0, nodeId: '', dataType: 'Double', scale: 1, offset: 0, archiveMode: 'None', deadband: 0, archivePeriodSec: 60, registerCount: 1
})))
function templateError(error: unknown): string {
  return error instanceof TemplateBindingError ? tr('hmiTemplateEditing.invalidBinding', [error.nodeId, error.binding]) : errorMessage(error, tr('hmiLibraryView.k40525a'))
}
function editTemplate(row: LibraryItem): void {
  if (editSaving.value) return
  if (!row.document) { actionError.value = tr('hmiTemplateEditing.missingDocument'); return }
  const bindings = row.placeholders.map(allocatePlaceholder)
  try {
    const doc = templateToEditor(JSON.parse(JSON.stringify(row.document)) as ScreenDocument, bindings)
    editing.value = row; placeholders.value = bindings; newPlaceholder.value = ''; actionError.value = ''
    editError.value = ''; editDoc.value = doc; editKey.value++; editVisible.value = true
  } catch (error) { actionError.value = templateError(error) }
}
function addPlaceholder(): void {
  if (editSaving.value) return
  const p = newPlaceholder.value.trim()
  if (!/^[a-zA-Z][a-zA-Z0-9_-]{0,63}$/.test(p) || placeholders.value.some(placeholder => placeholder.key === p)) { editError.value = tr('hmiLibraryView.k5d1712'); return }
  placeholders.value.push(allocatePlaceholder(p)); newPlaceholder.value = ''; editError.value = ''
}
function removePlaceholder(placeholder: TemplatePlaceholder): void {
  if (editSaving.value) return
  if (editDoc.value && templatePlaceholderInUse(editDoc.value, placeholder)) { editError.value = tr('hmiTemplateEditing.placeholderInUse', [placeholder.key]); return }
  placeholders.value = placeholders.value.filter(p => p.tagId !== placeholder.tagId); editError.value = ''
}
async function saveTemplate(): Promise<void> {
  const row = editing.value, doc = editDoc.value; if (editSaving.value || !row || !doc) return
  editSaving.value = true; editError.value = ''
  try {
    const converted = templateFromEditor(doc, placeholders.value), nodes = converted.nodes.map(cleanNode)
    const width = Math.max(16, ...nodes.map(n => n.x + n.width)), height = Math.max(16, ...nodes.map(n => n.y + n.height))
    const r = await hmiLibraryApi.update(row.id, { ...toInput(row), placeholders: placeholders.value.map(p => p.key), width, height, revision: row.revision, document: { ...converted, nodes } })
    ElMessage.success(r.usedBy.length ? tr('hmiTemplateEditing.savedAffected', [r.usedBy.join('、')]) : tr('hmiLibraryView.kcdfab9'))
    editVisible.value = false
    try { await library.reload() } catch (error) { actionError.value = errorMessage(error, tr('hmiLibraryView.listFailed')) }
  } catch (error) { editError.value = templateError(error) }
  finally { editSaving.value = false }
}
function reload(): void {
  loading.value = true
  library.reload().then(() => { loadError.value = '' }).catch(e => { loadError.value = errorMessage(e, tr('hmiLibraryView.listFailed')) }).finally(() => { loading.value = false })
}
onMounted(reload)
</script>

<style scoped>
.gallery { display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 10px; background: #2b3138; padding: 12px; border-radius: 6px; }
figure { margin: 0; text-align: center; color: #dee2e6; font-size: 12px; }
figcaption small { display: block; color: #adb5bd; font-size: 10px; }
.preview { height: 70px; } .preview :deep(svg) { height: 100%; width: 100%; }
.compare-row { display: grid; grid-template-columns: 1fr 1fr; gap: 12px; margin-top: 12px; }
.compare-row .preview { background: #2b3138; border-radius: 4px; }
.preview.small { height: 36px; background: #2b3138; color: #adb5bd; border-radius: 4px; }
</style>

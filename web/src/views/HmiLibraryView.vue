<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><span class="spacer" />
      <el-button data-test="library-import-svg" @click="svgInput?.click()">{{ $t('hmiLibraryView.ke94e6a') }}</el-button>
      <el-button data-test="library-import" @click="jsonInput?.click()">{{ $t('hmiLibraryView.ke6f963') }}</el-button>
      <el-button data-test="library-export" :disabled="!library.items.length" @click="exportLibrary()">{{ $t('hmiLibraryView.k10d52a') }}</el-button>
      <input ref="svgInput" type="file" accept=".svg,image/svg+xml" multiple hidden data-test="library-svg-file" @change="importSvg" />
      <input ref="jsonInput" type="file" accept=".json,application/json" hidden data-test="library-json-file" @change="importJson" />
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="library-load-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="library-action-error" />
    <p class="page-desc">{{ $t('hmiLibraryView.k758ce1') }}</p>
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
          <el-table-column :label="$t('hmiLibraryView.kde61aa')" width="90"><template #default="{ row }"><div v-if="row.kind === 'symbol'" class="preview small" v-html="row.svg" /><el-tag v-else size="small">{{ $t('hmiLibraryView.k06d0f3') }}</el-tag></template></el-table-column>
          <el-table-column prop="key" :label="$t('hmiLibraryView.k5a8c89')" min-width="140" />
          <el-table-column prop="name" :label="$t('hmiLibraryView.k1be7ae')" min-width="140" />
          <el-table-column prop="category" :label="$t('hmiLibraryView.k435c52')" width="110" />
          <el-table-column :label="$t('hmiLibraryView.ka6ea64')" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.placeholders.join('、') }}</span></template></el-table-column>
          <el-table-column :label="$t('hmiLibraryView.kc9c775')" min-width="150"><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}</span></template></el-table-column>
          <el-table-column :label="$t('hmiLibraryView.kf3ea6d')" width="160"><template #default="{ row }">
            <el-button v-if="row.kind === 'template'" size="small" :data-test="`library-edit-${row.key}`" @click="editTemplate(row)">{{ $t('hmiLibraryView.ka7f814') }}</el-button>
            <el-button size="small" type="danger" :data-test="`library-delete-${row.key}`" @click="remove(row)">{{ $t('hmiLibraryView.k3755f5') }}</el-button>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>
  <el-dialog v-model="editVisible" :title="$t('hmiLibraryView.k80efab', [editing?.name ?? ''])" width="96%" top="2vh" destroy-on-close data-test="library-template-dialog">
    <el-alert v-if="editError" :title="editError" type="error" :closable="false" show-icon data-test="library-template-error" />
    <div class="toolbar">
      <span class="tiny">{{ $t('hmiLibraryView.ka6ea64') }}</span>
      <el-tag v-for="p in placeholders" :key="p" closable size="small" @close="placeholders = placeholders.filter(x => x !== p)">{{ p }}</el-tag>
      <el-input v-model="newPlaceholder" size="small" style="width: 140px" :placeholder="$t('hmiLibraryView.k8b95a0')" data-test="placeholder-new" @keyup.enter="addPlaceholder()" />
      <el-button size="small" @click="addPlaceholder()">{{ $t('hmiLibraryView.k94191c') }}</el-button>
      <span class="tiny muted">{{ $t('hmiLibraryView.kdd280d') }}</span>
    </div>
    <ScreenDesignerView v-if="editVisible && editDoc" :key="editKey" embedded :model-value="editDoc" :binding-tags="placeholderTags" @update:model-value="d => (editDoc = d)" @save="saveTemplate()" />
    <template #footer><el-button @click="editVisible = false">{{ $t('hmiLibraryView.k4d0b46') }}</el-button><el-button type="primary" data-test="library-template-save" @click="saveTemplate()">{{ $t('hmiLibraryView.kfbfc58') }}</el-button></template>
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
import type { LibraryItem, ScreenDocument, ScreenNode } from '../screens/model'
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
  const items: LibraryInput[] = []
  for (const f of files) {
    try {
      const svg = sanitizeSvg(await f.text())
      const vb = /viewBox="([\d.\s-]+)"/.exec(svg)?.[1]?.split(/\s+/).map(Number)
      const base = f.name.replace(/\.svg$/i, '').toLowerCase().replace(/[^a-z0-9_-]/g, '-').replace(/^[^a-z]+/, '')
      items.push({ key: (base || 'symbol').slice(0, 64), name: f.name.replace(/\.svg$/i, '').slice(0, 100), category: tr('hmiLibraryView.kc49333'), kind: 'symbol', svg,
        width: Math.min(400, Math.max(16, vb?.[2] ?? 100)), height: Math.min(400, Math.max(16, vb?.[3] ?? 100)) })
    } catch (e) { actionError.value = `${f.name}：${(e as Error).message}`; return }
  }
  if (items.length && await guard(async () => { const r = await hmiLibraryApi.import(items); ElMessage.success(tr('hmiLibraryView.k1228b3', [r.added, r.updated])) }, tr('hmiLibraryView.ka01a8d'))) await showImported()
}
async function importJson(ev: Event): Promise<void> {
  const file = (ev.target as HTMLInputElement).files?.[0]; (ev.target as HTMLInputElement).value = ''; if (!file) return
  actionError.value = ''
  let items: LibraryInput[]
  try { const parsed = JSON.parse(await file.text()); items = (Array.isArray(parsed) ? parsed : parsed.items) as LibraryInput[]; if (!Array.isArray(items)) throw new Error(tr('hmiLibraryView.kf42b5e')) }
  catch (e) { actionError.value = tr('hmiLibraryView.k04c9f0', [(e as Error).message]); return }
  for (const x of items) if (x.kind === 'symbol' && x.svg) { try { x.svg = sanitizeSvg(x.svg) } catch { actionError.value = tr('hmiLibraryView.k448e4e', [x.key]); return } }
  const existing = items.filter(x => library.items.some(y => y.key === x.key)).map(x => x.key)
  if (existing.length) { try { await ElMessageBox.confirm(tr('hmiLibraryView.k9e2089', [existing.join('、')]), tr('hmiLibraryView.ke6f963'), { type: 'warning' }) } catch { return } }
  if (await guard(async () => { const r = await hmiLibraryApi.import(items.map(x => ({ ...x, revision: undefined }))); ElMessage.success(tr('hmiLibraryView.k1228b3', [r.added, r.updated])) }, tr('hmiLibraryView.ka01a8d'))) await showImported()
}
async function showImported(): Promise<void> {
  try { await library.reload(); tab.value = 'user' } catch (e) { actionError.value = errorMessage(e, tr('hmiLibraryView.listFailed')) }
}
function exportLibrary(): void {
  const blob = new Blob([JSON.stringify({ format: 1, items: library.items.map(toInput) }, null, 2)], { type: 'application/json' })
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'scada-hmi-library.json'; a.click(); URL.revokeObjectURL(a.href)
}
async function remove(row: LibraryItem): Promise<void> {
  try { await ElMessageBox.confirm(tr('hmiLibraryView.k5c9bb5', [row.name]), tr('hmiLibraryView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => hmiLibraryApi.remove(row.id), tr('hmiLibraryView.k72250c'))) {
    try { await library.reload() } catch (e) { actionError.value = errorMessage(e, tr('hmiLibraryView.listFailed')) }
  }
}

// 模板编辑：复用画面设计器（嵌入模式），占位符以负编号“伪测点”呈现，保存时换回占位符。
const editVisible = ref(false), editError = ref(''), editing = ref<LibraryItem | null>(null), editDoc = ref<ScreenDocument | null>(null), editKey = ref(0)
const placeholders = ref<string[]>([]), newPlaceholder = ref('')
const placeholderTags = computed(() => placeholders.value.map((p, i) => ({ id: -(i + 1), name: p, writable: true, unit: '', deviceId: null }) as unknown as Tag))
const idOf = (key?: string | null) => (key ? -(placeholders.value.indexOf(key) + 1) || null : null)
const keyOf = (id?: number | null) => (id && id < 0 ? placeholders.value[-id - 1] ?? null : null)
function editTemplate(row: LibraryItem): void {
  editing.value = row; placeholders.value = [...row.placeholders]
  const doc = JSON.parse(JSON.stringify(row.document)) as ScreenDocument
  doc.nodes = doc.nodes.map(n => ({ ...n, tagId: idOf(n.bind), alarmTagId: idOf(n.alarmBind), bind: null, alarmBind: null, action: { ...n.action, tagId: idOf(n.action.bind), bind: null } }))
  editError.value = ''; editDoc.value = doc; editKey.value++; editVisible.value = true
}
function addPlaceholder(): void {
  const p = newPlaceholder.value.trim()
  if (!/^[a-zA-Z][a-zA-Z0-9_-]{0,63}$/.test(p) || placeholders.value.includes(p)) { editError.value = tr('hmiLibraryView.k5d1712'); return }
  placeholders.value.push(p); newPlaceholder.value = ''; editError.value = ''
}
async function saveTemplate(): Promise<void> {
  const row = editing.value, doc = editDoc.value; if (!row || !doc) return
  const nodes: ScreenNode[] = doc.nodes.map(n => {
    const c: ScreenNode = { ...n, bind: keyOf(n.tagId), alarmBind: keyOf(n.alarmTagId), tagId: null, alarmTagId: null, action: { ...n.action, bind: n.action.type === 'command' ? keyOf(n.action.tagId) : null, tagId: null } }
    for (const k of ['bind', 'alarmBind'] as const) if (c[k] == null) delete c[k]
    if (c.action.bind == null) delete c.action.bind
    return c
  })
  const width = Math.max(16, ...nodes.map(n => n.x + n.width)), height = Math.max(16, ...nodes.map(n => n.y + n.height))
  await guard(async () => {
    const r = await hmiLibraryApi.update(row.id, { ...toInput(row), placeholders: placeholders.value, width, height, revision: row.revision, document: { ...doc, nodes } })
    ElMessage.success(r.usedBy.length ? tr('hmiLibraryView.k987c91', [r.usedBy.join('、')]) : tr('hmiLibraryView.kcdfab9'))
    editVisible.value = false; await library.reload()
  }, tr('hmiLibraryView.k40525a'), editError)
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
.preview.small { height: 36px; background: #2b3138; color: #adb5bd; border-radius: 4px; }
</style>

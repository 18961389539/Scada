<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>图库与模板</strong><span class="spacer" />
      <el-button data-test="library-import-svg" @click="svgInput?.click()">导入 SVG 符号</el-button>
      <el-button data-test="library-import" @click="jsonInput?.click()">导入图库</el-button>
      <el-button data-test="library-export" :disabled="!library.items.length" @click="exportLibrary()">导出图库</el-button>
      <input ref="svgInput" type="file" accept=".svg,image/svg+xml" multiple hidden data-test="library-svg-file" @change="importSvg" />
      <input ref="jsonInput" type="file" accept=".json,application/json" hidden data-test="library-json-file" @change="importJson" />
    </div>
    <p class="tiny muted">内置符号为自研 ISA-101 风格（灰度，运行/报警用状态色）。用户符号只允许静态 SVG（导入时清洗，服务端再次检查）。模板是含占位符的面板：画面中的实例只保存模板键与绑定，模板修改保存后，所有画面（含已发布画面）的实例立即使用新模板；控制动作仍由服务端按实例绑定解析并走控制闭环。图库随工程包导出与配置快照保存。</p>
    <el-tabs v-model="tab">
      <el-tab-pane label="内置符号" name="builtin">
        <div class="gallery" data-test="builtin-gallery">
          <figure v-for="s in builtinSymbols" :key="s.key" :data-symbol="s.key"><div class="preview" :style="{ color: previewColor }" v-html="s.svg" /><figcaption>{{ locale === 'en-US' ? s.nameEn : s.name }}<small>{{ s.key }}</small></figcaption></figure>
        </div>
        <div class="toolbar"><span class="tiny">预览状态</span><el-radio-group v-model="previewState" size="small"><el-radio-button value="stopped">停止</el-radio-button><el-radio-button value="running">运行</el-radio-button><el-radio-button value="alarm">报警</el-radio-button><el-radio-button value="unknown">未知</el-radio-button></el-radio-group></div>
      </el-tab-pane>
      <el-tab-pane :label="`用户图库 (${library.items.length})`" name="user">
        <el-table :data="library.items" size="small" data-test="library-items" empty-text="尚无用户符号或模板">
          <el-table-column label="预览" width="90"><template #default="{ row }"><div v-if="row.kind === 'symbol'" class="preview small" v-html="row.svg" /><el-tag v-else size="small">模板</el-tag></template></el-table-column>
          <el-table-column prop="key" label="键" min-width="140" />
          <el-table-column prop="name" label="名称" min-width="140" />
          <el-table-column prop="category" label="分类" width="110" />
          <el-table-column label="占位符" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.placeholders.join('、') }}</span></template></el-table-column>
          <el-table-column label="修改" min-width="150"><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}</span></template></el-table-column>
          <el-table-column label="操作" width="160"><template #default="{ row }">
            <el-button v-if="row.kind === 'template'" size="small" :data-test="`library-edit-${row.key}`" @click="editTemplate(row)">编辑</el-button>
            <el-button size="small" type="danger" :data-test="`library-delete-${row.key}`" @click="remove(row)">删除</el-button>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>
  <el-dialog v-model="editVisible" :title="`编辑模板 ${editing?.name ?? ''}`" width="96%" top="2vh" destroy-on-close data-test="library-template-dialog">
    <div class="toolbar">
      <span class="tiny">占位符</span>
      <el-tag v-for="p in placeholders" :key="p" closable size="small" @close="placeholders = placeholders.filter(x => x !== p)">{{ p }}</el-tag>
      <el-input v-model="newPlaceholder" size="small" style="width: 140px" placeholder="新占位符" data-test="placeholder-new" @keyup.enter="addPlaceholder()" />
      <el-button size="small" @click="addPlaceholder()">添加</el-button>
      <span class="tiny muted">在设计器中把图元“绑定”到占位符；保存后所有实例同步。</span>
    </div>
    <ScreenDesignerView v-if="editVisible && editDoc" :key="editKey" embedded :model-value="editDoc" :binding-tags="placeholderTags" @update:model-value="d => (editDoc = d)" @save="saveTemplate()" />
    <template #footer><el-button @click="editVisible = false">取消</el-button><el-button type="primary" data-test="library-template-save" @click="saveTemplate()">保存模板</el-button></template>
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

const { locale } = useI18n()
const library = useLibraryStore()
const tab = ref('builtin'), previewState = ref<keyof typeof isaColors>('running')
const previewColor = computed(() => isaColors[previewState.value])
const svgInput = ref<HTMLInputElement>(), jsonInput = ref<HTMLInputElement>()

async function guard(fn: () => Promise<unknown>, fallback: string): Promise<boolean> { try { await fn(); return true } catch (e) { ElMessage.error(errorMessage(e, fallback)); return false } }
function toInput(x: LibraryItem): LibraryInput { return { key: x.key, name: x.name, category: x.category, kind: x.kind, svg: x.svg, document: x.document, placeholders: x.placeholders, width: x.width, height: x.height } }

async function importSvg(ev: Event): Promise<void> {
  const files = [...((ev.target as HTMLInputElement).files ?? [])]; (ev.target as HTMLInputElement).value = ''
  const items: LibraryInput[] = []
  for (const f of files) {
    try {
      const svg = sanitizeSvg(await f.text())
      const vb = /viewBox="([\d.\s-]+)"/.exec(svg)?.[1]?.split(/\s+/).map(Number)
      const base = f.name.replace(/\.svg$/i, '').toLowerCase().replace(/[^a-z0-9_-]/g, '-').replace(/^[^a-z]+/, '')
      items.push({ key: (base || 'symbol').slice(0, 64), name: f.name.replace(/\.svg$/i, '').slice(0, 100), category: '自定义', kind: 'symbol', svg,
        width: Math.min(400, Math.max(16, vb?.[2] ?? 100)), height: Math.min(400, Math.max(16, vb?.[3] ?? 100)) })
    } catch (e) { ElMessage.error(`${f.name}：${(e as Error).message}`); return }
  }
  if (items.length && await guard(async () => { const r = await hmiLibraryApi.import(items); ElMessage.success(`已导入：新增 ${r.added}，覆盖 ${r.updated}`) }, '导入失败')) { await library.reload(); tab.value = 'user' }
}
async function importJson(ev: Event): Promise<void> {
  const file = (ev.target as HTMLInputElement).files?.[0]; (ev.target as HTMLInputElement).value = ''; if (!file) return
  let items: LibraryInput[]
  try { const parsed = JSON.parse(await file.text()); items = (Array.isArray(parsed) ? parsed : parsed.items) as LibraryInput[]; if (!Array.isArray(items)) throw new Error('格式无效') }
  catch (e) { ElMessage.error(`文件无效：${(e as Error).message}`); return }
  for (const x of items) if (x.kind === 'symbol' && x.svg) { try { x.svg = sanitizeSvg(x.svg) } catch { ElMessage.error(`${x.key}：SVG 无效`); return } }
  const existing = items.filter(x => library.items.some(y => y.key === x.key)).map(x => x.key)
  if (existing.length) { try { await ElMessageBox.confirm(`将覆盖已有项：${existing.join('、')}。被画面引用的模板会立即生效。`, '导入图库', { type: 'warning' }) } catch { return } }
  if (await guard(async () => { const r = await hmiLibraryApi.import(items.map(x => ({ ...x, revision: undefined }))); ElMessage.success(`已导入：新增 ${r.added}，覆盖 ${r.updated}`) }, '导入失败')) { await library.reload(); tab.value = 'user' }
}
function exportLibrary(): void {
  const blob = new Blob([JSON.stringify({ format: 1, items: library.items.map(toInput) }, null, 2)], { type: 'application/json' })
  const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'scada-hmi-library.json'; a.click(); URL.revokeObjectURL(a.href)
}
async function remove(row: LibraryItem): Promise<void> {
  try { await ElMessageBox.confirm(`删除 ${row.name}？被画面引用时会被拒绝。`, '删除', { type: 'warning' }) } catch { return }
  if (await guard(() => hmiLibraryApi.remove(row.id), '删除失败')) await library.reload()
}

// 模板编辑：复用画面设计器（嵌入模式），占位符以负编号“伪测点”呈现，保存时换回占位符。
const editVisible = ref(false), editing = ref<LibraryItem | null>(null), editDoc = ref<ScreenDocument | null>(null), editKey = ref(0)
const placeholders = ref<string[]>([]), newPlaceholder = ref('')
const placeholderTags = computed(() => placeholders.value.map((p, i) => ({ id: -(i + 1), name: p, writable: true, unit: '', deviceId: null }) as unknown as Tag))
const idOf = (key?: string | null) => (key ? -(placeholders.value.indexOf(key) + 1) || null : null)
const keyOf = (id?: number | null) => (id && id < 0 ? placeholders.value[-id - 1] ?? null : null)
function editTemplate(row: LibraryItem): void {
  editing.value = row; placeholders.value = [...row.placeholders]
  const doc = JSON.parse(JSON.stringify(row.document)) as ScreenDocument
  doc.nodes = doc.nodes.map(n => ({ ...n, tagId: idOf(n.bind), alarmTagId: idOf(n.alarmBind), bind: null, alarmBind: null, action: { ...n.action, tagId: idOf(n.action.bind), bind: null } }))
  editDoc.value = doc; editKey.value++; editVisible.value = true
}
function addPlaceholder(): void {
  const p = newPlaceholder.value.trim()
  if (!/^[a-zA-Z][a-zA-Z0-9_-]{0,63}$/.test(p) || placeholders.value.includes(p)) { ElMessage.error('占位符须为字母开头的唯一键'); return }
  placeholders.value.push(p); newPlaceholder.value = ''
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
    ElMessage.success(r.usedBy.length ? `已保存，以下画面的实例已同步：${r.usedBy.join('、')}` : '已保存')
    editVisible.value = false; await library.reload()
  }, '保存失败')
}
onMounted(() => library.reload().catch(e => ElMessage.error(errorMessage(e, '加载失败'))))
</script>

<style scoped>
.gallery { display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 10px; background: #2b3138; padding: 12px; border-radius: 6px; }
figure { margin: 0; text-align: center; color: #dee2e6; font-size: 12px; }
figcaption small { display: block; color: #868e96; font-size: 10px; }
.preview { height: 70px; } .preview :deep(svg) { height: 100%; width: 100%; }
.preview.small { height: 36px; background: #2b3138; color: #adb5bd; border-radius: 4px; }
</style>

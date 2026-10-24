<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>计算测点</strong><el-input v-model="search" placeholder="名称、表达式或编号" clearable style="width: 240px" /><span class="spacer" />
      <el-switch v-model="automatic" active-text="每 2 秒刷新" /><el-button :loading="loading" :disabled="saving" @click="refresh()">刷新计算状态</el-button>
      <el-button type="primary" :disabled="saving" @click="open(null)">新建计算测点</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
    <p class="tiny muted">计算在中心执行，结果可用于实时监视、历史、报警和画面绑定。计算测点只读；输入缺失、过期或坏质量时保留上次值并标记质量。</p>
    <el-table :data="filtered" v-loading="loading" row-key="tag.id" empty-text="尚无计算测点">
      <el-table-column label="测点" min-width="165"><template #default="{ row }"><strong>#{{ row.tag.id }} · {{ row.tag.name }}</strong><div class="tiny muted">{{ row.tag.unit || '无单位' }} · 版本 {{ row.tag.calculationRevision }}</div></template></el-table-column>
      <el-table-column label="表达式 / 输入" min-width="240"><template #default="{ row }"><code class="formula">{{ row.tag.expression }}</code><div class="tiny muted">{{ row.dependencies.map((id: number) => `#${id}`).join('、') || '常量表达式' }}</div></template></el-table-column>
      <el-table-column label="运行设置" width="160"><template #default="{ row }">{{ row.tag.calculationEnabled ? '启用' : '停用' }} · {{ row.tag.calculationIntervalMs }} ms<div class="tiny muted">输入有效 {{ row.tag.inputMaxAgeMs }} ms</div></template></el-table-column>
      <el-table-column label="当前值 / 质量" width="170"><template #default="{ row }"><strong>{{ row.current ? formatValue(row.current.value) : '--' }}</strong>
        <div><el-tag :type="row.current?.quality === 'Good' && !stale ? 'success' : 'warning'">{{ stale ? '待刷新' : qualityLabel(row.current) }}</el-tag></div>
      </template></el-table-column>
      <el-table-column label="时间 / 提示" min-width="220"><template #default="{ row }"><div class="tiny">{{ row.current ? formatTime(row.current.ts) : '等待计算' }}</div><span>{{ row.definitionError || row.current?.error }}</span></template></el-table-column>
      <el-table-column label="操作" width="125"><template #default="{ row }"><el-button link type="primary" :disabled="saving" @click="open(row.tag)">编辑</el-button><el-button link type="danger" :disabled="saving" @click="remove(row.tag)">删除</el-button></template></el-table-column>
    </el-table>
  </div>
    <el-dialog v-model="editor" :title="editing === null ? '新建计算测点' : '编辑计算测点'" width="820px" :close-on-click-modal="false" :before-close="beforeClose">
      <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" />
      <el-form label-width="125px" :disabled="saving" @submit.prevent>
        <el-row :gutter="12"><el-col :span="16"><el-form-item label="名称" required><el-input v-model="form.name" maxlength="100" aria-label="计算测点名称" /></el-form-item></el-col>
          <el-col :span="8"><el-form-item label="启用"><el-switch v-model="form.enabled" /></el-form-item></el-col></el-row>
        <el-form-item label="描述"><el-input v-model="form.description" maxlength="500" /></el-form-item>
        <el-form-item label="插入测点引用"><el-select v-model="selectedInput" filterable placeholder="按名称或编号查找输入" style="flex: 1; min-width: 0" aria-label="表达式输入测点">
          <el-option v-for="tag in inputs.filter(t => t.id !== editing)" :key="tag.id" :value="tag.id" :label="`#${tag.id} · ${tag.name} · ${tag.deviceId === null ? '计算' : `设备 #${tag.deviceId}`}`" />
        </el-select><el-button style="margin-left: 8px" :disabled="!selectedInput" @click="form.expression += `#${selectedInput}`">插入引用</el-button></el-form-item>
        <el-form-item label="表达式" required><el-input v-model="form.expression" type="textarea" :rows="4" maxlength="4096" show-word-limit aria-label="计算表达式" /></el-form-item>
        <p class="tiny muted expression-help">用 #编号 引用工程量，例如 (#1 + #2) / 2。支持算术、比较、&& / ||、if、min/max、abs、sqrt、pow、clamp、round 等。布尔结果为 1/0；不执行文件、网络或系统代码。</p>
        <el-row :gutter="12"><el-col :span="12"><el-form-item label="计算周期(ms)"><el-input-number v-model="form.intervalMs" :min="100" :max="600000" :step="100" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="输入有效期(ms)"><el-input-number v-model="form.inputMaxAgeMs" :min="100" :max="3600000" :step="1000" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="单位"><el-input v-model="form.unit" maxlength="32" /></el-form-item></el-col><el-col :span="12"><el-form-item label="存储方式"><el-select v-model="form.archiveMode"><el-option v-for="(label, value) in archiveModeLabels" :key="value" :value="value" :label="label" /></el-select></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="历史死区"><el-input-number v-model="form.deadband" :min="0" /></el-form-item></el-col><el-col :span="12"><el-form-item label="存储周期(秒)"><el-input-number v-model="form.archivePeriodSec" :min="1" /></el-form-item></el-col>
        </el-row>
      </el-form>
      <el-alert v-if="preview" :type="preview.value.quality === 'Good' ? 'success' : 'warning'" :closable="false" :title="`预览值 ${formatValue(preview.value.value)} · ${qualityLabels[preview.value.quality]}${preview.value.error ? `：${preview.value.error}` : ''}`" />
      <p v-if="preview" class="tiny muted">预览仅读取当前输入，不发布、不归档或触发报警。引用 {{ preview.dependencies.map(id => `#${id}`).join('、') || '无' }}。</p>
      <template #footer><el-button v-if="editing !== null" :disabled="saving" @click="reread">重新读取定义</el-button><el-button :disabled="saving" @click="close">取消</el-button><el-button :loading="previewing" :disabled="saving" @click="evaluate">校验并预览</el-button><el-button type="primary" :loading="saving" @click="save">保存计算定义</el-button></template>
    </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { calculationApi, type CalculationInfo, type CalculationInput, type CalculationPreview } from '../api/calculations'
import { errorMessage } from '../api/http'
import { archiveModeLabels, formatTime, formatValue, qualityLabels } from '../utils'
import type { Tag, TagValue } from '../types'
const route = useRoute(), router = useRouter(), rows = ref<CalculationInfo[]>([]), inputs = ref<Tag[]>([]), search = ref(''), automatic = ref(true)
const loading = ref(false), saving = ref(false), readError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null), selectedInput = ref<number>()
const preview = ref<CalculationPreview | null>(null), previewing = ref(false), lastRead = ref(0), tick = ref(performance.now())
const empty = (): CalculationInput => ({ name: '', expression: '', description: '', unit: '', enabled: true, intervalMs: 1000, inputMaxAgeMs: 15000, archiveMode: 'OnChange', deadband: 0, archivePeriodSec: 600, revision: 0 })
const form = reactive<CalculationInput>(empty()), baseline = ref('')
const dirty = computed(() => editor.value && JSON.stringify(form) !== baseline.value)
const filtered = computed(() => rows.value.filter(x => `${x.tag.id} ${x.tag.name} ${x.tag.expression}`.toLowerCase().includes(search.value.trim().toLowerCase())))
const stale = computed(() => !!readError.value || !lastRead.value || tick.value - lastRead.value > 5000)
function qualityLabel(value: TagValue | null): string { return qualityLabels[value?.quality ?? 'Uncertain'] }
let alive = true, generation = 0, previewGeneration = 0, readController: AbortController | undefined, previewController: AbortController | undefined, polling: number | undefined, ticker: number | undefined
async function refresh(force = false): Promise<boolean> {
  if (!alive || saving.value && !force) return false
  const current = ++generation; readController?.abort(); const controller = readController = new AbortController(); loading.value = true
  try {
    const [list, tags] = await Promise.all([calculationApi.list(controller.signal), calculationApi.inputs(controller.signal)])
    if (!alive || current !== generation) return false
    rows.value = list; inputs.value = tags; readError.value = ''; lastRead.value = performance.now(); tick.value = lastRead.value
    const id = Number(route.query.tagId); if (!editor.value && id > 0) { const item = list.find(x => x.tag.id === id); if (item) open(item.tag) }
    return true
  } catch (e) { if (alive && current === generation && !controller.signal.aborted) readError.value = errorMessage(e, '读取计算状态失败'); return false }
  finally { if (alive && current === generation) loading.value = false }
}
function open(tag: Tag | null): void {
  editing.value = tag?.id ?? null; Object.assign(form, tag ? { name: tag.name, expression: tag.expression, description: tag.description ?? '', unit: tag.unit ?? '', enabled: tag.calculationEnabled,
    intervalMs: tag.calculationIntervalMs, inputMaxAgeMs: tag.inputMaxAgeMs, archiveMode: tag.archiveMode, deadband: tag.deadband, archivePeriodSec: tag.archivePeriodSec, revision: tag.calculationRevision } : empty())
  baseline.value = JSON.stringify(form); mutationError.value = ''; selectedInput.value = undefined; preview.value = null; editor.value = true
}
async function permitClose(): Promise<boolean> {
  if (saving.value) return false
  if (!dirty.value) return true
  try { await ElMessageBox.confirm('放弃尚未保存的计算定义？', '离开编辑', { confirmButtonText: '放弃修改', type: 'warning' }); return true } catch { return false }
}
function clearEditor(): void {
  editor.value = false; previewGeneration++; previewController?.abort(); preview.value = null
  if (route.query.tagId) { const query = { ...route.query }; delete query.tagId; void router.replace({ query }) }
}
async function close(): Promise<void> { if (await permitClose()) clearEditor() }
async function beforeClose(done: () => void): Promise<void> { if (await permitClose()) { clearEditor(); done() } }
async function reread(): Promise<void> {
  if (!(await permitClose()) || !(await refresh())) return
  const tag = rows.value.find(x => x.tag.id === editing.value)?.tag
  if (tag) open(tag); else mutationError.value = '计算定义已删除，请核对配置后关闭编辑。'
}
async function evaluate(): Promise<void> {
  const serialized = JSON.stringify(form), current = ++previewGeneration; previewController?.abort(); const controller = previewController = new AbortController(); previewing.value = true; mutationError.value = ''
  try { const result = await calculationApi.preview(editing.value, { ...form }, controller.signal); if (alive && editor.value && current === previewGeneration && serialized === JSON.stringify(form)) preview.value = result }
  catch (e) { if (alive && current === previewGeneration && !controller.signal.aborted) mutationError.value = errorMessage(e) }
  finally { if (alive && current === previewGeneration) previewing.value = false }
}
async function save(): Promise<void> {
  if (saving.value) return
  saving.value = true; mutationError.value = ''; generation++; readController?.abort(); loading.value = false
  previewGeneration++; previewController?.abort(); preview.value = null; previewing.value = false
  try { await calculationApi.save(editing.value, { ...form }); if (alive) { baseline.value = JSON.stringify(form); clearEditor(); ElMessage.success('计算定义已保存'); await refresh(true) } }
  catch (e) { if (alive) { mutationError.value = `${errorMessage(e)}；请刷新并核对实际状态，不会自动重复提交。`; await refresh(true) } }
  finally { if (alive) saving.value = false }
}
async function remove(tag: Tag): Promise<void> {
  if (saving.value) return
  try { await ElMessageBox.confirm(`删除计算测点 #${tag.id} ${tag.name}？历史记录保留；有计算依赖时会拒绝删除。`, '删除计算测点', { type: 'warning', confirmButtonText: '确认删除' }) } catch { return }
  if (!alive) return
  saving.value = true
  try { await calculationApi.remove(tag); if (alive) { ElMessage.success('计算测点已删除'); await refresh(true) } }
  catch (e) { if (alive) { ElMessage.error(errorMessage(e)); await refresh(true) } }
  finally { if (alive) saving.value = false }
}
watch(() => JSON.stringify(form), () => { previewGeneration++; previewController?.abort(); preview.value = null; previewing.value = false })
watch(() => route.query.tagId, () => { void refresh() })
onBeforeRouteLeave(async () => { const allowed = await permitClose(); if (allowed) { editor.value = false; previewController?.abort() } return allowed })
onMounted(() => { void refresh(); polling = window.setInterval(() => { if (automatic.value && !loading.value && !saving.value) void refresh() }, 2000); ticker = window.setInterval(() => { tick.value = performance.now() }, 1000) })
onBeforeUnmount(() => { alive = false; generation++; previewGeneration++; readController?.abort(); previewController?.abort(); if (polling) clearInterval(polling); if (ticker) clearInterval(ticker) })
</script>
<style scoped>.formula { overflow-wrap: anywhere; white-space: pre-wrap; font-size: 12px; }.expression-help { margin: -10px 0 18px 125px; line-height: 1.6; }</style>

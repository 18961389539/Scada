<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>{{ $t('calculationsView.k4e0387') }}</strong><el-input v-model="search" :placeholder="$t('calculationsView.ke4d900')" clearable style="width: 240px" /><span class="spacer" />
      <el-switch v-model="automatic" :active-text="$t('calculationsView.k1646aa')" /><el-button :loading="loading" :disabled="saving" @click="refresh()">{{ $t('calculationsView.kef5297') }}</el-button>
      <el-button type="primary" :disabled="saving" @click="open(null)">{{ $t('calculationsView.ke33a29') }}</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
    <p class="tiny muted">{{ $t('calculationsView.k698e88') }}</p>
    <el-table :data="filtered" v-loading="loading" row-key="tag.id" :empty-text="$t('calculationsView.k4180bf')">
      <el-table-column :label="$t('calculationsView.k8cff1c')" min-width="165"><template #default="{ row }"><strong>#{{ row.tag.id }} · {{ row.tag.name }}</strong><div class="tiny muted">{{ $t('calculationsView.kf41ad7', [row.tag.unit || $t('calculationsView.k8f4056'), row.tag.calculationRevision]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calculationsView.kc89b06')" min-width="240"><template #default="{ row }"><code class="formula">{{ row.tag.expression }}</code><div class="tiny muted">{{ row.dependencies.map((id: number) => `#${id}`).join('、') || $t('calculationsView.k353fcf') }}</div></template></el-table-column>
      <el-table-column :label="$t('calculationsView.k1ffb1e')" width="160"><template #default="{ row }">{{ row.tag.calculationEnabled ? $t('calculationsView.kd4e9ca') : $t('calculationsView.kd989e5') }} · {{ row.tag.calculationIntervalMs }} ms<div class="tiny muted">{{ $t('calculationsView.k6ed298', [row.tag.inputMaxAgeMs]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calculationsView.k05b199')" width="170"><template #default="{ row }"><strong>{{ row.current ? formatValue(row.current.value) : '--' }}</strong>
        <div><el-tag :type="row.current?.quality === 'Good' && !stale ? 'success' : 'warning'">{{ stale ? $t('calculationsView.k98f3fb') : qualityLabel(row.current) }}</el-tag></div>
      </template></el-table-column>
      <el-table-column :label="$t('calculationsView.kc23cba')" min-width="220"><template #default="{ row }"><div class="tiny">{{ row.current ? formatTime(row.current.ts) : $t('calculationsView.k046113') }}</div><span>{{ row.definitionError || row.current?.error }}</span></template></el-table-column>
      <el-table-column :label="$t('calculationsView.kf3ea6d')" width="125"><template #default="{ row }"><el-button link type="primary" :disabled="saving" @click="open(row.tag)">{{ $t('calculationsView.ka7f814') }}</el-button><el-button link type="danger" :disabled="saving" @click="remove(row.tag)">{{ $t('calculationsView.k3755f5') }}</el-button></template></el-table-column>
    </el-table>
  </div>
    <el-dialog v-model="editor" :title="editing === null ? $t('calculationsView.ke33a29') : $t('calculationsView.kc07dfd')" width="820px" :close-on-click-modal="false" :before-close="beforeClose">
      <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" />
      <el-form label-width="125px" :disabled="saving" @submit.prevent>
        <el-row :gutter="12"><el-col :span="16"><el-form-item :label="$t('calculationsView.k1be7ae')" required><el-input v-model="form.name" maxlength="100" :aria-label="$t('calculationsView.k099dcc')" /></el-form-item></el-col>
          <el-col :span="8"><el-form-item :label="$t('calculationsView.kd4e9ca')"><el-switch v-model="form.enabled" /></el-form-item></el-col></el-row>
        <el-form-item :label="$t('calculationsView.k412f54')"><el-input v-model="form.description" maxlength="500" /></el-form-item>
        <el-form-item :label="$t('calculationsView.k6587de')"><el-select v-model="selectedInput" filterable :placeholder="$t('calculationsView.kefa7ad')" style="flex: 1; min-width: 0" :aria-label="$t('calculationsView.kbec1bf')">
          <el-option v-for="tag in inputs.filter(t => t.id !== editing)" :key="tag.id" :value="tag.id" :label="$t('calculationsView.k19043a', [tag.id, tag.name, tag.deviceId === null ? $t('calculationsView.k27849e') : $t('calculationsView.k9d4c63', [tag.deviceId])])" />
        </el-select><el-button style="margin-left: 8px" :disabled="!selectedInput" @click="form.expression += `#${selectedInput}`">{{ $t('calculationsView.k7fa8aa') }}</el-button></el-form-item>
        <el-form-item :label="$t('calculationsView.k513c1c')" required><el-input v-model="form.expression" type="textarea" :rows="4" maxlength="4096" show-word-limit :aria-label="$t('calculationsView.k80a343')" /></el-form-item>
        <p class="tiny muted expression-help">{{ $t('calculationsView.k5de168') }}</p>
        <el-row :gutter="12"><el-col :span="12"><el-form-item :label="$t('calculationsView.kb1fbaf')"><el-input-number v-model="form.intervalMs" :min="100" :max="600000" :step="100" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calculationsView.ka59f03')"><el-input-number v-model="form.inputMaxAgeMs" :min="100" :max="3600000" :step="1000" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calculationsView.k8ec8c8')"><el-input v-model="form.unit" maxlength="32" /></el-form-item></el-col><el-col :span="12"><el-form-item :label="$t('calculationsView.k30a088')"><el-select v-model="form.archiveMode"><el-option v-for="(label, value) in archiveModeLabels" :key="value" :value="value" :label="label" /></el-select></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calculationsView.kd020e2')"><el-input-number v-model="form.deadband" :min="0" /></el-form-item></el-col><el-col :span="12"><el-form-item :label="$t('calculationsView.k9ed289')"><el-input-number v-model="form.archivePeriodSec" :min="1" /></el-form-item></el-col>
        </el-row>
      </el-form>
      <el-alert v-if="preview" :type="preview.value.quality === 'Good' ? 'success' : 'warning'" :closable="false" :title="$t('calculationsView.kfe0166', [formatValue(preview.value.value), qualityLabels[preview.value.quality], preview.value.error ? `：${preview.value.error}` : ''])" />
      <p v-if="preview" class="tiny muted">{{ $t('calculationsView.kdc2401', [preview.dependencies.map(id => `#${id}`).join('、') || $t('calculationsView.k720777')]) }}</p>
      <template #footer><el-button v-if="editing !== null" :disabled="saving" @click="reread">{{ $t('calculationsView.k1bedea') }}</el-button><el-button :disabled="saving" @click="close">{{ $t('calculationsView.k4d0b46') }}</el-button><el-button :loading="previewing" :disabled="saving" @click="evaluate">{{ $t('calculationsView.k0de057') }}</el-button><el-button type="primary" :loading="saving" @click="save">{{ $t('calculationsView.k33d0d9') }}</el-button></template>
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
import { tr } from '../i18n'
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
  } catch (e) { if (alive && current === generation && !controller.signal.aborted) readError.value = errorMessage(e, tr('calculationsView.k92ac3f')); return false }
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
  try { await ElMessageBox.confirm(tr('calculationsView.k39daf2'), tr('calculationsView.kbe1bff'), { confirmButtonText: tr('calculationsView.k1cead0'), type: 'warning' }); return true } catch { return false }
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
  if (tag) open(tag); else mutationError.value = tr('calculationsView.k8587f2')
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
  try { await calculationApi.save(editing.value, { ...form }); if (alive) { baseline.value = JSON.stringify(form); clearEditor(); ElMessage.success(tr('calculationsView.k042552')); await refresh(true) } }
  catch (e) { if (alive) { mutationError.value = tr('calculationsView.kc70c7d', [errorMessage(e)]); await refresh(true) } }
  finally { if (alive) saving.value = false }
}
async function remove(tag: Tag): Promise<void> {
  if (saving.value) return
  try { await ElMessageBox.confirm(tr('calculationsView.ka442e1', [tag.id, tag.name]), tr('calculationsView.k10b8b0'), { type: 'warning', confirmButtonText: tr('calculationsView.k3c06ab') }) } catch { return }
  if (!alive) return
  saving.value = true
  try { await calculationApi.remove(tag); if (alive) { ElMessage.success(tr('calculationsView.kc8f7a1')); await refresh(true) } }
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

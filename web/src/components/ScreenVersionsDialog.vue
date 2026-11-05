<template>
  <el-dialog :model-value="modelValue" :title="$t('screenVersions.title')" class="screen-versions-dialog" :before-close="close" :close-on-click-modal="!blocked" :close-on-press-escape="!blocked" :show-close="!blocked" destroy-on-close data-test="screen-versions-dialog" @update:model-value="close()">
    <p class="help">{{ $t('screenVersions.historyHint') }}</p>
    <el-alert v-if="dirty" :title="$t('screenVersions.dirtyBlocked')" type="warning" :closable="false" show-icon data-test="screen-versions-dirty" />
    <el-alert v-if="conflict" :title="$t('screenVersions.conflict')" type="error" :closable="false" show-icon data-test="screen-versions-conflict" />
    <el-alert v-if="submitError" :title="submitError" type="error" :closable="false" show-icon data-test="screen-rollback-error" />
    <ul v-if="submitIssues.length" class="issues" data-test="screen-rollback-issues"><li v-for="(issue, index) in submitIssues" :key="index">{{ issue.nodeId ? `${issue.nodeId}: ` : '' }}{{ issue.message }}</li></ul>
    <div class="history-heading"><h3>{{ $t('screenVersions.versions') }}</h3><el-button size="small" :disabled="blocked || listLoading" data-test="screen-versions-refresh" @click="loadVersions()">{{ $t('screenVersions.refresh') }}</el-button></div>
    <el-alert v-if="listError" :title="listError" type="error" :closable="false" show-icon data-test="screen-versions-list-error" />
    <el-button v-if="listError" :disabled="blocked || listLoading" data-test="screen-versions-list-retry" @click="loadVersions(failedAppend)">{{ $t('screenVersions.retry') }}</el-button>
    <div v-loading="listLoading" class="version-list" data-test="screen-version-list" :aria-busy="listLoading">
      <el-empty v-if="!listLoading && !listError && !versions.length" :description="$t('screenVersions.empty')" :image-size="56" data-test="screen-versions-empty" />
      <button v-for="version in versions" :key="version.revision" type="button" class="version-row" :class="{ selected: selectedRevision === version.revision }" :disabled="blocked || listLoading" :aria-pressed="selectedRevision === version.revision" :data-test="`screen-version-${version.revision}`" @click="selectVersion(version.revision)">
        <span class="version-heading"><strong>r{{ version.revision }} · {{ version.name }}</strong><el-tag v-if="version.revision === publishedRevision" type="info" size="small" data-test="screen-version-current">{{ $t('screenVersions.current') }}</el-tag></span>
        <span>{{ version.publishedBy }} · {{ formatTime(version.publishedTs) }} · {{ kindLabel(version.kind) }}</span>
        <span v-if="version.restoredFromRevision != null">{{ $t('screenVersions.restoredFrom', [version.restoredFromRevision]) }}</span>
        <span class="version-note">{{ version.note || '—' }}</span>
      </button>
    </div>
    <el-button v-if="hasMore" :loading="listLoading" :disabled="blocked || listLoading" data-test="screen-versions-more" @click="loadVersions(true)">{{ $t('screenVersions.loadOlder') }}</el-button>
    <section v-if="selectedRevision != null" v-loading="detailLoading" class="version-detail" :aria-busy="detailLoading">
      <el-alert v-if="detailError" :title="detailError" type="error" :closable="false" show-icon data-test="screen-version-detail-error" />
      <el-button v-if="detailError" :disabled="blocked || detailLoading" data-test="screen-version-detail-retry" @click="selectVersion(selectedRevision)">{{ $t('screenVersions.retry') }}</el-button>
      <template v-if="detail">
        <h3 data-test="screen-version-detail-heading">{{ detail.name }} · r{{ detail.revision }}</h3>
        <p class="help">{{ detail.publishedBy }} · {{ formatTime(detail.publishedTs) }} · {{ kindLabel(detail.kind) }}</p>
        <p v-if="detail.restoredFromRevision != null" class="help">{{ $t('screenVersions.restoredFrom', [detail.restoredFromRevision]) }}</p>
        <p class="version-note" data-test="screen-version-detail-note">{{ detail.note || '—' }}</p>
        <section v-if="currentDocument" class="version-diff" data-test="screen-version-diff">
          <h4>{{ $t('screenVersions.diffTitle') }}</h4>
          <p v-if="!diffRows.length" class="help" data-test="screen-version-diff-none">{{ $t('screenVersions.diffNone') }}</p>
          <p v-else class="help" data-test="screen-version-diff-summary">{{ $t('screenVersions.diffSummary', [diffRows.length]) }}</p>
          <ul v-if="diffRows.length" class="diff-list">
            <li v-for="(row, index) in diffRows.slice(0, 8)" :key="index"><strong>{{ row.label }}</strong>：{{ row.fields.join(', ') }}</li>
            <li v-if="diffRows.length > 8" class="help">{{ $t('screenVersions.diffMore', [diffRows.length - 8]) }}</li>
          </ul>
        </section>
        <el-alert v-if="detail.requiresRepublish" :title="$t('screenVersions.legacyBlocked')" type="warning" :closable="false" show-icon data-test="screen-version-legacy" />
        <p class="help">{{ $t('screenVersions.previewHint') }}</p>
        <el-button :disabled="blocked || detail.requiresRepublish" data-test="screen-version-preview" @click="previewVisible = !previewVisible">{{ $t(previewVisible ? 'screenVersions.hidePreview' : 'screenVersions.preview') }}</el-button>
        <div v-if="previewVisible && !detail.requiresRepublish" class="version-preview" :inert="blocked" data-test="screen-version-preview-content">
          <ScreenRuntime :key="detail.revision" :preview-document="detail.document" :preview-name="detail.name" published-preview initial-zoom="fit" />
        </div>
        <el-alert :title="$t('screenVersions.rollbackEffect', [detail.revision, detail.name])" type="info" :closable="false" show-icon class="rollback-effect" />
        <el-form label-position="top" @submit.prevent>
          <el-form-item :label="$t('screenVersions.note')" :error="noteError" required>
            <el-input v-model="note" type="textarea" :rows="3" :disabled="blocked || detail.requiresRepublish" :aria-label="$t('screenVersions.note')" data-test="screen-rollback-note" />
          </el-form-item>
          <p class="help" data-test="screen-rollback-note-hint">{{ $t('screenVersions.noteHint', [note.trim().length]) }}</p>
          <el-checkbox v-model="acknowledged" :disabled="blocked || dirty || detail.requiresRepublish || conflict" data-test="screen-rollback-ack">{{ $t('screenVersions.acknowledge') }}</el-checkbox>
        </el-form>
      </template>
    </section>
    <template #footer><el-button :disabled="blocked" data-test="screen-versions-close" @click="close()">{{ $t('screenVersions.close') }}</el-button><el-button type="warning" :disabled="!canSubmit" :loading="submitting" data-test="screen-rollback-submit" @click="submit">{{ $t('screenVersions.confirmRollback') }}</el-button></template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { isAxiosError } from 'axios'
import { screenApi, type ScreenPublicationResult, type ScreenVersion, type ScreenVersionSummary } from '../api/screens'
import { errorMessage } from '../api/http'
import type { ScreenDocument, ScreenIssue, ScreenNode } from '../screens/model'
import { tr } from '../i18n'
import { formatTime } from '../utils'
import ScreenRuntime from './ScreenRuntime.vue'

const props = defineProps<{ modelValue: boolean; screenId: number; revision: number; publishedRevision: number | null; dirty: boolean; busy: boolean; currentDocument?: ScreenDocument | null }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; busy: [value: boolean]; 'rolled-back': [result: ScreenPublicationResult] }>()
const versions = ref<ScreenVersionSummary[]>([]), detail = ref<ScreenVersion | null>(null), selectedRevision = ref<number | null>(null)
const listLoading = ref(false), detailLoading = ref(false), submitting = ref(false), hasMore = ref(false), failedAppend = ref(false)
const listError = ref(''), detailError = ref(''), submitError = ref(''), submitIssues = ref<ScreenIssue[]>([]), conflict = ref(false)
const note = ref(''), acknowledged = ref(false), previewVisible = ref(false)
const pageSize = 50
let generation = 0, listGeneration = 0, detailGeneration = 0
const blocked = computed(() => submitting.value || props.busy)
const noteError = computed(() => note.value && !note.value.trim() ? tr('screenVersions.noteRequired') : note.value.trim().length > 500 ? tr('screenVersions.noteTooLong') : '')
const canSubmit = computed(() => props.modelValue && !!detail.value && !detail.value.requiresRepublish && !blocked.value && !props.dirty && !conflict.value && !listLoading.value && !detailLoading.value && acknowledged.value && note.value.trim().length >= 1 && note.value.trim().length <= 500)
const diffGroups: { labelKey: string; keys: (keyof ScreenNode)[] }[] = [
  { labelKey: 'screenVersions.diffGeometry', keys: ['x', 'y', 'width', 'height', 'rotation'] },
  { labelKey: 'screenVersions.diffText', keys: ['text', 'texts', 'fontSize'] },
  { labelKey: 'screenVersions.diffStyle', keys: ['color', 'activeColor'] },
  { labelKey: 'screenVersions.diffData', keys: ['tagId', 'alarmTagId', 'unit', 'min', 'max', 'decimals', 'activeThreshold', 'states', 'tags', 'tagBinds', 'windowSec', 'lineId', 'animation', 'animationBind'] },
  { labelKey: 'screenVersions.diffAction', keys: ['action'] },
  { labelKey: 'screenVersions.diffStructure', keys: ['type', 'symbol', 'templateKey', 'bindings', 'bind', 'alarmBind', 'points', 'source', 'target', 'commands'] }
]
function nodeLabel(node: ScreenNode): string { return `${node.text || node.type} (#${node.id})` }
const diffRows = computed(() => {
  const current = props.currentDocument
  const version = detail.value?.document
  if (!current || !version) return []
  const currentNodes = new Map(current.nodes.map(node => [node.id, node]))
  const versionNodes = new Map(version.nodes.map(node => [node.id, node]))
  const rows: { label: string; fields: string[] }[] = []
  for (const [id, node] of currentNodes) {
    const previous = versionNodes.get(id)
    if (!previous) { rows.push({ label: nodeLabel(node), fields: [tr('screenVersions.diffAdded')] }); continue }
    const fields = diffGroups.filter(group => JSON.stringify(group.keys.map(key => previous[key])) !== JSON.stringify(group.keys.map(key => node[key]))).map(group => tr(group.labelKey))
    if (fields.length) rows.push({ label: nodeLabel(node), fields })
  }
  for (const [id, node] of versionNodes) if (!currentNodes.has(id)) rows.push({ label: nodeLabel(node), fields: [tr('screenVersions.diffRemoved')] })
  return rows
})

function invalidate(): void { generation++; listGeneration++; detailGeneration++; previewVisible.value = false; listLoading.value = false; detailLoading.value = false }
function close(done?: () => void): void {
  if (blocked.value) return
  invalidate()
  emit('update:modelValue', false)
  done?.()
}
watch([() => props.modelValue, () => props.screenId], ([visible]) => {
  invalidate()
  if (!visible) return
  versions.value = []; detail.value = null; selectedRevision.value = null; hasMore.value = false
  listError.value = ''; detailError.value = ''; submitError.value = ''; submitIssues.value = []; note.value = ''; acknowledged.value = false
  void loadVersions()
}, { immediate: true })
watch(() => props.dirty, () => { acknowledged.value = false })
watch(() => props.revision, () => { conflict.value = false; acknowledged.value = false })
onBeforeUnmount(invalidate)

async function loadVersions(append = false): Promise<void> {
  if (blocked.value || listLoading.value || !props.modelValue) return
  const session = generation, request = ++listGeneration
  const before = append ? versions.value.at(-1)?.revision : undefined
  failedAppend.value = append; listLoading.value = true; listError.value = ''
  if (!append) {
    detailGeneration++; selectedRevision.value = null; detail.value = null; detailError.value = ''; detailLoading.value = false
    previewVisible.value = false; acknowledged.value = false
  }
  try {
    const result = await screenApi.versions(props.screenId, before, pageSize)
    if (session !== generation || request !== listGeneration || !props.modelValue) return
    versions.value = append ? [...new Map([...versions.value, ...result].map(version => [version.revision, version])).values()] : result
    hasMore.value = result.length === pageSize
    if (!append && result[0]) void selectVersion(result[0].revision)
  } catch (reason) {
    if (session === generation && request === listGeneration && props.modelValue) listError.value = errorMessage(reason, tr('screenVersions.listFailed'))
  } finally { if (session === generation && request === listGeneration) listLoading.value = false }
}
async function selectVersion(revision: number): Promise<void> {
  if (blocked.value || !props.modelValue) return
  const session = generation, request = ++detailGeneration
  selectedRevision.value = revision; detail.value = null; detailError.value = ''; detailLoading.value = true
  previewVisible.value = false; note.value = ''; acknowledged.value = false
  if (!conflict.value) { submitError.value = ''; submitIssues.value = [] }
  try {
    const result = await screenApi.version(props.screenId, revision)
    if (session === generation && request === detailGeneration && props.modelValue) detail.value = result
  } catch (reason) {
    if (session === generation && request === detailGeneration && props.modelValue) detailError.value = errorMessage(reason, tr('screenVersions.detailFailed'))
  } finally { if (session === generation && request === detailGeneration) detailLoading.value = false }
}
async function submit(): Promise<void> {
  if (!canSubmit.value || !detail.value) return
  const session = generation, screenId = props.screenId, revision = props.revision, targetRevision = detail.value.revision, reasonNote = note.value.trim()
  submitting.value = true; emit('busy', true); submitError.value = ''; submitIssues.value = []
  try {
    const result = await screenApi.rollback(screenId, revision, targetRevision, reasonNote)
    if (session !== generation || !props.modelValue) return
    emit('rolled-back', result)
    emit('update:modelValue', false)
  } catch (reason) {
    if (session !== generation || !props.modelValue) return
    conflict.value = isAxiosError(reason) && reason.response?.status === 409
    submitError.value = errorMessage(reason, tr('screenVersions.rollbackFailed'))
    const issues = isAxiosError<{ issues?: ScreenIssue[] }>(reason) ? reason.response?.data?.issues : undefined
    submitIssues.value = Array.isArray(issues) ? issues.filter(issue => !!issue && typeof issue.message === 'string') : []
    acknowledged.value = false
  } finally { submitting.value = false; emit('busy', false) }
}
function kindLabel(kind: string): string {
  return ['publish', 'rollback', 'import', 'migration'].includes(kind) ? tr(`screenVersions.kind_${kind}`) : kind
}
</script>

<style scoped>
:global(.screen-versions-dialog) { width: min(1120px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.screen-versions-dialog .el-dialog__body) { min-height: 0; overflow-y: auto; }
.help { color: var(--scada-text-muted); font-size: 12px; line-height: 1.6; }
.history-heading, .version-heading { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; }
h3 { font-size: 15px; overflow-wrap: anywhere; }
.version-list { display: grid; gap: 8px; max-height: 240px; overflow: auto; margin: 12px 0; min-height: 48px; }
.version-row { display: grid; gap: 5px; width: 100%; padding: 10px; text-align: left; color: inherit; background: var(--scada-panel); border: 1px solid var(--scada-border); border-radius: 6px; cursor: pointer; }
.version-row.selected { border-color: var(--scada-primary); }.version-row:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -3px; }.version-row:disabled { cursor: not-allowed; opacity: .6; }
.version-row > span:not(:first-child) { font-size: 12px; }.version-note { overflow-wrap: anywhere; white-space: pre-wrap; }
.version-detail { min-height: 80px; margin-top: 16px; }.version-preview { margin-top: 12px; max-height: 600px; overflow: auto; border: 1px solid var(--scada-border); padding: 8px; }.rollback-effect { margin: 18px 0 12px; }
.issues { overflow-wrap: anywhere; }.el-checkbox { height: auto; white-space: normal; }:deep(.el-checkbox__label) { white-space: normal; line-height: 1.6; }
@media (max-width: 600px) { :global(.screen-versions-dialog .el-dialog__footer) { display: flex; gap: 8px; flex-wrap: wrap; } :global(.screen-versions-dialog .el-dialog__footer .el-button) { margin: 0; } }
</style>

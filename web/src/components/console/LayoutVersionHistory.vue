<template>
  <el-dialog v-model="visible" :title="$t(mode === 'publish' ? 'layoutLifecycle.publishTitle' : 'layoutLifecycle.historyTitle')" class="layout-lifecycle" :before-close="close" data-test="layout-lifecycle-dialog">
    <div v-loading="loading" class="lifecycle-body">
      <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon data-test="layout-lifecycle-error" />
      <el-button v-if="error || conflict" :disabled="loading || submitting" data-test="layout-lifecycle-reload" @click="reload">{{ $t('layoutLifecycle.reloadReview') }}</el-button>
      <template v-if="draft">
        <p class="saved-summary">{{ draft.name }} · {{ $t('layoutLifecycle.savedDraft', [draft.revision, draft.updatedBy, formatTime(draft.updatedTs)]) }}</p>
        <el-alert v-if="mode === 'publish'" :title="$t('layoutLifecycle.savedOnly')" type="info" :closable="false" show-icon />
        <template v-else>
          <p class="help">{{ $t('layoutLifecycle.historyLimit') }}</p>
          <div class="version-list" data-test="layout-version-list">
            <el-empty v-if="!versions.length" :description="$t('layoutLifecycle.noHistory')" :image-size="56" />
            <button v-for="version in versions" :key="version.revision" type="button" class="version-row" :class="{ selected: selectedRevision === version.revision }" :aria-pressed="selectedRevision === version.revision" :data-test="`layout-version-${version.revision}`" @click="viewVersion(version.revision)">
              <span class="version-heading"><strong>V{{ version.revision }} · {{ version.name }}</strong><el-tag v-if="version.revision === draft.publishedRevision" size="small" type="success">{{ $t('layoutLifecycle.currentPublished') }}</el-tag></span>
              <span>{{ version.publishedBy }} · {{ formatTime(version.publishedTs) }} · {{ versionKind(version) }}</span>
              <span class="version-note">{{ version.note }}</span>
            </button>
          </div>
        </template>
        <div v-if="candidate" v-loading="versionLoading" class="review-content">
          <section class="impact-review" data-test="layout-impact">
            <div class="comparison-heading"><h4>{{ $t('layoutSafety.impactTitle') }}</h4><el-button size="small" :loading="impactLoading" :disabled="submitting || loading" data-test="layout-impact-refresh" @click="refreshImpact">{{ $t('layoutSafety.refreshImpact') }}</el-button></div>
            <el-alert v-if="impactError" :title="impactError" type="error" :closable="false" show-icon data-test="layout-impact-error" />
            <template v-else>
              <p class="help">{{ draft.publishedRevision != null ? $t('layoutSafety.currentPublication', [draft.publishedName || draft.name, draft.publishedRevision]) : $t('layoutLifecycle.unpublished') }} · {{ $t('layoutSafety.impactCount', [impactStations.length]) }}</p>
              <p v-if="impactReadTs" class="help">{{ $t('layoutSafety.impactReadAt', [formatTime(impactReadTs)]) }}</p>
              <p v-if="!impactStations.length" class="help" data-test="layout-impact-empty">{{ $t('layoutSafety.noImpact') }}</p>
              <ul v-else class="impact-stations"><li v-for="station in impactStations" :key="station.id"><span><strong>{{ station.name }}</strong> <el-tag v-if="isLocalWorkstation(station)" size="small" type="success">{{ $t('layoutImprovements.thisStation') }}</el-tag><span v-if="station.description" class="help"> · {{ station.description }}</span></span><el-tag size="small" :type="station.allowControl ? 'success' : 'warning'">{{ $t(station.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag></li></ul>
            </template>
            <p class="help">{{ $t('layoutSafety.applyEffect') }}</p>
            <el-alert v-if="impactChanged" :title="$t('layoutSafety.impactChanged')" type="warning" :closable="false" show-icon data-test="layout-impact-changed" />
          </section>
          <h4>{{ mode === 'publish' ? $t('layoutLifecycle.savedDraftPreview') : $t('layoutLifecycle.versionPreview', [selectedRevision]) }}</h4>
          <p v-if="mode === 'history' && selectedVersion" class="help">{{ selectedVersion.publishedBy }} · {{ formatTime(selectedVersion.publishedTs) }} · {{ selectedVersion.note }}</p>
          <div class="static-monitors" data-test="layout-version-preview">
            <div v-for="(monitor, index) in previewMonitors" :key="index" class="static-monitor">
              <strong>{{ monitor.name || $t('layoutLifecycle.monitorLocation', [index + 1]) }} · {{ monitor.columns }} × {{ monitor.rows }}</strong>
              <div class="static-console" :style="{ aspectRatio: `${previewSize(monitor).width} / ${previewSize(monitor).height}` }">
              <div class="static-toolbar">{{ $t('layoutPreview.consoleToolbar') }}</div><div class="static-alarm">{{ $t('layoutPreview.fixedAlarm') }}</div>
              <div class="static-grid" :style="{ gridTemplateColumns: `repeat(${monitor.columns}, minmax(0,1fr))`, gridTemplateRows: `repeat(${monitor.rows}, minmax(0,1fr))` }">
                <div v-for="(region, regionIndex) in monitor.regions" :key="regionIndex" :class="`kind-${region.kind}`" :style="previewRegion(monitor, region)" :title="`${region.id} · ${region.kind} · ${region.title || ''}`"><span>{{ region.id || '?' }}</span><span>{{ region.title || region.kind }}</span></div>
              </div>
              </div>
            </div>
          </div>
          <p class="help">{{ $t('layoutLifecycle.previewStatic') }}</p>
          <el-button data-test="layout-version-runtime-preview" :disabled="!!issues.length" @click="runtimePreviewVisible = true">{{ $t('layoutPreview.runtimePreview') }}</el-button>
          <h4>{{ $t('layoutLifecycle.references') }}</h4>
          <el-alert v-if="screenError" :title="screenError" type="warning" :closable="false" show-icon />
          <p v-if="!references.length" class="help">{{ $t('layoutLifecycle.noReferences') }}</p>
          <ul v-else class="references" data-test="layout-publish-references"><li v-for="reference in references" :key="reference.location"><span>{{ reference.location }} → {{ reference.screen }}</span><el-tag size="small" :type="reference.valid && !screenError ? 'success' : 'danger'">{{ $t(reference.valid && !screenError ? 'layoutLifecycle.referenceValid' : 'layoutLifecycle.referenceInvalid') }}</el-tag></li></ul>
          <div v-if="issues.length" class="validation" role="status" data-test="layout-publish-issues"><strong>{{ $t('layoutLifecycle.publishBlocked', [issues.length]) }}</strong><ul><li v-for="(issue, index) in issues" :key="index">{{ issue }}</li></ul></div>
          <div class="comparison-heading"><h4>{{ $t('layoutLifecycle.comparison') }}</h4><el-select v-if="mode === 'history'" v-model="compareTarget" size="small" data-test="layout-version-compare"><el-option value="published" :label="$t('layoutLifecycle.comparePublished')" /><el-option value="draft" :label="$t('layoutLifecycle.compareDraft')" /></el-select></div>
          <p class="help" data-test="layout-comparison-summary">{{ mode === 'history' && compareTarget === 'draft' ? $t('layoutLifecycle.diffDraftToVersion', [selectedRevision]) : $t('layoutLifecycle.diffPublishedToCandidate', [draft.publishedRevision ?? '—', mode === 'publish' ? $t('layoutLifecycle.draft') : `V${selectedRevision}`]) }}</p>
          <p v-if="!differences.length" class="help" data-test="layout-no-differences">{{ $t('layoutLifecycle.noDifferences') }}</p>
          <ul v-if="comparison.summary.length" class="diff-summary" data-test="layout-diff-summary"><li v-for="item in comparison.summary" :key="item">{{ item }}</li></ul>
          <div v-if="differences.length" class="diff-scroll"><table class="diff-table" data-test="layout-version-diff"><thead><tr><th>{{ $t('layoutLifecycle.diffField') }}</th><th>{{ $t('layoutLifecycle.diffBefore') }}</th><th>{{ $t('layoutLifecycle.diffAfter') }}</th></tr></thead><tbody><tr v-for="difference in differences" :key="difference.path"><td>{{ difference.path }}</td><td>{{ difference.before }}</td><td>{{ difference.after }}</td></tr></tbody></table></div>
          <el-button v-if="mode === 'history' && !rollbackConfirm" :disabled="versionLoading || submitting || impactLoading || !!impactError || !!screenError || !!issues.length || selectedRevision === draft.publishedRevision" type="warning" data-test="layout-rollback-start" @click="rollbackConfirm = true">{{ $t('layoutLifecycle.rollbackTo', [selectedRevision]) }}</el-button>
          <div v-if="mode === 'publish' || rollbackConfirm" class="publish-form">
            <el-alert v-if="rollbackConfirm" :title="$t('layoutLifecycle.rollbackEffect', [selectedRevision, candidate.name])" type="warning" :closable="false" show-icon data-test="layout-rollback-confirmation" />
            <el-form label-position="top" @submit.prevent>
              <el-form-item :label="$t(mode === 'publish' ? 'layoutLifecycle.publishNote' : 'layoutLifecycle.rollbackNote')" data-test="layout-lifecycle-note"><el-input v-model="note" type="textarea" :rows="3" maxlength="500" show-word-limit :disabled="submitting" /></el-form-item>
            </el-form>
            <el-checkbox v-if="rollbackConfirm" v-model="rollbackAcknowledged" :disabled="submitting" data-test="layout-rollback-ack">{{ $t('layoutLifecycle.rollbackAcknowledge') }}</el-checkbox>
          </div>
        </div>
      </template>
    </div>
    <template #footer><el-button :disabled="submitting" @click="close()">{{ $t('layoutLifecycle.close') }}</el-button><el-button v-if="mode === 'publish' || rollbackConfirm" type="primary" :loading="submitting" :disabled="!canSubmit" data-test="layout-lifecycle-submit" @click="submit">{{ $t(mode === 'publish' ? 'layoutLifecycle.confirmPublish' : 'layoutLifecycle.confirmRollback') }}</el-button></template>
  </el-dialog>
  <el-dialog v-model="runtimePreviewVisible" :title="$t('layoutPreview.runtimePreview')" class="layout-runtime-dialog" append-to-body destroy-on-close>
    <LayoutRuntimePreview v-if="runtimePreviewVisible && candidate" :document="candidate.document" :name="candidate.name" />
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { hmiLayoutApi, isLocalWorkstation, type HmiLayout, type HmiLayoutVersion, type HmiLayoutVersionSummary, type HmiMonitor, type HmiRegion, type HmiWorkstation } from '../../api/hmiLayouts'
import { screenApi } from '../../api/screens'
import { errorMessage } from '../../api/http'
import { tr } from '../../i18n'
import { formatTime } from '../../utils'
import { layoutComparison, layoutPublishIssues, layoutScreenReferences } from './layoutLifecycle'
import { contentMonitor, previewSize } from './layoutGeometry'
import LayoutRuntimePreview from './LayoutRuntimePreview.vue'

const emit = defineEmits<{ changed: [] }>()
const visible = ref(false), mode = ref<'publish' | 'history'>('publish'), layoutId = ref(0)
const loading = ref(false), submitting = ref(false), versionLoading = ref(false), error = ref(''), screenError = ref(''), conflict = ref(false)
const draft = ref<HmiLayout | null>(null), published = ref<HmiLayoutVersion | null>(null), versions = ref<HmiLayoutVersionSummary[]>([])
const selectedRevision = ref<number | null>(null), selectedVersion = ref<HmiLayoutVersion | null>(null), compareTarget = ref('published')
const note = ref(''), rollbackConfirm = ref(false), rollbackAcknowledged = ref(false), screens = ref(new Map<number, string>())
const workstations = ref<HmiWorkstation[]>([]), impactError = ref(''), impactLoading = ref(false), impactChanged = ref(false), impactReadTs = ref(0)
const impactStations = computed(() => workstations.value.filter(station => station.layoutId === layoutId.value))
function impactSignature(stations: HmiWorkstation[]): string { return JSON.stringify(stations.filter(station => station.layoutId === layoutId.value).sort((a, b) => a.id - b.id).map(station => [station.id, station.name, station.description, station.allowControl, station.updatedTs])) }
async function refreshImpact(): Promise<void> {
  if (impactLoading.value || submitting.value) return
  const currentGeneration = generation
  impactLoading.value = true
  try {
    const result = await hmiLayoutApi.workstations()
    if (currentGeneration !== generation || !visible.value) return
    impactChanged.value = impactReadTs.value > 0 && impactSignature(result) !== impactSignature(workstations.value)
    workstations.value = result; impactError.value = ''; impactReadTs.value = Date.now()
  } catch (reason) { if (currentGeneration === generation) impactError.value = errorMessage(reason, tr('layoutSafety.impactFailed')) }
  finally { if (currentGeneration === generation) impactLoading.value = false }
}
let generation = 0, versionGeneration = 0
const candidate = computed(() => mode.value === 'publish' ? draft.value : selectedVersion.value)
const runtimePreviewVisible = ref(false)
const previewMonitors = computed(() => candidate.value?.document.monitors.map(contentMonitor) ?? [])
const issues = computed(() => candidate.value ? layoutPublishIssues(candidate.value, screens.value) : [])
const references = computed(() => candidate.value ? layoutScreenReferences(candidate.value, screens.value) : [])
const comparison = computed(() => candidate.value ? layoutComparison(mode.value === 'history' && compareTarget.value === 'draft' ? draft.value : published.value, candidate.value, screens.value) : { differences: [], summary: [] })
const differences = computed(() => comparison.value.differences)
const canSubmit = computed(() => !!draft.value && !!candidate.value && !loading.value && !versionLoading.value && !submitting.value && !impactLoading.value && !impactError.value && !screenError.value && !conflict.value && !issues.value.length && !!note.value.trim() && note.value.trim().length <= 500 && (mode.value === 'publish' || rollbackAcknowledged.value))

function close(done?: () => void): void {
  if (submitting.value) return
  generation++; versionGeneration++
  impactLoading.value = false
  runtimePreviewVisible.value = false
  if (done) done(); else visible.value = false
}
async function open(id: number, nextMode: 'publish' | 'history'): Promise<void> {
  if (submitting.value) return
  layoutId.value = id; mode.value = nextMode; note.value = ''; draft.value = null; published.value = null; versions.value = []; selectedVersion.value = null; selectedRevision.value = null; compareTarget.value = 'published'; rollbackConfirm.value = false; rollbackAcknowledged.value = false; versionLoading.value = false; visible.value = true
  await reload()
}
async function reload(): Promise<void> {
  if (submitting.value) return
  const currentGeneration = ++generation
  versionGeneration++
  loading.value = true; error.value = ''; conflict.value = false; screenError.value = ''; impactError.value = ''; impactChanged.value = false; impactLoading.value = false; rollbackConfirm.value = false; rollbackAcknowledged.value = false
  try {
    const [saved, history, screenResult, stationResult] = await Promise.all([hmiLayoutApi.get(layoutId.value, true), hmiLayoutApi.versions(layoutId.value), screenApi.list().then(result => ({ ok: true as const, result })).catch(reason => ({ ok: false as const, reason })), hmiLayoutApi.workstations().then(result => ({ ok: true as const, result })).catch(reason => ({ ok: false as const, reason }))])
    const current = saved.publishedRevision != null ? await hmiLayoutApi.version(saved.id, saved.publishedRevision) : null
    if (currentGeneration !== generation || !visible.value) return
    draft.value = saved; versions.value = history; published.value = current
    if (screenResult.ok) screens.value = new Map(screenResult.result.filter(screen => screen.publishedRevision != null).map(screen => [screen.id, screen.name]))
    else { screens.value = new Map(); screenError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed')) }
    workstations.value = stationResult.ok ? stationResult.result : []; impactReadTs.value = stationResult.ok ? Date.now() : 0
    if (!stationResult.ok) impactError.value = errorMessage(stationResult.reason, tr('layoutSafety.impactFailed'))
    if (mode.value === 'history') {
      const target = history.find(version => version.revision === selectedRevision.value) ?? history[0]
      selectedVersion.value = null
      if (target) await viewVersion(target.revision)
    }
  } catch (reason) { if (currentGeneration === generation) { error.value = errorMessage(reason, tr('layoutLifecycle.loadFailed')); draft.value = null } }
  finally { if (currentGeneration === generation) loading.value = false }
}
async function viewVersion(revision: number): Promise<void> {
  if (submitting.value) return
  const currentGeneration = ++versionGeneration
  selectedRevision.value = revision; selectedVersion.value = null; rollbackConfirm.value = false; rollbackAcknowledged.value = false; versionLoading.value = true
  if (!conflict.value) error.value = ''
  try {
    const version = await hmiLayoutApi.version(layoutId.value, revision)
    if (currentGeneration === versionGeneration && visible.value) selectedVersion.value = version
  } catch (reason) { if (currentGeneration === versionGeneration) error.value = errorMessage(reason, tr('layoutLifecycle.loadFailed')) }
  finally { if (currentGeneration === versionGeneration) versionLoading.value = false }
}
async function submit(): Promise<void> {
  if (!canSubmit.value || !draft.value) return
  submitting.value = true; error.value = ''
  try {
    let currentStations: HmiWorkstation[]
    try { currentStations = await hmiLayoutApi.workstations() }
    catch (reason) { impactError.value = errorMessage(reason, tr('layoutSafety.impactFailed')); return }
    const changed = impactSignature(currentStations) !== impactSignature(workstations.value)
    workstations.value = currentStations; impactReadTs.value = Date.now(); impactError.value = ''; impactChanged.value = changed
    if (changed) return
    if (mode.value === 'publish') await hmiLayoutApi.publish(draft.value.id, draft.value.revision, note.value.trim())
    else if (selectedRevision.value != null) await hmiLayoutApi.rollback(draft.value.id, draft.value.revision, selectedRevision.value, note.value.trim())
    ElMessage.success(tr(mode.value === 'publish' ? 'layoutLifecycle.publishedSuccess' : 'layoutLifecycle.rollbackSuccess'))
    emit('changed'); visible.value = false
  } catch (reason) {
    conflict.value = (reason as { response?: { status?: number } }).response?.status === 409
    error.value = conflict.value ? tr('layoutLifecycle.conflict') : errorMessage(reason, tr('layoutLifecycle.actionFailed'))
  } finally { submitting.value = false }
}
function versionKind(version: HmiLayoutVersionSummary): string {
  if (version.restoredFromRevision != null) return tr('layoutLifecycle.restoredFrom', [version.restoredFromRevision])
  return tr(['publish', 'rollback', 'migration', 'import'].includes(version.kind) ? `layoutLifecycle.kind_${version.kind}` : 'layoutLifecycle.kind_publish')
}
function previewRegion(monitor: HmiMonitor, region: HmiRegion): Record<string, string> {
  const row = Math.min(monitor.rows, Math.max(1, region.row || 1)), col = Math.min(monitor.columns, Math.max(1, region.col || 1))
  return { gridRow: `${row} / span ${Math.max(1, Math.min(monitor.rows - row + 1, region.rowSpan || 1))}`, gridColumn: `${col} / span ${Math.max(1, Math.min(monitor.columns - col + 1, region.colSpan || 1))}` }
}
defineExpose({ openPublish: (id: number) => open(id, 'publish'), openHistory: (id: number) => open(id, 'history') })
</script>

<style scoped>
:global(.layout-lifecycle) { width: min(1120px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.layout-lifecycle .el-dialog__body) { min-height: 0; overflow-y: auto; }
.lifecycle-body { min-height: 80px; min-width: 0; }
.saved-summary, .version-note { overflow-wrap: anywhere; }
.help { color: var(--scada-text-muted); font-size: 12px; line-height: 1.6; }
h4 { margin: 18px 0 10px; }
.version-list { display: grid; gap: 8px; max-height: 250px; overflow: auto; margin-bottom: 14px; }
.version-row { display: grid; gap: 5px; width: 100%; padding: 10px; border: 1px solid var(--scada-border); border-radius: 6px; text-align: left; color: inherit; background: var(--scada-panel); cursor: pointer; }
.version-row.selected { border-color: var(--scada-primary); }
.version-row:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -3px; }
.version-row > span:not(:first-child) { font-size: 12px; }
.version-heading { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; overflow-wrap: anywhere; }
.static-monitors { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 270px), 1fr)); gap: 12px; }
.static-monitor { min-width: 0; border: 1px solid var(--scada-border); border-radius: 6px; padding: 8px; }
.static-monitor > strong { display: block; font-size: 12px; margin-bottom: 6px; overflow-wrap: anywhere; }
.static-console { display: flex; flex-direction: column; overflow: hidden; background: #0a111b; }
.static-toolbar, .static-alarm { font-size: 10px; color: #e1ebf4; padding: 3px; background: #101b2d; }
.static-alarm { background: #1a2533; border-block: 1px solid #3b5878; }
.static-grid { display: grid; flex: 1; min-height: 0; gap: 2px; background: #0a111b; overflow: hidden; }
.static-grid > div { min-width: 0; min-height: 0; font-size: 10px; background: #22344d; color: #e1ebf4; padding: 3px; overflow: hidden; }
.static-grid span { display: block; }
.static-grid .kind-alarmBanner { background: #7a1010; }.static-grid .kind-alarmList { background: #5c3b10; }.static-grid .kind-navigation { background: #2c3e50; }
.references { padding-left: 20px; font-size: 12px; }
.references li { margin: 5px 0; overflow-wrap: anywhere; }.references .el-tag { margin-left: 8px; }
.validation { padding: 12px; border: 1px solid var(--scada-warning); border-radius: 6px; font-size: 12px; }.validation ul { margin-bottom: 0; padding-left: 20px; }
.comparison-heading { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }.comparison-heading .el-select { width: 220px; }
.diff-scroll { overflow-x: auto; max-height: 320px; margin: 8px 0 18px; }
.diff-summary { display: flex; flex-wrap: wrap; gap: 6px 22px; padding: 12px 12px 12px 28px; font-size: 12px; border: 1px solid var(--scada-border); border-radius: 6px; }
.impact-review { padding: 12px; margin-top: 12px; border: 1px solid var(--scada-border); border-radius: 6px; }.impact-review h4 { margin: 0; }.impact-stations { list-style: none; padding: 0; max-height: 200px; overflow: auto; }.impact-stations li { display: flex; align-items: center; flex-wrap: wrap; justify-content: space-between; gap: 8px; padding: 6px 0; overflow-wrap: anywhere; }
.diff-table { border-collapse: collapse; table-layout: fixed; width: 100%; font-size: 12px; }
.diff-table th, .diff-table td { text-align: left; padding: 8px; border: 1px solid var(--scada-border); overflow-wrap: anywhere; vertical-align: top; }.diff-table th:first-child { width: 45%; }
.publish-form { margin-top: 14px; }.publish-form .el-form { margin-top: 12px; }.publish-form .el-checkbox { height: auto; white-space: normal; }.publish-form :deep(.el-checkbox__label) { white-space: normal; line-height: 1.6; }
@media (max-width: 600px) { .diff-table { min-width: 520px; } .version-heading { align-items: start; flex-direction: column; } :global(.layout-lifecycle .el-dialog__footer) { display: flex; flex-wrap: wrap; gap: 8px; } :global(.layout-lifecycle .el-dialog__footer .el-button) { margin: 0; } }
</style>

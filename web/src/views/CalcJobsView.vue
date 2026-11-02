<template>
  <div class="page"><div class="panel">
    <div class="toolbar">
      <el-input v-model="search" :placeholder="$t('calcJobsView.k5e33fd')" clearable style="width: 220px" data-test="calc-job-search" /><span class="spacer" />
      <el-switch v-model="automatic" :active-text="$t('calcJobsView.kd8fee6')" />
      <el-button :loading="loading" @click="refresh()">{{ $t('calcJobsView.k38108e') }}</el-button>
      <el-button data-test="calc-job-libraries" @click="openLibraries">{{ $t('designReview2.scriptLibraries') }}</el-button>
      <el-button v-if="configurableAreas.length" type="primary" data-test="calc-job-new" @click="open(null)">{{ $t('calcJobsView.k26083b') }}</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" show-icon data-test="calc-job-read-error" />
    <el-alert v-if="areasError" :title="areasError" type="error" :closable="false" show-icon data-test="calc-job-areas-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="calc-job-action-error" />
    <p class="page-desc">{{ $t('calcJobsView.k42436e') }}</p>
    <el-table :data="filtered" v-loading="loading" row-key="job.id" :empty-text="readError && !filtered.length ? readError : $t('calcJobsView.kad5cdc')" data-test="calc-job-table">
      <el-table-column :label="$t('calcJobsView.k3172b3')" min-width="170" show-overflow-tooltip><template #default="{ row }"><strong>#{{ row.job.id }} · {{ row.job.name }}</strong>
        <div class="tiny muted">{{ $t('calcJobsView.k22d559', [kindLabels[row.job.kind as CalcJobKind], areaName(row.job.areaId), row.job.revision]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.k9ac85a')" min-width="160" show-overflow-tooltip><template #default="{ row }">
        <span v-if="!isScript(row.job.kind)">#{{ row.job.inputTagId }} {{ tagName(row.job.inputTagId) }}</span><span v-else>{{ $t('calcJobsView.k8280de', [describeTags(row.job.readTags) || $t('calcJobsView.k720777'), describeTags(row.job.writeTags) || $t('calcJobsView.k720777')]) }}</span>
        <div class="tiny muted">{{ $t('calcJobsView.k1ccc51', [row.job.enabled ? $t('calcJobsView.kd4e9ca') : $t('calcJobsView.kd989e5'), row.job.intervalMs, resetLabels[row.job.resetPeriod as CalcResetPeriod]]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.kb5259e')" min-width="260" show-overflow-tooltip><template #default="{ row }">
        <div v-for="output in row.outputs" :key="output.tagId" class="tiny"><code>{{ output.name }}</code> = <strong>{{ value(row, output.name) }}</strong> <span class="muted">#{{ output.tagId }}</span></div>
        <el-tag v-if="row.status?.values?.maintenanceDue === 1" type="danger" size="small">{{ $t('calcJobsView.k1f37f2') }}</el-tag></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.kea815f')" min-width="200" show-overflow-tooltip><template #default="{ row }"><div class="tiny">{{ row.status?.periodKey || '--' }}</div>
        <el-tag v-if="row.status?.isRunning" size="small">{{ $t('calcJobsView.scriptRunning') }}</el-tag>
        <div class="tiny" :class="row.status?.lastError ? 'danger' : 'muted'">{{ row.status?.lastError || row.status?.lastEvent || '' }}</div>
        <div v-if="row.status?.lastScriptMs != null" class="tiny muted">{{ row.job.kind === 'CSharp' ? `${row.status.lastScriptMs.toFixed(2)} ms` : $t('calcJobsView.k31d47d', [row.status.lastScriptSteps, row.status.lastScriptMs.toFixed(2)]) }}</div></template></el-table-column>
      <el-table-column :label="$t('calcJobsView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button v-if="canEditArea(row.job.areaId)" link type="primary" @click="open(row.job)">{{ $t('calcJobsView.ka7f814') }}</el-button>
        <el-button link @click="showSnapshots(row.job)">{{ $t('calcJobsView.k83caf1') }}</el-button>
        <el-button v-if="canControlArea(row.job.areaId) && !isScript(row.job.kind)" link type="warning" @click="reset(row.job)">{{ $t('calcJobsView.k52a5c3') }}</el-button>
        <el-button v-if="canControlArea(row.job.areaId) && Object.keys(row.status?.pendingWrites ?? {}).length" link type="warning" :disabled="row.status?.isRunning" data-test="calc-job-acknowledge" @click="acknowledge(row.job)">{{ $t('calcJobsView.acknowledgeWrites') }}</el-button>
        <el-button v-if="canControlArea(row.job.areaId) && row.job.kind === 'RunHours'" link type="success" @click="maintenance(row.job)">{{ $t('calcJobsView.k33a9d1') }}</el-button>
        <el-button v-if="canEditArea(row.job.areaId)" link type="danger" @click="remove(row.job)">{{ $t('calcJobsView.k3755f5') }}</el-button></template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="editor" class="calc-editor" :title="editing === null ? $t('calcJobsView.k26083b') : $t('calcJobsView.keba89c')" width="min(1100px, calc(100vw - 24px))" :close-on-click-modal="false" :before-close="closeEditor" destroy-on-close @keydown.ctrl.s.prevent="onEditorSaveKey" @keydown.meta.s.prevent="onEditorSaveKey" @keydown.ctrl.enter.prevent="onPreviewKey">
    <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" data-test="calc-job-error" />
    <el-form label-width="130px" :disabled="saving" @submit.prevent>
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item :label="$t('calcJobsView.k1be7ae')" required><el-input v-model="form.name" maxlength="80" data-test="calc-job-name" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('calcJobsView.ke4e46c')"><el-select v-model="form.kind" :disabled="editing !== null" data-test="calc-job-kind">
          <el-option v-for="[key, label] in availableKinds" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('calcJobsView.k17fc93')" required><el-select v-model="form.areaId" data-test="calc-job-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('calcJobsView.kd4e9ca')"><el-switch v-model="form.enabled" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('calcJobsView.kb7b729')" label-width="80px"><el-input-number v-model="form.intervalMs" :min="200" :max="3600000" :step="500" controls-position="right" /></el-form-item></el-col>
      </el-row>
      <template v-if="!isScript(form.kind)">
        <el-form-item :label="inputLabel" required><el-select v-model="form.inputTagId" filterable style="width: 100%" data-test="calc-job-input">
          <el-option v-for="t in tags" :key="t.id" :value="t.id" :label="`#${t.id} · ${t.name}${t.unit ? ` (${t.unit})` : ''}`" /></el-select></el-form-item>
        <el-row :gutter="12">
          <el-col v-if="form.kind === 'Accumulator'" :span="12"><el-form-item :label="$t('calcJobsView.k4c8020')"><el-radio-group v-model="form.mode"><el-radio-button value="Pulse">{{ $t('calcJobsView.k43dbfd') }}</el-radio-button><el-radio-button value="Flow">{{ $t('calcJobsView.k43fc5e') }}</el-radio-button></el-radio-group></el-form-item></el-col>
          <el-col v-if="form.kind !== 'RunHours'" :span="12"><el-form-item :label="form.kind === 'Accumulator' && form.mode === 'Pulse' ? $t('calcJobsView.k1b30a5') : $t('calcJobsView.k70993e')"><el-input-number v-model="form.factor" :min="0" :step="0.1" :precision="8" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'Accumulator' && form.mode === 'Pulse'" :span="12"><el-form-item :label="$t('calcJobsView.k855788')"><el-input-number v-model="form.counterMax" :min="1" :placeholder="$t('calcJobsView.k25c5d5')" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'Accumulator' && form.mode === 'Pulse'" :span="12"><el-form-item :label="$t('calcJobsView.kb486bb')"><el-input-number v-model="form.maxDelta" :min="0" :placeholder="$t('calcJobsView.kca6ffc')" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'RunHours'" :span="12"><el-form-item :label="$t('calcJobsView.kff9e08')"><el-input-number v-model="form.runThreshold" :step="0.5" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'RunHours'" :span="12"><el-form-item :label="$t('calcJobsView.k58aab1')"><el-input-number v-model="form.maintenanceHours" :min="0" :placeholder="$t('calcJobsView.k2712e9')" controls-position="right" data-test="calc-job-maintenance" /></el-form-item></el-col>
          <el-col v-if="!(form.kind === 'Accumulator' && form.mode === 'Pulse')" :span="12"><el-form-item :label="$t('calcJobsView.kaf3bcd')"><el-select v-model="form.badQuality">
            <el-option value="Hold" :label="$t('calcJobsView.k2057e7')" /><el-option value="Skip" :label="$t('calcJobsView.k8ad6f0')" /><el-option value="Zero" :label="$t('calcJobsView.k9c7de4')" /></el-select></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.kf0d705')"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k1bb3f8')"><el-select v-model="form.resetPeriod" data-test="calc-job-reset"><el-option v-for="(label, key) in resetLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
          <el-col v-if="form.resetPeriod === 'Shift'" :span="12"><el-form-item :label="$t('calcJobsView.kb41bd4')"><el-input v-model="form.shiftStarts" placeholder="08:00,20:00" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('calcJobsView.k8ec8c8')"><el-input v-model="form.unit" maxlength="32" :placeholder="$t('calcJobsView.kbc0598')" /></el-form-item></el-col>
        </el-row>
        <el-form-item v-if="form.kind === 'Energy'" :label="$t('calcJobsView.kdef2e5')">
          <div style="width: 100%">
            <div v-for="(p, i) in tou" :key="i" class="tou-row">
              <el-select v-model="p.band" style="width: 110px"><el-option v-for="(label, key) in bandLabels" :key="key" :value="key" :label="label" /></el-select>
              <el-input v-model="p.start" style="width: 90px" placeholder="08:00" /> {{ $t('calcJobsView.k43401e') }} <el-input v-model="p.end" style="width: 90px" placeholder="11:00" />
              <el-button link type="danger" @click="tou.splice(i, 1)">{{ $t('calcJobsView.k2f752c') }}</el-button>
            </div>
            <el-button size="small" data-test="calc-job-add-tou" @click="tou.push({ band: 'peak', start: '08:00', end: '11:00' })">{{ $t('calcJobsView.k0c4fd5') }}</el-button>
            <div class="tiny muted">{{ $t('calcJobsView.k91cf8b') }}</div>
          </div>
        </el-form-item>
        <p class="tiny muted">{{ help[form.kind] }}</p>
      </template>
      <template v-else>
        <el-alert v-if="!form.publishedRevision" :title="$t('designReview2.scriptUnpublished')" type="info" :closable="false" show-icon />
        <el-alert v-else-if="publishedDrift" type="warning" :closable="false" show-icon>
          <template #title>{{ $t('designReview2.scriptDrift') }} <el-button link type="primary" data-test="calc-job-diff" @click="diffDialog = true">{{ $t('designReview2.scriptViewDiff') }}</el-button></template>
        </el-alert>
        <p v-else class="tiny muted">{{ $t('designReview2.scriptDraftHint') }}</p>
        <el-alert v-if="libraryBundleDrift" :title="$t('designReview2.scriptLibraryDrift')" type="warning" :closable="false" show-icon data-test="calc-job-library-drift" />
        <el-alert v-if="form.kind === 'CSharp' && capabilities && !capabilities.licensed" :title="$t('designReview2.scriptLicenseMissing')" type="warning" :closable="false" show-icon data-test="csharp-unlicensed" />
        <el-alert v-else-if="form.kind === 'CSharp' && !capabilities?.enabled" :title="capabilitiesError || $t('calcJobsView.csharpDisabled')" type="warning" :closable="false" show-icon data-test="csharp-disabled" />
        <template v-if="form.kind === 'CSharp'">
          <ScriptCodeEditor ref="scriptEditor" v-model="form.script" :diagnostics="spans" :read-tags="readCompletion" :write-tags="writeCompletion" :functions="completionFunctions" :known="knownTag" :accept-tag="acceptTag" :max-length="16384" :disabled="saving" @save="save" @preview="checkScript(true)" />
          <div class="script-check">
            <div class="script-check-toolbar">
              <el-dropdown trigger="click" @command="insertScriptSnippet">
                <el-button :disabled="saving || csharpBlocked">{{ $t('designReview2.scriptSnippets') }}</el-button>
                <template #dropdown><el-dropdown-menu><el-dropdown-item v-for="item in scriptSnippets()" :key="item.id" :command="item.insert">{{ item.label }}</el-dropdown-item></el-dropdown-menu></template>
              </el-dropdown>
              <el-button :icon="Check" :loading="checking === 'compile'" :disabled="csharpBlocked || checking !== null" data-test="csharp-compile" @click="checkScript(false)">{{ $t('calcJobsView.compileScript') }}</el-button>
              <el-button :icon="VideoPlay" :loading="checking === 'preview'" :disabled="csharpBlocked || checking !== null" data-test="csharp-preview" @click="checkScript(true)">{{ $t('calcJobsView.previewScript') }}</el-button>
              <el-checkbox v-model="usePreviewArgument" data-test="csharp-preview-argument">{{ $t('designReview2.scriptPreviewArgument') }}</el-checkbox>
              <el-input-number v-if="usePreviewArgument" v-model="previewArgument" controls-position="right" data-test="csharp-preview-argument-value" />
              <span v-if="autoChecking" class="tiny muted" data-test="csharp-auto-compile">{{ $t('designReview2.scriptChecking') }}</span>
              <span v-if="capabilities" class="tiny muted">{{ $t('calcJobsView.csharpLimits', [capabilities.timeoutMs, capabilities.maxInputs, capabilities.maxWrites]) }}</span>
            </div>
            <p class="tiny muted">{{ $t('designReview2.scriptSampleHint') }} {{ $t('designReview2.scriptAutoCompile') }}</p>
            <div class="script-work">
              <div>
                <div v-if="diagnostics.length" class="script-diagnostics danger" data-test="csharp-diagnostics"><button v-for="(message, index) in diagnostics" :key="index" type="button" class="script-jump" @click="revealDiagnostic(message)">{{ message }}</button></div>
                <div v-if="checkResult" data-test="csharp-result">
                  <el-tag :type="checkResult.success ? 'success' : 'danger'">{{ $t(checkResult.success ? 'calcJobsView.checkPassed' : 'calcJobsView.checkFailed') }}</el-tag>
                  <span class="tiny muted"> {{ checkResult.elapsedMs.toFixed(2) }} ms</span>
                  <p v-if="!previewResult" class="tiny">{{ $t('calcJobsView.outputNames') }}: {{ checkResult.outputNames.join(', ') || '--' }}</p>
                  <template v-else>
                    <h4>{{ $t('calcJobsView.previewOutputs') }}</h4>
                    <div v-for="(output, name) in checkResult.outputs" :key="name" class="tiny"><code>{{ name }}</code> = {{ formatValue(output) }}</div>
                    <h4>{{ $t('calcJobsView.writeIntents') }}</h4>
                    <div v-for="(output, tagId) in checkResult.writes" :key="tagId" class="tiny"><code>{{ tokenFor(Number(tagId)) }}</code> = {{ formatValue(output) }}</div>
                    <h4 v-if="checkResult.logs.length">{{ $t('calcJobsView.scriptLogs') }}</h4>
                    <pre v-if="checkResult.logs.length" class="script-diagnostics">{{ checkResult.logs.join('\n') }}</pre>
                    <h4 v-if="checkResult.trace?.length">{{ $t('designReview2.scriptTrace') }}</h4>
                    <pre v-if="checkResult.trace?.length" class="script-diagnostics" data-test="csharp-trace">{{ checkResult.trace.join('\n') }}</pre>
                  </template>
                </div>
              </div>
              <div v-if="previewInputs.length" data-test="csharp-preview-inputs">
                <div class="script-check-toolbar"><h4>{{ $t('designReview2.scriptPreviewInputs') }}</h4><el-button link data-test="csharp-preview-live" @click="previewLive">{{ $t('designReview2.scriptPreviewLive') }}</el-button></div>
                <div v-for="row in previewInputs" :key="row.tagId" class="preview-input">
                  <code>{{ row.name }}</code>
                  <el-input-number v-model="row.value" :step="0.1" controls-position="right" />
                  <el-select v-model="row.quality" style="width: 140px"><el-option v-for="quality in previewQualities" :key="quality" :value="quality" :label="quality" /></el-select>
                  <span class="tiny muted">{{ formatTime(row.timestamp) }}</span>
                </div>
              </div>
            </div>
          </div>
          <el-collapse v-model="scriptSetup">
            <el-collapse-item :title="$t('designReview2.scriptSetup')" name="setup">
              <el-row :gutter="12">
                <el-col :span="12"><el-form-item :label="$t('designReview2.scriptTrigger')"><el-select v-model="form.trigger" data-test="calc-job-trigger"><el-option v-for="item in triggerOptions" :key="item" :value="item" :label="triggerLabel(item)" /></el-select></el-form-item></el-col>
                <template v-if="form.trigger === 'OnChange'">
                  <el-col :span="6"><el-form-item :label="$t('designReview2.scriptDeadband')" label-width="80px"><el-input-number v-model="form.changeDeadband" :min="0" :step="0.1" controls-position="right" /></el-form-item></el-col>
                  <el-col :span="6"><el-form-item :label="$t('designReview2.scriptCooldown')" label-width="80px"><el-input-number v-model="form.changeCooldownMs" :min="0" :max="3600000" :step="500" controls-position="right" /></el-form-item></el-col>
                </template>
                <template v-if="form.trigger === 'OnAlarm'">
                  <el-col :span="12"><el-form-item :label="$t('designReview2.scriptAlarmLevel')"><el-select v-model="form.alarmMinLevel"><el-option value="Info" :label="$t('designReview2.scriptAlarmInfo')" /><el-option value="Warning" :label="$t('designReview2.scriptAlarmWarning')" /><el-option value="Critical" :label="$t('designReview2.scriptAlarmCritical')" /></el-select></el-form-item></el-col>
                  <el-col :span="24"><el-form-item :label="$t('designReview2.scriptAlarmEdge')"><el-checkbox v-model="form.alarmOnRaise">{{ $t('designReview2.scriptAlarmRaise') }}</el-checkbox><el-checkbox v-model="form.alarmOnAck">{{ $t('designReview2.scriptAlarmAck') }}</el-checkbox><el-checkbox v-model="form.alarmOnRecover">{{ $t('designReview2.scriptAlarmRecover') }}</el-checkbox></el-form-item></el-col>
                </template>
              </el-row>
              <el-row :gutter="12">
                <el-col :span="12"><el-form-item :label="$t('calcJobsView.kb9c22b')"><TagSelect :model-value="tagIds(form.readTags)" multiple :tags="tags" :devices="devices" :remote="tags.length > 400" data-test="calc-job-read-pick" @update:model-value="(ids: number[]) => setTagIds('readTags', ids)" /><el-input v-model="form.readTags" :placeholder="$t('designReview2.scriptReadHint')" data-test="calc-job-read" /></el-form-item></el-col>
                <el-col :span="12"><el-form-item :label="$t('calcJobsView.k2f2422')"><TagSelect :model-value="tagIds(form.writeTags)" multiple writable :tags="tags" :devices="devices" :remote="tags.length > 400" data-test="calc-job-write-pick" @update:model-value="(ids: number[]) => setTagIds('writeTags', ids)" /><el-input v-model="form.writeTags" :placeholder="$t('designReview2.scriptWriteHint')" data-test="calc-job-write" /></el-form-item></el-col>
                <el-col :span="12"><el-form-item :label="$t('calcJobsView.k053a91')"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
                <el-col :span="12"><el-form-item :label="$t('calcJobsView.k0f1b6b')"><el-input v-model="form.unit" maxlength="32" /></el-form-item></el-col>
                <el-col :span="12"><el-form-item :label="$t('designReview2.scriptHistory')"><el-input-number v-model="form.historyWindowSec" :min="0" :max="3600" :step="60" controls-position="right" data-test="calc-job-history" /></el-form-item><p class="tiny muted">{{ $t('designReview2.scriptHistoryHint') }}</p></el-col>
                <el-col :span="12"><el-form-item :label="$t('designReview2.scriptAlarmSnapshot')"><el-switch v-model="form.alarmSnapshot" data-test="calc-job-alarms" /></el-form-item></el-col>
              </el-row>
              <el-form-item :label="$t('designReview2.scriptLibraryPick')"><el-select :model-value="libraryNames" multiple filterable data-test="calc-job-library-pick" @update:model-value="(names: string[]) => form.libraries = names.join(', ')"><el-option v-for="item in libraries" :key="item.id" :value="item.name" :label="item.publishedRevision ? item.name : `${item.name} · ${$t('screenDesignerView.unpublished')}`" /></el-select></el-form-item>
              <div v-if="editing && references" data-test="calc-job-references">
                <p class="tiny">{{ $t('designReview2.scriptReferences') }} · {{ $t('designReview2.scriptReferenceScreens') }}：{{ references.screens.map(item => item.name).join('、') || $t('designReview2.scriptReferenceNone') }}</p>
                <p v-if="references.tags.some(item => !item.resolved)" class="tiny danger">{{ $t('designReview2.scriptReferenceStale') }}</p>
                <p class="tiny muted">{{ $t('designReview2.scriptReferenceTags') }}：{{ references.tags.map(item => item.resolved ? (item.currentName || item.token) : item.token).join('、') || '--' }}</p>
              </div>
              <p v-if="staleCalls.length" class="tiny danger" data-test="calc-job-stale-tags">{{ $t('designReview2.scriptReferenceStale') }}：{{ staleCalls.join('、') }}</p>
            </el-collapse-item>
          </el-collapse>
        </template>
        <template v-else>
          <el-row :gutter="12">
            <el-col :span="12"><el-form-item :label="$t('designReview2.scriptTrigger')"><el-select v-model="form.trigger" data-test="calc-job-trigger"><el-option v-for="item in triggerOptions" :key="item" :value="item" :label="triggerLabel(item)" /></el-select></el-form-item></el-col>
            <template v-if="form.trigger === 'OnChange'">
              <el-col :span="6"><el-form-item :label="$t('designReview2.scriptDeadband')" label-width="80px"><el-input-number v-model="form.changeDeadband" :min="0" :step="0.1" controls-position="right" /></el-form-item></el-col>
              <el-col :span="6"><el-form-item :label="$t('designReview2.scriptCooldown')" label-width="80px"><el-input-number v-model="form.changeCooldownMs" :min="0" :max="3600000" :step="500" controls-position="right" /></el-form-item></el-col>
            </template>
            <template v-if="form.trigger === 'OnAlarm'">
              <el-col :span="12"><el-form-item :label="$t('designReview2.scriptAlarmLevel')"><el-select v-model="form.alarmMinLevel"><el-option value="Info" :label="$t('designReview2.scriptAlarmInfo')" /><el-option value="Warning" :label="$t('designReview2.scriptAlarmWarning')" /><el-option value="Critical" :label="$t('designReview2.scriptAlarmCritical')" /></el-select></el-form-item></el-col>
              <el-col :span="24"><el-form-item :label="$t('designReview2.scriptAlarmEdge')"><el-checkbox v-model="form.alarmOnRaise">{{ $t('designReview2.scriptAlarmRaise') }}</el-checkbox><el-checkbox v-model="form.alarmOnAck">{{ $t('designReview2.scriptAlarmAck') }}</el-checkbox><el-checkbox v-model="form.alarmOnRecover">{{ $t('designReview2.scriptAlarmRecover') }}</el-checkbox></el-form-item></el-col>
            </template>
          </el-row>
          <el-row :gutter="12">
            <el-col :span="12"><el-form-item :label="$t('calcJobsView.kb9c22b')"><TagSelect :model-value="tagIds(form.readTags)" multiple :tags="tags" :devices="devices" :remote="tags.length > 400" data-test="calc-job-read-pick" @update:model-value="(ids: number[]) => setTagIds('readTags', ids)" /><el-input v-model="form.readTags" :placeholder="$t('designReview2.scriptReadHint')" data-test="calc-job-read" /></el-form-item></el-col>
            <el-col :span="12"><el-form-item :label="$t('calcJobsView.k2f2422')"><TagSelect :model-value="tagIds(form.writeTags)" multiple writable :tags="tags" :devices="devices" :remote="tags.length > 400" data-test="calc-job-write-pick" @update:model-value="(ids: number[]) => setTagIds('writeTags', ids)" /><el-input v-model="form.writeTags" :placeholder="$t('designReview2.scriptWriteHint')" data-test="calc-job-write" /></el-form-item></el-col>
            <el-col :span="12"><el-form-item :label="$t('calcJobsView.k053a91')"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
            <el-col :span="12"><el-form-item :label="$t('calcJobsView.k0f1b6b')"><el-input v-model="form.unit" maxlength="32" /></el-form-item></el-col>
          </el-row>
          <div v-if="editing && references" data-test="calc-job-references">
            <p class="tiny">{{ $t('designReview2.scriptReferences') }} · {{ $t('designReview2.scriptReferenceScreens') }}：{{ references.screens.map(item => item.name).join('、') || $t('designReview2.scriptReferenceNone') }}</p>
            <p v-if="references.tags.some(item => !item.resolved)" class="tiny danger">{{ $t('designReview2.scriptReferenceStale') }}</p>
            <p class="tiny muted">{{ $t('designReview2.scriptReferenceTags') }}：{{ references.tags.map(item => item.resolved ? (item.currentName || item.token) : item.token).join('、') || '--' }}</p>
          </div>
          <p v-if="staleCalls.length" class="tiny danger" data-test="calc-job-stale-tags">{{ $t('designReview2.scriptReferenceStale') }}：{{ staleCalls.join('、') }}</p>
          <el-form-item :label="$t('calcJobsView.k7fbccb')" required><el-input v-model="form.script" type="textarea" :rows="8" maxlength="16384" show-word-limit class="mono" data-test="calc-job-script"
            placeholder="$avg = (#1 + #2) / 2;&#10;@avg = $avg;&#10;#9 = if($avg > 80, 1, 0)" /></el-form-item>
          <p class="tiny muted">{{ $t('calcJobsView.k29d1b6') }}<code>{{ $t('calcJobsView.k578abf') }}</code>、<code>{{ $t('calcJobsView.k72ff89') }}</code>{{ $t('calcJobsView.k800829') }}<code>{{ $t('calcJobsView.kbb6bda') }}</code>{{ $t('calcJobsView.k480763', [limits]) }}</p>
        </template>
      </template>
    </el-form>
    <template #footer><el-button :disabled="saving" @click="closeEditor(() => editor = false)">{{ $t('calcJobsView.k4d0b46') }}</el-button><el-button v-if="isScript(form.kind) && editing" :loading="saving" data-test="calc-job-rollback" @click="openRollback">{{ $t('designReview2.scriptRollback') }}</el-button><el-button v-if="isScript(form.kind)" :loading="saving" :disabled="csharpBlocked" data-test="calc-job-publish" @click="publish">{{ $t('designReview2.scriptPublish') }}</el-button><el-button type="primary" :loading="saving" :disabled="csharpBlocked" data-test="calc-job-save" @click="save">{{ $t('calcJobsView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="libraryDialog" :title="$t('designReview2.scriptLibraries')" width="min(960px, calc(100vw - 24px))" @keydown.ctrl.s.prevent="onLibrarySaveKey" @keydown.meta.s.prevent="onLibrarySaveKey">
    <p class="tiny muted">{{ $t('designReview2.scriptLibraryHint') }}</p>
    <el-alert v-if="libraryError" :title="libraryError" type="error" :closable="false" show-icon />
    <el-table :data="libraries" :empty-text="libraryError || $t('designReview2.scriptLibraryEmpty')" max-height="220" data-test="script-library-table">
      <el-table-column prop="name" :label="$t('designReview2.scriptLibraryName')" width="160" />
      <el-table-column :label="$t('designReview2.scriptPublish')" width="110"><template #default="{ row }">{{ libraryStatus(row) }}</template></el-table-column>
      <el-table-column :label="$t('designReview2.scriptReferences')"><template #default="{ row }">{{ libraryUsers(row.id) }}</template></el-table-column>
      <el-table-column width="180"><template #default="{ row }"><el-button link @click="editLibrary(row)">{{ $t('calcJobsView.ka7f814') }}</el-button><el-button link type="danger" @click="removeLibrary(row)">{{ $t('calcJobsView.k3755f5') }}</el-button></template></el-table-column>
    </el-table>
    <el-form label-width="90px" class="library-form">
      <el-form-item :label="$t('designReview2.scriptLibraryName')"><el-input v-model="libraryForm.name" maxlength="64" :disabled="libraryForm.id !== null" data-test="script-library-name" /></el-form-item>
      <el-form-item v-if="libraryDialog" :label="$t('designReview2.scriptLibrarySource')"><el-tag v-if="libraryPassed" type="success" data-test="script-library-passed">{{ $t('designReview2.scriptLibraryPassed') }}</el-tag><ScriptCodeEditor ref="libraryEditor" v-model="libraryForm.source" file="library.cs" :diagnostics="librarySpans" :read-tags="readCompletion" :write-tags="writeCompletion" :functions="libraryEditorFunctions" :known="knownTag" :accept-tag="acceptTag" :max-length="8192" :disabled="librarySaving" @save="saveLibrary(false)" /><div v-if="librarySpans.length" class="script-diagnostics danger" data-test="script-library-diagnostics"><button v-for="(span, index) in librarySpans" :key="index" type="button" class="script-jump" @click="libraryEditor?.reveal(span.startLine, span.startColumn)">library.cs({{ span.startLine }},{{ span.startColumn }}): {{ span.message }}</button></div></el-form-item>
      <p v-if="libraryDraftDiffers" class="tiny">{{ $t('designReview2.scriptDrift') }}</p>
    </el-form>
    <template #footer>
      <el-button @click="libraryForm = { id: null, name: '', source: '', revision: 0 }">{{ $t('calcJobsView.k26083b') }}</el-button>
      <el-button :loading="librarySaving" data-test="script-library-save" @click="saveLibrary(false)">{{ $t('calcJobsView.kfadf24') }}</el-button>
      <el-button type="primary" :loading="librarySaving" data-test="script-library-publish" @click="saveLibrary(true)">{{ $t('designReview2.scriptPublish') }}</el-button>
    </template>
  </el-dialog>

  <el-dialog v-model="rollbackDialog" :title="$t('designReview2.scriptRollbackPick')" width="720px">
    <el-table :data="rollbackHistory" max-height="360" data-test="calc-job-rollback-list">
      <el-table-column :label="$t('calcJobsView.k89b4aa')" width="170"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
      <el-table-column prop="userName" :label="$t('designReview2.scriptPublicationUser')" width="120" show-overflow-tooltip />
      <el-table-column prop="revision" width="80" />
      <el-table-column :label="$t('designReview2.scriptTrigger')" width="110"><template #default="{ row }">{{ triggerLabel(row.trigger) }}</template></el-table-column>
      <el-table-column show-overflow-tooltip><template #default="{ row }">{{ row.script }}</template></el-table-column>
      <el-table-column width="160"><template #default="{ row }"><el-button link data-test="calc-job-publication-source" @click="sourcePreview = row">{{ $t('designReview2.scriptViewSource') }}</el-button><el-button v-if="row.id !== rollbackHistory[0]?.id" link type="primary" data-test="calc-job-rollback-version" @click="rollbackTo(row)">{{ $t('designReview2.scriptRollback') }}</el-button><span v-else class="tiny muted">{{ $t('designReview2.scriptRollbackCurrent') }}</span></template></el-table-column>
    </el-table>
  </el-dialog>

  <el-dialog v-model="diffDialog" :title="$t('designReview2.scriptDiffTitle')" width="min(880px, calc(100vw - 24px))">
    <p v-if="driftNotes.length" class="tiny">{{ driftNotes.join('、') }}</p>
    <p v-if="form.script === form.publishedScript" class="tiny muted">{{ $t('designReview2.scriptDiffSame') }}</p>
    <div v-else class="script-diff" data-test="calc-job-diff-body"><div v-for="(line, index) in scriptDiffLines" :key="index" :class="['script-diff-line', line.kind]">{{ line.text || ' ' }}</div></div>
  </el-dialog>
  <el-dialog :model-value="sourcePreview != null" :title="$t('designReview2.scriptViewSource')" width="min(760px, calc(100vw - 24px))" @update:model-value="(open: boolean) => { if (!open) sourcePreview = null }">
    <pre class="script-source" data-test="calc-job-publication-body">{{ sourcePreview?.script }}</pre>
    <template #footer><el-button v-if="sourcePreview && sourcePreview.id !== rollbackHistory[0]?.id" type="primary" @click="rollbackTo(sourcePreview)">{{ $t('designReview2.scriptRollback') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="snapshotDialog" :title="$t('calcJobsView.k96e23b', [snapshotJob?.name ?? ''])" width="760px">
    <el-alert v-if="snapshotError" :title="snapshotError" type="error" :closable="false" show-icon data-test="calc-job-snapshots-error" />
    <el-table :data="snapshots" :empty-text="snapshotError || $t('calcJobsView.kc33c8e')" max-height="420">
      <el-table-column :label="$t('calcJobsView.k89b4aa')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
      <el-table-column prop="reason" :label="$t('calcJobsView.k1ff9c3')" width="100" show-overflow-tooltip />
      <el-table-column prop="periodKey" :label="$t('calcJobsView.k8a12b1')" width="150" show-overflow-tooltip />
      <el-table-column :label="$t('calcJobsView.kf7a3d0')" show-overflow-tooltip><template #default="{ row }"><code class="tiny">{{ snapshotText(row.valuesJson) }}</code></template></el-table-column>
    </el-table>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Check, VideoPlay } from '@element-plus/icons-vue'
import { onBeforeRouteLeave } from 'vue-router'
import { calcJobApi, type CalcJob, type CalcJobInfo, type CalcJobInput, type CalcJobKind, type CalcJobPublication, type CalcJobSnapshot, type CalcResetPeriod, type CalcTrigger, type CSharpScriptCapabilities, type CSharpScriptCheck, type ScriptDiagnosticSpan, type ScriptJobReference, type ScriptLibrary, type ScriptPreviewSample, type ScriptReferenceList } from '../api/calcJobs'
import { scriptSnippets } from '../components/scriptSnippets'
import { areaApi, deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import TagSelect from '../components/TagSelect.vue'
import { formatTime, formatValue } from '../utils'
import { can } from '../access/policies'
import { usePolling } from '../composables/usePolling'
import { useAuthStore } from '../stores/auth'
import type { Area, Device, Tag } from '../types'
import { tr } from '../i18n'

const auth = useAuthStore()
const ScriptCodeEditor = defineAsyncComponent(() => import('../components/ScriptCodeEditor.vue'))
const isScript = (kind: CalcJobKind) => kind === 'Script' || kind === 'CSharp'
const canEditArea = (areaId: number) => can(auth.user, 'Configure', areaId)
const canControlArea = (areaId: number) => can(auth.user, 'Control', areaId)
const configurableAreas = computed(() => areas.value.filter((a) => canEditArea(a.id)))
const kindLabels: Record<CalcJobKind, string> = { Accumulator: tr('calcJobsView.kd0029b'), RunHours: tr('calcJobsView.kb722d8'), Energy: tr('calcJobsView.kd3aac1'), Script: tr('calcJobsView.k708f3a'), CSharp: tr('calcJobsView.csharpKind') }
const availableKinds = computed(() => Object.entries(kindLabels).filter(([kind]) => kind !== 'Script' || editing.value !== null))
const resetLabels: Record<CalcResetPeriod, string> = { None: tr('calcJobsView.k8146b1'), Shift: tr('calcJobsView.k204b6f'), Day: tr('calcJobsView.k5bbcdd'), Month: tr('calcJobsView.k7bae1e') }
const bandLabels: Record<string, string> = { sharp: tr('calcJobsView.ka8477c'), peak: tr('calcJobsView.k9a4761'), flat: tr('calcJobsView.k3c9aba'), valley: tr('calcJobsView.kce0b6d') }
const help: Record<CalcJobKind, string> = {
  Accumulator: tr('calcJobsView.k2c1e99'),
  RunHours: tr('calcJobsView.k39124b'),
  Energy: tr('calcJobsView.k5d90b7'),
  Script: '', CSharp: ''
}
const limits = tr('calcJobsView.k370020')
const rows = ref<CalcJobInfo[]>([]), tags = ref<Tag[]>([]), devices = ref<Device[]>([]), areas = ref<Area[]>([]), search = ref(''), automatic = ref(true)
const libraries = ref<ScriptLibrary[]>([]), libraryUsersById = ref<Record<number, ScriptJobReference[]>>({}), libraryDialog = ref(false), librarySaving = ref(false), libraryError = ref('')
const libraryForm = ref<{ id: number | null; name: string; source: string; revision: number }>({ id: null, name: '', source: '', revision: 0 })
const references = ref<ScriptReferenceList | null>(null)
const rollbackDialog = ref(false), rollbackHistory = ref<CalcJobPublication[]>([])
const loading = ref(false), saving = ref(false), readError = ref(''), areasError = ref(''), actionError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null)
const snapshotDialog = ref(false), snapshots = ref<CalcJobSnapshot[]>([]), snapshotJob = ref<CalcJob | null>(null), snapshotError = ref('')
const tou = ref<{ band: string; start: string; end: string }[]>([])
const capabilities = ref<CSharpScriptCapabilities | null>(null), capabilitiesError = ref('')
const checking = ref<'compile' | 'preview' | null>(null), checkResult = ref<CSharpScriptCheck | null>(null), diagnostics = ref<string[]>([]), spans = ref<ScriptDiagnosticSpan[]>([]), previewResult = ref(false)
const scriptEditor = ref<{ reveal: (line: number, column: number) => void; insertSnippet: (snippet: string) => void } | null>(null)
const libraryEditor = ref<{ reveal: (line: number, column: number) => void } | null>(null)
const librarySpans = ref<ScriptDiagnosticSpan[]>([])
const previewInputs = ref<ScriptPreviewSample[]>([])
const previewArgument = ref<number | null>(null)
const usePreviewArgument = ref(false)
const autoChecking = ref(false)
const libraryPassed = ref(false)
const diffDialog = ref(false)
const sourcePreview = ref<CalcJobPublication | null>(null)
const scriptSetup = ref<string[]>([])
const previewQualities = ['Good', 'Bad', 'Uncertain', 'CommFailure', 'Stale']
let checkController: AbortController | undefined
let libraryController: AbortController | undefined
let compileTimer = 0
let libraryTimer = 0
let baseline = ''
const libraryNames = computed(() => form.libraries.split(/[,;\s]+/).map(item => item.trim()).filter(Boolean))
const readCompletion = computed(() => [...new Set(tagIds(form.readTags).map(tokenFor))])
const writeCompletion = computed(() => [...new Set(tagIds(form.writeTags).map(tokenFor))])
const completionFunctions = computed(() => libraryFunctionHints(libraryNames.value.map(name => libraries.value.find(item => item.name === name)).filter((item): item is ScriptLibrary => !!item && item.publishedRevision > 0).map(item => ({ name: item.name, source: item.publishedSource }))))
const libraryEditorFunctions = computed(() => {
  const own = localFunctions(libraryForm.value.source).map(item => ({ name: item.name, detail: item.signature }))
  const seen = new Set(own.map(item => item.name))
  return [...own, ...completionFunctions.value.filter(item => !seen.has(item.name))]
})
const libraryUsers = (id: number) => (libraryUsersById.value[id] ?? []).map(item => item.name).join('、')
const libraryStatus = (item: ScriptLibrary) => tr(item.publishedRevision > 0 && item.source === item.publishedSource ? 'designReview2.scriptLibraryCurrent' : 'designReview2.scriptLibraryDraft')
const libraryDraftDiffers = computed(() => {
  const current = libraries.value.find(item => item.id === libraryForm.value.id)
  return !!current && current.publishedRevision > 0 && libraryForm.value.source !== current.publishedSource
})
const csharpBlocked = computed(() => form.kind === 'CSharp' && (!capabilities.value?.enabled || capabilities.value?.licensed === false))
function tokenSet(text: string): string {
  return text.split(/[,;\s]+/).map(item => item.trim()).filter(Boolean).map(token => {
    const id = resolveToken(token)
    return id == null ? token : `#${id}`
  }).sort().join(',')
}
function nameSet(text: string): string {
  return text.split(/[,;\s]+/).map(item => item.trim()).filter(Boolean).sort().join(',')
}
const publishedDrift = computed(() => form.publishedRevision > 0 && (
  form.script !== form.publishedScript
  || tokenSet(form.readTags) !== tokenSet(form.publishedReadTags)
  || tokenSet(form.writeTags) !== tokenSet(form.publishedWriteTags)
  || form.historyWindowSec !== form.publishedHistoryWindowSec
  || form.alarmSnapshot !== form.publishedAlarmSnapshot
  || nameSet(form.libraries) !== nameSet(form.publishedLibraries)
  || form.trigger !== form.publishedTrigger
  || form.intervalMs !== form.publishedIntervalMs
  || form.changeDeadband !== form.publishedChangeDeadband
  || form.changeCooldownMs !== form.publishedChangeCooldownMs
  || form.alarmMinLevel !== form.publishedAlarmMinLevel
  || form.alarmOnRaise !== form.publishedAlarmOnRaise
  || form.alarmOnAck !== form.publishedAlarmOnAck
  || form.alarmOnRecover !== form.publishedAlarmOnRecover))
const scriptDiffLines = computed(() => diffDialog.value ? lineDiff(form.publishedScript || '', form.script || '') : [])
const driftNotes = computed(() => {
  const notes: string[] = []
  if (tokenSet(form.readTags) !== tokenSet(form.publishedReadTags)) notes.push(tr('calcJobsView.kb9c22b'))
  if (tokenSet(form.writeTags) !== tokenSet(form.publishedWriteTags)) notes.push(tr('calcJobsView.k2f2422'))
  if (form.trigger !== form.publishedTrigger) notes.push(tr('designReview2.scriptTrigger'))
  if (form.intervalMs !== form.publishedIntervalMs) notes.push(tr('calcJobsView.kb7b729'))
  if (form.historyWindowSec !== form.publishedHistoryWindowSec) notes.push(tr('designReview2.scriptHistory'))
  if (form.alarmSnapshot !== form.publishedAlarmSnapshot) notes.push(tr('designReview2.scriptAlarmSnapshot'))
  if (nameSet(form.libraries) !== nameSet(form.publishedLibraries)) notes.push(tr('designReview2.scriptLibraryPick'))
  if (form.changeDeadband !== form.publishedChangeDeadband) notes.push(tr('designReview2.scriptDeadband'))
  if (form.changeCooldownMs !== form.publishedChangeCooldownMs) notes.push(tr('designReview2.scriptCooldown'))
  if (form.alarmMinLevel !== form.publishedAlarmMinLevel || form.alarmOnRaise !== form.publishedAlarmOnRaise || form.alarmOnAck !== form.publishedAlarmOnAck || form.alarmOnRecover !== form.publishedAlarmOnRecover) notes.push(tr('designReview2.scriptAlarmEdge'))
  return notes
})
const libraryBundleDrift = computed(() => {
  if (form.kind !== 'CSharp' || !form.publishedRevision) return false
  const names = form.publishedLibraries.split(/[,;\s]+/).map(item => item.trim()).filter(Boolean)
  const bundle = names.map(name => libraries.value.find(item => item.name === name)?.publishedSource ?? '\0').join('\n')
  return bundle !== form.publishedLibrarySource
})
const staleCalls = computed(() => {
  if (!isScript(form.kind)) return []
  const code = form.script.replace(/\/\*[\s\S]*?\*\//g, '').replace(/\/\/.*$/gm, '')
  const found = new Set<string>()
  for (const match of code.matchAll(/context\s*\.\s*(?:ReadSample|History|Read|Write)\s*\(\s*"([^"\\]*)"/g)) found.add(match[1])
  return [...found].filter(token => resolveToken(token) == null)
})
function resolveToken(token: string): number | null {
  const text = token.trim()
  if (!text) return null
  if (text.includes('/')) {
    const slash = text.indexOf('/')
    const device = devices.value.find(item => item.name === text.slice(0, slash))
    return tags.value.find(item => item.name === text.slice(slash + 1) && item.deviceId === device?.id)?.id ?? null
  }
  const numeric = text.startsWith('#') ? text.slice(1) : text
  if (/^\d+$/.test(numeric)) {
    const id = Number(numeric)
    if (tags.value.some(item => item.id === id)) return id
    if (!text.startsWith('#')) { const named = tags.value.filter(item => item.name === text); if (named.length === 1) return named[0].id }
    return null
  }
  if (text.startsWith('@')) return tags.value.find(item => item.deviceId == null && item.name === text.slice(1))?.id ?? null
  const named = tags.value.filter(item => item.name === text)
  return named.length === 1 ? named[0].id : null
}
function tokenFor(id: number): string {
  const tag = tags.value.find(item => item.id === id)
  if (!tag) return `#${id}`
  if (tags.value.filter(item => item.name === tag.name).length === 1) return tag.name
  const device = devices.value.find(item => item.id === tag.deviceId)
  return device ? `${device.name}/${tag.name}` : `#${tag.id}`
}
function tagIds(text: string): number[] { return text.split(/[,;\s]+/).map(resolveToken).filter((id): id is number => id != null) }
function knownTag(token: string, list: 'read' | 'write'): boolean {
  const id = resolveToken(token)
  return id != null && tagIds(list === 'read' ? form.readTags : form.writeTags).includes(id)
}
function acceptTag(token: string, list: 'read' | 'write'): void {
  const field = list === 'read' ? 'readTags' : 'writeTags'
  const parts = form[field].split(/[,;\s]+/).map(item => item.trim()).filter(Boolean)
  if (!parts.includes(token)) form[field] = [...parts, token].join(', ')
}
function insertScriptSnippet(snippet: string): void { scriptEditor.value?.insertSnippet(snippet) }
function onEditorSaveKey(): void { if (libraryDialog.value || !isScript(form.kind) || saving.value) return; void save() }
function onPreviewKey(): void { if (libraryDialog.value || form.kind !== 'CSharp' || csharpBlocked.value || checking.value) return; void checkScript(true) }
function onLibrarySaveKey(): void { if (!libraryDialog.value || librarySaving.value) return; void saveLibrary(false) }
function lineDiff(before: string, after: string): { kind: 'eq' | 'del' | 'add'; text: string }[] {
  const left = before.split('\n')
  const right = after.split('\n')
  if (left.length * right.length > 250_000) return [...left.map(text => ({ kind: 'del' as const, text })), ...right.map(text => ({ kind: 'add' as const, text }))]
  const scores = Array.from({ length: left.length + 1 }, () => new Array<number>(right.length + 1).fill(0))
  for (let row = left.length - 1; row >= 0; row--) for (let column = right.length - 1; column >= 0; column--)
    scores[row][column] = left[row] === right[column] ? scores[row + 1][column + 1] + 1 : Math.max(scores[row + 1][column], scores[row][column + 1])
  const lines: { kind: 'eq' | 'del' | 'add'; text: string }[] = []
  let row = 0, column = 0
  while (row < left.length && column < right.length) {
    if (left[row] === right[column]) { lines.push({ kind: 'eq', text: left[row] }); row++; column++ }
    else if (scores[row + 1][column] >= scores[row][column + 1]) lines.push({ kind: 'del', text: left[row++] })
    else lines.push({ kind: 'add', text: right[column++] })
  }
  while (row < left.length) lines.push({ kind: 'del', text: left[row++] })
  while (column < right.length) lines.push({ kind: 'add', text: right[column++] })
  return lines
}
function localFunctions(source: string): { name: string; signature: string }[] {
  const code = source.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\/\/.*$/gm, '').replace(/"(?:\\.|[^"\\])*"/g, '""')
  const skip = new Set(['if', 'while', 'for', 'foreach', 'switch', 'catch', 'using', 'lock', 'fixed', 'when'])
  const result: { name: string; signature: string }[] = []
  for (const match of code.matchAll(/(?:^|[\n;{}])\s*(?:async\s+)?(?:[\w.?\[\],<>]+\s+)+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*(?:=>|\{)/g)) {
    const name = match[1]
    if (!name || skip.has(name) || result.some(item => item.name === name)) continue
    result.push({ name, signature: `${name}(${match[2].trim()})` })
    if (result.length >= 40) break
  }
  return result
}
function libraryFunctionHints(items: { name: string; source: string }[]): { name: string; detail: string }[] {
  const result: { name: string; detail: string }[] = []
  for (const item of items) for (const fn of localFunctions(item.source)) {
    if (result.some(existing => existing.name === fn.name)) continue
    result.push({ name: fn.name, detail: `${item.name}: ${fn.signature}` })
  }
  return result
}
function setTagIds(field: 'readTags' | 'writeTags', ids: number[]): void {
  const unresolved = form[field].split(/[,;\s]+/).map(item => item.trim()).filter(item => item && resolveToken(item) == null)
  form[field] = [...ids.map(tokenFor), ...unresolved].join(', ')
}
async function openLibraries(): Promise<void> {
  libraryError.value = ''; libraryDialog.value = true
  try {
    libraries.value = await calcJobApi.libraries()
    const pairs = await Promise.all(libraries.value.map(async item => [item.id, await calcJobApi.libraryReferences(item.id)] as const))
    libraryUsersById.value = Object.fromEntries(pairs)
  } catch (error) { libraryError.value = errorMessage(error, tr('designReview2.scriptLibraryLoadFailed')) }
}
function editLibrary(item: ScriptLibrary): void { libraryForm.value = { id: item.id, name: item.name, source: item.source, revision: item.revision } }
async function saveLibrary(publish: boolean): Promise<void> {
  librarySaving.value = true; libraryError.value = ''
  try {
    const creating = libraryForm.value.id === null
    const saved = await calcJobApi.saveLibrary(libraryForm.value.id, libraryForm.value.name.trim(), libraryForm.value.source, libraryForm.value.revision)
    const published = publish ? await calcJobApi.publishLibrary(saved.id, saved.revision) : saved
    libraryForm.value = { id: published.id, name: published.name, source: published.source, revision: published.revision }
    ElMessage.success(tr(publish || creating ? 'designReview2.scriptLibraryPublished' : 'designReview2.scriptLibrarySaved'))
    await openLibraries()
  } catch (error) { libraryError.value = errorMessage(error, tr('calcJobsView.k40525a')) }
  finally { librarySaving.value = false }
}
async function removeLibrary(item: ScriptLibrary): Promise<void> {
  try { await ElMessageBox.confirm(item.name, tr('designReview2.scriptLibraries'), { type: 'warning' }) } catch { return }
  libraryError.value = ''
  try { await calcJobApi.removeLibrary(item.id, item.revision); if (libraryForm.value.id === item.id) libraryForm.value = { id: null, name: '', source: '', revision: 0 }; ElMessage.success(tr('designReview2.scriptLibraryRemoved')); await openLibraries() }
  catch (error) { libraryError.value = errorMessage(error, tr('calcJobsView.k40525a')) }
}
const triggerOptions: CalcTrigger[] = ['Periodic', 'OnChange', 'OnAlarm', 'OnStartup']
const triggerLabel = (item: CalcTrigger) => tr(`designReview2.scriptTrigger${item}`)
const empty = (): CalcJobInput => ({ name: '', kind: 'Accumulator', enabled: true, areaId: 1, intervalMs: 1000, revision: 0, inputTagId: null, mode: 'Pulse', factor: 1, counterMax: null, maxDelta: null,
  badQuality: 'Hold', maxGapSec: 60, runThreshold: 0.5, maintenanceHours: null, resetPeriod: 'Day', shiftStarts: '08:00,20:00', touJson: '[]', script: '', readTags: '', writeTags: '', unit: '',
  trigger: 'Periodic', changeDeadband: 0, changeCooldownMs: 1000, alarmMinLevel: 'Info', alarmOnRaise: true, alarmOnAck: false, alarmOnRecover: false,
  historyWindowSec: 0, alarmSnapshot: false, libraries: '', publishedLibraries: '', publishedLibrarySource: '', publishedScript: '', publishedReadTags: '', publishedWriteTags: '', publishedHistoryWindowSec: 0, publishedAlarmSnapshot: false, publishedRevision: 0,
  publishedTrigger: 'Periodic', publishedIntervalMs: 1000, publishedChangeDeadband: 0, publishedChangeCooldownMs: 1000, publishedAlarmMinLevel: 'Info', publishedAlarmOnRaise: true, publishedAlarmOnAck: false, publishedAlarmOnRecover: false })
const form = reactive<CalcJobInput>(empty())
const fingerprint = () => JSON.stringify([form, tou.value])
const dirty = () => editor.value && fingerprint() !== baseline
watch(() => form.kind, kind => {
  if (kind === 'CSharp' && editing.value === null && !form.script) form.script = 'var value = 21 * 2;\ncontext.Output("answer", value);\ncontext.Log(value.ToString(CultureInfo.InvariantCulture));'
})
watch(fingerprint, () => { checkController?.abort(); checking.value = null; checkResult.value = null })
watch(editor, visible => { if (!visible) checkController?.abort() })
const inputLabel = computed(() => form.kind === 'RunHours' ? tr('calcJobsView.k0def07') : form.kind === 'Energy' ? tr('calcJobsView.kd92f80') : form.mode === 'Pulse' ? tr('calcJobsView.kb3e788') : tr('calcJobsView.kb93fca'))
const filtered = computed(() => rows.value.filter(x => `${x.job.id} ${x.job.name} ${kindLabels[x.job.kind]}`.toLowerCase().includes(search.value.trim().toLowerCase())))
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? tr('calcJobsView.k90f301', [id])
const tagName = (id: number | null) => tags.value.find(t => t.id === id)?.name ?? ''
const tagToken = (id: number) => {
  const tag = tags.value.find(t => t.id === id)
  if (!tag) return String(id)
  return tags.value.some(t => t.id !== id && t.name === tag.name) ? `#${id}` : tag.name
}
const describeTags = (ids: string) => ids.split(',').map(part => part.trim()).filter(Boolean).map(part => (/^\d+$/.test(part) ? tagToken(Number(part)) : part)).join(', ')
const value = (row: CalcJobInfo, name: string) => { const v = row.status?.values?.[name]; return v === undefined ? '--' : formatValue(v) }
const snapshotText = (json: string) => { try { return Object.entries(JSON.parse(json) as Record<string, number>).map(([k, v]) => `${k}=${formatValue(v)}`).join('  ') } catch { return json } }

let alive = true
let refreshing = false
// background=true 表示 3 秒定时轮询：不点亮全表 loading 遮罩，也不与用户手动刷新抢占
async function refresh(background = false): Promise<void> {
  if (background && refreshing) return
  refreshing = true
  if (!background) loading.value = true
  try {
    const [list, tagList, deviceList, areaList, scriptCapabilities, libraryList] = await Promise.all([
      calcJobApi.list(), tagApi.list(), deviceApi.list().catch(() => devices.value),
      areaApi.list().then(list => { areasError.value = ''; return list }).catch((error) => { areasError.value = errorMessage(error, tr('calcJobsView.areasLoadFailed')); return areas.value }),
      calcJobApi.capabilities().then(result => { capabilitiesError.value = ''; return result }).catch(error => { capabilitiesError.value = errorMessage(error, tr('calcJobsView.csharpDisabled')); return null }),
      calcJobApi.libraries().catch(() => libraries.value)
    ])
    if (!alive) return
    rows.value = list; tags.value = tagList; devices.value = deviceList; areas.value = areaList; capabilities.value = scriptCapabilities; libraries.value = libraryList; readError.value = ''
  } catch (e) { readError.value = errorMessage(e, tr('calcJobsView.k38b211')) }
  finally { refreshing = false; if (!background) loading.value = false }
}
function open(job: CalcJob | null): void {
  const areaId = configurableAreas.value[0]?.id
  if (!job && areaId == null) return
  editing.value = job?.id ?? null
  const unit = job ? (tags.value.find(t => t.jobId === job.id && !['hours', 'periodHours', 'sinceMaintenance', 'starts', 'periodStarts', 'maintenanceDue'].includes(t.jobOutput ?? ''))?.unit ?? '') : ''
  Object.assign(form, job ? { ...empty(), ...job, unit } : { ...empty(), areaId })
  if (job && isScript(job.kind)) { form.readTags = describeTags(job.readTags); form.writeTags = describeTags(job.writeTags) }
  try { tou.value = JSON.parse(form.touJson || '[]') } catch { tou.value = [] }
  checkResult.value = null; diagnostics.value = []; spans.value = []; checking.value = null; references.value = null
  previewInputs.value = []; previewArgument.value = null; usePreviewArgument.value = false; autoChecking.value = false
  scriptSetup.value = form.kind === 'CSharp' && !form.readTags.trim() ? ['setup'] : []
  mutationError.value = ''; baseline = fingerprint(); editor.value = true
  if (job && isScript(job.kind)) void calcJobApi.references(job.id).then(result => { if (editing.value === job.id) references.value = result }).catch(() => { references.value = null })
}
function revealDiagnostic(message: string): void {
  const position = /script\.cs\((\d+),(\d+)\)/.exec(message)
  if (position) scriptEditor.value?.reveal(Number(position[1]), Number(position[2]))
}
async function checkScript(preview: boolean, automatic = false): Promise<void> {
  if (!automatic) window.clearTimeout(compileTimer)
  if (automatic && (!editor.value || form.kind !== 'CSharp' || csharpBlocked.value || !form.script.trim())) return
  checkController?.abort()
  const controller = new AbortController(); checkController = controller
  const draft = fingerprint()
  if (!automatic) { checking.value = preview ? 'preview' : 'compile'; checkResult.value = null; diagnostics.value = []; spans.value = []; autoChecking.value = false }
  else autoChecking.value = true
  try {
    const result = await calcJobApi.check({ ...form }, preview, controller.signal, preview ? { argument: usePreviewArgument.value ? previewArgument.value ?? 0 : null, inputs: previewInputs.value.length ? previewInputs.value.map(item => ({ tagId: item.tagId, value: item.value, quality: item.quality, timestamp: item.timestamp })) : null } : undefined)
    if (!alive || !editor.value || controller.signal.aborted || draft !== fingerprint()) return
    previewResult.value = preview; checkResult.value = result
    diagnostics.value = [...result.diagnostics, ...(result.error ? [result.error] : [])]
    spans.value = result.spans ?? []
    if (preview && result.inputs) previewInputs.value = result.inputs
  } catch (error) {
    if (!controller.signal.aborted && draft === fingerprint()) { diagnostics.value = [errorMessage(error, tr('calcJobsView.checkFailed'))]; spans.value = [] }
  } finally { if (checkController === controller) { checking.value = null; if (automatic) autoChecking.value = false } }
}
async function previewLive(): Promise<void> {
  previewInputs.value = []
  await checkScript(true)
}
async function checkLibrary(): Promise<void> {
  libraryController?.abort()
  const controller = new AbortController(); libraryController = controller
  const source = libraryForm.value.source
  if (!source.trim()) { librarySpans.value = []; return }
  try {
    const result = await calcJobApi.compileLibrary(source, controller.signal)
    if (controller.signal.aborted || source !== libraryForm.value.source) return
    librarySpans.value = (result.spans ?? []).filter(span => span.file === 'library.cs')
    libraryPassed.value = result.success && librarySpans.value.length === 0
    if (!result.success && librarySpans.value.length === 0) libraryError.value = [...result.diagnostics, ...(result.error ? [result.error] : [])].join('\n') || tr('calcJobsView.checkFailed')
  } catch (error) {
    if (!controller.signal.aborted && source === libraryForm.value.source) libraryError.value = errorMessage(error, tr('calcJobsView.checkFailed'))
  }
}
async function closeEditor(done: () => void): Promise<void> {
  if (saving.value) return
  if (dirty()) { try { await ElMessageBox.confirm(tr('calcJobsView.discardDraft'), tr('calcJobsView.unsavedDraft'), { type: 'warning' }) } catch { return } }
  done()
}
onBeforeRouteLeave(async () => {
  if (!dirty()) return true
  try { await ElMessageBox.confirm(tr('calcJobsView.discardDraft'), tr('calcJobsView.unsavedDraft'), { type: 'warning' }); return true } catch { return false }
})
function beforeUnload(event: BeforeUnloadEvent): void { if (dirty()) { event.preventDefault(); event.returnValue = '' } }
async function save(): Promise<void> {
  if (!configurableAreas.value.some(a => a.id === form.areaId)) { mutationError.value = tr('calcJobsView.areaRequired'); return }
  saving.value = true; mutationError.value = ''
  try {
    form.touJson = JSON.stringify(tou.value)
    await calcJobApi.save(editing.value, { ...form })
    editor.value = false; ElMessage.success(tr('calcJobsView.kcdfab9')); await refresh()
  } catch (e) { mutationError.value = errorMessage(e, tr('calcJobsView.k40525a')) }
  finally { saving.value = false }
}
function applySaved(job: CalcJob, unit: string): void {
  editing.value = job.id
  Object.assign(form, { ...empty(), ...job, unit })
  if (isScript(job.kind)) { form.readTags = describeTags(job.readTags); form.writeTags = describeTags(job.writeTags) }
  baseline = fingerprint()
}
async function openRollback(): Promise<void> {
  if (editing.value == null) return
  mutationError.value = ''
  try {
    rollbackHistory.value = await calcJobApi.publications(editing.value)
    if (rollbackHistory.value.length < 2) { mutationError.value = tr('designReview2.scriptRollbackEmpty'); return }
    rollbackDialog.value = true
  } catch (e) { mutationError.value = errorMessage(e, tr('calcJobsView.k40525a')) }
}
async function rollbackTo(item: CalcJobPublication): Promise<void> {
  if (editing.value == null) return
  try { await ElMessageBox.confirm(tr('designReview2.scriptRollbackConfirm'), tr('designReview2.scriptRollback'), { type: 'warning' }) }
  catch { return }
  saving.value = true; mutationError.value = ''
  try {
    const unit = form.unit
    applySaved(await calcJobApi.rollback(editing.value, item.id, form.revision), unit)
    rollbackDialog.value = false; sourcePreview.value = null
    ElMessage.success(tr('designReview2.scriptRolledBack')); await refresh()
  } catch (e) { mutationError.value = errorMessage(e, tr('calcJobsView.k40525a')) }
  finally { saving.value = false }
}
async function publish(): Promise<void> {
  if (!configurableAreas.value.some(a => a.id === form.areaId)) { mutationError.value = tr('calcJobsView.areaRequired'); return }
  saving.value = true; mutationError.value = ''
  try {
    form.touJson = JSON.stringify(tou.value)
    const saved = await calcJobApi.save(editing.value, { ...form })
    const unit = form.unit
    applySaved(saved, unit)
    applySaved(await calcJobApi.publish(saved.id, saved.revision), unit)
    ElMessage.success(tr('designReview2.scriptPublished')); await refresh()
  } catch (e) { mutationError.value = errorMessage(e, tr('calcJobsView.k40525a')) }
  finally { saving.value = false }
}
async function remove(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.kdbecb4', [job.name]), tr('calcJobsView.k3c06ab'), { type: 'warning' }) } catch { return }
  actionError.value = ''
  try { await calcJobApi.remove(job); actionError.value = ''; ElMessage.success(tr('calcJobsView.kfb5fe1')); await refresh() } catch (e) { actionError.value = errorMessage(e, tr('calcJobsView.k72250c')) }
}
async function reset(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.k932c29', [job.name]), tr('calcJobsView.k193804'), { type: 'warning' }) } catch { return }
  actionError.value = ''
  try { await calcJobApi.reset(job.id); actionError.value = ''; ElMessage.success(tr('calcJobsView.k960e8b')); await refresh() } catch (e) { actionError.value = errorMessage(e, tr('calcJobsView.kff5f6b')) }
}
async function maintenance(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.k1b5f0e', [job.name]), tr('calcJobsView.k33a9d1'), { type: 'info' }) } catch { return }
  actionError.value = ''
  try { await calcJobApi.maintenanceDone(job.id); actionError.value = ''; ElMessage.success(tr('calcJobsView.k53002a')); await refresh() } catch (e) { actionError.value = errorMessage(e, tr('calcJobsView.k09e424')) }
}
async function acknowledge(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(tr('calcJobsView.acknowledgeConfirm'), tr('calcJobsView.acknowledgeWrites'), { type: 'warning' }) } catch { return }
  actionError.value = ''
  try { await calcJobApi.acknowledgeWrites(job.id); await refresh() } catch (error) { actionError.value = errorMessage(error, tr('calcJobsView.k09e424')) }
}
async function showSnapshots(job: CalcJob): Promise<void> {
  snapshotJob.value = job; snapshots.value = []; snapshotError.value = ''; snapshotDialog.value = true
  try { snapshots.value = await calcJobApi.snapshots(job.id); snapshotError.value = '' } catch (e) { snapshotError.value = errorMessage(e, tr('calcJobsView.k2dd7e3')) }
}
// 编辑器打开或正在加载时跳过本轮，避免盖掉未保存的改动
const jobsPolling = usePolling(() => refresh(true), 3000, { enabled: () => automatic.value && !editor.value && !loading.value })
// 打开自动刷新时立刻补一次，不必等下一个 3s 节拍
watch(automatic, on => { if (on) void jobsPolling.run() })
onMounted(async () => { window.addEventListener('beforeunload', beforeUnload); await refresh() })
watch(() => [editor.value, form.kind, form.script, form.libraries, csharpBlocked.value] as const, () => {
  window.clearTimeout(compileTimer)
  if (!editor.value || form.kind !== 'CSharp' || csharpBlocked.value || !form.script.trim()) {
    if (editor.value && form.kind === 'CSharp' && !form.script.trim()) { diagnostics.value = []; spans.value = [] }
    return
  }
  compileTimer = window.setTimeout(() => void checkScript(false, true), 700)
})
watch([libraryDialog, () => libraryForm.value.source, () => capabilities.value?.enabled, () => capabilities.value?.licensed], () => {
  window.clearTimeout(libraryTimer)
  libraryPassed.value = false
  if (!libraryDialog.value) { libraryController?.abort(); return }
  if (!capabilities.value?.enabled || capabilities.value.licensed === false) return
  libraryTimer = window.setTimeout(() => void checkLibrary(), 700)
})
onBeforeUnmount(() => { alive = false; window.clearTimeout(compileTimer); window.clearTimeout(libraryTimer); checkController?.abort(); libraryController?.abort(); window.removeEventListener('beforeunload', beforeUnload) })
</script>
<style scoped>
.tou-row { display: flex; gap: 8px; align-items: center; margin-bottom: 6px; }
.danger { color: var(--el-color-danger); }
.mono :deep(textarea) { font-family: ui-monospace, Consolas, monospace; }
.script-check { min-width: 0; padding-block: 12px; border-top: 1px solid var(--el-border-color); }
.script-check-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-bottom: 12px; }
.script-check-toolbar :deep(.el-button + .el-button) { margin-left: 0; }
.script-diagnostics { max-height: 200px; overflow: auto; white-space: pre-wrap; overflow-wrap: anywhere; font-size: 13px; }
.script-jump { display: block; width: 100%; margin: 0; padding: 0; border: 0; background: transparent; color: inherit; font: inherit; text-align: left; cursor: pointer; white-space: pre-wrap; overflow-wrap: anywhere; }
.script-jump:hover { text-decoration: underline; }
.script-work { display: grid; grid-template-columns: minmax(0, 1.4fr) minmax(240px, 0.8fr); gap: 16px; align-items: start; }
.script-work:not(:has([data-test='csharp-preview-inputs'])) { grid-template-columns: minmax(0, 1fr); }
.preview-input { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 8px; }
.script-diff { max-height: 420px; overflow: auto; border: 1px solid var(--el-border-color); border-radius: 4px; }
.script-diff-line { font-family: Consolas, monospace; font-size: 12px; white-space: pre-wrap; padding: 0 8px; }
.script-diff-line.add { background: #e7f6e7; }
.script-diff-line.del { background: #fde8e8; }
.script-source { max-height: 420px; overflow: auto; white-space: pre-wrap; font-family: Consolas, monospace; font-size: 13px; }
:global(html.dark) .script-diff-line.add { background: #14331c; }
:global(html.dark) .script-diff-line.del { background: #3a1717; }
@media (max-width: 800px) { .script-work { grid-template-columns: 1fr; } }
.script-check h4 { font-size: 13px; margin: 12px 0 6px; }
@media (max-width: 600px) {
  :global(.calc-editor .el-col) { flex-basis: 100%; max-width: 100%; }
  :global(.calc-editor .el-form-item) { display: block; }
  :global(.calc-editor .el-form-item__label) { width: auto !important; height: auto; padding-bottom: 4px; justify-content: flex-start; }
  :global(.calc-editor .el-form-item__content) { margin-left: 0 !important; min-width: 0; }
}
</style>

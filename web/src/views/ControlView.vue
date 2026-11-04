<template>
  <div class="page"><div class="panel">
    <div class="toolbar">
      <h3>{{ $t('controlView.rules') }}</h3>
      <el-tooltip :content="$t('controlView.kb7e396')" placement="bottom-start" :show-after="200">
        <el-icon class="info-icon"><InfoFilled /></el-icon>
      </el-tooltip>
      <span class="spacer" />
      <el-button @click="refresh()">{{ $t('controlView.k38108e') }}</el-button>
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="control-load-error" />
    <el-alert v-if="requestsError" :title="requestsError" type="error" :closable="false" show-icon data-test="control-requests-error" />
    <el-alert v-if="runsError" :title="runsError" type="error" :closable="false" show-icon data-test="control-runs-error" />
    <el-alert v-if="areasError" :title="areasError" type="error" :closable="false" show-icon data-test="control-areas-error" />
    <el-alert v-if="modesError" :title="modesError" type="error" :closable="false" show-icon data-test="control-modes-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="control-action-error" />
    <el-tabs v-model="tab">
      <el-tab-pane :label="$t('controlView.k4bcbaa', [pending.length])" name="requests">
        <p v-if="orderedRequests.length" class="tiny muted control-kbd-hint" data-test="control-kbd-hint">{{ $t('controlUx.kbdRequests') }}</p>
        <el-table :data="orderedRequests" size="small" row-key="request.id" data-test="control-requests" :row-class-name="requestRowClass" :empty-text="loadError && !requests.length ? loadError : $t('controlView.k7d3e31')">
          <el-table-column :label="$t('controlView.k163aec')" min-width="220" show-overflow-tooltip><template #default="{ row }">
            <span v-if="row.request.kind === 'command'">{{ tagName(row.request.tagId) }} ← <strong>{{ row.request.value }}</strong></span>
            <span v-else-if="row.request.kind === 'recipe'">{{ $t('controlView.kaed0af', [row.recipe?.recipeName, row.recipe?.version]) }}
              <div v-for="r in row.recipe?.results ?? []" :key="r.key" class="tiny">{{ r.name }} ← <strong>{{ r.target }}</strong></div></span>
            <span v-else>{{ $t('controlView.k9038ea', [row.sequenceName]) }}</span>
            <div class="tiny muted">{{ row.request.source }}</div>
          </template></el-table-column>
          <el-table-column :label="$t('controlView.kbfdd7d')" min-width="150" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ row.request.requestedBy }} · {{ formatTime(row.request.requestedTs) }}</span></template></el-table-column>
          <el-table-column :label="$t('controlView.remaining')" width="120" data-test="request-expiry" show-overflow-tooltip><template #default="{ row }">
            <span v-if="row.request.status === 'Pending'" class="tiny" :class="expiringSoon(row.request.expiresTs) ? 'tag-fail' : 'muted'">
              {{ remaining(row.request.expiresTs) }}
            </span>
            <span v-else class="muted tiny">-</span>
          </template></el-table-column>
          <el-table-column :label="$t('controlView.k62e951')" width="100" show-overflow-tooltip><template #default="{ row }"><el-tag size="small" :type="statusType[requestStatus(row.request)] ?? 'info'">{{ statusLabels[requestStatus(row.request)] ?? row.request.status }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('controlView.k0a2c91')" min-width="200" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ row.request.decidedBy ? `${row.request.decidedBy}：` : '' }}{{ row.request.resultMessage ?? row.request.comment ?? '' }}</span></template></el-table-column>
          <el-table-column :label="$t('controlView.kf3ea6d')" width="190"><template #default="{ row }">
            <template v-if="requestStatus(row.request) === 'Pending'">
              <el-button v-if="!row.mine" size="small" type="primary" :loading="deciding.has(row.request.id)" :data-test="`approve-${row.request.id}`" @click="decide(row, true)">{{ $t('controlView.kea39c9') }}</el-button>
              <el-button size="small" :disabled="deciding.has(row.request.id)" :data-test="`reject-${row.request.id}`" @click="decide(row, false)">{{ row.mine ? $t('controlView.kf16418') : $t('controlView.k21ec78') }}</el-button>
            </template>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane :label="$t('controlView.k8d2356')" name="lockouts">
        <div class="toolbar">
          <el-select v-model="lockForm.target" filterable :placeholder="$t('controlView.k1eeca1')" style="width: 260px" data-test="lock-target">
            <el-option-group :label="$t('controlView.k01f2c1')"><el-option v-for="d in controllableDevices" :key="`d${d.id}`" :value="`d${d.id}`" :label="d.name" /></el-option-group>
            <el-option-group :label="$t('controlView.k8cff1c')"><el-option v-for="t in controllableTags" :key="`t${t.id}`" :value="`t${t.id}`" :label="tagName(t.id)" /></el-option-group>
          </el-select>
          <el-select v-model="lockForm.kind" style="width: 120px"><el-option value="NoOperate" :label="$t('controlView.k7966a0')" /><el-option value="Maintenance" :label="$t('controlView.k3799ee')" /></el-select>
          <el-input v-model="lockForm.reason" :placeholder="$t('controlView.k3cfd71')" style="width: 240px" maxlength="200" data-test="lock-reason" />
          <el-button type="danger" :disabled="!lockForm.target || !lockForm.reason.trim()" data-test="lock-place" @click="placeLock()">{{ $t('controlView.k8d2356') }}</el-button>
          <span class="spacer" /><el-checkbox v-model="showRemoved" @change="loadLockouts()">{{ $t('controlView.k634f10') }}</el-checkbox>
        </div>
        <p v-if="lockouts.length" class="tiny muted control-kbd-hint">{{ $t('controlUx.kbdLockouts') }}</p>
        <el-table :data="lockouts" size="small" row-key="id" data-test="lockouts" :row-class-name="lockoutRowClass" :empty-text="loadError && !lockouts.length ? loadError : $t('controlView.k1aab4e')">
          <el-table-column :label="$t('controlView.k539cb3')" min-width="180" show-overflow-tooltip><template #default="{ row }">{{ row.deviceId ? $t('controlView.k065e16', [deviceName(row.deviceId)]) : $t('controlView.k87f55c', [tagName(row.tagId)]) }}</template></el-table-column>
          <el-table-column :label="$t('controlView.k265dc5')" width="100" show-overflow-tooltip><template #default="{ row }"><el-tag size="small" :type="row.kind === 'NoOperate' ? 'danger' : 'warning'">{{ row.kind === 'NoOperate' ? $t('controlView.k7966a0') : $t('controlView.k3799ee') }}</el-tag></template></el-table-column>
          <el-table-column prop="reason" :label="$t('controlView.k1ff9c3')" min-width="160" show-overflow-tooltip />
          <el-table-column :label="$t('controlView.k8d2356')" min-width="160" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ row.placedBy }} · {{ formatTime(row.placedTs) }}</span></template></el-table-column>
          <el-table-column :label="$t('controlView.k0e3083')" min-width="160"><template #default="{ row }">
            <span v-if="row.removedTs" class="tiny">{{ row.removedBy }} · {{ formatTime(row.removedTs) }} · {{ row.removeReason }}</span>
            <el-button v-else-if="canRemoveLock(row)" size="small" :data-test="`unlock-${row.id}`" @click="removeLock(row)">{{ $t('controlView.k0e3083') }}</el-button>
          </template></el-table-column>
        </el-table>
        <el-button v-if="lockoutsMore" :loading="lockoutsLoadingOlder" data-test="lockouts-older" @click="loadOlderLockouts">{{ $t('designReview2.loadOlder') }}</el-button>
        <p class="tiny muted">{{ $t('controlView.kc26b48') }}</p>
      </el-tab-pane>

      <el-tab-pane :label="$t('controlView.k0e9ffe')" name="modes">
        <el-table :data="devices" size="small" data-test="modes">
          <el-table-column prop="name" :label="$t('controlView.k01f2c1')" min-width="160" show-overflow-tooltip />
          <el-table-column :label="$t('controlView.k0e9ffe')" width="120" show-overflow-tooltip><template #default="{ row }"><el-tag size="small" :type="!modesReady ? 'info' : modeOf(row.id)?.mode === 'Local' ? 'warning' : 'success'" :data-test="`mode-state-${row.id}`">{{ !modesReady ? $t('operationsReview.modeUnknown') : modeOf(row.id)?.mode === 'Local' ? $t('controlView.kee521f') : $t('controlView.k801947') }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('controlView.k0ca7ec')" min-width="220" show-overflow-tooltip><template #default="{ row }"><span v-if="modeOf(row.id)" class="tiny">{{ modeOf(row.id)!.changedBy }} · {{ formatTime(modeOf(row.id)!.changedTs) }} · {{ modeOf(row.id)!.reason }}</span></template></el-table-column>
          <el-table-column :label="$t('controlView.kf3ea6d')" width="140"><template #default="{ row }">
            <el-button v-if="canControlDevice(row.id)" size="small" :disabled="!modesReady" :loading="changingModes.has(row.id)" :data-test="`mode-${row.id}`" @click="toggleMode(row.id)">{{ !modesReady ? $t('operationsReview.modeUnknown') : $t('controlView.kb3190e', [modeOf(row.id)?.mode === 'Local' ? $t('controlView.k801947') : $t('controlView.kee521f')]) }}</el-button>
          </template></el-table-column>
        </el-table>
        <p class="tiny muted">{{ $t('controlView.k4ac15d') }}</p>
      </el-tab-pane>

      <el-tab-pane :label="$t('controlView.k31d7f3')" name="sequences">
        <div class="toolbar"><el-button v-if="configurableAreas.length" type="primary" data-test="sequence-new" @click="editSequence()">{{ $t('controlView.k69e7ba') }}</el-button></div>
        <el-table :data="sequences" size="small" data-test="sequences" :empty-text="loadError && !sequences.length ? loadError : $t('controlView.kdf719b')">
          <el-table-column prop="name" :label="$t('controlView.k1be7ae')" min-width="160" show-overflow-tooltip />
          <el-table-column :label="$t('controlView.kd13bfa')" min-width="260" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ stepsSummary(row.steps) }}</span></template></el-table-column>
          <el-table-column :label="$t('controlView.kb38c92')" width="80" show-overflow-tooltip><template #default="{ row }">{{ row.dualApproval ? $t('controlView.k1b5fdf') : '-' }}</template></el-table-column>
          <el-table-column :label="$t('controlView.kf3ea6d')" width="230"><template #default="{ row }">
            <el-button v-if="canControlArea(row.areaId)" size="small" type="primary" :data-test="`sequence-start-${row.id}`" @click="startSequence(row)">{{ $t('controlView.k28febb') }}</el-button>
            <el-button v-if="canConfigureArea(row.areaId)" size="small" @click="editSequence(row)">{{ $t('controlView.ka7f814') }}</el-button>
            <el-button v-if="canConfigureArea(row.areaId)" size="small" type="danger" @click="removeSequence(row)">{{ $t('controlView.k3755f5') }}</el-button>
          </template></el-table-column>
        </el-table>
        <h4>{{ $t('controlView.k24fb42') }}</h4>
        <p v-if="runs.length" class="tiny muted control-kbd-hint">{{ $t('controlUx.kbdRuns') }}</p>
        <el-table :data="runs" size="small" row-key="id" data-test="runs" :row-class-name="runRowClass" :empty-text="loadError && !runs.length ? loadError : $t('controlView.k58c81e')">
          <el-table-column :label="$t('controlView.k0c3acd')" min-width="160" show-overflow-tooltip><template #default="{ row }">#{{ row.id }} {{ row.sequenceName }}</template></el-table-column>
          <el-table-column :label="$t('controlView.k62e951')" width="90" show-overflow-tooltip><template #default="{ row }"><el-tag size="small" :type="runType[row.status] ?? 'info'" :data-test="`run-status-${row.id}`">{{ runLabels[row.status] ?? row.status }}</el-tag></template></el-table-column>
          <el-table-column :label="$t('controlView.kd13bfa')" min-width="320" show-overflow-tooltip><template #default="{ row }">
            <div v-for="s in row.steps" :key="s.index" class="tiny"><el-tag size="small" :type="stepType[s.status] ?? 'info'">{{ stepLabels[s.status] ?? s.status }}</el-tag> {{ s.stepId ?? s.index }}. {{ s.name }}<span v-if="(s.executions ?? 0) > 1" class="muted">{{ $t('controlView.k61803b', [s.executions]) }}</span> <span class="muted">{{ s.message }}</span></div>
          </template></el-table-column>
          <el-table-column :label="$t('controlView.k28febb')" min-width="170" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ row.startedBy }}{{ row.approvedBy ? $t('controlView.kabef81', [row.approvedBy]) : '' }} · {{ formatTime(row.startedTs) }}</span><div class="tiny muted">{{ row.message }}</div></template></el-table-column>
          <el-table-column label="" width="190"><template #default="{ row }"><el-button size="small" link :data-test="`run-flow-${row.id}`" @click="openRunFlow(row)">{{ $t('controlView.k3bffba') }}</el-button><el-button v-if="(row.status === 'Failed' || row.status === 'Aborted') && sequences.some(s => s.id === row.sequenceId) && canControlArea(row.areaId)" size="small" link type="primary" :data-test="`run-rerun-${row.id}`" @click="rerunRun(row)">{{ $t('controlUx.rerun') }}</el-button><el-button v-if="row.status === 'Running' && canControlArea(row.areaId)" size="small" type="danger" @click="abort(row)">{{ $t('controlView.k67847e') }}</el-button></template></el-table-column>
        </el-table>
        <el-button v-if="runsMore" :loading="runsLoadingOlder" data-test="runs-older" @click="loadOlderRuns">{{ $t('designReview2.loadOlder') }}</el-button>
      </el-tab-pane>

      <el-tab-pane :label="$t('controlView.k6a4d18')" name="points">
        <div class="toolbar">
          <el-select v-model="pointTag" filterable :placeholder="$t('controlView.k76ac2d')" style="width: 280px" data-test="point-tag"><el-option v-for="t in configurableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select>
          <el-button :disabled="!pointTag || !canConfigureTag(pointTag)" data-test="point-edit" @click="editPoint(pointTag!)">{{ $t('controlView.kd7d7ce') }}</el-button>
        </div>
        <el-table :data="points" size="small" data-test="points" :empty-text="loadError && !points.length ? loadError : $t('controlView.kd68130')">
          <el-table-column :label="$t('controlView.k8cff1c')" min-width="180" show-overflow-tooltip><template #default="{ row }">{{ tagName(row.tagId) }}</template></el-table-column>
          <el-table-column :label="$t('controlView.kf3c498')" min-width="360" show-overflow-tooltip><template #default="{ row }"><span class="tiny">{{ pointSummary(row) }}</span></template></el-table-column>
          <el-table-column :label="$t('controlView.kf3ea6d')" width="150"><template #default="{ row }">
            <el-button v-if="canConfigureTag(row.tagId)" size="small" @click="editPoint(row.tagId)">{{ $t('controlView.ka7f814') }}</el-button><el-button v-if="canConfigureTag(row.tagId)" size="small" type="danger" @click="removePoint(row.tagId)">{{ $t('controlView.k3755f5') }}</el-button>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="pointVisible" :title="$t('controlView.k754e7b', [pointForm ? tagName(pointForm.tagId) : ''])" width="560px" :before-close="pointEditor.beforeClose" :close-on-click-modal="!savingPoint" :close-on-press-escape="!savingPoint" :show-close="!savingPoint" data-test="point-dialog" @keydown="pointEditor.onKey">
    <el-alert v-if="pointError" :title="pointError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="point-save-error" />
    <el-form v-if="pointForm" label-width="120px" size="small" :disabled="savingPoint">
      <el-form-item :label="$t('controlView.k2f3719')"><el-switch v-model="pointForm.requireConfirm" /><span class="tiny muted">　{{ $t('controlView.k613065') }}</span></el-form-item>
      <el-form-item :label="$t('controlView.kfabc6b')"><el-switch v-model="pointForm.dualApproval" data-test="point-dual" />　{{ $t('controlView.k222460') }} <template v-if="pointForm.dualApproval"><el-input-number v-model="pointForm.approvalTimeoutSec" :min="30" :max="3600" /> {{ $t('controlView.keb6aab') }}</template></el-form-item>
      <el-form-item :label="$t('controlView.kb3bb3c')"><el-input-number v-model="pointForm.limitMin" :controls="false" :placeholder="$t('controlView.kaf3af1')" style="width: 110px" /> ～ <el-input-number v-model="pointForm.limitMax" :controls="false" :placeholder="$t('controlView.k0517c1')" style="width: 110px" data-test="point-max" /></el-form-item>
      <el-form-item :label="$t('controlView.k2a415d')"><el-input-number v-model="pointForm.maxStep" :controls="false" :min="0" :placeholder="$t('controlView.k09c4fc')" style="width: 110px" /></el-form-item>
      <el-form-item :label="$t('controlView.k0f7d64')"><el-input v-model="pointForm.permitExpression" :placeholder="$t('controlView.k89c17f')" data-test="point-permit" /></el-form-item>
      <el-form-item v-if="pointForm.permitExpression?.trim()" :label="$t('controlView.k296eb0')"><el-input v-model="pointForm.permitMessage" maxlength="200" :placeholder="$t('controlView.k0aa008')" /></el-form-item>
      <el-form-item :label="$t('controlView.k463dc4')"><el-input v-model="pointForm.feedbackExpression" :placeholder="$t('controlView.k79b797')" /></el-form-item>
      <el-form-item v-if="pointForm.feedbackExpression?.trim()" :label="$t('controlView.k7ca8c6')"><el-input-number v-model="pointForm.feedbackTimeoutMs" :min="100" :max="600000" :step="1000" /> {{ $t('controlView.kf3c3d5') }}</el-form-item>
    </el-form>
    <template #footer><el-button :disabled="savingPoint" @click="pointEditor.close()">{{ $t('controlView.k4d0b46') }}</el-button><el-button type="primary" :loading="savingPoint" data-test="point-save" @click="savePoint()">{{ $t('controlView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="sequenceVisible" :title="sequenceForm.id ? $t('controlView.k38ca13') : $t('controlView.k69e7ba')" width="1240px" :before-close="sequenceEditor.beforeClose" :close-on-click-modal="!savingSequence" :close-on-press-escape="!savingSequence" :show-close="!savingSequence" data-test="sequence-dialog" @keydown="sequenceEditor.onKey">
    <el-alert v-if="sequenceError" :title="sequenceError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="sequence-save-error" />
    <div :inert="savingSequence" :aria-busy="savingSequence">
    <el-form label-width="90px" size="small" :disabled="savingSequence">
      <el-form-item :label="$t('controlView.k1be7ae')"><el-input v-model="sequenceForm.name" maxlength="100" data-test="sequence-name" /></el-form-item>
      <el-form-item :label="$t('controlView.k17fc93')"><el-select v-model="sequenceForm.areaId" style="width: 200px" data-test="sequence-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select>
        <el-checkbox v-model="sequenceForm.dualApproval" style="margin-left: 16px">{{ $t('controlView.kf88a83') }}</el-checkbox></el-form-item>
      <el-form-item :label="$t('controlView.k26670d')"><el-input v-model="sequenceForm.description" maxlength="500" /></el-form-item>
    </el-form>
    <div class="toolbar">
      <el-radio-group v-model="editorMode" size="small" data-test="sequence-editor-mode"><el-radio-button value="flow">{{ $t('controlView.k3bffba') }}</el-radio-button><el-radio-button value="table">{{ $t('controlView.k150074') }}</el-radio-button></el-radio-group>
      <template v-if="editorMode === 'flow'">
        <el-dropdown trigger="click" @command="(type: ControlStepType) => addStep(type)"><el-button size="small" data-test="flow-add">{{ $t('controlView.k2517d1') }}</el-button>
          <template #dropdown><el-dropdown-menu><el-dropdown-item v-for="(label, key) in stepTypeLabels" :key="key" :command="key" :data-test="`flow-add-${key}`">{{ label }}</el-dropdown-item></el-dropdown-menu></template>
        </el-dropdown>
      </template>
      <span class="spacer" />
      <el-tag v-if="validation" size="small" :type="validation.ok ? 'success' : 'danger'" data-test="sequence-validation">{{ validation.ok ? $t('controlView.k7035f8') : validation.message }}</el-tag>
    </div>
    <div v-if="editorMode === 'flow'" class="flow-editor">
      <SequenceFlow v-model="selectedStepId" :steps="sequenceForm.steps" :editable="!savingSequence" :error-ids="validation?.stepIds ?? []" :describe="describeStep" :height="520" style="flex: 1; min-width: 0" @changed="scheduleValidate()" />
      <div class="flow-props" data-test="flow-props">
        <template v-if="selectedStep">
          <el-form label-width="76px" size="small">
            <el-form-item :label="$t('controlView.k9f42da')"><el-input :model-value="selectedStep.id ?? ''" data-test="prop-id" @change="(v: string) => renameStep(selectedStep!, v)" /></el-form-item>
            <el-form-item :label="$t('controlView.ke4e46c')"><el-select v-model="selectedStep.type" data-test="prop-type" @change="scheduleValidate()"><el-option v-for="(label, key) in stepTypeLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item>
            <el-form-item :label="$t('controlView.k07d0b5')"><el-input v-model="selectedStep.name" data-test="prop-name" /></el-form-item>
            <template v-if="selectedStep.type === 'write'">
              <el-form-item :label="$t('controlView.kc570f0')"><el-select v-model="selectedStep.tagId" filterable data-test="prop-tag" @change="scheduleValidate()"><el-option v-for="t in configurableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k5ae08a')"><el-input-number v-model="selectedStep.value" :controls="false" data-test="prop-value" @change="scheduleValidate()" /></el-form-item>
              <el-form-item :label="$t('controlView.k04d7bd')"><el-input v-model="selectedStep.confirmExpression" :placeholder="$t('controlView.kc64017')" @change="scheduleValidate()" /></el-form-item>
            </template>
            <el-form-item v-if="selectedStep.type === 'wait' || selectedStep.type === 'condition'" :label="$t('controlView.k72f1cf')"><el-input v-model="selectedStep.expression" :placeholder="$t('controlView.kc135ba')" data-test="prop-expression" @change="scheduleValidate()" /></el-form-item>
            <template v-if="selectedStep.type === 'condition'">
              <el-form-item :label="$t('controlView.kd90926')"><el-select v-model="selectedStep.then" clearable @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k640232')"><el-select v-model="selectedStep.else" clearable :placeholder="$t('controlView.kea0ef2')" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            </template>
            <template v-if="selectedStep.type === 'parallel'">
              <el-form-item :label="$t('controlView.k2f6f9d')"><el-select v-model="selectedStep.branches" multiple @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k871c34')"><el-select v-model="selectedStep.join" @change="scheduleValidate()"><el-option v-for="id in joinIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            </template>
            <el-form-item v-if="selectedStep.type === 'join'" :label="$t('controlView.k2607fc')"><el-radio-group v-model="selectedStep.joinMode" @change="scheduleValidate()"><el-radio-button value="all">{{ $t('controlView.k778fc8') }}</el-radio-button><el-radio-button value="any">{{ $t('controlView.k31af9a') }}</el-radio-button></el-radio-group></el-form-item>
            <template v-if="selectedStep.type === 'jump'">
              <el-form-item :label="$t('controlView.kfb25a9')"><el-select v-model="selectedStep.target" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item :label="$t('controlView.k046305')"><el-input-number v-model="selectedStep.maxIterations" :min="1" :max="1000" data-test="prop-max" @change="scheduleValidate()" /></el-form-item>
            </template>
            <el-form-item v-if="['write', 'wait', 'join'].includes(selectedStep.type ?? 'write')" :label="$t('controlView.kea0ef2')"><el-select v-model="selectedStep.next" clearable :placeholder="$t('controlView.k25bc02')" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            <el-form-item v-if="['write', 'wait'].includes(selectedStep.type ?? 'write')" :label="$t('controlView.k16436e')"><el-input-number v-model="selectedStep.timeoutMs" :min="100" :max="600000" :step="1000" data-test="prop-timeout" @change="scheduleValidate()" /></el-form-item>
            <el-form-item v-if="!['end', 'join'].includes(selectedStep.type ?? 'write')" :label="$t('controlView.kb05bd8')">
              <el-select v-model="selectedStep.onFailure" clearable :placeholder="$t('controlView.ka17f70')" style="width: 90px" data-test="prop-failure" @change="scheduleValidate()"><el-option value="stop" :label="$t('controlView.ka17f70')" /><el-option value="continue" :label="$t('controlView.k1fc1af')" /><el-option value="goto" :label="$t('controlView.kfb25a9')" /></el-select>
              <el-select v-if="selectedStep.onFailure === 'goto'" v-model="selectedStep.onFailureGoto" style="width: 90px" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
            </el-form-item>
          </el-form>
          <el-button size="small" type="danger" data-test="prop-delete" @click="deleteStep(selectedStep!)">{{ $t('controlView.k3a4b3d') }}</el-button>
        </template>
        <p v-else class="tiny muted">{{ $t('controlView.kb0349e') }}</p>
      </div>
    </div>
    <el-table v-else :data="sequenceForm.steps" size="small" data-test="sequence-steps">
      <el-table-column :label="$t('controlView.k9f42da')" width="90"><template #default="{ row }"><el-input v-model="row.id" size="small" /></template></el-table-column>
      <el-table-column :label="$t('controlView.ke4e46c')" width="105"><template #default="{ row }"><el-select v-model="row.type" size="small" data-test="step-type"><el-option v-for="(label, key) in stepTypeLabels" :key="key" :value="key" :label="label" /></el-select></template></el-table-column>
      <el-table-column :label="$t('controlView.k07d0b5')" width="120"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column :label="$t('controlView.k163aec')" min-width="330"><template #default="{ row }">
        <div v-if="row.type === 'write'" class="step-fields">
          <el-select v-model="row.tagId" filterable size="small" style="width: 170px" :placeholder="$t('controlView.kc570f0')"><el-option v-for="t in configurableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select>
          ← <el-input-number v-model="row.value" :controls="false" size="small" style="width: 70px" />
          <el-input v-model="row.confirmExpression" size="small" :placeholder="$t('controlView.k0ce1d5')" style="width: 170px" />
        </div>
        <div v-else-if="row.type === 'wait'" class="step-fields"><el-input v-model="row.expression" size="small" :placeholder="$t('controlView.kd41546')" style="width: 260px" /></div>
        <div v-else-if="row.type === 'condition'" class="step-fields">
          <el-input v-model="row.expression" size="small" :placeholder="$t('controlView.k2be2e1')" style="width: 150px" data-test="step-expression" />
          {{ $t('controlView.kd90926') }}<el-select v-model="row.then" size="small" style="width: 90px" data-test="step-then"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          {{ $t('controlView.k640232') }}<el-select v-model="row.else" size="small" clearable :placeholder="$t('controlView.kea0ef2')" style="width: 90px" data-test="step-else"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
        </div>
        <div v-else-if="row.type === 'parallel'" class="step-fields">
          {{ $t('controlView.k2f6f9d') }}<el-select v-model="row.branches" multiple size="small" style="width: 170px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          {{ $t('controlView.k871c34') }}<el-select v-model="row.join" size="small" style="width: 90px"><el-option v-for="id in joinIds" :key="id" :value="id" :label="id" /></el-select>
        </div>
        <div v-else-if="row.type === 'join'" class="step-fields"><el-radio-group v-model="row.joinMode" size="small"><el-radio-button value="all">{{ $t('controlView.k79236c') }}</el-radio-button><el-radio-button value="any">{{ $t('controlView.kf690a1') }}</el-radio-button></el-radio-group></div>
        <div v-else-if="row.type === 'jump'" class="step-fields">
          {{ $t('controlView.kfb25a9') }}<el-select v-model="row.target" size="small" style="width: 90px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          {{ $t('controlView.k4c5dfc') }}<el-input-number v-model="row.maxIterations" :min="1" :max="1000" size="small" style="width: 90px" />{{ $t('controlView.k5e5b81') }}
        </div>
        <span v-else class="tiny muted">{{ $t('controlView.k4f4393') }}</span>
      </template></el-table-column>
      <el-table-column :label="$t('controlView.kea0ef2')" width="95"><template #default="{ row }"><el-select v-if="['write', 'wait', 'join'].includes(row.type)" v-model="row.next" size="small" clearable :placeholder="$t('controlView.k25bc02')"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></template></el-table-column>
      <el-table-column :label="$t('controlView.k16436e')" width="90"><template #default="{ row }"><el-input-number v-if="['write', 'wait'].includes(row.type)" v-model="row.timeoutMs" :controls="false" size="small" style="width: 75px" /></template></el-table-column>
      <el-table-column :label="$t('controlView.kb05bd8')" width="170"><template #default="{ row }">
        <template v-if="!['end', 'join'].includes(row.type)">
          <el-select v-model="row.onFailure" size="small" style="width: 75px" clearable :placeholder="$t('controlView.ka17f70')"><el-option value="stop" :label="$t('controlView.ka17f70')" /><el-option value="continue" :label="$t('controlView.k1fc1af')" /><el-option value="goto" :label="$t('controlView.kfb25a9')" /></el-select>
          <el-select v-if="row.onFailure === 'goto'" v-model="row.onFailureGoto" size="small" style="width: 80px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
        </template>
      </template></el-table-column>
      <el-table-column label="" width="70"><template #default="{ $index }">
        <el-button size="small" link :disabled="$index === 0" @click="moveStep($index)">↑</el-button><el-button size="small" link type="danger" @click="sequenceForm.steps.splice($index, 1)">{{ $t('controlView.k38cadf') }}</el-button>
      </template></el-table-column>
    </el-table>
    <el-button v-if="editorMode === 'table'" size="small" style="margin-top: 8px" data-test="sequence-add-step" @click="addStep()">{{ $t('controlView.k926319') }}</el-button>
    <p class="tiny muted">{{ $t('controlView.k9ae2cd') }}</p>
    </div>
    <template #footer><el-button :disabled="savingSequence" @click="sequenceEditor.close()">{{ $t('controlView.k4d0b46') }}</el-button><el-button type="primary" :loading="savingSequence" data-test="sequence-save" @click="saveSequence()">{{ $t('controlView.kfadf24') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="runFlowVisible" :title="runFlow ? $t('controlView.k27f0ec', [runFlow.id, runFlow.sequenceName, runLabels[runFlow.status] ?? runFlow.status]) : ''" width="1000px" data-test="run-flow-dialog" @opened="fitRunFlow">
    <template v-if="runFlow && runFlowSteps">
      <div v-if="runFlowFailure" class="run-failure" :class="runFlow.status === 'Failed' ? 'is-failed' : 'is-aborted'" data-test="run-failure">
        <div class="run-failure-head">
          <el-tag size="small" :type="runType[runFlow.status] ?? 'info'">{{ runLabels[runFlow.status] ?? runFlow.status }}</el-tag>
          <strong data-test="run-failure-headline">{{ runFlowFailure.failedSteps.length ? $t('controlUx.runFailedAt', [runFlowFailure.failedSteps[0].id, runFlowFailure.failedSteps[0].name]) : $t('controlUx.runAbortedHeadline') }}</strong>
          <span v-if="runFlowFailure.executedIds.length" class="tiny" data-test="run-failure-executed">{{ $t('controlUx.runExecutedCount', [runFlowFailure.executedIds.length, runFlowFailure.executedIds.join('、')]) }}</span>
          <span v-if="runFlowFailure.notExecutedIds.length" class="tiny muted">{{ $t('controlUx.runNotExecutedCount', [runFlowFailure.notExecutedIds.length, runFlowFailure.notExecutedIds.join('、')]) }}</span>
        </div>
        <p class="run-failure-reason" data-test="run-failure-reason">{{ runFlowFailure.reason ? $t('controlUx.runReason', [runFlowFailure.reason]) : $t('controlUx.runReasonUnknown') }}</p>
        <p class="run-failure-advice" data-test="run-failure-advice">{{ $t(runFlow.status === 'Aborted' ? 'controlUx.runAdviceAborted' : 'controlUx.runAdviceFailed') }}</p>
      </div>
      <!-- 有摘要块时把画布收窄，总高度与原来持平，避免流程图被挤出可视区 -->
      <SequenceFlow ref="runFlowRef" :steps="runFlowSteps" :results="runFlow.steps" :describe="describeStep" :height="runFlowFailure ? 440 : 520" />
      <p v-if="runFlowRevisionChanged" class="tiny muted">{{ $t('controlView.k00bc51') }}</p>
    </template>
    <p v-else class="muted">{{ $t('controlView.kf65452') }}</p>
    <template #footer>
      <el-button @click="runFlowVisible = false">{{ $t('controlUx.close') }}</el-button>
      <el-button v-if="runFlowFailure && runFlowSequence && canControlArea(runFlow!.areaId)" type="primary" data-test="run-rerun" @click="rerunRun(runFlow!)">{{ $t('controlUx.rerun') }}</el-button>
    </template>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { InfoFilled } from '@element-plus/icons-vue'
import { areaApi, deviceApi, tagApi } from '../api'
import { controlApi, type ControlLockout, type ControlPoint, type ControlRequest, type ControlRun, type ControlSequence, type ControlStep, type ControlStepType, type DeviceControlState } from '../api/control'
import { commandErrorMessage, continueFrom, errorMessage } from '../api/http'
import type { RecipeDownload } from '../api/recipes'
import { formatTime } from '../utils'
import { can } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import SequenceFlow from '../components/sequence/SequenceFlow.vue'
import { usePolling } from '../composables/usePolling'
import { useEditDialog } from '../composables/useEditDialog'
import { tr } from '../i18n'
import { registerShortcuts } from '../shortcuts/registry'
import { modalOpen } from '../ui/overlay'

interface NamedDevice { id: number; name: string; areaId?: number | null }
interface NamedTag { id: number; name: string; deviceId: number | null; writable: boolean; kind?: string }
const auth = useAuthStore()
const canConfigureArea = (areaId?: number | null) => can(auth.user, 'Configure', areaId)
const canControlArea = (areaId?: number | null) => can(auth.user, 'Control', areaId)
function deviceArea(deviceId?: number | null): number | null | undefined {
  return devices.value.find(x => x.id === deviceId)?.areaId
}
function tagArea(tagId?: number | null): number | null | undefined {
  const tag = tags.value.find(x => x.id === tagId)
  return tag ? deviceArea(tag.deviceId) : undefined
}
const canConfigureDevice = (id: number) => canConfigureArea(deviceArea(id))
const canControlDevice = (id: number) => canControlArea(deviceArea(id))
const canConfigureTag = (id?: number | null) => canConfigureArea(tagArea(id))
const canControlTag = (id?: number | null) => canControlArea(tagArea(id))
function canRemoveLock(row: ControlLockout): boolean {
  const area = row.deviceId != null ? deviceArea(row.deviceId) : tagArea(row.tagId)
  if (canConfigureArea(area)) return true
  return canControlArea(area) && row.placedByUserId != null && row.placedByUserId === auth.user?.id
}
const controlTabs = ['requests', 'lockouts', 'modes', 'sequences', 'points'] as const
type ControlTab = typeof controlTabs[number]
const route = useRoute()
const router = useRouter()
function tabFromQuery(value: unknown): ControlTab {
  return typeof value === 'string' && (controlTabs as readonly string[]).includes(value) ? value as ControlTab : 'requests'
}
const tab = ref<ControlTab>(tabFromQuery(route.query.tab))
const focusedKey = ref<string | null>(null)
watch(() => route.query.tab, value => {
  const next = tabFromQuery(value)
  if (tab.value !== next) tab.value = next
})
watch(tab, value => {
  focusedKey.value = null
  const query = { ...route.query }
  if (value === 'requests') delete query.tab
  else query.tab = value
  if ((route.query.tab ?? undefined) === (query.tab ?? undefined)) return
  void router.replace({ query })
})
const devices = ref<NamedDevice[]>([]), tags = ref<NamedTag[]>([]), areas = ref<{ id: number; name: string }[]>([])
const configurableAreas = computed(() => areas.value.filter(a => canConfigureArea(a.id)))
type ControlRequestRow = { request: ControlRequest; sequenceName: string | null; recipe?: RecipeDownload | null; mine: boolean }
const requests = ref<ControlRequestRow[]>([])
const loadError = ref(''), areasError = ref(''), requestsError = ref(''), runsError = ref(''), actionError = ref('')
const pointError = ref(''), sequenceError = ref('')
const lockouts = ref<ControlLockout[]>([]), lockoutsMore = ref(false), lockoutsNext = ref<number | null>(null), lockoutsLoadingOlder = ref(false)
const modes = ref<DeviceControlState[]>([]), points = ref<ControlPoint[]>([])
const modesReady = ref(false), modesError = ref(''), changingModes = reactive(new Set<number>())
let disposed = false, modesSeq = 0
const sequences = ref<ControlSequence[]>([])
const latestRuns = ref<ControlRun[]>([]), olderRuns = ref<ControlRun[]>([]), runsMore = ref(false), runsNext = ref<number | null>(null), runsLoadingOlder = ref(false)
const runs = computed(() => {
  const seen = new Set<number>()
  return [...latestRuns.value, ...olderRuns.value].filter(row => seen.has(row.id) ? false : (seen.add(row.id), true))
})
const showRemoved = ref(false)
const pending = computed(() => requests.value.filter(x => requestStatus(x.request) === 'Pending'))
const orderedRequests = computed(() => {
  const waiting = requests.value.filter(row => requestStatus(row.request) === 'Pending')
    .slice()
    .sort((a, b) => a.request.expiresTs - b.request.expiresTs || b.request.requestedTs - a.request.requestedTs)
  const rest = requests.value.filter(row => requestStatus(row.request) !== 'Pending')
  return [...waiting, ...rest]
})
const deciding = reactive(new Set<string>())

// 双人复核有有效期（expiresTs），此前前端完全没展示：审批人不知道还剩多久
const now = ref(Date.now())
function requestStatus(request: ControlRequest): string { return request.status === 'Pending' && request.expiresTs <= now.value ? 'Expired' : request.status }
function requestIsCurrent(id: string): boolean {
  const request = requests.value.find(row => row.request.id === id)?.request
  return !!request && request.status === 'Pending' && request.expiresTs > Date.now()
}
function remaining(expiresTs: number): string {
  const ms = expiresTs - now.value
  if (ms <= 0) return tr('controlView.expired')
  const seconds = Math.floor(ms / 1000)
  if (seconds < 60) return tr('controlView.remainingSec', [seconds])
  return tr('controlView.remainingMin', [Math.floor(seconds / 60), seconds % 60])
}
function expiringSoon(expiresTs: number): boolean {
  return expiresTs - now.value <= 60_000
}

// 审批弹窗里要能看见「在批什么」，否则模态框遮住表格后只能盲批
function describeRequest(row: ControlRequestRow): string {
  const r = row.request
  if (r.kind === 'command') return `${tagName(r.tagId)} ← ${r.value ?? ''}`
  if (r.kind === 'recipe') return `${row.recipe?.recipeName ?? ''} v${row.recipe?.version ?? ''}`
  return row.sequenceName ?? ''
}
const writableTags = computed(() => tags.value.filter(x => x.writable && x.deviceId != null))
const configurableTags = computed(() => writableTags.value.filter(t => canConfigureTag(t.id)))
const controllableTags = computed(() => writableTags.value.filter(t => canControlTag(t.id)))
const controllableDevices = computed(() => devices.value.filter(d => canControlDevice(d.id)))
const statusLabels: Record<string, string> = { Pending: tr('controlView.k4607ba'), Executed: tr('controlView.kab1f36'), Failed: tr('controlView.k3e3c80'), Rejected: tr('controlView.ke944e4'), Expired: tr('controlView.k411ebf'), Cancelled: tr('controlView.kc05f5f') }
const statusType: Record<string, string> = { Pending: 'warning', Executed: 'success', Failed: 'danger', Rejected: 'info', Expired: 'info', Cancelled: 'info' }
const runLabels: Record<string, string> = { Running: tr('controlView.k594249'), Completed: tr('controlView.k33246f'), Failed: tr('controlView.k3e3c80'), Aborted: tr('controlView.k5c1747') }
const runType: Record<string, string> = { Running: 'warning', Completed: 'success', Failed: 'danger', Aborted: 'info' }
const stepLabels: Record<string, string> = { pending: tr('controlView.k25d23b'), running: tr('controlView.k1f425b'), done: tr('controlView.k33246f'), failed: tr('controlView.k3e3c80'), skipped: tr('controlView.k31a985') }
const stepType: Record<string, string> = { running: 'warning', done: 'success', failed: 'danger' }
const deviceName = (id: number | null) => devices.value.find(x => x.id === id)?.name ?? `#${id}`
function tagName(id: number | null): string {
  const t = tags.value.find(x => x.id === id); if (!t) return `#${id}`
  return t.deviceId ? `${deviceName(t.deviceId)}/${t.name}` : t.name
}
const modeOf = (deviceId: number) => modes.value.find(x => x.deviceId === deviceId)
function pointSummary(p: ControlPoint): string {
  const parts: string[] = []
  if (p.requireConfirm) parts.push(tr('controlView.k5930b5')); if (p.dualApproval) parts.push(tr('controlView.ke933e1', [p.approvalTimeoutSec]))
  if (p.limitMin != null || p.limitMax != null) parts.push(tr('controlView.k0ddda5', [p.limitMin ?? '-', p.limitMax ?? '-']))
  if (p.maxStep != null) parts.push(tr('controlView.kf1ec42', [p.maxStep])); if (p.permitExpression) parts.push(tr('controlView.kd9028d', [p.permitExpression]))
  if (p.feedbackExpression) parts.push(tr('controlView.k51c036', [p.feedbackExpression, p.feedbackTimeoutMs]))
  return parts.join('；') || tr('controlView.kb693a7')
}

async function guard(action: () => Promise<unknown>, fallback: string, target: { value: string }): Promise<boolean> {
  target.value = ''
  try { await action(); return true } catch (e) { target.value = errorMessage(e, fallback); return false }
}
// 现场动作（启停顺控、挂牌/摘牌、就地远程切换、中止）的失败不能用「请稍后重试」：
// 超时和断网时无法判断动作是否已经生效，照着重试可能让顺控启动两次、或让人以为
// 挂牌已经挂上。commandErrorMessage 会明确说「结果未知，先核对」。
async function guardCommand(action: () => Promise<unknown>, fallback: string, target: { value: string }): Promise<boolean> {
  target.value = ''
  try { await action(); return true } catch (e) { target.value = commandErrorMessage(e, fallback); return false }
}
async function loadLockouts(): Promise<void> {
  const page = await controlApi.lockouts(showRemoved.value)
  lockouts.value = page.items
  const older = continueFrom(page, 500)
  lockoutsMore.value = older.more; lockoutsNext.value = older.next
}
async function loadOlderLockouts(): Promise<void> {
  if (lockoutsLoadingOlder.value || lockoutsNext.value == null) return
  lockoutsLoadingOlder.value = true
  try {
    const page = await controlApi.lockouts(showRemoved.value, lockoutsNext.value)
    const ids = new Set(lockouts.value.map(row => row.id))
    lockouts.value = [...lockouts.value, ...page.items.filter(row => !ids.has(row.id))]
    const older = continueFrom(page, 500)
    lockoutsMore.value = older.more; lockoutsNext.value = older.next
  } catch (e) { loadError.value = errorMessage(e, tr('designReview2.loadOlderFailed')) }
  finally { lockoutsLoadingOlder.value = false }
}
async function loadModes(): Promise<void> {
  const seq = ++modesSeq
  modesReady.value = false
  try {
    const result = await controlApi.modes()
    if (disposed || seq !== modesSeq) return
    modes.value = result; modesReady.value = true; modesError.value = ''
  } catch (e) {
    if (!disposed && seq === modesSeq) modesError.value = errorMessage(e, tr('operationsReview.modesFailed'))
  }
}
async function refresh(): Promise<void> {
  try {
    const [d, t, a] = await Promise.all([
      deviceApi.list(), tagApi.list(),
      areaApi.list().then(list => { areasError.value = ''; return list }).catch((error) => { areasError.value = errorMessage(error, tr('controlView.areasLoadFailed')); return areas.value })
    ])
    devices.value = d as unknown as NamedDevice[]; tags.value = t as unknown as NamedTag[]; areas.value = a as { id: number; name: string }[]
    const [requestRows, pointRows, sequenceRows, runPage] = await Promise.all([controlApi.requests(), controlApi.points(), controlApi.sequences(), controlApi.runs(), loadModes()])
    requests.value = requestRows; points.value = pointRows; sequences.value = sequenceRows
    latestRuns.value = runPage.items; olderRuns.value = []
    const older = continueFrom(runPage, 200); runsMore.value = older.more; runsNext.value = older.next
    await loadLockouts()
    loadError.value = ''; requestsError.value = ''; runsError.value = ''
  } catch (e) {
    loadError.value = errorMessage(e, tr('controlView.kf6b7a4'))
  }
}

async function decide(row: ControlRequestRow, approve: boolean): Promise<void> {
  const r = row.request
  if (disposed || deciding.has(r.id) || !requestIsCurrent(r.id) || (approve && row.mine)) return
  deciding.add(r.id)
  try {
    // 弹窗会遮住表格：把请求摘要与剩余有效期带进来，避免看不到详情只能盲批
    const summary = tr('controlView.requestSummary', [describeRequest(row), r.requestedBy, formatTime(r.requestedTs), remaining(r.expiresTs)])
    const prompt = approve ? `${summary} — ${tr('controlView.k72554c')}` : `${summary} — ${tr('controlView.ka2c8f8')}`
    let comment = ''
    try { comment = (await ElMessageBox.prompt(prompt, approve ? tr('controlView.kea39c9') : tr('controlView.k0bf71b'), { inputValue: '', confirmButtonText: approve ? tr('controlView.k62e26d') : tr('controlView.kf526c8') })).value ?? '' }
    catch { return }
    if (disposed) return
    if (!requestIsCurrent(r.id)) { actionError.value = tr('operationsReview.requestChanged'); return }
    // 批准即下发命令，超时同样要知道「可能已经执行了」
    await guardCommand(async () => {
      if (approve) {
        const result = await controlApi.approve(r.id, comment)
        if (result.runId) ElMessage.success(tr('controlView.kdca8dd', [result.runId]))
        else if (r.kind === 'recipe') ElMessage.success(result.request.resultMessage ?? tr('controlView.k6ea386'))
        else if (result.result?.success) ElMessage.success(tr('controlView.k82a465', [result.result.readBack]))
        else actionError.value = result.result?.message ?? result.request.resultMessage ?? tr('controlView.k9746cf')
      } else await controlApi.reject(r.id, comment)
    }, tr('controlView.k09e424'), actionError)
    await refresh()
  } finally { deciding.delete(r.id) }
}

const lockForm = reactive({ target: '', kind: 'NoOperate', reason: '' })
async function placeLock(): Promise<void> {
  const id = Number(lockForm.target.slice(1))
  if (await guardCommand(() => controlApi.placeLockout({ deviceId: lockForm.target.startsWith('d') ? id : null, tagId: lockForm.target.startsWith('t') ? id : null, kind: lockForm.kind, reason: lockForm.reason.trim() }), tr('controlView.k56fa45'), actionError)) {
    ElMessage.success(tr('controlView.k127732')); lockForm.reason = ''; await loadLockouts()
  }
}
async function removeLock(row: ControlLockout): Promise<void> {
  let reason = ''
  try { reason = (await ElMessageBox.prompt(tr('controlView.k1c8743'), tr('controlView.k0e3083'), { inputPattern: /\S/, inputErrorMessage: tr('controlView.k44d98b') })).value }
  catch { return }
  if (await guardCommand(() => controlApi.removeLockout(row.id, reason), tr('controlView.k40edec'), actionError)) { ElMessage.success(tr('controlView.kb0dfde')); await loadLockouts() }
}
async function toggleMode(deviceId: number): Promise<void> {
  if (!modesReady.value || changingModes.has(deviceId) || disposed) return
  changingModes.add(deviceId)
  const next = modeOf(deviceId)?.mode === 'Local' ? 'Remote' : 'Local'
  try {
    let reason = ''
    try { reason = (await ElMessageBox.prompt(tr('controlView.kf81167', [deviceName(deviceId), next === 'Local' ? tr('controlView.k03d4f7') : tr('controlView.k801947')]), tr('controlView.k8010de'), { inputPattern: /\S/, inputErrorMessage: tr('controlView.k18169c') })).value }
    catch { return }
    if (disposed || !modesReady.value) return
    if (await guardCommand(() => controlApi.setMode(deviceId, next, reason), tr('controlView.k9e2fdc'), actionError) && !disposed) { ElMessage.success(tr('controlView.k1dfbf8')); await loadModes() }
  } finally { changingModes.delete(deviceId) }
}

const pointVisible = ref(false), savingPoint = ref(false), pointTag = ref<number | null>(null), pointForm = ref<ControlPoint | null>(null)
const optionalText = (value: string | null | undefined): string | null => value === '' || value == null ? null : value
const pointEditor = useEditDialog({ visible: pointVisible, saving: savingPoint, snapshot: () => JSON.stringify(pointForm.value && {
  ...pointForm.value,
  permitExpression: optionalText(pointForm.value.permitExpression),
  permitMessage: optionalText(pointForm.value.permitMessage),
  feedbackExpression: optionalText(pointForm.value.feedbackExpression)
}), save: savePoint })
function editPoint(tagId: number): void {
  if (savingPoint.value) return
  const existing = points.value.find(x => x.tagId === tagId)
  pointError.value = ''
  pointForm.value = existing ? { ...existing } : { tagId, requireConfirm: true, dualApproval: false, approvalTimeoutSec: 300, permitExpression: null, permitMessage: null, limitMin: null, limitMax: null, maxStep: null, feedbackExpression: null, feedbackTimeoutMs: 10000 }
  pointVisible.value = true
}
async function savePoint(): Promise<void> {
  if (!pointForm.value || savingPoint.value || disposed || !pointVisible.value) return
  const payload = { ...pointForm.value }
  savingPoint.value = true
  try {
    if (await guard(() => controlApi.savePoint(payload), tr('controlView.k40525a'), pointError) && !disposed) {
      ElMessage.success(tr('controlView.kcdfab9')); pointVisible.value = false
      await guard(async () => { points.value = await controlApi.points() }, tr('controlView.kf6b7a4'), loadError)
    }
  } finally { savingPoint.value = false }
}
async function removePoint(tagId: number): Promise<void> {
  try { await ElMessageBox.confirm(tr('controlView.k71549f', [tagName(tagId)]), tr('controlView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => controlApi.removePoint(tagId), tr('controlView.k72250c'), actionError)) points.value = await controlApi.points()
}

const stepTypeLabels: Record<string, string> = { write: tr('controlView.kc570f0'), wait: tr('controlView.k25d23b'), condition: tr('controlView.k315a7e'), parallel: tr('controlView.k090e93'), join: tr('controlView.k871c34'), jump: tr('controlView.k817b2f'), end: tr('controlView.k76b988') }
const stepIds = computed(() => sequenceForm.steps.map(x => x.id).filter((x): x is string => !!x))
const joinIds = computed(() => sequenceForm.steps.filter(x => x.type === 'join').map(x => x.id).filter((x): x is string => !!x))
function addStep(type: ControlStepType = 'write'): void {
  const used = new Set(stepIds.value); let n = sequenceForm.steps.length + 1; while (used.has(`s${n}`)) n++
  const step: ControlStep = { id: `s${n}`, type, name: type === 'write' ? tr('controlView.k182817', [n]) : `${stepTypeLabels[type]}${n}`, tagId: configurableTags.value[0]?.id ?? 0, value: 0, confirmExpression: null, timeoutMs: 10000, delayMs: 0, joinMode: 'all', maxIterations: 3 }
  // 图形模式下新节点放在现有节点下方，避免与自动布局重叠。
  if (editorMode.value === 'flow') { const ys = sequenceForm.steps.map(s => s.y ?? 0); step.x = 20; step.y = (ys.length ? Math.max(...ys) : 0) + 112 }
  sequenceForm.steps.push(step); selectedStepId.value = step.id!; scheduleValidate()
}
const editorMode = ref<'flow' | 'table'>('flow')
const selectedStepId = ref<string | null>(null)
const selectedStep = computed(() => sequenceForm.steps.find(s => s.id === selectedStepId.value) ?? null)
const validation = ref<{ ok: boolean; message: string | null; stepIds: string[] } | null>(null)
let validateTimer: number | undefined
function scheduleValidate(): void {
  window.clearTimeout(validateTimer)
  validateTimer = window.setTimeout(async () => {
    if (!sequenceForm.steps.length) { validation.value = null; return }
    try { validation.value = await controlApi.validateSequence({ ...sequenceForm, name: sequenceForm.name || 'x', steps: cleanSteps() }) } catch { /* 校验接口失败时不阻断编辑，保存时后端仍会校验 */ }
  }, 300)
}
// 改编号时同步改写其他步骤对它的引用，避免断链。
function renameStep(step: ControlStep, value: string): void {
  const old = step.id, id = value.trim(); if (!id || id === old) return
  if (sequenceForm.steps.some(s => s.id === id)) { sequenceError.value = tr('controlView.kd6486a', [id]); return }
  step.id = id
  for (const s of sequenceForm.steps) {
    for (const k of ['next', 'then', 'else', 'join', 'target', 'onFailureGoto'] as const) if (s[k] === old) s[k] = id
    if (s.branches) s.branches = s.branches.map(b => (b === old ? id : b))
  }
  selectedStepId.value = id; scheduleValidate()
}
function deleteStep(step: ControlStep): void {
  const i = sequenceForm.steps.indexOf(step); if (i < 0) return
  sequenceForm.steps.splice(i, 1)
  for (const s of sequenceForm.steps) {
    for (const k of ['next', 'then', 'else', 'join', 'target'] as const) if (s[k] === step.id) s[k] = null
    if (s.onFailureGoto === step.id) { s.onFailureGoto = null; s.onFailure = null }
    if (s.branches) s.branches = s.branches.filter(b => b !== step.id)
  }
  selectedStepId.value = null; scheduleValidate()
}
const runFlowVisible = ref(false), runFlowId = ref<number | null>(null)
const runFlow = computed(() => runs.value.find(r => r.id === runFlowId.value) ?? null)
const runFlowSequence = computed(() => sequences.value.find(s => s.id === runFlow.value?.sequenceId) ?? null)
const runFlowSteps = computed(() => runFlowSequence.value?.steps ?? null)
const runFlowRevisionChanged = computed(() => !!runFlow.value && !!runFlowSequence.value && runFlowSequence.value.updatedTs > runFlow.value.startedTs)
// 失败/中止的运行，光看节点图看不出「哪一步失败、为什么、现场动到哪一步了」。
// 这里把操作员真正要判断的三件事算出来：失败原因、已执行（现场已被改变）、未执行。
const runFlowFailure = computed(() => {
  const run = runFlow.value
  if (!run || (run.status !== 'Failed' && run.status !== 'Aborted')) return null
  const stepIds = (list: typeof run.steps) => list.map(s => s.stepId ?? `#${s.index}`)
  const failed = run.steps.filter(s => s.status === 'failed')
  const executed = run.steps.filter(s => s.status === 'done')
  const notExecuted = run.steps.filter(s => s.status === 'pending' || s.status === 'skipped')
  const unknown = run.steps.some(s => s.status === 'running')
  const reason = run.message ?? failed.find(s => s.message)?.message ?? ''
  return {
    failedSteps: failed.map(s => ({ id: s.stepId ?? `#${s.index}`, name: s.name, message: s.message })),
    executedIds: stepIds(executed),
    notExecutedIds: stepIds(notExecuted),
    reason,
    unknown
  }
})
function openRunFlow(row: ControlRun): void { runFlowId.value = row.id; runFlowVisible.value = true }
// 弹窗里的画布首次渲染时容器还没有尺寸，组件内那次 fit 会落空，等打开动画结束再处理一次。
// 失败/中止的运行直接定位到出错那一步（保持 100%），其余情况整览。
const runFlowRef = ref<{ fit: (options?: { padding?: number; minZoom?: number }) => void; focusNode: (id: string) => void } | null>(null)
function fitRunFlow(): void {
  const failedId = runFlowFailure.value?.failedSteps[0]?.id
  if (failedId) runFlowRef.value?.focusNode(failedId)
  else runFlowRef.value?.fit({ padding: 0.1, minZoom: 0.8 })
}
// 失败后重跑：直接复用「执行」的确认流程，确认框里列出的是当前定义，不是当时那一版
function rerunRun(row: ControlRun): void {
  const sequence = sequences.value.find(s => s.id === row.sequenceId)
  if (sequence) void startSequence(sequence)
}
function moveStep(i: number): void { const [s] = sequenceForm.steps.splice(i, 1); sequenceForm.steps.splice(i - 1, 0, s!) }
const sequenceVisible = ref(false), savingSequence = ref(false)
const sequenceForm = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; steps: ControlStep[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, steps: [] })
const sequenceEditor = useEditDialog({ visible: sequenceVisible, saving: savingSequence, snapshot: () => JSON.stringify({
  ...sequenceForm,
  description: optionalText(sequenceForm.description),
  steps: sequenceForm.steps.map(step => ({
    ...step,
    confirmExpression: optionalText(step.confirmExpression), expression: optionalText(step.expression),
    next: optionalText(step.next), then: optionalText(step.then), else: optionalText(step.else),
    join: optionalText(step.join), target: optionalText(step.target),
    onFailure: optionalText(step.onFailure), onFailureGoto: optionalText(step.onFailureGoto)
  }))
}), save: saveSequence })
function editSequence(row?: ControlSequence): void {
  if (savingSequence.value) return
  const areaId = configurableAreas.value[0]?.id
  if (!row && areaId == null) return
  Object.assign(sequenceForm, row ? { id: row.id, name: row.name, areaId: row.areaId, description: row.description, dualApproval: row.dualApproval, revision: row.revision, steps: JSON.parse(JSON.stringify(row.steps)) }
    : { id: undefined, name: '', areaId, description: null, dualApproval: false, revision: 0, steps: [] })
  selectedStepId.value = null; validation.value = null; sequenceError.value = ''; sequenceVisible.value = true
  if (row) scheduleValidate()
}
function cleanSteps(): ControlStep[] {
  return sequenceForm.steps.map(s => ({ ...s, confirmExpression: s.confirmExpression || null, expression: s.expression || null, next: s.next || null, else: s.else || null, onFailure: s.onFailure || null, onFailureGoto: s.onFailure === 'goto' ? s.onFailureGoto || null : null, tagId: (s.type ?? 'write') === 'write' ? s.tagId : null }))
}
async function saveSequence(): Promise<void> {
  if (savingSequence.value || disposed || !sequenceVisible.value) return
  const payload = JSON.parse(JSON.stringify({ ...sequenceForm, steps: cleanSteps() }))
  savingSequence.value = true
  try {
    if (await guard(() => controlApi.saveSequence(payload), tr('controlView.k40525a'), sequenceError) && !disposed) {
      ElMessage.success(tr('controlView.kcdfab9')); sequenceVisible.value = false
      await guard(async () => { sequences.value = await controlApi.sequences() }, tr('controlView.kf6b7a4'), loadError)
    }
  } finally { savingSequence.value = false }
}
async function removeSequence(row: ControlSequence): Promise<void> {
  try { await ElMessageBox.confirm(tr('controlView.ke80fda', [row.name]), tr('controlView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guard(() => controlApi.removeSequence(row.id), tr('controlView.k72250c'), actionError)) sequences.value = await controlApi.sequences()
}
function describeStep(s: ControlStep): string {
  switch (s.type ?? 'write') {
    case 'write': return tr('controlView.k05884f', [tagName(s.tagId), s.value, s.confirmExpression ? tr('controlView.k6ea817', [s.confirmExpression]) : ''])
    case 'wait': return tr('controlView.k1b9683', [s.expression])
    case 'condition': return tr('controlView.k7c6fbe', [s.expression, s.then, s.else ?? tr('controlView.kea0ef2')])
    case 'parallel': return tr('controlView.k4e03cc', [(s.branches ?? []).join('、'), s.join])
    case 'join': return tr('controlView.k2b87ac', [s.joinMode === 'any' ? tr('controlView.k31af9a') : tr('controlView.k778fc8')])
    case 'jump': return tr('controlView.ka4f091', [s.target, s.maxIterations])
    default: return tr('controlView.k76b988')
  }
}
function stepsSummary(steps: ControlStep[]): string { return steps.map(s => `${s.id}.${s.name}${s.type && s.type !== 'write' ? `[${stepTypeLabels[s.type] ?? s.type}]` : ''}`).join(' → ') }
async function startSequence(row: ControlSequence): Promise<void> {
  const text = row.steps.map(s => `${s.id}. ${s.name}：${describeStep(s)}`).join('；')
  try { await ElMessageBox.confirm(text, tr('controlView.k69fc97', [row.name, row.dualApproval ? tr('controlView.kdc05b6') : '']), { type: 'warning', confirmButtonText: row.dualApproval ? tr('controlView.k61385c') : tr('controlView.kae1109') }) } catch { return }
  await guardCommand(async () => {
    const result = await controlApi.start(row.id)
    if (result.pending) ElMessage.warning(tr('controlView.k480392')); else ElMessage.success(tr('controlView.k42a539', [result.runId]))
  }, tr('controlView.k65525f'), actionError)
  await refresh()
}
async function loadOlderRuns(): Promise<void> {
  if (runsLoadingOlder.value || runsNext.value == null) return
  runsLoadingOlder.value = true
  try {
    const page = await controlApi.runs(undefined, runsNext.value)
    const ids = new Set(runs.value.map(row => row.id))
    olderRuns.value = [...olderRuns.value, ...page.items.filter(row => !ids.has(row.id))]
    const older = continueFrom(page, 200)
    runsMore.value = older.more; runsNext.value = older.next; runsError.value = ''
  } catch (e) { runsError.value = errorMessage(e, tr('designReview2.loadOlderFailed')) }
  finally { runsLoadingOlder.value = false }
}
async function abort(row: ControlRun): Promise<void> {
  try { await ElMessageBox.confirm(tr('controlView.k689787'), tr('controlView.kf11c45', [row.id]), { type: 'warning' }) } catch { return }
  await guardCommand(() => controlApi.abort(row.id), tr('controlView.k50cf66'), actionError); await refresh()
}

// 运行中的流程要秒级进度；没有运行项时只走本地时钟，不发请求。
// 例外：停在「顺控」页时每 5 秒查一次——运行可能是别人启动的，或在本页打开之后才开始的。
// 只在「本地已经有 Running」时才轮询，会让这些运行在本页彻底不可见，失败也不会冒出来。
let lastRunSync = 0
usePolling(async () => {
  now.value = Date.now()
  const running = runs.value.some(x => x.status === 'Running')
  if (!running && (tab.value !== 'sequences' || now.value - lastRunSync < 5000)) return
  lastRunSync = now.value
  try {
    const page = await controlApi.runs()
    latestRuns.value = page.items
    if (!olderRuns.value.length) { const older = continueFrom(page, 200); runsMore.value = older.more; runsNext.value = older.next }
    runsError.value = ''
  }
  catch (e) { runsError.value = errorMessage(e, tr('controlView.runsLoadFailed')) }
}, 1000)
// 待审批请求自动刷新：审批人盯着页面时不必反复手动点「刷新」
usePolling(async () => {
  if (tab.value !== 'requests') return
  try { requests.value = await controlApi.requests(); requestsError.value = '' }
  catch (e) { requestsError.value = errorMessage(e, tr('controlView.requestsLoadFailed')) }
}, 5000)
function requestRowClass({ row }: { row: ControlRequestRow }): string {
  return tab.value === 'requests' && focusedKey.value === row.request.id ? 'control-row-focused' : ''
}
function lockoutRowClass({ row }: { row: ControlLockout }): string {
  return tab.value === 'lockouts' && focusedKey.value === String(row.id) ? 'control-row-focused' : ''
}
function runRowClass({ row }: { row: ControlRun }): string {
  return tab.value === 'sequences' && focusedKey.value === `run:${row.id}` ? 'control-row-focused' : ''
}
function focusRows(): { key: string }[] {
  if (tab.value === 'requests') return orderedRequests.value.map(row => ({ key: row.request.id }))
  if (tab.value === 'lockouts') return lockouts.value.map(row => ({ key: String(row.id) }))
  if (tab.value === 'sequences') return runs.value.map(row => ({ key: `run:${row.id}` }))
  return []
}
function focusControlRow(index: number): void {
  const rows = focusRows()
  if (index < 0 || index >= rows.length) return
  focusedKey.value = rows[index].key
  void nextTick(() => document.querySelector('.control-row-focused')?.scrollIntoView({ block: 'nearest' }))
}
function moveControlFocus(delta: number): void {
  const rows = focusRows()
  if (!rows.length) return
  const index = rows.findIndex(row => row.key === focusedKey.value)
  if (index < 0) { focusControlRow(delta > 0 ? 0 : rows.length - 1); return }
  focusControlRow(Math.min(rows.length - 1, Math.max(0, index + delta)))
}
function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null
  return !!element && (element.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(element.tagName))
}
function onInteractiveElement(): boolean {
  const element = document.activeElement as HTMLElement | null
  return !!element && /^(BUTTON|A)$/.test(element.tagName)
}
function activateFocusedRow(): void {
  if (tab.value === 'requests') {
    const row = orderedRequests.value.find(item => item.request.id === focusedKey.value)
    if (row && requestStatus(row.request) === 'Pending') void decide(row, !row.mine)
    return
  }
  if (tab.value === 'lockouts') {
    const row = lockouts.value.find(item => String(item.id) === focusedKey.value)
    if (row && !row.removedTs && canRemoveLock(row)) void removeLock(row)
    return
  }
  if (tab.value === 'sequences') {
    const row = runs.value.find(item => `run:${item.id}` === focusedKey.value)
    if (row) openRunFlow(row)
  }
}
function onControlKeyboard(event: KeyboardEvent): void {
  if (disposed || event.defaultPrevented || event.ctrlKey || event.metaKey || event.altKey) return
  if (tab.value !== 'requests' && tab.value !== 'lockouts' && tab.value !== 'sequences') return
  if (isTypingTarget(event.target) || modalOpen()) return
  if (event.key === 'ArrowDown' || event.key === 'j' || event.key === 'J') { event.preventDefault(); moveControlFocus(1); return }
  if (event.key === 'ArrowUp' || event.key === 'k' || event.key === 'K') { event.preventDefault(); moveControlFocus(-1); return }
  if (event.key === 'Home') { event.preventDefault(); focusControlRow(0); return }
  if (event.key === 'End') { event.preventDefault(); focusControlRow(focusRows().length - 1); return }
  if (event.key === 'Escape') {
    if (focusedKey.value !== null) { event.preventDefault(); focusedKey.value = null }
    return
  }
  if (!focusedKey.value) return
  if (event.key === 'Enter') {
    if (onInteractiveElement()) return
    event.preventDefault()
    activateFocusedRow()
    return
  }
  if ((event.key === 'r' || event.key === 'R') && tab.value === 'requests' && !onInteractiveElement()) {
    const row = orderedRequests.value.find(item => item.request.id === focusedKey.value)
    if (row && !row.mine && requestStatus(row.request) === 'Pending') { event.preventDefault(); void decide(row, false) }
  }
}
let disposeShortcuts: (() => void) | undefined
onMounted(async () => {
  disposeShortcuts = registerShortcuts({
    id: 'control',
    titleKey: 'shortcuts.control',
    items: [
      { keys: ['↓', 'J'], descKey: 'shortcuts.controlDown' },
      { keys: ['↑', 'K'], descKey: 'shortcuts.controlUp' },
      { keys: ['Enter'], descKey: 'shortcuts.controlPrimary' },
      { keys: ['R'], descKey: 'shortcuts.controlReject' },
      { keys: ['Esc'], descKey: 'shortcuts.closeLayer' }
    ]
  })
  window.addEventListener('keydown', onControlKeyboard)
  await refresh()
})
onBeforeUnmount(() => {
  disposed = true
  modesSeq++
  disposeShortcuts?.()
  window.removeEventListener('keydown', onControlKeyboard)
})
</script>

<style scoped>
.info-icon { color: var(--scada-text-muted); cursor: help; margin-top: -2px; }
.control-kbd-hint { margin: 0 0 6px; }
:deep(.control-row-focused) { --el-table-tr-bg-color: var(--scada-primary-soft); }
:deep(.control-row-focused td:first-child) { box-shadow: inset 3px 0 0 var(--scada-primary); }
.step-fields { display: flex; gap: 4px; align-items: center; flex-wrap: wrap; }
h4 { margin: 16px 0 6px; }
/* 失败摘要块：红节点表达不出原因与「现场动到哪一步」 */
.run-failure { margin-bottom: 10px; padding: 8px 12px; border: 1px solid var(--scada-border); border-left: 3px solid var(--scada-danger); border-radius: var(--scada-radius-sm); background: var(--scada-panel-muted); }
.run-failure.is-aborted { border-left-color: var(--scada-info); }
.run-failure-head { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
.run-failure-reason { margin: 6px 0 0; word-break: break-word; }
.run-failure-advice { margin: 4px 0 0; color: var(--scada-text-muted); }
.flow-editor { display: flex; gap: 10px; align-items: stretch; }
.flow-props { width: 300px; flex-shrink: 0; border: 1px solid var(--el-border-color-lighter); border-radius: 4px; padding: 8px; overflow: auto; max-height: 560px; }
@media (max-width: 900px) {
  .flow-editor { flex-direction: column; }
  .flow-props { box-sizing: border-box; width: 100%; max-height: none; }
}
</style>

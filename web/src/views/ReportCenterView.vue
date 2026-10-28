<template>
  <div class="page">
  <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="report-action-error" />
  <div class="panel">
    <div class="toolbar">
      <h3>{{ $t('ux.reports.business') }}</h3><span class="spacer" />
      <router-link to="/reports"><el-button data-test="business-statistics-link">{{ $t('ux.reports.statistics') }}</el-button></router-link>
      <el-button :loading="loading" @click="refresh()">{{ $t('reportCenterView.k38108e') }}</el-button>
    </div>
    <p class="tiny muted">{{ $t('ux.reports.businessHint') }}</p>
    <el-radio-group v-model="centerTab" :aria-label="$t('ux.reports.business')" data-test="report-center-tabs">
      <el-radio-button value="archives" data-test="report-center-archives">{{ $t('ux.reports.archives') }}</el-radio-button>
      <el-radio-button value="templates" data-test="report-center-templates">{{ $t('ux.reports.templates') }}</el-radio-button>
    </el-radio-group>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" show-icon data-test="report-center-error" />
    <el-alert v-if="areasError" :title="areasError" type="error" :closable="false" show-icon data-test="report-areas-error" />
  </div>
  <div v-if="centerTab === 'templates'" class="panel" style="margin-top: 12px">
    <div class="toolbar">
      <h3>{{ $t('reportCenterView.templatesTitle') }}</h3>
      <el-tooltip :content="$t('reportCenterView.kb85703')" placement="bottom-start" :show-after="200">
        <el-icon class="info-icon"><InfoFilled /></el-icon>
      </el-tooltip>
      <span class="spacer" />
      <el-button v-if="configurableAreas.length" type="primary" data-test="report-template-new" @click="open(null)">{{ $t('reportCenterView.k58c4a6') }}</el-button>
    </div>
    <el-alert v-if="tagsError && !editor" :title="tagsError" type="error" :closable="false" show-icon data-test="report-tags-error" />
    <el-alert v-if="jobsError && !editor" :title="jobsError" type="error" :closable="false" show-icon data-test="report-jobs-error" />
    <el-alert v-if="channelsError && !editor" :title="channelsError" type="error" :closable="false" show-icon data-test="report-channels-error" />
    <el-table :data="templates" v-loading="loading" row-key="id" data-test="report-template-table">
      <template #empty>
        <el-empty :description="readError || $t('reportCenterView.k1a97b4')" :image-size="72">
          <el-button v-if="configurableAreas.length && !readError" type="primary" @click="open(null)">{{ $t('reportCenterView.k58c4a6') }}</el-button>
        </el-empty>
      </template>
      <el-table-column :label="$t('reportCenterView.k06d0f3')" min-width="180"><template #default="{ row }"><strong>{{ row.name }}</strong>
        <div class="tiny muted">{{ $t('reportCenterView.k22d559', [periodLabels[row.period as ReportPeriod], areaName(row.areaId), row.revision]) }}</div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kad3ee6')" min-width="240"><template #default="{ row }"><span class="tiny">{{ row.columns.map((c: ReportColumn) => c.title).join('、') }}</span></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kc727c8')" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.scheduleEnabled ? $t('reportCenterView.k053ac8', [row.scheduleDelayMin]) : $t('reportCenterView.k2a4a4d') }}</span>
        <div class="tiny muted">{{ row.emailChannelId ? row.emailRecipients : $t('reportCenterView.k5741e3') }}</div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button link :data-test="`report-records-${row.id}`" @click="selectTemplate(row)">{{ $t('reportCenterView.kbe78b2') }}</el-button>
        <el-button v-if="canEditArea(row.areaId)" link type="success" :data-test="`report-generate-${row.id}`" @click="generate(row)">{{ $t('reportCenterView.k25b4a0') }}</el-button>
        <el-button v-if="canEditArea(row.areaId)" link type="primary" @click="open(row)">{{ $t('reportCenterView.ka7f814') }}</el-button>
        <el-button v-if="canEditArea(row.areaId)" link type="danger" @click="remove(row)">{{ $t('reportCenterView.k3755f5') }}</el-button></template></el-table-column>
    </el-table>
  </div>

  <div v-if="centerTab === 'archives'" class="panel" style="margin-top: 12px">
    <div class="toolbar"><h3>{{ $t('reportCenterView.kb11f52') }}</h3><span class="hint">{{ selected ? $t('reportCenterView.k11d276', [selected.name]) : $t('ux.reports.allArchives') }}</span><span class="spacer" />
      <el-select :model-value="selected?.id" clearable filterable :placeholder="$t('ux.reports.templateFilter')" :aria-label="$t('ux.reports.templateFilter')" data-test="report-archive-template" style="width: 240px" @change="filterArchives"><el-option v-for="template in templates" :key="template.id" :value="template.id" :label="template.name" /></el-select>
      <el-button v-if="selected" link @click="selectTemplate(null)">{{ $t('reportCenterView.k82f076') }}</el-button></div>
    <el-table v-loading="loading" :data="runs" row-key="id" :empty-text="readError && !runs.length ? readError : $t('reportCenterView.kc50145')" data-test="report-run-table">
      <el-table-column :label="$t('reportCenterView.kca53e6')" min-width="200"><template #default="{ row }"><strong>{{ row.templateName }}</strong> <el-tag size="small">v{{ row.version }}</el-tag>
        <div class="tiny muted">{{ $t('reportCenterView.k917029', [row.periodKey, row.templateRevision]) }}</div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.k4aa230')" min-width="200"><template #default="{ row }"><span class="tiny">{{ formatTime(row.generatedTs) }} · {{ triggerLabels[row.trigger] ?? row.trigger }} · {{ row.generatedBy }}</span>
        <el-tag v-if="row.purgedTs" size="small" type="info">{{ $t('reportCenterView.kfb730a') }}</el-tag>
        <div class="tiny muted mono">SHA256 {{ row.pdfSha256.slice(0, 12) }}…</div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.k4af806')" width="150"><template #default="{ row }">
        <el-tag v-if="row.signed" size="small" type="success" :data-test="`report-signed-${row.id}`">{{ $t('reportCenterView.kd7dfc4') }}</el-tag>
        <el-tooltip v-else :content="row.signatureError ?? $t('reportCenterView.k3d1f9f')"><el-tag size="small" type="warning">{{ $t('reportCenterView.k7bda7d') }}</el-tag></el-tooltip>
        <div><el-tag size="small" :type="statusTypes[(row.approvalStatus ?? 'none') as ApprovalStatus]" :data-test="`report-approval-${row.id}`">{{ statusLabels[(row.approvalStatus ?? 'none') as ApprovalStatus] }}</el-tag></div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kf3ea6d')" width="260"><template #default="{ row }">
        <el-button link type="primary" :data-test="`report-view-${row.id}`" @click="preview(row)">{{ $t('reportCenterView.kf7acef') }}</el-button>
        <el-button link :disabled="!!row.purgedTs" @click="download(row, 'xlsx')">Excel</el-button>
        <el-button link :disabled="!!row.purgedTs" @click="download(row, 'pdf')">PDF</el-button>
        <el-button link type="primary" :data-test="`report-verify-${row.id}`" @click="openVerify(row)">{{ $t('reportCenterView.ka46ea2') }}</el-button></template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="viewer" :title="table?.title ?? $t('reportCenterView.kca53e6')" width="960px">
    <div v-if="previewLoading" v-loading="true" style="min-height: 100px" data-test="report-preview-loading" />
    <el-alert v-if="previewError" :title="previewError" type="error" :closable="false" show-icon data-test="report-preview-error" />
    <el-table v-if="table" :data="[...table.rows, { ...table.summary }]" size="small" max-height="520" data-test="report-preview">
      <el-table-column prop="label" :label="$t('reportCenterView.kf453a4')" width="140" fixed />
      <el-table-column v-for="(c, i) in table.columns" :key="c.key" :label="c.unit ? `${c.title} (${c.unit})` : c.title" min-width="110">
        <template #default="{ row }">{{ row.values[i] == null ? '--' : Number(row.values[i]).toFixed(c.decimals) }}</template></el-table-column>
    </el-table>
  </el-dialog>

  <el-dialog v-model="verifier" :title="verifyRun ? $t('reportCenterView.k9f1581', [verifyRun.templateName, verifyRun.periodKey, verifyRun.version]) : $t('reportCenterView.kc1c297')" width="820px" :close-on-click-modal="!signing" :close-on-press-escape="!signing" :show-close="!signing" data-test="report-verify-dialog">
    <div v-if="verifyLoading" v-loading="true" style="min-height: 100px" data-test="report-verify-loading" />
    <el-alert v-if="verifyError" :title="verifyError" type="error" :closable="false" show-icon data-test="report-verify-error" />
    <template v-if="verification">
      <el-alert :type="verification.ok ? 'success' : 'error'" :closable="false" data-test="report-verify-result"
        :title="verification.ok ? $t('reportCenterView.kc193da') : verification.signed ? $t('reportCenterView.kc5fd1d') : $t('reportCenterView.kd33e9d')" />
      <div class="tiny muted" style="margin: 8px 0">{{ $t('reportCenterView.kcc9441', [verification.signerSubject ?? '—']) }} <span class="mono">{{ verification.signerThumbprint ?? '—' }}</span>
        <span v-if="verification.signedTs"> {{ $t('reportCenterView.k7477fd', [formatTime(verification.signedTs)]) }}</span>
        <span v-if="verification.signerThumbprint && verification.currentThumbprint !== verification.signerThumbprint"> {{ $t('reportCenterView.k031115') }}</span></div>
      <el-table :data="verification.files" size="small">
        <el-table-column prop="format" :label="$t('reportCenterView.k49deaf')" width="70" />
        <el-table-column label="SHA256" width="90"><template #default="{ row }"><el-tag size="small" :type="row.hashOk ? 'success' : 'danger'">{{ row.hashOk ? $t('reportCenterView.k5ab754') : row.exists ? $t('reportCenterView.kea88dc') : $t('reportCenterView.k2fe9b7') }}</el-tag></template></el-table-column>
        <el-table-column :label="$t('reportCenterView.kcb9e71')" min-width="220"><template #default="{ row }"><el-tag size="small" :type="row.signature.valid ? 'success' : 'danger'">{{ row.signature.valid ? $t('reportCenterView.kad385d') : row.signature.error }}</el-tag>
          <span v-if="row.signature.valid" class="tiny muted"> {{ row.signature.selfSigned ? $t('reportCenterView.kd9bcdf') : row.signature.chainTrusted ? $t('reportCenterView.k86e956') : $t('reportCenterView.kc8aa91') }}</span></template></el-table-column>
        <el-table-column label="" width="90"><template #default="{ row }"><el-button link :disabled="!verification.signed" @click="downloadSignature(row.format)">{{ $t('reportCenterView.k8fe812') }}</el-button></template></el-table-column>
      </el-table>
      <div class="toolbar" style="margin-top: 8px"><span class="tiny">{{ $t('reportCenterView.k09fd2a') }}</span>
        <input type="file" accept=".pdf,.xlsx" :disabled="signing" data-test="report-verify-file" @change="verifyUpload" />
        <el-icon v-if="fileChecking" class="is-loading" aria-hidden="true"><Loading /></el-icon>
        <el-tag v-if="uploadResult" size="small" :type="uploadResult.ok ? 'success' : 'danger'" data-test="report-verify-file-result">{{ uploadResult.ok ? $t('reportCenterView.kaa4d23', [uploadResult.matched]) : $t('reportCenterView.k529489') }}</el-tag></div>
      <el-alert v-if="fileError" :title="fileError" type="error" :closable="false" show-icon data-test="report-verify-file-error" />

      <h4 style="margin: 12px 0 6px">{{ $t('reportCenterView.kf8696a') }} <el-tag size="small" :type="statusTypes[verification.status]">{{ statusLabels[verification.status] }}</el-tag></h4>
      <el-table :data="verification.approvals" size="small" :empty-text="$t('reportCenterView.k4233bc')" data-test="report-approval-table">
        <el-table-column :label="$t('reportCenterView.kd13bfa')" width="110"><template #default="{ row }">{{ row.approval.step === 'review' ? $t('reportCenterView.kfe945e') : $t('reportCenterView.k62e26d') }}{{ row.approval.decision === 'rejected' ? $t('reportCenterView.k69922c') : '' }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.k86369d')" width="120"><template #default="{ row }">{{ row.approval.displayName || row.approval.userName }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.ka20700')" min-width="180"><template #default="{ row }">{{ row.approval.reason }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.k89b4aa')" width="160"><template #default="{ row }">{{ formatTime(row.approval.ts) }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.k80144e')" width="80"><template #default="{ row }"><el-tag size="small" :type="row.ok ? 'success' : 'danger'">{{ row.ok ? $t('reportCenterView.kad385d') : $t('reportCenterView.keb645a') }}</el-tag></template></el-table-column>
      </el-table>
      <el-form v-if="nextStep" :disabled="signing" label-width="80px" style="margin-top: 10px" data-test="report-sign-form" @submit.prevent>
        <el-form-item :label="nextStep === 'review' ? $t('reportCenterView.kfe945e') : $t('reportCenterView.k62e26d')">
          <el-radio-group v-model="signForm.decision"><el-radio value="approved">{{ $t('reportCenterView.kdcc423') }}</el-radio><el-radio value="rejected">{{ $t('reportCenterView.k21ec78') }}</el-radio></el-radio-group></el-form-item>
        <el-form-item :label="$t('reportCenterView.ka20700')" required><el-input v-model="signForm.reason" maxlength="500" data-test="report-sign-reason" /></el-form-item>
        <el-form-item :label="$t('reportCenterView.kc839a8')" required><el-input v-model="signForm.password" type="password" show-password autocomplete="current-password" data-test="report-sign-password" /></el-form-item>
        <el-alert v-if="signError" :title="signError" type="error" :closable="false" />
        <el-button type="primary" :loading="signing" data-test="report-sign-submit" @click="submitSign()">{{ $t('reportCenterView.k094333', [nextStep === 'review' ? $t('reportCenterView.kfe945e') : $t('reportCenterView.k62e26d')]) }}</el-button>
        <span class="tiny muted"> {{ $t('reportCenterView.kad28f4') }}</span>
      </el-form>
    </template>
  </el-dialog>

  <el-dialog v-model="editor" :title="editing === null ? $t('reportCenterView.k58c4a6') : $t('reportCenterView.kb7410c')" width="980px" :close-on-click-modal="false">
    <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" data-test="report-template-error" />
    <el-alert v-if="tagsError" :title="tagsError" type="error" :closable="false" show-icon data-test="report-editor-tags-error" />
    <el-alert v-if="jobsError" :title="jobsError" type="error" :closable="false" show-icon data-test="report-editor-jobs-error" />
    <el-alert v-if="channelsError" :title="channelsError" type="error" :closable="false" show-icon data-test="report-editor-channels-error" />
    <el-form label-width="110px" :disabled="saving" @submit.prevent>
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item :label="$t('reportCenterView.k1be7ae')" required><el-input v-model="form.name" maxlength="80" data-test="report-template-name" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k8a12b1')"><el-select v-model="form.period" data-test="report-template-period"><el-option v-for="(label, key) in periodLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k17fc93')" label-width="60px"><el-select v-model="form.areaId" data-test="report-template-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item></el-col>
        <el-col v-if="form.period === 'Shift'" :span="12"><el-form-item :label="$t('reportCenterView.kb41bd4')"><el-input v-model="form.shiftStarts" placeholder="08:00,20:00" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k392cb2')"><el-switch v-model="form.scheduleEnabled" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k3b6748')" label-width="80px"><el-input-number v-model="form.scheduleDelayMin" :min="0" :max="1440" controls-position="right" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('reportCenterView.kdfc9b8')"><el-select v-model="form.emailChannelId" clearable :placeholder="$t('reportCenterView.k5741e3')" data-test="report-email-channel"><el-option v-for="c in emailChannels" :key="c.id" :value="c.id" :label="c.name" /></el-select></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('reportCenterView.k529414')"><el-input v-model="form.emailRecipients" :placeholder="$t('reportCenterView.k4db6b8')" /></el-form-item></el-col>
      </el-row>
      <el-form-item :label="$t('reportCenterView.kad3ee6')">
        <div style="width: 100%">
          <div v-for="(c, i) in form.columns" :key="i" class="col-row" :data-test="`report-column-${i}`">
            <el-input v-model="c.key" :placeholder="$t('reportCenterView.k5a8c89')" style="width: 90px" />
            <el-input v-model="c.title" :placeholder="$t('reportCenterView.k748d7d')" style="width: 120px" />
            <el-select v-model="c.kind" style="width: 120px" :data-test="`report-column-kind-${i}`"><el-option v-for="(label, key) in kindLabels" :key="key" :value="key" :label="label" /></el-select>
            <el-select v-if="c.kind.startsWith('Tag')" v-model="c.tagId" filterable :placeholder="$t('reportCenterView.k8cff1c')" style="width: 180px" :data-test="`report-column-tag-${i}`"><el-option v-for="t in tags" :key="t.id" :value="t.id" :label="`#${t.id} · ${t.name}`" /></el-select>
            <template v-if="c.kind === 'CalcSnapshot'"><el-select v-model="c.jobId" :placeholder="$t('reportCenterView.k8efdcf')" style="width: 140px" :data-test="`report-column-job-${i}`"><el-option v-for="j in jobs" :key="j.job.id" :value="j.job.id" :label="j.job.name" /></el-select>
              <el-input v-model="c.output" :placeholder="$t('reportCenterView.k68ad85')" style="width: 90px" /></template>
            <el-select v-if="c.kind === 'AlarmCount'" v-model="c.minLevel" clearable :placeholder="$t('reportCenterView.k7b715f')" style="width: 110px"><el-option value="Info" :label="$t('reportCenterView.kab3656')" /><el-option value="Warning" :label="$t('reportCenterView.k5521e3')" /><el-option value="Critical" :label="$t('reportCenterView.k81ffc6')" /></el-select>
            <el-select v-if="c.kind === 'AlarmKpi'" v-model="c.output" :placeholder="$t('alarmAnalytics.chooseMetric')" style="width: 180px" :data-test="`report-kpi-metric-${i}`"><el-option v-for="m in alarmKpiMetrics" :key="m" :value="m" :label="$t(`alarmAnalytics.metric.${m}`)" /></el-select>
            <el-select v-if="c.kind === 'OeeKpi'" v-model="c.output" :placeholder="$t('oee.chooseMetric')" style="width: 180px" :data-test="`report-oee-metric-${i}`"><el-option v-for="m in oeeKpiMetrics" :key="m" :value="m" :label="$t(`oee.metric.${m}`)" /></el-select>
            <el-input v-if="c.kind === 'Formula'" v-model="c.formula" :placeholder="$t('reportCenterView.kefe743')" style="width: 180px" />
            <el-input-number v-if="c.kind === 'TagSum'" v-model="c.factor" :step="0.1" :placeholder="$t('reportCenterView.k3dd194')" controls-position="right" style="width: 100px" />
            <el-input-number v-model="c.decimals" :min="0" :max="6" controls-position="right" style="width: 80px" />
            <el-input v-model="c.unit" :placeholder="$t('reportCenterView.k8ec8c8')" style="width: 70px" />
            <el-button link type="danger" @click="form.columns.splice(i, 1)">{{ $t('reportCenterView.k2f752c') }}</el-button>
          </div>
          <el-button size="small" data-test="report-add-column" @click="addColumn()">{{ $t('reportCenterView.k90fcf2') }}</el-button>
          <el-button size="small" data-test="report-alarm-kpi-preset" @click="addAlarmKpiPreset()">{{ $t('alarmAnalytics.kpiPreset') }}</el-button>
          <el-button size="small" data-test="report-oee-kpi-preset" @click="addOeeKpiPreset()">{{ $t('oee.kpiPreset') }}</el-button>
          <div class="tiny muted">{{ $t('reportCenterView.kb11e1e') }}</div>
        </div>
      </el-form-item>
    </el-form>
    <template #footer><el-button @click="editor = false">{{ $t('reportCenterView.k4d0b46') }}</el-button><el-button type="primary" :loading="saving" data-test="report-template-save" @click="save()">{{ $t('reportCenterView.kfadf24') }}</el-button></template>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { InfoFilled, Loading } from '@element-plus/icons-vue'
import { reportCenterApi, type ApprovalStatus, type ReportVerification, type ReportColumn, type ReportColumnKind, type ReportPeriod, type ReportRun, type ReportTableData, type ReportTemplate, type ReportTemplateInput } from '../api/reports'
import { oeeKpiMetrics } from '../api/oee'
import { calcJobApi, type CalcJobInfo } from '../api/calcJobs'
import { areaApi, notificationApi, tagApi } from '../api'
import { errorMessage, errorText } from '../api/http'
import { formatTime } from '../utils'
import { can } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import type { Area, NotificationChannel, Tag } from '../types'
import { tr } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => can(auth.user, 'Configure'))
const canEditArea = (areaId: number) => can(auth.user, 'Configure', areaId)
const configurableAreas = computed(() => areas.value.filter((a) => canEditArea(a.id)))
const triggerLabels: Record<string, string> = { schedule: tr('reportCenterView.k4c02a6'), backfill: tr('reportCenterView.kf6a44b'), manual: tr('reportCenterView.k2a4a4d') }
const periodLabels: Record<ReportPeriod, string> = { Shift: tr('reportCenterView.ke523cc'), Day: tr('reportCenterView.k592f2d'), Month: tr('reportCenterView.kdd7325') }
const kindLabels: Record<ReportColumnKind, string> = { TagAvg: tr('reportCenterView.ka4d0f5'), TagMax: tr('reportCenterView.k018219'), TagMin: tr('reportCenterView.kc3d641'), TagDiff: tr('reportCenterView.k49c4c8'), TagSum: tr('reportCenterView.k884c13'), CalcSnapshot: tr('reportCenterView.k856878'), AlarmCount: tr('reportCenterView.k697a27'), Formula: tr('reportCenterView.k3f2703'), AlarmKpi: tr('alarmAnalytics.kpiColumn'), OeeKpi: tr('oee.kpiColumn') }
const alarmKpiMetrics = ['total', 'annunciated', 'perOperator10Min', 'perOperatorHour', 'peak10Min', 'floodPercent', 'floods', 'avgAckSec', 'medianAckSec', 'avgClearSec', 'medianClearSec', 'chatterEpisodes', 'fleeting', 'standing', 'stale', 'shelvedHours', 'suppressedHours', 'highPercent']
// 报警 KPI 预设：一键填入常用 EEMUA 191 指标列（可再增删）。
function addOeeKpiPreset(): void {
  for (const [m, decimals] of [['oee', 3], ['availability', 3], ['performance', 3], ['quality', 3], ['good', 0], ['scrapRate', 3]] as [string, number][]) {
    if (form.columns.some(c => c.kind === 'OeeKpi' && c.output === m)) continue
    form.columns.push({ key: m, title: tr(`oee.metric.${m}`), kind: 'OeeKpi', output: m, decimals, unit: '' })
  }
  if (!form.name) form.name = tr('oee.kpiPreset')
}
function addAlarmKpiPreset(): void {
  const preset: [string, number][] = [['annunciated', 0], ['perOperator10Min', 2], ['peak10Min', 0], ['floodPercent', 1], ['avgAckSec', 0], ['chatterEpisodes', 0], ['fleeting', 0], ['standing', 0], ['shelvedHours', 1], ['highPercent', 1]]
  for (const [m, decimals] of preset) {
    if (form.columns.some(c => c.kind === 'AlarmKpi' && c.output === m)) continue
    form.columns.push({ key: m, title: tr(`alarmAnalytics.metric.${m}`), kind: 'AlarmKpi', output: m, decimals, unit: '' })
  }
  if (!form.name) form.name = tr('alarmAnalytics.kpiPresetName')
}
const templates = ref<ReportTemplate[]>([]), runs = ref<ReportRun[]>([]), areas = ref<Area[]>([]), tags = ref<Tag[]>([]), jobs = ref<CalcJobInfo[]>([]), channels = ref<NotificationChannel[]>([])
const actionError = ref('')
const selected = ref<ReportTemplate | null>(null), loading = ref(false), saving = ref(false), readError = ref(''), areasError = ref(''), tagsError = ref(''), jobsError = ref(''), channelsError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null)
const centerTab = ref<'archives' | 'templates'>('archives')
const viewer = ref(false), table = ref<ReportTableData | null>(null), previewError = ref('')
const previewLoading = ref(false), verifyLoading = ref(false)
let previewSeq = 0, verifySeq = 0, uploadSeq = 0, refreshSeq = 0, disposed = false
const statusLabels: Record<ApprovalStatus, string> = { none: tr('reportCenterView.kf53b68'), reviewed: tr('reportCenterView.k53c61c'), approved: tr('reportCenterView.k402875'), rejected: tr('reportCenterView.ke944e4') }
const statusTypes: Record<ApprovalStatus, 'info' | 'warning' | 'success' | 'danger'> = { none: 'info', reviewed: 'warning', approved: 'success', rejected: 'danger' }
const verifier = ref(false), verifyRun = ref<ReportRun | null>(null), verification = ref<ReportVerification | null>(null), verifyError = ref('')
const uploadResult = ref<{ ok: boolean; matched: string | null } | null>(null), fileError = ref('')
const fileChecking = ref(false)
const signForm = reactive({ decision: 'approved' as 'approved' | 'rejected', reason: '', password: '' }), signing = ref(false), signError = ref('')
const nextStep = computed<'review' | 'approve' | null>(() => {
  const v = verification.value
  if (!v || !v.ok) return null
  return v.status === 'none' ? 'review' : v.status === 'reviewed' ? 'approve' : null
})
const empty = (): ReportTemplateInput => ({ name: '', period: 'Day', areaId: configurableAreas.value[0]?.id ?? areas.value[0]?.id ?? 1, shiftStarts: '08:00,20:00', columns: [], scheduleEnabled: false, scheduleDelayMin: 10, emailChannelId: null, emailRecipients: '', revision: 0 })
const form = reactive<ReportTemplateInput>(empty())
const emailChannels = computed(() => channels.value.filter(c => c.type === 'Email'))
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? tr('reportCenterView.k90f301', [id])

function loadCatalog<T>(load: Promise<T>, error: { value: string }, fallback: string, current: T, seq: number): Promise<T> {
  return load.then(list => { if (!disposed && seq === refreshSeq) error.value = ''; return list }).catch((reason) => { if (!disposed && seq === refreshSeq) error.value = errorMessage(reason, fallback); return current })
}
async function refresh(): Promise<void> {
  if (disposed) return
  const seq = ++refreshSeq
  loading.value = true
  try {
    const [list, runList, areaList, tagList, jobList, channelList] = await Promise.all([
      reportCenterApi.templates(), reportCenterApi.runs(selected.value?.id),
      loadCatalog(areaApi.list(), areasError, tr('reportCenterView.areasLoadFailed'), areas.value, seq),
      loadCatalog(tagApi.list(), tagsError, tr('reportCenterView.tagsLoadFailed'), tags.value, seq),
      loadCatalog(calcJobApi.list(), jobsError, tr('reportCenterView.jobsLoadFailed'), jobs.value, seq),
      canConfigure.value
        ? loadCatalog(notificationApi.channels(), channelsError, tr('reportCenterView.channelsLoadFailed'), channels.value, seq)
        : Promise.resolve(channels.value).then(list => { if (!disposed && seq === refreshSeq) channelsError.value = ''; return list })
    ])
    if (disposed || seq !== refreshSeq) return
    templates.value = list; runs.value = runList; areas.value = areaList; tags.value = tagList; jobs.value = jobList; channels.value = channelList; readError.value = ''
  } catch (e) { if (!disposed && seq === refreshSeq) readError.value = errorMessage(e, tr('reportCenterView.k3de1db')) }
  finally { if (!disposed && seq === refreshSeq) loading.value = false }
}
async function selectTemplate(t: ReportTemplate | null): Promise<void> {
  centerTab.value = 'archives'
  if (selected.value?.id !== t?.id) runs.value = []
  selected.value = t; await refresh()
}
async function filterArchives(value: unknown): Promise<void> {
  if (value === undefined || value === null || value === '') { await selectTemplate(null); return }
  const template = templates.value.find(item => item.id === value)
  if (!template) { actionError.value = tr('ux.reports.templateUnavailable'); return }
  await selectTemplate(template)
}
function addColumn(): void { const n = form.columns.length + 1; form.columns.push({ key: `c${n}`, title: tr('reportCenterView.kd7c101', [n]), kind: 'TagAvg', tagId: tags.value[0]?.id ?? null, decimals: 2, unit: '' }) }
function open(t: ReportTemplate | null): void {
  const areaId = configurableAreas.value[0]?.id
  if (!t && areaId == null) return
  editing.value = t?.id ?? null
  Object.assign(form, t ? { ...empty(), ...t, columns: t.columns.map(c => ({ ...c })) } : { ...empty(), areaId })
  mutationError.value = ''; editor.value = true
}
async function save(): Promise<void> {
  if (!configurableAreas.value.some(a => a.id === form.areaId)) { mutationError.value = tr('reportCenterView.areaRequired'); return }
  saving.value = true; mutationError.value = ''
  try { await reportCenterApi.save(editing.value, { ...form }); editor.value = false; ElMessage.success(tr('reportCenterView.kcdfab9')); await refresh() }
  catch (e) { mutationError.value = errorMessage(e, tr('reportCenterView.k40525a')) }
  finally { saving.value = false }
}
async function remove(t: ReportTemplate): Promise<void> {
  try { await ElMessageBox.confirm(tr('reportCenterView.k6e88a6', [t.name]), tr('reportCenterView.k3c06ab'), { type: 'warning' }) } catch { return }
  actionError.value = ''
  try { await reportCenterApi.remove(t); actionError.value = ''; ElMessage.success(tr('reportCenterView.kfb5fe1')); await refresh() }
  catch (e) { actionError.value = errorMessage(e, tr('reportCenterView.k72250c')) }
}
async function generate(t: ReportTemplate): Promise<void> {
  const existing = runs.value.filter(r => r.templateId === t.id).length
  try {
    await ElMessageBox.confirm(tr('reportCenterView.kdfd811', [t.name, existing ? tr('reportCenterView.kf72c7f', [existing]) : '']), tr('reportCenterView.kbce024'), { type: 'warning', confirmButtonText: tr('reportCenterView.k4aa230') })
  } catch { return }
  actionError.value = ''
  try { const run = await reportCenterApi.generate(t.id); actionError.value = ''; ElMessage.success(tr('reportCenterView.kc3956a', [run.periodKey, run.version])); await selectTemplate(t) }
  catch (e) { actionError.value = errorMessage(e, tr('reportCenterView.k64ba75')) }
}
async function preview(run: ReportRun): Promise<void> {
  if (disposed) return
  const seq = ++previewSeq
  table.value = null; previewError.value = ''; previewLoading.value = true; viewer.value = true
  try {
    const result = await reportCenterApi.run(run.id)
    if (disposed || seq !== previewSeq) return
    table.value = result.table
  } catch (e) {
    if (!disposed && seq === previewSeq) previewError.value = errorMessage(e, tr('reportCenterView.k3de1db'))
  } finally { if (!disposed && seq === previewSeq) previewLoading.value = false }
}
async function download(run: ReportRun, format: 'xlsx' | 'pdf'): Promise<void> {
  actionError.value = ''
  try {
    const blob = await reportCenterApi.download(run.id, format)
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${run.templateName}_${run.periodKey}_v${run.version}.${format}`; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) { actionError.value = await errorText(e, tr('reportCenterView.kcdf7d8')) }
}
async function openVerify(run: ReportRun): Promise<void> {
  if (disposed || signing.value) return
  const seq = ++verifySeq
  uploadSeq++; fileChecking.value = false
  verification.value = null; verifyLoading.value = true; verifier.value = true
  verifyRun.value = run; uploadResult.value = null; signError.value = ''; verifyError.value = ''; fileError.value = ''; Object.assign(signForm, { decision: 'approved', reason: '', password: '' })
  try {
    const result = await reportCenterApi.verify(run.id)
    if (!disposed && seq === verifySeq) verification.value = result
  } catch (e) { if (!disposed && seq === verifySeq) verifyError.value = errorMessage(e, tr('reportCenterView.k31231e')) }
  finally { if (!disposed && seq === verifySeq) verifyLoading.value = false }
}
async function downloadSignature(format: 'xlsx' | 'pdf'): Promise<void> {
  const run = verifyRun.value; if (!run) return
  const seq = verifySeq
  fileError.value = ''
  try {
    const blob = await reportCenterApi.signature(run.id, format)
    if (disposed || seq !== verifySeq) return
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${run.templateName}_${run.periodKey}_v${run.version}.${format}.p7s`; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) {
    const message = await errorText(e, tr('reportCenterView.k678158'))
    if (!disposed && seq === verifySeq) fileError.value = message
  }
}
async function verifyUpload(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement, file = input.files?.[0], run = verifyRun.value
  if (!file || !run || disposed || !verifier.value || signing.value) return
  const session = verifySeq, seq = ++uploadSeq
  const current = () => !disposed && session === verifySeq && seq === uploadSeq
  fileError.value = ''; uploadResult.value = null; fileChecking.value = true
  try {
    const result = await reportCenterApi.verifyFile(run.id, file)
    if (current()) uploadResult.value = result
  } catch (e) {
    if (current()) { fileError.value = errorMessage(e, tr('reportCenterView.k9d5a9b')); input.value = '' }
  } finally { if (current()) fileChecking.value = false }
}
async function submitSign(): Promise<void> {
  const run = verifyRun.value, step = nextStep.value
  if (!run || !step || disposed || signing.value || !verifier.value || verifyLoading.value || verification.value?.runId !== run.id) return
  const seq = verifySeq, payload = { step, ...signForm }
  signing.value = true; signError.value = ''
  try {
    await reportCenterApi.sign(run.id, payload)
    if (disposed || seq !== verifySeq) return
    ElMessage.success(tr('reportCenterView.ke5ecbf')); signForm.password = ''; signForm.reason = ''
    verification.value = null; verifyLoading.value = true
    try {
      const result = await reportCenterApi.verify(run.id)
      if (!disposed && seq === verifySeq) verification.value = result
    } catch (e) { if (!disposed && seq === verifySeq) verifyError.value = errorMessage(e, tr('reportCenterView.k31231e')) }
    finally { if (!disposed && seq === verifySeq) verifyLoading.value = false }
    if (!disposed && seq === verifySeq) await refresh()
  } catch (e) { if (!disposed && seq === verifySeq) { signError.value = errorMessage(e, tr('reportCenterView.kf3d3eb')); signForm.password = '' } }
  finally { signing.value = false }
}
watch(viewer, (visible) => { if (!visible) { previewSeq++; previewLoading.value = false; table.value = null } }, { flush: 'sync' })
watch(verifier, (visible) => { if (!visible) { verifySeq++; uploadSeq++; verifyLoading.value = false; fileChecking.value = false; verification.value = null; signForm.password = '' } }, { flush: 'sync' })
onMounted(refresh)
onBeforeUnmount(() => { disposed = true; previewSeq++; verifySeq++; uploadSeq++; refreshSeq++; signForm.password = '' })
</script>
<style scoped>
.col-row { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-bottom: 6px; }
.mono { font-family: ui-monospace, Consolas, monospace; }
.info-icon { color: var(--scada-text-muted); cursor: help; }
</style>

<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>{{ $t('reportCenterView.kb41505') }}</strong><span class="spacer" />
      <el-button :loading="loading" @click="refresh()">{{ $t('reportCenterView.k38108e') }}</el-button>
      <el-button v-if="canConfigure" type="primary" data-test="report-template-new" @click="open(null)">{{ $t('reportCenterView.k58c4a6') }}</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
    <p class="tiny muted">{{ $t('reportCenterView.kb85703') }}</p>
    <el-table :data="templates" v-loading="loading" row-key="id" :empty-text="$t('reportCenterView.k1a97b4')" data-test="report-template-table">
      <el-table-column :label="$t('reportCenterView.k06d0f3')" min-width="180"><template #default="{ row }"><strong>{{ row.name }}</strong>
        <div class="tiny muted">{{ $t('reportCenterView.k22d559', [periodLabels[row.period as ReportPeriod], areaName(row.areaId), row.revision]) }}</div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kad3ee6')" min-width="240"><template #default="{ row }"><span class="tiny">{{ row.columns.map((c: ReportColumn) => c.title).join('、') }}</span></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kc727c8')" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.scheduleEnabled ? $t('reportCenterView.k053ac8', [row.scheduleDelayMin]) : $t('reportCenterView.k2a4a4d') }}</span>
        <div class="tiny muted">{{ row.emailChannelId ? row.emailRecipients : $t('reportCenterView.k5741e3') }}</div></template></el-table-column>
      <el-table-column :label="$t('reportCenterView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button link @click="selectTemplate(row)">{{ $t('reportCenterView.kbe78b2') }}</el-button>
        <el-button v-if="canConfigure" link type="success" :data-test="`report-generate-${row.id}`" @click="generate(row)">{{ $t('reportCenterView.k25b4a0') }}</el-button>
        <el-button v-if="canConfigure" link type="primary" @click="open(row)">{{ $t('reportCenterView.ka7f814') }}</el-button>
        <el-button v-if="canConfigure" link type="danger" @click="remove(row)">{{ $t('reportCenterView.k3755f5') }}</el-button></template></el-table-column>
    </el-table>
  </div>

  <div class="panel" style="margin-top: 12px">
    <div class="toolbar"><strong>{{ $t('reportCenterView.kb11f52') }}</strong><span class="tiny muted">{{ selected ? $t('reportCenterView.k11d276', [selected.name]) : $t('reportCenterView.k146966') }}</span><span class="spacer" />
      <el-button v-if="selected" link @click="selectTemplate(null)">{{ $t('reportCenterView.k82f076') }}</el-button></div>
    <el-table :data="runs" row-key="id" :empty-text="$t('reportCenterView.kc50145')" data-test="report-run-table">
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
    <el-table v-if="table" :data="[...table.rows, { ...table.summary }]" size="small" max-height="520" data-test="report-preview">
      <el-table-column prop="label" :label="$t('reportCenterView.kf453a4')" width="140" fixed />
      <el-table-column v-for="(c, i) in table.columns" :key="c.key" :label="c.unit ? `${c.title} (${c.unit})` : c.title" min-width="110">
        <template #default="{ row }">{{ row.values[i] == null ? '--' : Number(row.values[i]).toFixed(c.decimals) }}</template></el-table-column>
    </el-table>
  </el-dialog>

  <el-dialog v-model="verifier" :title="verifyRun ? $t('reportCenterView.k9f1581', [verifyRun.templateName, verifyRun.periodKey, verifyRun.version]) : $t('reportCenterView.kc1c297')" width="820px" data-test="report-verify-dialog">
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
        <input type="file" accept=".pdf,.xlsx" data-test="report-verify-file" @change="verifyUpload" />
        <el-tag v-if="uploadResult" size="small" :type="uploadResult.ok ? 'success' : 'danger'" data-test="report-verify-file-result">{{ uploadResult.ok ? $t('reportCenterView.kaa4d23', [uploadResult.matched]) : $t('reportCenterView.k529489') }}</el-tag></div>

      <h4 style="margin: 12px 0 6px">{{ $t('reportCenterView.kf8696a') }} <el-tag size="small" :type="statusTypes[verification.status]">{{ statusLabels[verification.status] }}</el-tag></h4>
      <el-table :data="verification.approvals" size="small" :empty-text="$t('reportCenterView.k4233bc')" data-test="report-approval-table">
        <el-table-column :label="$t('reportCenterView.kd13bfa')" width="110"><template #default="{ row }">{{ row.approval.step === 'review' ? $t('reportCenterView.kfe945e') : $t('reportCenterView.k62e26d') }}{{ row.approval.decision === 'rejected' ? $t('reportCenterView.k69922c') : '' }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.k86369d')" width="120"><template #default="{ row }">{{ row.approval.displayName || row.approval.userName }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.ka20700')" min-width="180"><template #default="{ row }">{{ row.approval.reason }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.k89b4aa')" width="160"><template #default="{ row }">{{ formatTime(row.approval.ts) }}</template></el-table-column>
        <el-table-column :label="$t('reportCenterView.k80144e')" width="80"><template #default="{ row }"><el-tag size="small" :type="row.ok ? 'success' : 'danger'">{{ row.ok ? $t('reportCenterView.kad385d') : $t('reportCenterView.keb645a') }}</el-tag></template></el-table-column>
      </el-table>
      <el-form v-if="nextStep" label-width="80px" style="margin-top: 10px" data-test="report-sign-form" @submit.prevent>
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
    <el-form label-width="110px" :disabled="saving" @submit.prevent>
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item :label="$t('reportCenterView.k1be7ae')" required><el-input v-model="form.name" maxlength="80" data-test="report-template-name" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k8a12b1')"><el-select v-model="form.period" data-test="report-template-period"><el-option v-for="(label, key) in periodLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k17fc93')" label-width="60px"><el-select v-model="form.areaId"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item></el-col>
        <el-col v-if="form.period === 'Shift'" :span="12"><el-form-item :label="$t('reportCenterView.kb41bd4')"><el-input v-model="form.shiftStarts" placeholder="08:00,20:00" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k392cb2')"><el-switch v-model="form.scheduleEnabled" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item :label="$t('reportCenterView.k3b6748')" label-width="80px"><el-input-number v-model="form.scheduleDelayMin" :min="0" :max="1440" controls-position="right" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('reportCenterView.kdfc9b8')"><el-select v-model="form.emailChannelId" clearable :placeholder="$t('reportCenterView.k5741e3')"><el-option v-for="c in emailChannels" :key="c.id" :value="c.id" :label="c.name" /></el-select></el-form-item></el-col>
        <el-col :span="12"><el-form-item :label="$t('reportCenterView.k529414')"><el-input v-model="form.emailRecipients" :placeholder="$t('reportCenterView.k4db6b8')" /></el-form-item></el-col>
      </el-row>
      <el-form-item :label="$t('reportCenterView.kad3ee6')">
        <div style="width: 100%">
          <div v-for="(c, i) in form.columns" :key="i" class="col-row" :data-test="`report-column-${i}`">
            <el-input v-model="c.key" :placeholder="$t('reportCenterView.k5a8c89')" style="width: 90px" />
            <el-input v-model="c.title" :placeholder="$t('reportCenterView.k748d7d')" style="width: 120px" />
            <el-select v-model="c.kind" style="width: 120px"><el-option v-for="(label, key) in kindLabels" :key="key" :value="key" :label="label" /></el-select>
            <el-select v-if="c.kind.startsWith('Tag')" v-model="c.tagId" filterable :placeholder="$t('reportCenterView.k8cff1c')" style="width: 180px"><el-option v-for="t in tags" :key="t.id" :value="t.id" :label="`#${t.id} · ${t.name}`" /></el-select>
            <template v-if="c.kind === 'CalcSnapshot'"><el-select v-model="c.jobId" :placeholder="$t('reportCenterView.k8efdcf')" style="width: 140px"><el-option v-for="j in jobs" :key="j.job.id" :value="j.job.id" :label="j.job.name" /></el-select>
              <el-input v-model="c.output" :placeholder="$t('reportCenterView.k68ad85')" style="width: 90px" /></template>
            <el-select v-if="c.kind === 'AlarmCount'" v-model="c.minLevel" clearable :placeholder="$t('reportCenterView.k7b715f')" style="width: 110px"><el-option value="Info" :label="$t('reportCenterView.kab3656')" /><el-option value="Warning" :label="$t('reportCenterView.k5521e3')" /><el-option value="Critical" :label="$t('reportCenterView.k81ffc6')" /></el-select>
            <el-select v-if="c.kind === 'AlarmKpi'" v-model="c.output" :placeholder="$t('alarmAnalytics.chooseMetric')" style="width: 180px" :data-test="`report-kpi-metric-${i}`"><el-option v-for="m in alarmKpiMetrics" :key="m" :value="m" :label="$t(`alarmAnalytics.metric.${m}`)" /></el-select>
            <el-input v-if="c.kind === 'Formula'" v-model="c.formula" :placeholder="$t('reportCenterView.kefe743')" style="width: 180px" />
            <el-input-number v-if="c.kind === 'TagSum'" v-model="c.factor" :step="0.1" :placeholder="$t('reportCenterView.k3dd194')" controls-position="right" style="width: 100px" />
            <el-input-number v-model="c.decimals" :min="0" :max="6" controls-position="right" style="width: 80px" />
            <el-input v-model="c.unit" :placeholder="$t('reportCenterView.k8ec8c8')" style="width: 70px" />
            <el-button link type="danger" @click="form.columns.splice(i, 1)">{{ $t('reportCenterView.k2f752c') }}</el-button>
          </div>
          <el-button size="small" data-test="report-add-column" @click="addColumn()">{{ $t('reportCenterView.k90fcf2') }}</el-button>
          <el-button size="small" data-test="report-alarm-kpi-preset" @click="addAlarmKpiPreset()">{{ $t('alarmAnalytics.kpiPreset') }}</el-button>
          <div class="tiny muted">{{ $t('reportCenterView.kb11e1e') }}</div>
        </div>
      </el-form-item>
    </el-form>
    <template #footer><el-button @click="editor = false">{{ $t('reportCenterView.k4d0b46') }}</el-button><el-button type="primary" :loading="saving" data-test="report-template-save" @click="save()">{{ $t('reportCenterView.kfadf24') }}</el-button></template>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { reportCenterApi, type ApprovalStatus, type ReportVerification, type ReportColumn, type ReportColumnKind, type ReportPeriod, type ReportRun, type ReportTableData, type ReportTemplate, type ReportTemplateInput } from '../api/reports'
import { calcJobApi, type CalcJobInfo } from '../api/calcJobs'
import { areaApi, notificationApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { formatTime, hasAny } from '../utils'
import { useAuthStore } from '../stores/auth'
import type { Area, NotificationChannel, Tag } from '../types'
import { tr } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
const triggerLabels: Record<string, string> = { schedule: tr('reportCenterView.k4c02a6'), backfill: tr('reportCenterView.kf6a44b'), manual: tr('reportCenterView.k2a4a4d') }
const periodLabels: Record<ReportPeriod, string> = { Shift: tr('reportCenterView.ke523cc'), Day: tr('reportCenterView.k592f2d'), Month: tr('reportCenterView.kdd7325') }
const kindLabels: Record<ReportColumnKind, string> = { TagAvg: tr('reportCenterView.ka4d0f5'), TagMax: tr('reportCenterView.k018219'), TagMin: tr('reportCenterView.kc3d641'), TagDiff: tr('reportCenterView.k49c4c8'), TagSum: tr('reportCenterView.k884c13'), CalcSnapshot: tr('reportCenterView.k856878'), AlarmCount: tr('reportCenterView.k697a27'), Formula: tr('reportCenterView.k3f2703'), AlarmKpi: tr('alarmAnalytics.kpiColumn') }
const alarmKpiMetrics = ['total', 'annunciated', 'perOperator10Min', 'perOperatorHour', 'peak10Min', 'floodPercent', 'floods', 'avgAckSec', 'medianAckSec', 'avgClearSec', 'medianClearSec', 'chatterEpisodes', 'fleeting', 'standing', 'stale', 'shelvedHours', 'suppressedHours', 'highPercent']
// 报警 KPI 预设：一键填入常用 EEMUA 191 指标列（可再增删）。
function addAlarmKpiPreset(): void {
  const preset: [string, number][] = [['annunciated', 0], ['perOperator10Min', 2], ['peak10Min', 0], ['floodPercent', 1], ['avgAckSec', 0], ['chatterEpisodes', 0], ['fleeting', 0], ['standing', 0], ['shelvedHours', 1], ['highPercent', 1]]
  for (const [m, decimals] of preset) {
    if (form.columns.some(c => c.kind === 'AlarmKpi' && c.output === m)) continue
    form.columns.push({ key: m, title: tr(`alarmAnalytics.metric.${m}`), kind: 'AlarmKpi', output: m, decimals, unit: '' })
  }
  if (!form.name) form.name = tr('alarmAnalytics.kpiPresetName')
}
const templates = ref<ReportTemplate[]>([]), runs = ref<ReportRun[]>([]), areas = ref<Area[]>([]), tags = ref<Tag[]>([]), jobs = ref<CalcJobInfo[]>([]), channels = ref<NotificationChannel[]>([])
const selected = ref<ReportTemplate | null>(null), loading = ref(false), saving = ref(false), readError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null)
const viewer = ref(false), table = ref<ReportTableData | null>(null)
const statusLabels: Record<ApprovalStatus, string> = { none: tr('reportCenterView.kf53b68'), reviewed: tr('reportCenterView.k53c61c'), approved: tr('reportCenterView.k402875'), rejected: tr('reportCenterView.ke944e4') }
const statusTypes: Record<ApprovalStatus, 'info' | 'warning' | 'success' | 'danger'> = { none: 'info', reviewed: 'warning', approved: 'success', rejected: 'danger' }
const verifier = ref(false), verifyRun = ref<ReportRun | null>(null), verification = ref<ReportVerification | null>(null)
const uploadResult = ref<{ ok: boolean; matched: string | null } | null>(null)
const signForm = reactive({ decision: 'approved' as 'approved' | 'rejected', reason: '', password: '' }), signing = ref(false), signError = ref('')
const nextStep = computed<'review' | 'approve' | null>(() => {
  const v = verification.value
  if (!v || !v.ok) return null
  return v.status === 'none' ? 'review' : v.status === 'reviewed' ? 'approve' : null
})
const empty = (): ReportTemplateInput => ({ name: '', period: 'Day', areaId: areas.value[0]?.id ?? 1, shiftStarts: '08:00,20:00', columns: [], scheduleEnabled: false, scheduleDelayMin: 10, emailChannelId: null, emailRecipients: '', revision: 0 })
const form = reactive<ReportTemplateInput>(empty())
const emailChannels = computed(() => channels.value.filter(c => c.type === 'Email'))
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? tr('reportCenterView.k90f301', [id])

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const [list, runList, areaList, tagList, jobList, channelList] = await Promise.all([reportCenterApi.templates(), reportCenterApi.runs(selected.value?.id),
      areaApi.list().catch(() => areas.value), tagApi.list().catch(() => tags.value), calcJobApi.list().catch(() => jobs.value), canConfigure.value ? notificationApi.channels().catch(() => channels.value) : Promise.resolve(channels.value)])
    templates.value = list; runs.value = runList; areas.value = areaList; tags.value = tagList; jobs.value = jobList; channels.value = channelList; readError.value = ''
  } catch (e) { readError.value = errorMessage(e, tr('reportCenterView.k3de1db')) }
  finally { loading.value = false }
}
async function selectTemplate(t: ReportTemplate | null): Promise<void> { selected.value = t; await refresh() }
function addColumn(): void { const n = form.columns.length + 1; form.columns.push({ key: `c${n}`, title: tr('reportCenterView.kd7c101', [n]), kind: 'TagAvg', tagId: tags.value[0]?.id ?? null, decimals: 2, unit: '' }) }
function open(t: ReportTemplate | null): void {
  editing.value = t?.id ?? null
  Object.assign(form, t ? { ...empty(), ...t, columns: t.columns.map(c => ({ ...c })) } : empty())
  mutationError.value = ''; editor.value = true
}
async function save(): Promise<void> {
  saving.value = true; mutationError.value = ''
  try { await reportCenterApi.save(editing.value, { ...form }); editor.value = false; ElMessage.success(tr('reportCenterView.kcdfab9')); await refresh() }
  catch (e) { mutationError.value = errorMessage(e, tr('reportCenterView.k40525a')) }
  finally { saving.value = false }
}
async function remove(t: ReportTemplate): Promise<void> {
  try { await ElMessageBox.confirm(tr('reportCenterView.k6e88a6', [t.name]), tr('reportCenterView.k3c06ab'), { type: 'warning' }) } catch { return }
  try { await reportCenterApi.remove(t); ElMessage.success(tr('reportCenterView.kfb5fe1')); await refresh() } catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.k72250c'))) }
}
async function generate(t: ReportTemplate): Promise<void> {
  const existing = runs.value.filter(r => r.templateId === t.id).length
  try {
    await ElMessageBox.confirm(tr('reportCenterView.kdfd811', [t.name, existing ? tr('reportCenterView.kf72c7f', [existing]) : '']), tr('reportCenterView.kbce024'), { type: 'warning', confirmButtonText: tr('reportCenterView.k4aa230') })
  } catch { return }
  try { const run = await reportCenterApi.generate(t.id); ElMessage.success(tr('reportCenterView.kc3956a', [run.periodKey, run.version])); await refresh() }
  catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.k64ba75'))) }
}
async function preview(run: ReportRun): Promise<void> {
  try { table.value = (await reportCenterApi.run(run.id)).table; viewer.value = true } catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.k3de1db'))) }
}
async function download(run: ReportRun, format: 'xlsx' | 'pdf'): Promise<void> {
  try {
    const blob = await reportCenterApi.download(run.id, format)
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${run.templateName}_${run.periodKey}_v${run.version}.${format}`; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.kcdf7d8'))) }
}
async function openVerify(run: ReportRun): Promise<void> {
  verifyRun.value = run; uploadResult.value = null; signError.value = ''; Object.assign(signForm, { decision: 'approved', reason: '', password: '' })
  try { verification.value = await reportCenterApi.verify(run.id); verifier.value = true } catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.k31231e'))) }
}
async function downloadSignature(format: 'xlsx' | 'pdf'): Promise<void> {
  const run = verifyRun.value; if (!run) return
  try {
    const blob = await reportCenterApi.signature(run.id, format)
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${run.templateName}_${run.periodKey}_v${run.version}.${format}.p7s`; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.k678158'))) }
}
async function verifyUpload(event: Event): Promise<void> {
  const file = (event.target as HTMLInputElement).files?.[0]; const run = verifyRun.value
  if (!file || !run) return
  try { uploadResult.value = await reportCenterApi.verifyFile(run.id, file) } catch (e) { ElMessage.error(errorMessage(e, tr('reportCenterView.k9d5a9b'))) }
}
async function submitSign(): Promise<void> {
  const run = verifyRun.value, step = nextStep.value
  if (!run || !step) return
  signing.value = true; signError.value = ''
  try {
    await reportCenterApi.sign(run.id, { step, ...signForm })
    ElMessage.success(tr('reportCenterView.ke5ecbf')); signForm.password = ''; signForm.reason = ''
    verification.value = await reportCenterApi.verify(run.id); await refresh()
  } catch (e) { signError.value = errorMessage(e, tr('reportCenterView.kf3d3eb')); signForm.password = '' }
  finally { signing.value = false }
}
onMounted(refresh)
</script>
<style scoped>
.col-row { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-bottom: 6px; }
.mono { font-family: ui-monospace, Consolas, monospace; }
</style>

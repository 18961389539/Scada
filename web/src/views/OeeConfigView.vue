<template>
  <div class="page" v-loading="loading">
    <el-alert v-if="metaError" :title="metaError" type="error" :closable="false" show-icon data-test="oee-meta-error" />
    <el-button v-if="metaError" data-test="oee-meta-retry" @click="refreshMeta()">{{ $t('common.retry') }}</el-button>
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="oee-action-error" />
    <div class="panel">
      <div class="toolbar">
        <h3>{{ $t('oee.configTitle') }}</h3>
        <span class="spacer" />
        <el-button v-if="isGlobalConfigure" data-test="oee-thresholds" @click="openThresholds">{{ $t('oee.thresholds') }}</el-button>
      </div>
      <el-tabs v-model="cfgTab">
        <el-tab-pane :label="$t('oee.lines')" name="lines">
          <el-button v-if="canConfigure" type="primary" size="small" data-test="oee-add-line" @click="editLine()">{{ $t('oee.add') }}</el-button>
          <el-table :data="lines" size="small" class="mt" data-test="oee-lines" :empty-text="metaError || undefined">
            <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
            <el-table-column :label="$t('oee.area')" width="100"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
            <el-table-column prop="idealCycleSec" :label="$t('oee.idealCycle')" width="100" />
            <el-table-column :label="$t('oee.enabled')" width="80"><template #default="{ row }">{{ yesNo(row.enabled) }}</template></el-table-column>
            <el-table-column width="140"><template #default="{ row }">
              <el-button v-if="canEditArea(row.areaId)" link type="primary" @click="editLine(row)">{{ $t('oee.edit') }}</el-button>
              <el-button v-if="canEditArea(row.areaId)" link type="danger" @click="removeLine(row.id)">{{ $t('oee.remove') }}</el-button>
            </template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('oee.products')" name="products">
          <el-button v-if="canConfigure" type="primary" size="small" data-test="oee-add-product" @click="editProduct()">{{ $t('oee.add') }}</el-button>
          <el-table :data="products" size="small" class="mt" data-test="oee-products">
            <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
            <el-table-column prop="code" :label="$t('oee.code')" width="100" />
            <el-table-column prop="idealCycleSec" :label="$t('oee.idealCycle')" width="100" />
            <el-table-column width="140"><template #default="{ row }">
              <el-button v-if="canEditArea(row.areaId)" link type="primary" @click="editProduct(row)">{{ $t('oee.edit') }}</el-button>
              <el-button v-if="canEditArea(row.areaId)" link type="danger" @click="removeProduct(row.id)">{{ $t('oee.remove') }}</el-button>
            </template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('oee.shifts')" name="shifts">
          <el-button v-if="canConfigure" type="primary" size="small" data-test="oee-add-shift" @click="editShift()">{{ $t('oee.add') }}</el-button>
          <el-table :data="shifts" size="small" class="mt" data-test="oee-shifts">
            <el-table-column prop="name" :label="$t('oee.name')" min-width="120" />
            <el-table-column :label="$t('oee.area')" width="100"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
            <el-table-column :label="$t('oee.time')" width="140"><template #default="{ row }">{{ fmtMin(row.startMinute) }}–{{ fmtMin(row.endMinute) }}</template></el-table-column>
            <el-table-column width="140"><template #default="{ row }">
              <el-button v-if="canEditArea(row.areaId)" link type="primary" @click="editShift(row)">{{ $t('oee.edit') }}</el-button>
              <el-button v-if="canEditArea(row.areaId)" link type="danger" @click="removeShift(row.id)">{{ $t('oee.remove') }}</el-button>
            </template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('oee.reasons')" name="reasons">
          <el-button v-if="canConfigure" type="primary" size="small" data-test="oee-add-reason" @click="editReason()">{{ $t('oee.add') }}</el-button>
          <el-table :data="reasons" size="small" class="mt" data-test="oee-reasons">
            <el-table-column prop="code" :label="$t('oee.code')" width="100" />
            <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
            <el-table-column :label="$t('oee.category')" width="120"><template #default="{ row }">{{ labelOf(categoryLabels, row.category) }}</template></el-table-column>
            <el-table-column :label="$t('oee.planned')" width="80"><template #default="{ row }">{{ yesNo(row.isPlanned) }}</template></el-table-column>
            <el-table-column width="140"><template #default="{ row }">
              <el-button v-if="canEditArea(row.areaId)" link type="primary" @click="editReason(row)">{{ $t('oee.edit') }}</el-button>
              <el-button v-if="canEditArea(row.areaId)" link type="danger" @click="removeReason(row.id)">{{ $t('oee.remove') }}</el-button>
            </template></el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('oee.downtimes')" name="downtimes">
          <el-button v-if="controllableLines.length" type="primary" size="small" data-test="oee-add-downtime" @click="editDowntime()">{{ $t('oee.add') }}</el-button>
          <el-button v-if="controllableLines.length" size="small" data-test="oee-add-correction" @click="editCorrection()">{{ $t('oee.correction') }}</el-button>
          <el-table :data="downtimes" size="small" class="mt" data-test="oee-downtimes">
            <el-table-column prop="lineName" :label="$t('oee.line')" width="120" />
            <el-table-column :label="$t('oee.start')" width="160"><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
            <el-table-column :label="$t('oee.end')" width="160"><template #default="{ row }">{{ row.endTs ? formatTime(row.endTs) : '—' }}</template></el-table-column>
            <el-table-column :label="$t('oee.state')" width="90"><template #default="{ row }">{{ labelOf(stateLabels, row.state) }}</template></el-table-column>
            <el-table-column :label="$t('oee.source')" width="80"><template #default="{ row }">{{ labelOf(sourceLabels, row.source) }}</template></el-table-column>
            <el-table-column :label="$t('oee.confirmed')" width="80"><template #default="{ row }">{{ yesNo(row.confirmed) }}</template></el-table-column>
            <el-table-column width="180"><template #default="{ row }">
              <el-button v-if="canControlLine(row.lineId) && !row.confirmed" link type="primary" @click="confirmDt(row.id)">{{ $t('oee.confirm') }}</el-button>
              <el-button v-if="canControlLine(row.lineId)" link type="danger" @click="removeDt(row.id)">{{ $t('oee.remove') }}</el-button>
            </template></el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="lineDlg" :title="$t('oee.line')" width="520px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="lineFormRef" :model="lineForm" :rules="lineRules" label-width="120px" status-icon>
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="lineForm.name" maxlength="100" data-test="oee-line-name" /></el-form-item>
        <el-form-item :label="$t('oee.area')" prop="areaId"><el-select v-model="lineForm.areaId" style="width:100%" data-test="oee-line-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.idealCycle')" prop="idealCycleSec" :error="fieldErrors.idealCycleSec"><el-input-number v-model="lineForm.idealCycleSec" :min="0.001" :step="1" /></el-form-item>
        <el-form-item :label="$t('oee.product')"><el-select v-model="lineForm.productId" clearable style="width:100%"><el-option v-for="p in products" :key="p.id" :value="p.id" :label="p.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.enabled')"><el-switch v-model="lineForm.enabled" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="lineDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-line-save" @click="saveLine">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="productDlg" :title="$t('oee.product')" width="520px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="productFormRef" :model="productForm" :rules="productRules" label-width="120px" status-icon>
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="productForm.name" maxlength="100" data-test="oee-product-name" /></el-form-item>
        <el-form-item :label="$t('oee.code')" prop="code" :error="fieldErrors.code"><el-input v-model="productForm.code" maxlength="60" /></el-form-item>
        <el-form-item :label="$t('oee.area')"><el-select v-model="productForm.areaId" :clearable="isGlobalConfigure" :placeholder="$t('oee.global')" style="width:100%" data-test="oee-product-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.idealCycle')" prop="idealCycleSec" :error="fieldErrors.idealCycleSec"><el-input-number v-model="productForm.idealCycleSec" :min="0.001" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="productDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-product-save" @click="saveProduct">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="shiftDlg" :title="$t('oee.shift')" width="480px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="shiftFormRef" :model="shiftForm" :rules="shiftRules" label-width="120px" status-icon>
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="shiftForm.name" maxlength="100" data-test="oee-shift-name" /></el-form-item>
        <el-form-item :label="$t('oee.area')" prop="areaId"><el-select v-model="shiftForm.areaId" style="width:100%" data-test="oee-shift-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.startMin')" prop="startMinute"><el-input-number v-model="shiftForm.startMinute" :min="0" :max="1439" /></el-form-item>
        <el-form-item :label="$t('oee.endMin')" prop="endMinute"><el-input-number v-model="shiftForm.endMinute" :min="0" :max="1439" /></el-form-item>
        <el-form-item :label="$t('oee.enabled')"><el-switch v-model="shiftForm.enabled" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="shiftDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-shift-save" @click="saveShift">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="reasonDlg" :title="$t('oee.reason')" width="480px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="reasonFormRef" :model="reasonForm" :rules="reasonRules" label-width="120px" status-icon>
        <el-form-item :label="$t('oee.code')" prop="code" :error="fieldErrors.code"><el-input v-model="reasonForm.code" maxlength="30" data-test="oee-reason-code" /></el-form-item>
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="reasonForm.name" maxlength="100" data-test="oee-reason-name" /></el-form-item>
        <el-form-item :label="$t('oee.category')"><el-select v-model="reasonForm.category" style="width:100%" data-test="oee-reason-category"><el-option v-for="(label, key) in categoryLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.area')"><el-select v-model="reasonForm.areaId" :clearable="isGlobalConfigure" :placeholder="$t('oee.global')" style="width:100%" data-test="oee-reason-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.planned')"><el-switch v-model="reasonForm.isPlanned" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="reasonDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-reason-save" @click="saveReason">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="dtDlg" :title="$t('oee.downtime')" width="520px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="dtFormRef" :model="dtForm" :rules="dtRules" label-width="120px" status-icon>
        <el-form-item :label="$t('oee.line')" prop="lineId"><el-select v-model="dtForm.lineId" style="width:100%" data-test="oee-dt-line"><el-option v-for="l in controllableLines" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.start')" prop="start"><el-date-picker v-model="dtForm.start" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.end')" prop="end" :error="fieldErrors.end"><el-date-picker v-model="dtForm.end" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.state')"><el-select v-model="dtForm.state" style="width:100%" data-test="oee-dt-state"><el-option v-for="key in downtimeStates" :key="key" :value="key" :label="stateLabels[key]" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.reason')"><el-select v-model="dtForm.reasonId" clearable style="width:100%"><el-option v-for="r in reasons" :key="r.id" :value="r.id" :label="`${r.code} ${r.name}`" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.note')"><el-input v-model="dtForm.note" maxlength="500" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="dtDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-dt-save" @click="saveDt">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="corrDlg" :title="$t('oee.correction')" width="520px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="corrFormRef" :model="corrForm" :rules="corrRules" label-width="120px" status-icon>
        <el-form-item :label="$t('oee.line')" prop="lineId"><el-select v-model="corrForm.lineId" style="width:100%" data-test="oee-corr-line"><el-option v-for="l in controllableLines" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.start')" prop="start"><el-date-picker v-model="corrForm.start" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.end')" prop="end" :error="fieldErrors.end"><el-date-picker v-model="corrForm.end" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.good')"><el-input-number v-model="corrForm.goodDelta" data-test="oee-corr-good" /></el-form-item>
        <el-form-item :label="$t('oee.reject')"><el-input-number v-model="corrForm.rejectDelta" /></el-form-item>
        <el-form-item :label="$t('oee.scrap')"><el-input-number v-model="corrForm.scrapDelta" /></el-form-item>
        <el-form-item :label="$t('oee.reason')" prop="reason" :error="fieldErrors.reason"><el-input v-model="corrForm.reason" maxlength="200" data-test="oee-corr-reason" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="corrDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-corr-save" @click="saveCorr">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="thDlg" :title="$t('oee.thresholds')" width="480px">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form label-width="160px" v-if="thForm">
        <el-form-item :label="$t('oee.targetOee')"><el-input-number v-model="thForm.targetOee" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetA')"><el-input-number v-model="thForm.targetAvailability" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetP')"><el-input-number v-model="thForm.targetPerformance" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetQ')"><el-input-number v-model="thForm.targetQuality" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.clampP')"><el-switch v-model="thForm.clampPerformance" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="thDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-th-save" @click="saveTh">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { oeeApi, type DowntimeReason, type OeeThresholds, type ProductionLine, type ProductionProduct, type ProductionShift, type DowntimeEvent } from '../api/oee'
import { areaApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { formatTime } from '../utils'
import { can, canGlobal } from '../access/policies'
import { errorMessage } from '../api/http'
import { useEntityEditor } from '../composables/useEntityEditor'
import { t } from '../i18n'

const auth = useAuthStore()
const categoryLabels: Record<string, string> = { Availability: t('oee.availability'), Performance: t('oee.performance'), Quality: t('oee.quality') }
const stateLabels: Record<string, string> = { Unknown: t('oee.stateUnknown'), Running: t('oee.stateRunning'), Stopped: t('oee.stateStopped'), Fault: t('oee.stateFault'), Setup: t('oee.stateSetup') }
const sourceLabels: Record<string, string> = { manual: t('oee.sourceManual'), tag: t('oee.sourceTag'), alarm: t('oee.sourceAlarm') }
const downtimeStates = ['Stopped', 'Fault', 'Setup']
function labelOf(map: Record<string, string>, value: string): string { return map[value] ?? value }
function yesNo(value: boolean): string { return value ? t('oee.yes') : t('oee.no') }
const canConfigure = computed(() => can(auth.user, 'Configure'))
const isGlobalConfigure = computed(() => canGlobal(auth.user, 'Configure'))
const canEditArea = (areaId?: number | null): boolean =>
  areaId == null ? isGlobalConfigure.value : can(auth.user, 'Configure', areaId)

const cfgTab = ref('lines')
const loading = ref(true)
const actionError = ref('')
const metaError = ref('')
const { saving, fieldErrors, formError, resetErrors, saveWith, remove: confirmRemove } = useEntityEditor(actionError, {
  validationFailed: t('oee.validationFailed'),
  saveFailed: t('oee.saveFailed'),
  saved: t('oee.saved'),
  conflictHint: message => t('oee.conflictHint', [message]),
  removeConfirm: t('oee.removeConfirm'),
  removeTitle: t('oee.remove'),
  removeText: t('oee.remove'),
  cancelText: t('oee.cancel'),
  removeFailed: t('oee.removeFailed')
})
const lineFormRef = ref<FormInstance>(), productFormRef = ref<FormInstance>(), shiftFormRef = ref<FormInstance>()
const reasonFormRef = ref<FormInstance>(), dtFormRef = ref<FormInstance>(), corrFormRef = ref<FormInstance>()

const areas = ref<{ id: number; name: string }[]>([]), lines = ref<ProductionLine[]>([]), products = ref<ProductionProduct[]>([])
const shifts = ref<ProductionShift[]>([]), reasons = ref<DowntimeReason[]>([]), downtimes = ref<DowntimeEvent[]>([])
const configurableAreas = computed(() => areas.value.filter(a => can(auth.user, 'Configure', a.id)))
const controllableLines = computed(() => lines.value.filter(l => can(auth.user, 'Control', l.areaId)))
function canControlLine(lineId: number): boolean { return controllableLines.value.some(l => l.id === lineId) }
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? String(id)
const fmtMin = (m: number) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`

async function refreshMeta() {
  try {
    const [a, l, p, s, r] = await Promise.all([areaApi.list(), oeeApi.lines(), oeeApi.products(), oeeApi.shifts(), oeeApi.reasons()])
    areas.value = a; lines.value = l; products.value = p; shifts.value = s; reasons.value = r
    metaError.value = ''
  } catch (error) {
    metaError.value = errorMessage(error, t('oee.loadFailed'))
  }
}

async function loadDowntimes() {
  try {
    const to = Date.now()
    downtimes.value = await oeeApi.downtimes({ from: to - 7 * 86400000, to })
  } catch (error) {
    actionError.value = errorMessage(error, t('oee.loadFailed'))
  }
}

// ---- 表单校验与服务端约束保持一致，避免提交后才被拒 ----
type RuleCallback = (error?: string | Error) => void
const textRule = (message: string) => [{ required: true, whitespace: true, message, trigger: 'blur' as const }]
const cycleRule = (read: () => { idealCycleSec?: number; ratedUnitsPerHour?: number | null }) => [{
  trigger: 'change',
  validator: (_rule: unknown, _value: unknown, callback: RuleCallback) => {
    const form = read()
    if ((form.idealCycleSec ?? 0) > 0 || (form.ratedUnitsPerHour ?? 0) > 0) callback()
    else callback(new Error(t('oee.cycleRequired')))
  }
}]
const rangeRule = (read: () => { start?: Date | null; end?: Date | null }) => [{
  trigger: 'change',
  validator: (_rule: unknown, _value: unknown, callback: RuleCallback) => {
    const form = read()
    if (!form.start || !form.end) callback(new Error(t('oee.timeRequired')))
    else if (form.end.getTime() <= form.start.getTime()) callback(new Error(t('oee.endAfterStart')))
    else callback()
  }
}]

const lineRules = computed<FormRules>(() => ({
  name: textRule(t('oee.nameRequired')),
  areaId: [{ required: true, message: t('oee.areaRequired'), trigger: 'change' }],
  idealCycleSec: cycleRule(() => lineForm.value)
}))
const productRules = computed<FormRules>(() => ({
  name: textRule(t('oee.nameRequired')),
  idealCycleSec: cycleRule(() => productForm.value)
}))
const shiftRules = computed<FormRules>(() => ({
  name: textRule(t('oee.nameRequired')),
  areaId: [{ required: true, message: t('oee.areaRequired'), trigger: 'change' }]
}))
const reasonRules = computed<FormRules>(() => ({
  code: textRule(t('oee.codeRequired')),
  name: textRule(t('oee.nameRequired'))
}))
const dtRules = computed<FormRules>(() => ({
  lineId: [{ required: true, message: t('oee.lineRequired'), trigger: 'change' }],
  start: rangeRule(() => dtForm.value),
  end: rangeRule(() => dtForm.value)
}))
const corrRules = computed<FormRules>(() => ({
  lineId: [{ required: true, message: t('oee.lineRequired'), trigger: 'change' }],
  start: rangeRule(() => corrForm.value),
  end: rangeRule(() => corrForm.value),
  reason: textRule(t('oee.reasonRequired'))
}))

const lineDlg = ref(false), lineForm = ref<any>({}), lineIdEdit = ref<number | null>(null)
function editLine(row?: ProductionLine) {
  lineIdEdit.value = row?.id ?? null
  resetErrors()
  lineForm.value = row ? { ...row } : { name: '', areaId: configurableAreas.value[0]?.id, idealCycleSec: 60, enabled: true, stateMapJson: '{}' }
  lineDlg.value = true
}
async function saveLine(): Promise<void> {
  if (await saveWith(lineFormRef.value, () => oeeApi.saveLine(lineIdEdit.value, { ...lineForm.value }))) lineDlg.value = false
}

async function removeLine(id: number) { await confirmRemove(() => oeeApi.removeLine(id), refreshMeta) }

const productDlg = ref(false), productForm = ref<any>({}), productIdEdit = ref<number | null>(null)
function defaultAreaId(): number | null {
  if (canGlobal(auth.user, 'Configure')) return null
  return configurableAreas.value[0]?.id ?? null
}
function editProduct(row?: ProductionProduct) {
  productIdEdit.value = row?.id ?? null
  resetErrors()
  productForm.value = row ? { ...row } : { name: '', code: '', areaId: defaultAreaId(), idealCycleSec: 60 }
  productDlg.value = true
}
async function saveProduct(): Promise<void> {
  if (await saveWith(productFormRef.value, () => oeeApi.saveProduct(productIdEdit.value, { ...productForm.value }))) productDlg.value = false
}
async function removeProduct(id: number) { await confirmRemove(() => oeeApi.removeProduct(id), refreshMeta) }

const shiftDlg = ref(false), shiftForm = ref<any>({}), shiftIdEdit = ref<number | null>(null)
function editShift(row?: ProductionShift) {
  shiftIdEdit.value = row?.id ?? null
  resetErrors()
  shiftForm.value = row ? { ...row } : { name: '', areaId: configurableAreas.value[0]?.id, startMinute: 480, endMinute: 1200, days: 127, enabled: true }
  shiftDlg.value = true
}
async function saveShift(): Promise<void> {
  if (await saveWith(shiftFormRef.value, () => oeeApi.saveShift(shiftIdEdit.value, { ...shiftForm.value }))) shiftDlg.value = false
}
async function removeShift(id: number) { await confirmRemove(() => oeeApi.removeShift(id), refreshMeta) }

const reasonDlg = ref(false), reasonForm = ref<any>({}), reasonIdEdit = ref<number | null>(null)
function editReason(row?: DowntimeReason) {
  reasonIdEdit.value = row?.id ?? null
  resetErrors()
  reasonForm.value = row ? { ...row } : { code: '', name: '', category: 'Availability', isPlanned: false, enabled: true, areaId: defaultAreaId() }
  reasonDlg.value = true
}
async function saveReason(): Promise<void> {
  if (await saveWith(reasonFormRef.value, () => oeeApi.saveReason(reasonIdEdit.value, { ...reasonForm.value }))) reasonDlg.value = false
}
async function removeReason(id: number) { await confirmRemove(() => oeeApi.removeReason(id), refreshMeta) }

const dtDlg = ref(false), dtForm = ref<any>({})
function editDowntime() {
  resetErrors()
  dtForm.value = { lineId: controllableLines.value[0]?.id, start: new Date(Date.now() - 3600000), end: new Date(), state: 'Fault', reasonId: null, note: '' }
  dtDlg.value = true
}
async function saveDt(): Promise<void> {
  const form = dtForm.value
  const ok = await saveWith(dtFormRef.value, () => oeeApi.createDowntime({
    lineId: form.lineId, startTs: form.start.getTime(), endTs: form.end?.getTime(),
    state: form.state, reasonId: form.reasonId, note: form.note, confirmed: true
  }), loadDowntimes)
  if (ok) dtDlg.value = false
}
async function confirmDt(id: number) { await oeeApi.confirmDowntime(id); await loadDowntimes() }
async function removeDt(id: number) { await confirmRemove(() => oeeApi.removeDowntime(id), loadDowntimes) }

const corrDlg = ref(false), corrForm = ref<any>({})
function editCorrection() {
  resetErrors()
  const to = Date.now()
  corrForm.value = { lineId: controllableLines.value[0]?.id, start: new Date(to - 3600000), end: new Date(to), goodDelta: 0, rejectDelta: 0, scrapDelta: 0, reason: '' }
  corrDlg.value = true
}
async function saveCorr(): Promise<void> {
  const form = corrForm.value
  const ok = await saveWith(corrFormRef.value, () => oeeApi.correct({
    lineId: form.lineId, periodFrom: form.start.getTime(), periodTo: form.end.getTime(),
    goodDelta: form.goodDelta, rejectDelta: form.rejectDelta, scrapDelta: form.scrapDelta, reason: form.reason
  }), loadDowntimes)
  if (ok) corrDlg.value = false
}

const thDlg = ref(false), thForm = ref<OeeThresholds | null>(null), thRev = ref(0)
function isConflict(error: unknown): boolean {
  return (error as { response?: { status?: number } } | undefined)?.response?.status === 409
}
async function openThresholds() {
  actionError.value = ''
  formError.value = ''
  try {
    const r = await oeeApi.thresholds(); thForm.value = { ...r.thresholds }; thRev.value = r.revision; formError.value = ''; thDlg.value = true
  } catch (error) {
    actionError.value = errorMessage(error, t('oee.loadFailed'))
  }
}
async function saveTh(): Promise<void> {
  if (!thForm.value || saving.value) return
  saving.value = true
  formError.value = ''
  try {
    await oeeApi.saveThresholds(thForm.value, thRev.value)
    ElMessage.success(t('oee.saved'))
    thDlg.value = false
  } catch (error) {
    const message = errorMessage(error, t('oee.saveFailed'))
    formError.value = isConflict(error) ? t('oee.conflictHint', [message]) : message
  } finally {
    saving.value = false
  }
}

onMounted(async () => { try { await refreshMeta(); await loadDowntimes() } finally { loading.value = false } })
</script>

<style scoped>
.toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
.spacer { flex: 1; }
.mt { margin-top: 8px; }
</style>
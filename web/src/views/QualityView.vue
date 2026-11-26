<template>
  <div class="page quality-page">
    <div class="panel">
      <div class="toolbar">
        <span class="hint">{{ $t('quality.subtitle') }}</span>
        <span class="spacer" />
        <el-button data-test="quality-refresh" @click="refreshActive">{{ $t('quality.refresh') }}</el-button>
      </div>
      <ShiftTimeZoneBar />
      <el-alert v-if="catalogError" :title="catalogError" type="error" :closable="false" show-icon data-test="quality-catalog-error" />

      <el-tabs v-model="tab" class="quality-tabs">
        <el-tab-pane :label="$t('quality.tabs.records')" name="records">
          <div class="toolbar filter-row">
            <el-input v-model="recordSearch" :placeholder="$t('quality.search')" clearable style="width: 240px" data-test="quality-search" @keyup.enter="queryRecords" @clear="queryRecords" />
            <el-select v-model="recordResult" clearable :placeholder="$t('quality.allResults')" style="width: 130px" data-test="quality-result-filter" @change="queryRecords">
              <el-option v-for="result in resultOptions" :key="result" :label="resultLabels[result]" :value="result" />
            </el-select>
            <el-select v-model="recordDefectCodeId" clearable filterable :placeholder="$t('quality.defectCode')" style="width: 170px" data-test="quality-defect-filter" @change="queryRecords">
              <el-option v-for="code in defectCodes" :key="code.id" :label="`${code.code} ${code.name}`" :value="code.id" />
            </el-select>
            <el-select v-model="recordAreaId" clearable :placeholder="$t('quality.allAreas')" style="width: 130px" data-test="quality-area-filter" @change="queryRecords">
              <el-option v-for="area in areas" :key="area.id" :label="area.name" :value="area.id" />
            </el-select>
            <el-date-picker v-model="recordRange" type="datetimerange" :start-placeholder="$t('quality.from')" :end-placeholder="$t('quality.to')" :aria-label="$t('common.timeRange')" style="width: 340px" @change="queryRecords" />
            <el-checkbox v-model="includeDeleted" data-test="quality-include-deleted" @change="queryRecords">{{ $t('quality.includeDeleted') }}</el-checkbox>
            <span class="spacer" />
            <el-button :loading="exporting === 'records'" data-test="quality-export-records" @click="exportCurrent('records', 'xlsx')">{{ $t('quality.exportExcel') }}</el-button>
            <el-button :loading="exporting === 'records-pdf'" data-test="quality-export-records-pdf" @click="exportCurrent('records', 'pdf')">{{ $t('quality.exportPdf') }}</el-button>
            <el-button v-if="canControlAny" type="primary" data-test="quality-add" @click="openInspection()">{{ $t('quality.addInspection') }}</el-button>
          </div>
          <el-alert v-if="recordError" :title="recordError" type="error" :closable="false" show-icon data-test="quality-record-error" />
          <el-table :data="records" size="small" v-loading="recordLoading" :empty-text="$t('quality.empty')" data-test="quality-records">
            <el-table-column :label="$t('quality.time')" width="150">
              <template #default="{ row }">{{ formatTime(row.ts) }}</template>
            </el-table-column>
            <el-table-column :label="$t('quality.sn')" width="140" show-overflow-tooltip>
              <template #default="{ row }">
                <router-link v-if="row.sn" :to="{ path: '/traceability', query: { sn: row.sn } }" class="link">{{ row.sn }}</router-link>
                <span v-else class="muted">-</span>
              </template>
            </el-table-column>
            <el-table-column prop="batchNo" :label="$t('quality.batchNo')" width="120" show-overflow-tooltip />
            <el-table-column prop="product" :label="$t('quality.product')" width="120" show-overflow-tooltip />
            <el-table-column :label="$t('quality.area')" width="100" show-overflow-tooltip>
              <template #default="{ row }">{{ areaName(row.areaId) }}</template>
            </el-table-column>
            <el-table-column :label="$t('quality.result')" width="95">
              <template #default="{ row }"><el-tag :type="resultType(row.result)" size="small">{{ resultLabel(row.result) }}</el-tag></template>
            </el-table-column>
            <el-table-column :label="$t('quality.defectCode')" min-width="130" show-overflow-tooltip>
              <template #default="{ row }">{{ row.defectCode ? `${row.defectCode} ${row.defectName}` : '-' }}</template>
            </el-table-column>
            <el-table-column prop="deviceName" :label="$t('quality.device')" width="120" show-overflow-tooltip />
            <el-table-column prop="tagName" :label="$t('quality.tag')" width="130" show-overflow-tooltip />
            <el-table-column prop="inspector" :label="$t('quality.inspector')" width="100" show-overflow-tooltip />
            <el-table-column prop="createdBy" :label="$t('quality.createdBy')" width="100" show-overflow-tooltip />
            <el-table-column :label="$t('quality.updated')" width="200" show-overflow-tooltip>
              <template #default="{ row }">
                <span>{{ row.updatedTs ? formatTime(row.updatedTs) : '—' }}</span>
                <span v-if="row.updatedBy" class="muted"> {{ row.updatedBy }}</span>
                <el-tag v-if="isRevised(row)" type="warning" size="small" class="revised" data-test="quality-revised">{{ $t('quality.revised') }}</el-tag>
                <el-tag v-if="row.deletedTs" type="info" size="small" data-test="quality-deleted">{{ $t('quality.deletedTag') }}</el-tag>
                <div v-if="row.amendmentReason || row.deleteReason" class="tiny muted">{{ row.deleteReason || row.amendmentReason }}</div>
              </template>
            </el-table-column>
            <el-table-column :label="$t('quality.actions')" width="280" fixed="right">
              <template #default="{ row }">
                <el-button v-if="row.tagId" link type="primary" data-test="quality-trend" @click="openTrend(row)">{{ $t('quality.viewTrend') }}</el-button>
                <el-button link data-test="quality-history" @click="openRevisions(row)">{{ $t('quality.history') }}</el-button>
                <el-button v-if="canConfigureArea(row.areaId) && !row.deletedTs" link data-test="quality-edit" @click="openInspection(row)">{{ $t('quality.edit') }}</el-button>
                <el-button v-if="canConfigureArea(row.areaId) && !row.deletedTs" link type="danger" data-test="quality-delete" @click="removeInspection(row)">{{ $t('quality.delete') }}</el-button>
              </template>
            </el-table-column>
          </el-table>
          <el-pagination v-if="recordTotal > 0" v-model:current-page="recordPage" :page-size="recordPageSize" :total="recordTotal"
            layout="total, prev, pager, next" class="pager" data-test="quality-pager" @current-change="loadRecords" />
        </el-tab-pane>

        <el-tab-pane :label="$t('quality.tabs.dashboard')" name="dashboard">
          <div class="toolbar filter-row">
            <el-select v-model="dashboardDays" style="width: 140px" data-test="quality-dashboard-range" @change="loadSummary">
              <el-option :label="$t('quality.lastDays', [7])" :value="7" />
              <el-option :label="$t('quality.lastDays', [30])" :value="30" />
              <el-option :label="$t('quality.lastDays', [90])" :value="90" />
            </el-select>
            <el-select v-model="dashboardAreaId" clearable :placeholder="$t('quality.allAreas')" style="width: 140px" data-test="quality-dashboard-area" @change="loadSummary">
              <el-option v-for="area in areas" :key="area.id" :label="area.name" :value="area.id" />
            </el-select>
            <el-input v-model="dashboardProduct" :placeholder="$t('quality.product')" clearable style="width: 150px" @keyup.enter="loadSummary" @clear="loadSummary" />
            <el-input v-model="dashboardBatchNo" :placeholder="$t('quality.batchNo')" clearable style="width: 150px" @keyup.enter="loadSummary" @clear="loadSummary" />
            <el-button @click="loadSummary">{{ $t('quality.query') }}</el-button>
            <span class="spacer" />
            <el-button :loading="exporting === 'dashboard'" data-test="quality-export-summary" @click="exportCurrent('dashboard', 'xlsx')">{{ $t('quality.exportExcel') }}</el-button>
            <el-button :loading="exporting === 'dashboard-pdf'" data-test="quality-export-summary-pdf" @click="exportCurrent('dashboard', 'pdf')">{{ $t('quality.exportPdf') }}</el-button>
          </div>
          <el-alert v-if="summaryError" :title="summaryError" type="error" :closable="false" show-icon data-test="quality-summary-error" />
          <template v-if="summary">
            <div class="kpi-row">
              <div class="kpi-card"><span class="kpi-label">{{ $t('quality.total') }}</span><strong>{{ summary.total }}</strong></div>
              <div class="kpi-card"><span class="kpi-label">{{ $t('quality.yield') }}</span><strong>{{ formatPercent(summary.yield) }}</strong></div>
              <div class="kpi-card"><span class="kpi-label">{{ $t('quality.defectRate') }}</span><strong>{{ formatPercent(summary.defectRate) }}</strong></div>
              <div class="kpi-card"><span class="kpi-label">{{ $t('quality.scrapRate') }}</span><strong>{{ formatPercent(summary.scrapRate) }}</strong></div>
            </div>
            <div class="chart-grid">
              <div><div class="panel-subtitle">{{ $t('quality.yieldTrend') }}</div><div ref="trendChartRef" class="quality-chart" data-test="quality-trend-chart" /></div>
              <div><div class="panel-subtitle">{{ $t('quality.pareto') }} <span class="muted tiny">{{ $t('quality.paretoHint') }}</span></div><div ref="paretoChartRef" class="quality-chart" data-test="quality-pareto-chart" /></div>
            </div>
            <div class="panel-subtitle">{{ $t('quality.byDevice') }}</div>
            <el-table :data="summary.byDevice" size="small" :empty-text="$t('quality.noData')" data-test="quality-by-device">
              <el-table-column prop="deviceName" :label="$t('quality.device')" min-width="160" show-overflow-tooltip />
              <el-table-column prop="total" :label="$t('quality.total')" width="100" />
              <el-table-column prop="passed" :label="$t('quality.passed')" width="100" />
              <el-table-column prop="failed" :label="$t('quality.failed')" width="100" />
              <el-table-column :label="$t('quality.yield')" width="100">
                <template #default="{ row }">{{ formatPercent(row.yield) }}</template>
              </el-table-column>
            </el-table>
          </template>
        </el-tab-pane>

        <el-tab-pane :label="$t('quality.tabs.defectCodes')" name="defectCodes">
          <div class="toolbar filter-row">
            <el-checkbox v-model="defectEnabledOnly" data-test="quality-defect-enabled" @change="loadDefectCodes">{{ $t('quality.enabledOnly') }}</el-checkbox>
            <span class="spacer" />
            <el-button v-if="canConfigureAny" type="primary" data-test="quality-defect-add" @click="openDefectCode()">{{ $t('quality.addDefectCode') }}</el-button>
          </div>
          <el-alert v-if="defectError" :title="defectError" type="error" :closable="false" show-icon data-test="quality-defect-error" />
          <el-table :data="defectCodes" size="small" v-loading="defectLoading" :empty-text="$t('quality.noDefectCodes')" data-test="quality-defect-table">
            <el-table-column prop="code" :label="$t('quality.defectCodeShort')" width="130" show-overflow-tooltip />
            <el-table-column prop="name" :label="$t('quality.defectName')" min-width="160" show-overflow-tooltip />
            <el-table-column prop="category" :label="$t('quality.defectCategory')" width="130" show-overflow-tooltip />
            <el-table-column prop="description" :label="$t('quality.description')" min-width="200" show-overflow-tooltip />
            <el-table-column :label="$t('quality.enabled')" width="80">
              <template #default="{ row }">{{ row.enabled ? $t('quality.yes') : $t('quality.no') }}</template>
            </el-table-column>
            <el-table-column :label="$t('quality.actions')" width="150" fixed="right">
              <template #default="{ row }">
                <el-button v-if="canConfigureAny" link type="primary" data-test="quality-defect-edit" @click="openDefectCode(row)">{{ $t('quality.edit') }}</el-button>
                <el-popconfirm v-if="canConfigureAny" :title="$t('quality.confirmDeleteDefect', [row.code])" @confirm="removeDefectCode(row)">
                  <template #reference><el-button link type="danger">{{ $t('quality.delete') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="inspectionDialog" :title="inspectionForm.id ? $t('quality.editInspection') : $t('quality.addInspection')" width="760px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="quality-save-error" />
      <el-form label-width="110px" size="small">
        <el-row :gutter="12">
          <el-col :span="12"><el-form-item :label="$t('quality.sn')"><el-input v-model="inspectionForm.sn" data-test="quality-sn" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('quality.batchNo')"><el-input v-model="inspectionForm.batchNo" data-test="quality-batch" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item :label="$t('quality.product')"><el-input v-model="inspectionForm.product" /></el-form-item></el-col>
          <el-col :span="12">
            <el-form-item :label="$t('quality.time')">
              <el-date-picker v-model="inspectionForm.tsDate" type="datetime" style="width: 100%" data-test="quality-ts" />
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('quality.area')">
              <el-select v-model="inspectionForm.areaId" style="width: 100%" data-test="quality-area" @change="onInspectionAreaChange">
                <el-option v-for="area in inspectionAreaOptions" :key="area.id" :label="area.name" :value="area.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('quality.device')">
              <el-select v-model="inspectionForm.deviceId" clearable filterable :placeholder="$t('quality.none')" style="width: 100%">
                <el-option v-for="device in inspectionDeviceOptions" :key="device.id" :label="device.name" :value="device.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('quality.tag')">
              <el-select v-model="inspectionForm.tagId" clearable filterable :placeholder="$t('quality.none')" style="width: 100%">
                <el-option v-for="tag in inspectionTagOptions" :key="tag.id" :label="`#${tag.id} ${tag.name}`" :value="tag.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('quality.result')">
              <el-select v-model="inspectionForm.result" style="width: 100%" data-test="quality-result" @change="onResultChange">
                <el-option v-for="result in resultOptions" :key="result" :label="resultLabels[result]" :value="result" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item :label="$t('quality.defectCode')">
              <el-select v-model="inspectionForm.defectCodeId" clearable filterable :placeholder="$t('quality.none')" style="width: 100%" data-test="quality-defect">
                <el-option v-for="code in defectCodes.filter(item => item.enabled)" :key="code.id" :label="`${code.code} ${code.name}`" :value="code.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12"><el-form-item :label="$t('quality.inspector')"><el-input v-model="inspectionForm.inspector" /></el-form-item></el-col>
          <el-col :span="24"><el-form-item :label="$t('quality.note')"><el-input v-model="inspectionForm.note" type="textarea" :rows="2" /></el-form-item></el-col>
          <el-col v-if="inspectionForm.id" :span="24">
            <el-form-item :label="$t('quality.amendmentReason')" required>
              <el-input v-model="inspectionForm.amendmentReason" type="textarea" :rows="2" maxlength="500" show-word-limit data-test="quality-amendment-reason" />
            </el-form-item>
          </el-col>
          <el-col v-if="inspectionAudit" :span="24">
            <p class="tiny muted" data-test="quality-audit">
              {{ $t('quality.createdBy') }}：{{ inspectionAudit.createdBy || '—' }} · {{ formatTime(inspectionAudit.createdTs) }}
              <template v-if="inspectionAudit.updatedTs">
                · {{ $t('quality.updated') }}：{{ inspectionAudit.updatedBy || '—' }} · {{ formatTime(inspectionAudit.updatedTs) }}
                <el-tag v-if="inspectionAudit.updatedTs > inspectionAudit.createdTs" type="warning" size="small">{{ $t('quality.revised') }}</el-tag>
              </template>
            </p>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="inspectionDialog = false">{{ $t('quality.cancel') }}</el-button>
        <el-button type="primary" :loading="saving" data-test="quality-save" @click="saveInspection">{{ $t('quality.save') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="revisionDialog" :title="$t('quality.historyTitle')" width="720px" destroy-on-close>
      <el-alert v-if="revisionError" :title="revisionError" type="error" :closable="false" show-icon />
      <el-table :data="revisions" size="small" v-loading="revisionLoading" :empty-text="$t('quality.noRevisions')" data-test="quality-revisions">
        <el-table-column :label="$t('quality.time')" width="160"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
        <el-table-column :label="$t('quality.historyAction')" width="90"><template #default="{ row }">{{ actionLabel(row.action) }}</template></el-table-column>
        <el-table-column prop="user" :label="$t('quality.inspector')" width="110" show-overflow-tooltip />
        <el-table-column prop="reason" :label="$t('quality.amendmentReason')" min-width="180" show-overflow-tooltip />
      </el-table>
    </el-dialog>
    <el-dialog v-model="defectDialog" :title="defectForm.id ? $t('quality.editDefectCode') : $t('quality.addDefectCode')" width="560px" destroy-on-close>
      <el-alert v-if="dialogError" :title="dialogError" type="error" :closable="false" show-icon data-test="quality-defect-save-error" />
      <el-form label-width="100px" size="small">
        <el-form-item :label="$t('quality.defectCodeShort')"><el-input v-model="defectForm.code" data-test="quality-defect-code" /></el-form-item>
        <el-form-item :label="$t('quality.defectName')"><el-input v-model="defectForm.name" data-test="quality-defect-name" /></el-form-item>
        <el-form-item :label="$t('quality.defectCategory')"><el-input v-model="defectForm.category" /></el-form-item>
        <el-form-item :label="$t('quality.description')"><el-input v-model="defectForm.description" type="textarea" :rows="2" /></el-form-item>
        <el-form-item :label="$t('quality.enabled')"><el-switch v-model="defectForm.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="defectDialog = false">{{ $t('quality.cancel') }}</el-button>
        <el-button type="primary" :loading="saving" data-test="quality-defect-save" @click="saveDefectCode">{{ $t('quality.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import ShiftTimeZoneBar from '../components/ShiftTimeZoneBar.vue'
import { computed, nextTick, onMounted, onUnmounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import * as echarts from '../charts'
import { areaApi, deviceApi, qualityApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { can } from '../access/policies'
import { tr } from '../i18n'
import { chartPalette, onThemeChanged } from '../theme'
import { useAuthStore } from '../stores/auth'
import { downloadBlob, formatTime } from '../utils'
import type {
  Area, Device, QualityDefectCode, QualityDefectCodeInput, QualityInspection, QualityInspectionInput,
  QualityResult, QualityRevision, QualitySummary, Tag
} from '../types'

const auth = useAuthStore()
const router = useRouter()
const tab = ref('records')
const areas = ref<Area[]>([])
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const defectCodes = ref<QualityDefectCode[]>([])
const records = ref<QualityInspection[]>([])
const summary = ref<QualitySummary | null>(null)
const recordLoading = ref(false), defectLoading = ref(false), saving = ref(false)
const recordError = ref(''), summaryError = ref(''), defectError = ref(''), catalogError = ref(''), dialogError = ref('')
const exporting = ref('')
const inspectionAudit = ref<{ createdBy: string; createdTs: number; updatedBy: string; updatedTs: number } | null>(null)

const recordSearch = ref('')
const recordResult = ref<QualityResult | ''>('')
const recordDefectCodeId = ref<number | null>(null)
const recordAreaId = ref<number | null>(null)
const recordRange = ref<[Date, Date]>([new Date(Date.now() - 7 * 24 * 3600_000), new Date()])
const recordPage = ref(1), recordPageSize = 50, recordTotal = ref(0)
const dashboardDays = ref(7), dashboardAreaId = ref<number | null>(null)
const dashboardProduct = ref(''), dashboardBatchNo = ref('')
const defectEnabledOnly = ref(false)

const resultOptions: QualityResult[] = ['Passed', 'Failed', 'Rework', 'Scrap']
const resultLabels: Record<QualityResult, string> = {
  Passed: tr('quality.resultPassed'), Failed: tr('quality.resultFailed'),
  Rework: tr('quality.resultRework'), Scrap: tr('quality.resultScrap')
}
const canConfigureAny = computed(() => can(auth.user, 'Configure'))
const canControlAny = computed(() => can(auth.user, 'Control'))
const controlAreas = computed(() => areas.value.filter(area => can(auth.user, 'Control', area.id)))
const canConfigureArea = (areaId: number) => can(auth.user, 'Configure', areaId)

const inspectionDialog = ref(false), defectDialog = ref(false), revisionDialog = ref(false)
const revisions = ref<QualityRevision[]>([])
const revisionLoading = ref(false), revisionError = ref('')
const includeDeleted = ref(false)
const originalKey = ref<{ sn: string; result: QualityResult; ts: number; areaId: number } | null>(null)
const trendChartRef = ref<HTMLDivElement>(), paretoChartRef = ref<HTMLDivElement>()
let trendChart: echarts.ECharts | null = null
let paretoChart: echarts.ECharts | null = null

const inspectionForm = reactive({
  id: 0, sn: '', batchNo: '', product: '', tsDate: new Date() as Date | null,
  areaId: null as number | null, deviceId: null as number | null, tagId: null as number | null,
  result: 'Passed' as QualityResult, defectCodeId: null as number | null, inspector: '', note: '', amendmentReason: ''
})
const defectForm = reactive({ id: 0, code: '', name: '', category: '', description: '', enabled: true })
const inspectionAreaOptions = computed(() => areas.value.filter(area =>
  can(auth.user, 'Control', area.id) || (inspectionForm.id > 0 && can(auth.user, 'Configure', area.id))))

function areaName(id: number): string { return areas.value.find(area => area.id === id)?.name ?? `#${id}` }
function formatPercent(value: number | null): string {
  return value == null ? '-' : `${(value * 100).toFixed(1)}%`
}
function resultLabel(result: QualityResult): string { return resultLabels[result] }
function resultType(result: QualityResult): string {
  return result === 'Passed' ? 'success' : result === 'Failed' ? 'danger' : result === 'Rework' ? 'warning' : 'info'
}
function tagAreaId(tag: Tag): number | null {
  if (tag.areaId != null) return tag.areaId
  if (tag.deviceId != null) return devices.value.find(device => device.id === tag.deviceId)?.areaId ?? null
  return null
}
const inspectionDeviceOptions = computed(() => devices.value.filter(device =>
  !inspectionForm.areaId || device.areaId === inspectionForm.areaId))
const inspectionTagOptions = computed(() => tags.value.filter(tag =>
  !inspectionForm.areaId || tagAreaId(tag) === inspectionForm.areaId))

async function loadCatalogs() {
  try {
    const [areaList, deviceList, tagList] = await Promise.all([areaApi.list(), deviceApi.list(), tagApi.list()])
    areas.value = areaList
    devices.value = deviceList
    tags.value = tagList
    catalogError.value = ''
  } catch (e) {
    catalogError.value = errorMessage(e, tr('quality.loadFailed'))
  }
}
async function loadDefectCodes() {
  defectLoading.value = true
  try {
    defectCodes.value = await qualityApi.defectCodes({ enabled: defectEnabledOnly.value || undefined })
    defectError.value = ''
  } catch (e) {
    defectError.value = errorMessage(e, tr('quality.loadFailed'))
  } finally {
    defectLoading.value = false
  }
}
let recordsSeq = 0
let summarySeq = 0
async function loadRecords() {
  const seq = ++recordsSeq
  recordLoading.value = true
  try {
    const from = recordRange.value?.[0]?.getTime()
    const to = recordRange.value?.[1]?.getTime()
    const page = await qualityApi.inspections({
      search: recordSearch.value.trim() || undefined,
      result: recordResult.value || undefined,
      defectCodeId: recordDefectCodeId.value ?? undefined,
      areaId: recordAreaId.value ?? undefined,
      from, to, page: recordPage.value, pageSize: recordPageSize,
      includeDeleted: includeDeleted.value || undefined
    })
    if (seq !== recordsSeq) return
    records.value = page.items
    recordTotal.value = page.total
    recordError.value = ''
  } catch (e) {
    if (seq !== recordsSeq) return
    recordError.value = errorMessage(e, tr('quality.loadFailed'))
  } finally {
    if (seq === recordsSeq) recordLoading.value = false
  }
}
function queryRecords() {
  recordPage.value = 1
  void loadRecords()
}
function isRevised(row: QualityInspection): boolean {
  return !!row.amendmentReason || (!!row.updatedTs && row.updatedTs > (row.createdTs || 0) && !row.deletedTs)
}
function actionLabel(action: string): string {
  if (action === 'create') return tr('quality.actionCreate')
  if (action === 'update') return tr('quality.actionUpdate')
  if (action === 'delete') return tr('quality.actionDelete')
  return action
}
async function openRevisions(row: QualityInspection) {
  revisionDialog.value = true
  revisionLoading.value = true
  revisionError.value = ''
  try {
    revisions.value = await qualityApi.revisions(row.id)
  } catch (e) {
    revisions.value = []
    revisionError.value = errorMessage(e, tr('quality.loadFailed'))
  } finally {
    revisionLoading.value = false
  }
}
async function exportCurrent(scope: 'records' | 'dashboard', format: 'xlsx' | 'pdf') {
  const key = format === 'pdf' ? `${scope}-pdf` : scope
  if (exporting.value) return
  exporting.value = key
  const targetError = scope === 'records' ? recordError : summaryError
  targetError.value = ''
  try {
    const params: Record<string, unknown> = { format }
    if (scope === 'records') {
      params.search = recordSearch.value.trim() || undefined
      params.result = recordResult.value || undefined
      params.defectCodeId = recordDefectCodeId.value ?? undefined
      params.areaId = recordAreaId.value ?? undefined
      params.from = recordRange.value?.[0]?.getTime()
      params.to = recordRange.value?.[1]?.getTime()
    } else {
      params.to = Date.now()
      params.from = Number(params.to) - dashboardDays.value * 24 * 3600_000
      params.areaId = dashboardAreaId.value ?? undefined
      params.product = dashboardProduct.value.trim() || undefined
      params.batchNo = dashboardBatchNo.value.trim() || undefined
    }
    const url = qualityApi.exportUrl(params)
    const token = auth.token
    const r = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
    if (!r.ok) {
      const body = await r.text().catch(() => '')
      let detail = body
      try { detail = (JSON.parse(body) as { message?: string }).message ?? body } catch { /* 非 JSON 就按原文 */ }
      targetError.value = detail.slice(0, 200) || tr('quality.loadFailed')
      return
    }
    downloadBlob(await r.blob(), `quality.${format}`)
  } catch (e) {
    targetError.value = errorMessage(e, tr('quality.loadFailed'))
  } finally {
    exporting.value = ''
  }
}
async function loadSummary() {
  const seq = ++summarySeq
  try {
    const to = Date.now()
    const from = to - dashboardDays.value * 24 * 3600_000
    const result = await qualityApi.summary({
      from, to, areaId: dashboardAreaId.value ?? undefined,
      product: dashboardProduct.value.trim() || undefined,
      batchNo: dashboardBatchNo.value.trim() || undefined
    })
    if (seq !== summarySeq) return
    summary.value = result
    summaryError.value = ''
    await nextTick()
    if (seq !== summarySeq) return
    renderSummary()
  } catch (e) {
    if (seq !== summarySeq) return
    summaryError.value = errorMessage(e, tr('quality.loadFailed'))
  }
}
function renderSummary() {
  if (!summary.value) return
  if (trendChartRef.value && !trendChart) trendChart = echarts.init(trendChartRef.value)
  if (paretoChartRef.value && !paretoChart) paretoChart = echarts.init(paretoChartRef.value)
  const data = summary.value
  const p = chartPalette()
  const tooltip = { backgroundColor: p.annotationBg, borderColor: p.split, textStyle: { color: p.annotationText } }
  const split = { lineStyle: { color: p.split } }
  trendChart?.setOption({
    textStyle: { color: p.text },
    tooltip: { trigger: 'axis', ...tooltip },
    grid: { left: 50, right: 20, top: 20, bottom: 34 },
    xAxis: { type: 'category', data: data.byDay.map(day => day.date), axisLabel: { color: p.muted }, splitLine: split },
    yAxis: { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%', color: p.muted }, splitLine: split },
    series: [{
      name: tr('quality.yield'), type: 'line', smooth: true, showSymbol: true,
      data: data.byDay.map(day => day.yield == null ? null : Number((day.yield * 100).toFixed(2))
      )
    }]
  }, true)
  const totalDefects = data.byDefect.reduce((sum, point) => sum + point.count, 0)
  let running = 0
  paretoChart?.setOption({
    textStyle: { color: p.text },
    tooltip: { trigger: 'axis', axisPointer: { type: 'shadow' }, ...tooltip },
    grid: { left: 50, right: 50, top: 20, bottom: 60 },
    xAxis: { type: 'category', data: data.byDefect.map(point => point.name), axisLabel: { interval: 0, rotate: 25, color: p.muted }, splitLine: split },
    yAxis: [
      { type: 'value', name: tr('quality.defectCount'), nameTextStyle: { color: p.muted }, axisLabel: { color: p.muted }, splitLine: split },
      { type: 'value', min: 0, max: 100, axisLabel: { formatter: '{value}%', color: p.muted }, splitLine: split }
    ],
    series: [
      { name: tr('quality.defectCount'), type: 'bar', data: data.byDefect.map(point => point.count), barMaxWidth: 28 },
      {
        name: tr('quality.cumulative'), type: 'line', yAxisIndex: 1, smooth: false, showSymbol: true,
        data: data.byDefect.map(point => {
          running += point.count
          return totalDefects > 0 ? Number((running / totalDefects * 100).toFixed(2)) : 0
        })
      }
    ]
  }, true)
  type ChartClickParams = { componentType?: string; seriesType?: string; dataIndex?: number }
  paretoChart?.off('click')
  paretoChart?.on('click', params => {
    const event = params as ChartClickParams
    if (event.componentType !== 'series' || event.seriesType !== 'bar' || event.dataIndex == null) return
    const point = data.byDefect[event.dataIndex]
    if (!point?.defectCodeId) return
    tab.value = 'records'
    recordDefectCodeId.value = point.defectCodeId
    const to = Date.now()
    recordRange.value = [new Date(to - dashboardDays.value * 24 * 3600_000), new Date(to)]
    queryRecords()
  })
}
watch(tab, value => {
  if (value === 'dashboard') void nextTick().then(renderSummary)
  else if (value === 'defectCodes') void loadDefectCodes()
})

function openInspection(row?: QualityInspection) {
  inspectionForm.id = row?.id ?? 0
  inspectionForm.sn = row?.sn ?? ''
  inspectionForm.batchNo = row?.batchNo ?? ''
  inspectionForm.product = row?.product ?? ''
  inspectionForm.tsDate = row ? new Date(row.ts) : new Date()
  inspectionForm.areaId = row?.areaId ?? controlAreas.value[0]?.id ?? null
  inspectionForm.deviceId = row?.deviceId ?? null
  inspectionForm.tagId = row?.tagId ?? null
  inspectionForm.result = row?.result ?? 'Passed'
  inspectionForm.defectCodeId = row?.defectCodeId ?? null
  inspectionForm.inspector = row?.inspector ?? ''
  inspectionForm.note = row?.note ?? ''
  inspectionForm.amendmentReason = ''
  originalKey.value = row ? { sn: row.sn, result: row.result, ts: row.ts, areaId: row.areaId } : null
  inspectionAudit.value = row ? { createdBy: row.createdBy, createdTs: row.createdTs, updatedBy: row.updatedBy, updatedTs: row.updatedTs } : null
  dialogError.value = ''
  inspectionDialog.value = true
}
function onInspectionAreaChange() {
  const device = devices.value.find(item => item.id === inspectionForm.deviceId)
  if (device && device.areaId !== inspectionForm.areaId) inspectionForm.deviceId = null
  const tag = tags.value.find(item => item.id === inspectionForm.tagId)
  if (tag && tagAreaId(tag) !== inspectionForm.areaId) inspectionForm.tagId = null
}
function onResultChange() {
  if (inspectionForm.result === 'Passed') inspectionForm.defectCodeId = null
}
async function saveInspection() {
  if (inspectionForm.areaId == null) {
    dialogError.value = tr('quality.noAreas')
    return
  }
  if (inspectionForm.id && !inspectionForm.amendmentReason.trim()) {
    dialogError.value = tr('quality.reasonRequired')
    return
  }
  const nextTs = inspectionForm.tsDate?.getTime() ?? null
  const keyChanged = !!(inspectionForm.id && originalKey.value && (
    inspectionForm.sn.trim() !== originalKey.value.sn
    || inspectionForm.result !== originalKey.value.result
    || inspectionForm.areaId !== originalKey.value.areaId
    || nextTs !== originalKey.value.ts
  ))
  if (keyChanged) {
    try {
      await ElMessageBox.confirm(tr('quality.confirmKeyChange'), tr('quality.editInspection'), { type: 'warning' })
    } catch {
      return
    }
  }
  saving.value = true
  dialogError.value = ''
  try {
    const input: QualityInspectionInput = {
      sn: inspectionForm.sn.trim(), batchNo: inspectionForm.batchNo.trim(), product: inspectionForm.product.trim(),
      areaId: inspectionForm.areaId, deviceId: inspectionForm.deviceId, tagId: inspectionForm.tagId,
      result: inspectionForm.result, defectCodeId: inspectionForm.defectCodeId,
      inspector: inspectionForm.inspector.trim(), ts: nextTs,
      note: inspectionForm.note.trim(),
      amendmentReason: inspectionForm.id ? inspectionForm.amendmentReason.trim() : undefined
    }
    if (inspectionForm.id) await qualityApi.updateInspection(inspectionForm.id, input)
    else await qualityApi.createInspection(input)
    inspectionDialog.value = false
    await Promise.all([loadRecords(), loadSummary()])
    ElMessage.success(tr('quality.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('quality.saveFailed'))
  } finally {
    saving.value = false
  }
}
async function removeInspection(row: QualityInspection) {
  recordError.value = ''
  try {
    const { value } = await ElMessageBox.prompt(
      tr('quality.confirmDeleteInspection', [row.sn || row.id]),
      tr('quality.delete'),
      { inputPlaceholder: tr('quality.deleteReason'), inputValidator: (text: string) => !!text?.trim() || tr('quality.reasonRequired'), type: 'warning' }
    )
    await qualityApi.removeInspection(row.id, String(value).trim())
    await Promise.all([loadRecords(), loadSummary()])
    ElMessage.success(tr('quality.deleted'))
  } catch (e) {
    if (e === 'cancel' || e === 'close') return
    recordError.value = errorMessage(e, tr('quality.deleteFailed'))
  }
}
function openTrend(row: QualityInspection) {
  if (!row.tagId) return
  const from = row.ts - 10 * 60_000
  const to = row.ts + 10 * 60_000
  void router.push({ path: '/trends', query: { tagId: String(row.tagId), from: String(from), to: String(to) } })
}

function openDefectCode(row?: QualityDefectCode) {
  defectForm.id = row?.id ?? 0
  defectForm.code = row?.code ?? ''
  defectForm.name = row?.name ?? ''
  defectForm.category = row?.category ?? ''
  defectForm.description = row?.description ?? ''
  defectForm.enabled = row?.enabled ?? true
  dialogError.value = ''
  defectDialog.value = true
}
async function saveDefectCode() {
  saving.value = true
  dialogError.value = ''
  try {
    const input: QualityDefectCodeInput = {
      code: defectForm.code.trim(), name: defectForm.name.trim(), category: defectForm.category.trim(),
      description: defectForm.description.trim(), enabled: defectForm.enabled
    }
    if (defectForm.id) await qualityApi.updateDefectCode(defectForm.id, input)
    else await qualityApi.createDefectCode(input)
    defectDialog.value = false
    await loadDefectCodes()
    ElMessage.success(tr('quality.saved'))
  } catch (e) {
    dialogError.value = errorMessage(e, tr('quality.saveFailed'))
  } finally {
    saving.value = false
  }
}
async function removeDefectCode(row: QualityDefectCode) {
  defectError.value = ''
  try {
    await qualityApi.removeDefectCode(row.id)
    await loadDefectCodes()
    ElMessage.success(tr('quality.deleted'))
  } catch (e) {
    defectError.value = errorMessage(e, tr('quality.deleteFailed'))
  }
}

function refreshActive() {
  if (tab.value === 'records') void loadRecords()
  else if (tab.value === 'dashboard') void loadSummary()
  else void loadDefectCodes()
}
function resize() { trendChart?.resize(); paretoChart?.resize() }
let disposeTheme: (() => void) | undefined
onMounted(async () => {
  await loadCatalogs()
  await Promise.all([loadDefectCodes(), loadRecords(), loadSummary()])
  window.addEventListener('resize', resize)
  disposeTheme = onThemeChanged(() => renderSummary())
})
onUnmounted(() => {
  disposeTheme?.()
  window.removeEventListener('resize', resize)
  trendChart?.dispose()
  paretoChart?.dispose()
  trendChart = null
  paretoChart = null
})
</script>

<style scoped>
.filter-row { flex-wrap: wrap; }
.kpi-row { display: flex; gap: 12px; flex-wrap: wrap; margin-bottom: 10px; }
.kpi-card { flex: 1 1 160px; border: 1px solid var(--el-border-color); border-radius: 8px; padding: 12px 14px; display: flex; flex-direction: column; gap: 4px; }
.kpi-label { color: var(--el-text-color-secondary); font-size: 12px; }
.kpi-card strong { font-size: 22px; }
.chart-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(360px, 1fr)); gap: 12px; }
.quality-chart { width: 100%; height: 300px; }
.panel-subtitle { margin: 12px 0 6px; font-weight: 600; }
.pager { margin-top: 10px; justify-content: flex-end; }
.link { color: var(--el-color-primary); text-decoration: none; }
</style>

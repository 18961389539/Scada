<template>
  <div class="page">
    <div class="panel">
      <el-tabs v-model="mainTab">
        <el-tab-pane :label="$t('oee.dashboard')" name="dash" data-test="oee-tab-dash">
          <div class="toolbar wrap">
            <el-date-picker v-model="range" type="datetimerange" style="width: 340px" data-test="oee-range" />
            <el-button-group>
              <el-button size="small" @click="setQuick(24)">24h</el-button>
              <el-button size="small" @click="setQuick(24 * 7)">7d</el-button>
              <el-button size="small" @click="setQuick(24 * 30)">30d</el-button>
            </el-button-group>
            <el-select v-model="areaId" clearable :placeholder="$t('oee.allAreas')" style="width: 140px" data-test="oee-area">
              <el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" />
            </el-select>
            <el-select v-model="lineId" clearable filterable :placeholder="$t('oee.allLines')" style="width: 160px" data-test="oee-line">
              <el-option v-for="l in filteredLines" :key="l.id" :value="l.id" :label="l.name" />
            </el-select>
            <el-select v-model="productId" clearable filterable :placeholder="$t('oee.allProducts')" style="width: 140px">
              <el-option v-for="p in products" :key="p.id" :value="p.id" :label="p.name" />
            </el-select>
            <el-select v-model="shiftId" clearable :placeholder="$t('oee.allShifts')" style="width: 140px">
              <el-option v-for="s in shifts" :key="s.id" :value="s.id" :label="s.name" />
            </el-select>
            <el-button type="primary" :loading="loading" data-test="oee-query" @click="load">{{ $t('oee.query') }}</el-button>
            <span class="spacer" />
            <el-button :disabled="!report" data-test="oee-export-xlsx" @click="exportAs('xlsx')">{{ $t('oee.exportExcel') }}</el-button>
            <el-button :disabled="!report" data-test="oee-export-pdf" @click="exportAs('pdf')">{{ $t('oee.exportPdf') }}</el-button>
            <el-button v-if="canConfigure" data-test="oee-thresholds" @click="openThresholds">{{ $t('oee.thresholds') }}</el-button>
          </div>
          <el-alert v-if="report?.usedRollups" type="info" :closable="false" show-icon class="note" :title="$t('oee.rollupNote')" />
          <template v-if="kpi">
            <div class="cards">
              <div class="card" :class="pctClass(kpi.oee, th.targetOee)" data-test="kpi-oee"><div class="value">{{ pct(kpi.oee) }}</div><div class="label">OEE</div></div>
              <div class="card" :class="pctClass(kpi.availability, th.targetAvailability)" data-test="kpi-a"><div class="value">{{ pct(kpi.availability) }}</div><div class="label">{{ $t('oee.availability') }}</div></div>
              <div class="card" :class="pctClass(kpi.performance, th.targetPerformance)" data-test="kpi-p"><div class="value">{{ pct(kpi.performance) }}</div><div class="label">{{ $t('oee.performance') }}{{ kpi.performanceClamped ? ' *' : '' }}</div></div>
              <div class="card" :class="pctClass(kpi.quality, th.targetQuality)" data-test="kpi-q"><div class="value">{{ pct(kpi.quality) }}</div><div class="label">{{ $t('oee.quality') }}</div></div>
              <div class="card" data-test="kpi-good"><div class="value">{{ num(kpi.good) }}</div><div class="label">{{ $t('oee.good') }}</div></div>
              <div class="card"><div class="value">{{ pct(kpi.scrapRate) }}</div><div class="label">{{ $t('oee.scrapRate') }}</div></div>
              <div class="card"><div class="value">{{ hours(kpi.runMs) }} / {{ hours(kpi.plannedMs) }}</div><div class="label">{{ $t('oee.runPlanned') }}</div></div>
              <div class="card"><div class="value">{{ num(kpi.mtbfHours) }} / {{ num(kpi.mttrHours) }}</div><div class="label">MTBF / MTTR (h)</div></div>
              <div class="card"><div class="value">{{ kpi.faultCount }}</div><div class="label">{{ $t('oee.faults') }}</div></div>
              <div class="card"><div class="value">{{ num(kpi.productionVsTargetPercent) }}%</div><div class="label">{{ $t('oee.vsTarget') }}</div></div>
            </div>
            <el-row :gutter="16">
              <el-col :span="12"><h4>{{ $t('oee.trend') }}</h4><div ref="trendRef" class="chart" data-test="oee-trend-chart" /></el-col>
              <el-col :span="12"><h4>{{ $t('oee.pareto') }}</h4><div ref="paretoRef" class="chart" data-test="oee-pareto-chart" /></el-col>
            </el-row>
            <el-tabs v-model="tab">
              <el-tab-pane :label="$t('oee.tabPareto')" name="pareto">
                <el-table :data="kpi.downtimePareto" size="small" data-test="oee-pareto-table">
                  <el-table-column prop="code" :label="$t('oee.code')" width="100" />
                  <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
                  <el-table-column prop="category" :label="$t('oee.category')" width="110" />
                  <el-table-column prop="hours" :label="$t('oee.hours')" width="90" />
                  <el-table-column prop="count" :label="$t('oee.count')" width="80" />
                  <el-table-column prop="percent" label="%" width="80" />
                  <el-table-column prop="cumulativePercent" :label="$t('oee.cumulative')" width="90" />
                </el-table>
              </el-tab-pane>
              <el-tab-pane :label="$t('oee.tabShift')" name="shift">
                <el-table :data="kpi.byShift" size="small" data-test="oee-by-shift">
                  <el-table-column prop="shiftName" :label="$t('oee.shift')" min-width="120" />
                  <el-table-column label="OEE" width="90"><template #default="{ row }">{{ pct(row.oee) }}</template></el-table-column>
                  <el-table-column label="A" width="90"><template #default="{ row }">{{ pct(row.availability) }}</template></el-table-column>
                  <el-table-column label="P" width="90"><template #default="{ row }">{{ pct(row.performance) }}</template></el-table-column>
                  <el-table-column label="Q" width="90"><template #default="{ row }">{{ pct(row.quality) }}</template></el-table-column>
                  <el-table-column prop="good" :label="$t('oee.good')" width="90" />
                </el-table>
              </el-tab-pane>
              <el-tab-pane :label="$t('oee.tabProduct')" name="product">
                <el-table :data="kpi.byProduct" size="small">
                  <el-table-column prop="productName" :label="$t('oee.product')" min-width="120" />
                  <el-table-column label="OEE" width="90"><template #default="{ row }">{{ pct(row.oee) }}</template></el-table-column>
                  <el-table-column prop="good" :label="$t('oee.good')" width="90" />
                  <el-table-column prop="total" :label="$t('oee.total')" width="90" />
                </el-table>
              </el-tab-pane>
              <el-tab-pane :label="$t('oee.tabDay')" name="day">
                <el-table :data="kpi.byDay" size="small" data-test="oee-by-day" @row-click="(r: any) => drillDay(r)">
                  <el-table-column prop="label" :label="$t('oee.date')" width="120" />
                  <el-table-column label="OEE" width="90"><template #default="{ row }">{{ pct(row.oee) }}</template></el-table-column>
                  <el-table-column label="A" width="90"><template #default="{ row }">{{ pct(row.availability) }}</template></el-table-column>
                  <el-table-column label="P" width="90"><template #default="{ row }">{{ pct(row.performance) }}</template></el-table-column>
                  <el-table-column label="Q" width="90"><template #default="{ row }">{{ pct(row.quality) }}</template></el-table-column>
                  <el-table-column prop="good" :label="$t('oee.good')" width="90" />
                </el-table>
              </el-tab-pane>
            </el-tabs>
          </template>
        </el-tab-pane>

        <el-tab-pane :label="$t('oee.config')" name="config" data-test="oee-tab-config">
          <el-tabs v-model="cfgTab">
            <el-tab-pane :label="$t('oee.lines')" name="lines">
              <el-button v-if="canConfigure" type="primary" size="small" data-test="oee-add-line" @click="editLine()">{{ $t('oee.add') }}</el-button>
              <el-table :data="lines" size="small" class="mt" data-test="oee-lines">
                <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
                <el-table-column :label="$t('oee.area')" width="100"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
                <el-table-column prop="idealCycleSec" :label="$t('oee.idealCycle')" width="100" />
                <el-table-column prop="enabled" :label="$t('oee.enabled')" width="80" />
                <el-table-column width="140"><template #default="{ row }">
                  <el-button v-if="canConfigure" link type="primary" @click="editLine(row)">{{ $t('oee.edit') }}</el-button>
                  <el-button v-if="canConfigure" link type="danger" @click="removeLine(row.id)">{{ $t('oee.remove') }}</el-button>
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
                  <el-button v-if="canConfigure" link type="primary" @click="editProduct(row)">{{ $t('oee.edit') }}</el-button>
                  <el-button v-if="canConfigure" link type="danger" @click="removeProduct(row.id)">{{ $t('oee.remove') }}</el-button>
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
                  <el-button v-if="canConfigure" link type="primary" @click="editShift(row)">{{ $t('oee.edit') }}</el-button>
                  <el-button v-if="canConfigure" link type="danger" @click="removeShift(row.id)">{{ $t('oee.remove') }}</el-button>
                </template></el-table-column>
              </el-table>
            </el-tab-pane>
            <el-tab-pane :label="$t('oee.reasons')" name="reasons">
              <el-button v-if="canConfigure" type="primary" size="small" data-test="oee-add-reason" @click="editReason()">{{ $t('oee.add') }}</el-button>
              <el-table :data="reasons" size="small" class="mt" data-test="oee-reasons">
                <el-table-column prop="code" :label="$t('oee.code')" width="100" />
                <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
                <el-table-column prop="category" :label="$t('oee.category')" width="120" />
                <el-table-column prop="isPlanned" :label="$t('oee.planned')" width="80" />
                <el-table-column width="140"><template #default="{ row }">
                  <el-button v-if="canConfigure" link type="primary" @click="editReason(row)">{{ $t('oee.edit') }}</el-button>
                  <el-button v-if="canConfigure" link type="danger" @click="removeReason(row.id)">{{ $t('oee.remove') }}</el-button>
                </template></el-table-column>
              </el-table>
            </el-tab-pane>
            <el-tab-pane :label="$t('oee.downtimes')" name="downtimes">
              <el-button v-if="canControl" type="primary" size="small" data-test="oee-add-downtime" @click="editDowntime()">{{ $t('oee.add') }}</el-button>
              <el-button v-if="canControl" size="small" data-test="oee-add-correction" @click="editCorrection()">{{ $t('oee.correction') }}</el-button>
              <el-table :data="downtimes" size="small" class="mt" data-test="oee-downtimes">
                <el-table-column prop="lineName" :label="$t('oee.line')" width="120" />
                <el-table-column :label="$t('oee.start')" width="160"><template #default="{ row }">{{ fmtTs(row.startTs) }}</template></el-table-column>
                <el-table-column :label="$t('oee.end')" width="160"><template #default="{ row }">{{ row.endTs ? fmtTs(row.endTs) : '—' }}</template></el-table-column>
                <el-table-column prop="state" :label="$t('oee.state')" width="90" />
                <el-table-column prop="source" :label="$t('oee.source')" width="80" />
                <el-table-column prop="confirmed" :label="$t('oee.confirmed')" width="80" />
                <el-table-column width="180"><template #default="{ row }">
                  <el-button v-if="canControl && !row.confirmed" link type="primary" @click="confirmDt(row.id)">{{ $t('oee.confirm') }}</el-button>
                  <el-button v-if="canControl" link type="danger" @click="removeDt(row.id)">{{ $t('oee.remove') }}</el-button>
                </template></el-table-column>
              </el-table>
            </el-tab-pane>
          </el-tabs>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="lineDlg" :title="$t('oee.line')" width="520px">
      <el-form label-width="120px">
        <el-form-item :label="$t('oee.name')"><el-input v-model="lineForm.name" data-test="oee-line-name" /></el-form-item>
        <el-form-item :label="$t('oee.area')"><el-select v-model="lineForm.areaId" style="width:100%"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.idealCycle')"><el-input-number v-model="lineForm.idealCycleSec" :min="0.001" :step="1" /></el-form-item>
        <el-form-item :label="$t('oee.product')"><el-select v-model="lineForm.productId" clearable style="width:100%"><el-option v-for="p in products" :key="p.id" :value="p.id" :label="p.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.enabled')"><el-switch v-model="lineForm.enabled" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="lineDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-line-save" @click="saveLine">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="productDlg" :title="$t('oee.product')" width="480px">
      <el-form label-width="120px">
        <el-form-item :label="$t('oee.name')"><el-input v-model="productForm.name" data-test="oee-product-name" /></el-form-item>
        <el-form-item :label="$t('oee.code')"><el-input v-model="productForm.code" /></el-form-item>
        <el-form-item :label="$t('oee.idealCycle')"><el-input-number v-model="productForm.idealCycleSec" :min="0.001" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="productDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-product-save" @click="saveProduct">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="shiftDlg" :title="$t('oee.shift')" width="480px">
      <el-form label-width="120px">
        <el-form-item :label="$t('oee.name')"><el-input v-model="shiftForm.name" data-test="oee-shift-name" /></el-form-item>
        <el-form-item :label="$t('oee.area')"><el-select v-model="shiftForm.areaId" style="width:100%"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.startMin')"><el-input-number v-model="shiftForm.startMinute" :min="0" :max="1439" /></el-form-item>
        <el-form-item :label="$t('oee.endMin')"><el-input-number v-model="shiftForm.endMinute" :min="0" :max="1439" /></el-form-item>
        <el-form-item :label="$t('oee.enabled')"><el-switch v-model="shiftForm.enabled" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="shiftDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-shift-save" @click="saveShift">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="reasonDlg" :title="$t('oee.reason')" width="480px">
      <el-form label-width="120px">
        <el-form-item :label="$t('oee.code')"><el-input v-model="reasonForm.code" data-test="oee-reason-code" /></el-form-item>
        <el-form-item :label="$t('oee.name')"><el-input v-model="reasonForm.name" data-test="oee-reason-name" /></el-form-item>
        <el-form-item :label="$t('oee.category')"><el-select v-model="reasonForm.category" style="width:100%"><el-option value="Availability" /><el-option value="Performance" /><el-option value="Quality" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.planned')"><el-switch v-model="reasonForm.isPlanned" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="reasonDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-reason-save" @click="saveReason">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="dtDlg" :title="$t('oee.downtime')" width="520px">
      <el-form label-width="120px">
        <el-form-item :label="$t('oee.line')"><el-select v-model="dtForm.lineId" style="width:100%" data-test="oee-dt-line"><el-option v-for="l in lines" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.start')"><el-date-picker v-model="dtForm.start" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.end')"><el-date-picker v-model="dtForm.end" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.state')"><el-select v-model="dtForm.state" style="width:100%"><el-option value="Stopped" /><el-option value="Fault" /><el-option value="Setup" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.reason')"><el-select v-model="dtForm.reasonId" clearable style="width:100%"><el-option v-for="r in reasons" :key="r.id" :value="r.id" :label="`${r.code} ${r.name}`" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.note')"><el-input v-model="dtForm.note" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="dtDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-dt-save" @click="saveDt">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="corrDlg" :title="$t('oee.correction')" width="520px">
      <el-form label-width="120px">
        <el-form-item :label="$t('oee.line')"><el-select v-model="corrForm.lineId" style="width:100%" data-test="oee-corr-line"><el-option v-for="l in lines" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.start')"><el-date-picker v-model="corrForm.start" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.end')"><el-date-picker v-model="corrForm.end" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.good')"><el-input-number v-model="corrForm.goodDelta" data-test="oee-corr-good" /></el-form-item>
        <el-form-item :label="$t('oee.reject')"><el-input-number v-model="corrForm.rejectDelta" /></el-form-item>
        <el-form-item :label="$t('oee.scrap')"><el-input-number v-model="corrForm.scrapDelta" /></el-form-item>
        <el-form-item :label="$t('oee.reason')"><el-input v-model="corrForm.reason" data-test="oee-corr-reason" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="corrDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-corr-save" @click="saveCorr">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="thDlg" :title="$t('oee.thresholds')" width="480px">
      <el-form label-width="160px" v-if="thForm">
        <el-form-item :label="$t('oee.targetOee')"><el-input-number v-model="thForm.targetOee" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetA')"><el-input-number v-model="thForm.targetAvailability" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetP')"><el-input-number v-model="thForm.targetPerformance" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetQ')"><el-input-number v-model="thForm.targetQuality" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.clampP')"><el-switch v-model="thForm.clampPerformance" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="thDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" data-test="oee-th-save" @click="saveTh">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import * as echarts from 'echarts'
import { ElMessage } from 'element-plus'
import { oeeApi, type DowntimeReason, type OeeReport, type OeeThresholds, type ProductionLine, type ProductionProduct, type ProductionShift, type DowntimeEvent } from '../api/oee'
import { areaApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { hasAny } from '../utils'
import { t } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
const canControl = computed(() => hasAny(auth.user, 'Control'))
const mainTab = ref('dash'), cfgTab = ref('lines'), tab = ref('pareto'), loading = ref(false)
const range = ref<[Date, Date]>([new Date(Date.now() - 86400000), new Date()])
const areaId = ref<number>(), lineId = ref<number>(), productId = ref<number>(), shiftId = ref<number>()
const areas = ref<{ id: number; name: string }[]>([]), lines = ref<ProductionLine[]>([]), products = ref<ProductionProduct[]>([])
const shifts = ref<ProductionShift[]>([]), reasons = ref<DowntimeReason[]>([]), downtimes = ref<DowntimeEvent[]>([])
const report = ref<OeeReport | null>(null)
const kpi = computed(() => report.value?.kpi ?? null)
const th = computed(() => report.value?.thresholds ?? { targetOee: 0.85, targetAvailability: 0.9, targetPerformance: 0.95, targetQuality: 0.99, clampPerformance: true, topN: 10 })
const filteredLines = computed(() => areaId.value ? lines.value.filter(l => l.areaId === areaId.value) : lines.value)
const trendRef = ref<HTMLDivElement>(), paretoRef = ref<HTMLDivElement>()
let trendChart: echarts.ECharts | null = null, paretoChart: echarts.ECharts | null = null

const pct = (v?: number | null) => v == null ? '—' : (v * 100).toFixed(1) + '%'
const num = (v?: number | null) => v == null ? '—' : Number(v.toFixed(2))
const hours = (ms: number) => (ms / 3600000).toFixed(2)
const pctClass = (v: number | null | undefined, target: number) => v == null ? '' : v >= target ? 'ok' : v >= target * 0.85 ? 'warn' : 'bad'
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? String(id)
const fmtMin = (m: number) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
const fmtTs = (ts: number) => new Date(ts).toLocaleString()

function setQuick(h: number) { range.value = [new Date(Date.now() - h * 3600000), new Date()] }
async function refreshMeta() {
  areas.value = await areaApi.list()
  lines.value = await oeeApi.lines(); products.value = await oeeApi.products()
  shifts.value = await oeeApi.shifts(); reasons.value = await oeeApi.reasons()
}
async function load() {
  if (!range.value) return
  loading.value = true
  try {
    report.value = await oeeApi.analytics({
      from: range.value[0].getTime(), to: range.value[1].getTime(),
      areaId: areaId.value, lineId: lineId.value, productId: productId.value, shiftId: shiftId.value
    })
    await nextTick(); renderCharts()
    downtimes.value = await oeeApi.downtimes({ from: range.value[0].getTime(), to: range.value[1].getTime(), lineId: lineId.value })
  } finally { loading.value = false }
}
function renderCharts() {
  if (!report.value || !trendRef.value || !paretoRef.value) return
  trendChart ??= echarts.init(trendRef.value); paretoChart ??= echarts.init(paretoRef.value)
  const trend = report.value.trend
  trendChart.setOption({
    tooltip: { trigger: 'axis' },
    legend: { data: ['OEE', 'A', 'P', 'Q'] },
    xAxis: { type: 'category', data: trend.map(x => x.label) },
    yAxis: { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } },
    series: [
      { name: 'OEE', type: 'line', data: trend.map(x => x.oee == null ? null : +(x.oee * 100).toFixed(2)) },
      { name: 'A', type: 'line', data: trend.map(x => x.availability == null ? null : +(x.availability * 100).toFixed(2)) },
      { name: 'P', type: 'line', data: trend.map(x => x.performance == null ? null : +(x.performance * 100).toFixed(2)) },
      { name: 'Q', type: 'line', data: trend.map(x => x.quality == null ? null : +(x.quality * 100).toFixed(2)) }
    ]
  })
  const pareto = report.value.kpi.downtimePareto
  paretoChart.setOption({
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: pareto.map(x => x.code) },
    yAxis: [{ type: 'value', name: 'h' }, { type: 'value', max: 100, axisLabel: { formatter: '{value}%' } }],
    series: [
      { type: 'bar', data: pareto.map(x => +x.hours.toFixed(2)) },
      { type: 'line', yAxisIndex: 1, data: pareto.map(x => x.cumulativePercent) }
    ]
  })
}
function drillDay(row: { from: number; to: number }) {
  range.value = [new Date(row.from), new Date(row.to)]; load()
}
async function exportAs(format: 'xlsx' | 'pdf') {
  const url = oeeApi.exportUrl({ format, from: range.value[0].getTime(), to: range.value[1].getTime(), areaId: areaId.value, lineId: lineId.value, productId: productId.value, shiftId: shiftId.value })
  const token = auth.token; const r = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
  if (!r.ok) { ElMessage.error(await r.text()); return }
  const blob = await r.blob(); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `oee.${format === 'pdf' ? 'pdf' : 'xlsx'}`; a.click()
}

const lineDlg = ref(false), lineForm = ref<any>({}), lineIdEdit = ref<number | null>(null)
function editLine(row?: ProductionLine) {
  lineIdEdit.value = row?.id ?? null
  lineForm.value = row ? { ...row } : { name: '', areaId: areas.value[0]?.id, idealCycleSec: 60, enabled: true, stateMapJson: '{}' }
  lineDlg.value = true
}
async function saveLine() { await oeeApi.saveLine(lineIdEdit.value, lineForm.value); lineDlg.value = false; await refreshMeta(); ElMessage.success(t('oee.saved')) }
async function removeLine(id: number) { await oeeApi.removeLine(id); await refreshMeta() }

const productDlg = ref(false), productForm = ref<any>({}), productIdEdit = ref<number | null>(null)
function editProduct(row?: ProductionProduct) {
  productIdEdit.value = row?.id ?? null
  productForm.value = row ? { ...row } : { name: '', idealCycleSec: 60 }
  productDlg.value = true
}
async function saveProduct() { await oeeApi.saveProduct(productIdEdit.value, productForm.value); productDlg.value = false; await refreshMeta(); ElMessage.success(t('oee.saved')) }
async function removeProduct(id: number) { await oeeApi.removeProduct(id); await refreshMeta() }

const shiftDlg = ref(false), shiftForm = ref<any>({}), shiftIdEdit = ref<number | null>(null)
function editShift(row?: ProductionShift) {
  shiftIdEdit.value = row?.id ?? null
  shiftForm.value = row ? { ...row } : { name: '', areaId: areas.value[0]?.id, startMinute: 480, endMinute: 1200, days: 127, enabled: true }
  shiftDlg.value = true
}
async function saveShift() { await oeeApi.saveShift(shiftIdEdit.value, shiftForm.value); shiftDlg.value = false; await refreshMeta(); ElMessage.success(t('oee.saved')) }
async function removeShift(id: number) { await oeeApi.removeShift(id); await refreshMeta() }

const reasonDlg = ref(false), reasonForm = ref<any>({}), reasonIdEdit = ref<number | null>(null)
function editReason(row?: DowntimeReason) {
  reasonIdEdit.value = row?.id ?? null
  reasonForm.value = row ? { ...row } : { code: '', name: '', category: 'Availability', isPlanned: false, enabled: true }
  reasonDlg.value = true
}
async function saveReason() { await oeeApi.saveReason(reasonIdEdit.value, reasonForm.value); reasonDlg.value = false; await refreshMeta(); ElMessage.success(t('oee.saved')) }
async function removeReason(id: number) { await oeeApi.removeReason(id); await refreshMeta() }

const dtDlg = ref(false), dtForm = ref<any>({})
function editDowntime() {
  dtForm.value = { lineId: lines.value[0]?.id, start: new Date(Date.now() - 3600000), end: new Date(), state: 'Fault', reasonId: null, note: '' }
  dtDlg.value = true
}
async function saveDt() {
  await oeeApi.createDowntime({
    lineId: dtForm.value.lineId, startTs: dtForm.value.start.getTime(), endTs: dtForm.value.end?.getTime(),
    state: dtForm.value.state, reasonId: dtForm.value.reasonId, note: dtForm.value.note, confirmed: true
  })
  dtDlg.value = false; await load(); ElMessage.success(t('oee.saved'))
}
async function confirmDt(id: number) { await oeeApi.confirmDowntime(id); await load() }
async function removeDt(id: number) { await oeeApi.removeDowntime(id); await load() }

const corrDlg = ref(false), corrForm = ref<any>({})
function editCorrection() {
  corrForm.value = { lineId: lines.value[0]?.id, start: range.value[0], end: range.value[1], goodDelta: 0, rejectDelta: 0, scrapDelta: 0, reason: '' }
  corrDlg.value = true
}
async function saveCorr() {
  await oeeApi.correct({
    lineId: corrForm.value.lineId, periodFrom: corrForm.value.start.getTime(), periodTo: corrForm.value.end.getTime(),
    goodDelta: corrForm.value.goodDelta, rejectDelta: corrForm.value.rejectDelta, scrapDelta: corrForm.value.scrapDelta, reason: corrForm.value.reason
  })
  corrDlg.value = false; await load(); ElMessage.success(t('oee.saved'))
}

const thDlg = ref(false), thForm = ref<OeeThresholds | null>(null), thRev = ref(0)
async function openThresholds() {
  const r = await oeeApi.thresholds(); thForm.value = { ...r.thresholds }; thRev.value = r.revision; thDlg.value = true
}
async function saveTh() {
  if (!thForm.value) return
  await oeeApi.saveThresholds(thForm.value, thRev.value); thDlg.value = false; await load(); ElMessage.success(t('oee.saved'))
}

watch(mainTab, v => { if (v === 'config') refreshMeta() })
onMounted(async () => { await refreshMeta(); await load() })
</script>

<style scoped>
.toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
.wrap { flex-wrap: wrap; }
.spacer { flex: 1; }
.cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: 10px; margin: 12px 0; }
.card { background: #132033; border-radius: 8px; padding: 12px; }
.card .value { font-size: 22px; font-weight: 600; color: #e8eef7; }
.card .label { font-size: 12px; color: #9db0cb; margin-top: 4px; }
.card.ok .value { color: #63e6be; }
.card.warn .value { color: #fcc419; }
.card.bad .value { color: #ff6b6b; }
.chart { height: 280px; }
.note { margin-bottom: 8px; }
.mt { margin-top: 8px; }
</style>

<template>
  <div class="page">
    <el-alert v-if="metaError" :title="metaError" type="error" :closable="false" show-icon data-test="oee-meta-error" />
    <el-alert v-if="queryError" :title="queryError" type="error" :closable="false" show-icon data-test="oee-query-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="oee-action-error" />
    <div class="panel oee-panel">
      <div v-if="mainTab === 'dash'" class="oee-actions">
        <el-button size="small" :disabled="!report" data-test="oee-export-xlsx" @click="exportAs('xlsx')">{{ $t('oee.exportExcel') }}</el-button>
        <el-button size="small" :disabled="!report" data-test="oee-export-pdf" @click="exportAs('pdf')">{{ $t('oee.exportPdf') }}</el-button>
        <el-button v-if="canGlobal" size="small" data-test="oee-thresholds" @click="openThresholds">{{ $t('oee.thresholds') }}</el-button>
      </div>
      <el-tabs v-model="mainTab">
        <el-tab-pane name="dash">
          <template #label><span data-test="oee-tab-dash">{{ $t('oee.dashboard') }}</span></template>
          <div class="toolbar wrap">
            <el-date-picker v-model="range" type="datetimerange" style="width: 340px" data-test="oee-range" @change="activeQuick = null" />
            <el-button-group>
              <el-button :type="activeQuick === 24 ? 'primary' : 'default'" @click="setQuick(24)">{{ $t('oee.range24h') }}</el-button>
              <el-button :type="activeQuick === 24 * 7 ? 'primary' : 'default'" @click="setQuick(24 * 7)">{{ $t('oee.range7d') }}</el-button>
              <el-button :type="activeQuick === 24 * 30 ? 'primary' : 'default'" @click="setQuick(24 * 30)">{{ $t('oee.range30d') }}</el-button>
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
          </div>
          <el-alert v-if="report?.usedRollups" type="info" :closable="false" show-icon class="note" :title="$t('oee.rollupNote')" />
          <!-- 没有任何计划生产时间与产量时，A/P/Q/OEE 全是「—」。不解释的话会被当成系统故障。 -->
          <el-alert v-if="kpi && !kpi.plannedMs && !kpi.total" type="warning" :closable="false" show-icon class="note" data-test="oee-empty" :title="$t('oee.noData')" />
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
              <div class="card"><div class="value">{{ count(kpi.faultCount) }}</div><div class="label">{{ $t('oee.faults') }}</div></div>
              <div class="card"><div class="value">{{ ratioPct(kpi.productionVsTargetPercent) }}</div><div class="label">{{ $t('oee.vsTarget') }}</div></div>
            </div>
            <el-row :gutter="16">
              <el-col :span="12"><h4>{{ $t('oee.trend') }}</h4>
                <div class="chart-wrap">
                  <div ref="trendRef" class="chart" data-test="oee-trend-chart" />
                  <div v-if="!hasProduction" class="chart-empty">{{ $t('oee.chartEmpty') }}</div>
                </div>
              </el-col>
              <el-col :span="12"><h4>{{ $t('oee.pareto') }}</h4>
                <div class="chart-wrap">
                  <div ref="paretoRef" class="chart" data-test="oee-pareto-chart" />
                  <div v-if="!hasProduction" class="chart-empty">{{ $t('oee.chartEmpty') }}</div>
                </div>
              </el-col>
            </el-row>
            <el-tabs v-model="tab">
              <el-tab-pane :label="$t('oee.tabPareto')" name="pareto">
                <el-table :data="kpi.downtimePareto" size="small" data-test="oee-pareto-table">
                  <el-table-column prop="code" :label="$t('oee.code')" width="100" />
                  <el-table-column prop="name" :label="$t('oee.name')" min-width="140" />
                  <el-table-column :label="$t('oee.category')" width="110"><template #default="{ row }">{{ labelOf(categoryLabels, row.category) }}</template></el-table-column>
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

        <el-tab-pane name="config">
          <template #label><span data-test="oee-tab-config">{{ $t('oee.config') }}</span></template>
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
        <el-form-item :label="$t('oee.area')"><el-select v-model="productForm.areaId" :clearable="canGlobal" :placeholder="$t('oee.global')" style="width:100%" data-test="oee-product-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
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
        <el-form-item :label="$t('oee.area')"><el-select v-model="reasonForm.areaId" :clearable="canGlobal" :placeholder="$t('oee.global')" style="width:100%" data-test="oee-reason-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
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
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import * as echarts from '../charts'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import { oeeApi, type DowntimeReason, type OeeReport, type OeeThresholds, type ProductionLine, type ProductionProduct, type ProductionShift, type DowntimeEvent } from '../api/oee'
import { areaApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { formatTime, hasAny, hasGlobal, hasInArea } from '../utils'
import { errorMessage } from '../api/http'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'
import { t } from '../i18n'
import { chartPalette } from '../theme'

const auth = useAuthStore()
const categoryLabels: Record<string, string> = { Availability: t('oee.availability'), Performance: t('oee.performance'), Quality: t('oee.quality') }
const stateLabels: Record<string, string> = { Unknown: t('oee.stateUnknown'), Running: t('oee.stateRunning'), Stopped: t('oee.stateStopped'), Fault: t('oee.stateFault'), Setup: t('oee.stateSetup') }
const sourceLabels: Record<string, string> = { manual: t('oee.sourceManual'), tag: t('oee.sourceTag'), alarm: t('oee.sourceAlarm') }
const downtimeStates = ['Stopped', 'Fault', 'Setup']
function labelOf(map: Record<string, string>, value: string): string { return map[value] ?? value }
function yesNo(value: boolean): string { return value ? t('oee.yes') : t('oee.no') }
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
// 阈值是全局配置：服务端 PUT /oee/thresholds 要求全局 Configure。按 hasAny 显示会让区域维护员
// 填完再被 403，必须用同一口径。
const canGlobal = computed(() => hasGlobal(auth.user, 'Configure'))
// 行级判权与服务端一致：有区域按区域权限，全局记录（areaId 为空）要全局权限
const canEditArea = (areaId?: number | null): boolean =>
  areaId == null ? canGlobal.value : hasInArea(auth.user, 'Configure', areaId)
const mainTab = ref('dash'), cfgTab = ref('lines'), tab = ref('pareto'), loading = ref(false)
// 保存态与字段级错误：所有主数据对话框共用，避免每个对话框各写一套校验/报错
const saving = ref(false)
const fieldErrors = reactive<FieldErrorMap>({})
const formError = ref('')
const actionError = ref('')
const lineFormRef = ref<FormInstance>(), productFormRef = ref<FormInstance>(), shiftFormRef = ref<FormInstance>()
const reasonFormRef = ref<FormInstance>(), dtFormRef = ref<FormInstance>(), corrFormRef = ref<FormInstance>()
const range = ref<[Date, Date]>([new Date(Date.now() - 86400000), new Date()])
const activeQuick = ref<number | null>(24)
const areaId = ref<number>(), lineId = ref<number>(), productId = ref<number>(), shiftId = ref<number>()
const areas = ref<{ id: number; name: string }[]>([]), lines = ref<ProductionLine[]>([]), products = ref<ProductionProduct[]>([])
const configurableAreas = computed(() => areas.value.filter(a => hasInArea(auth.user, 'Configure', a.id)))
const controllableLines = computed(() => lines.value.filter(l => hasInArea(auth.user, 'Control', l.areaId)))
function canControlLine(lineId: number): boolean { return controllableLines.value.some(l => l.id === lineId) }
const shifts = ref<ProductionShift[]>([]), reasons = ref<DowntimeReason[]>([]), downtimes = ref<DowntimeEvent[]>([])
const report = ref<OeeReport | null>(null)
const metaError = ref('')
const queryError = ref('')
const kpi = computed(() => report.value?.kpi ?? null)
const th = computed(() => report.value?.thresholds ?? { targetOee: 0.85, targetAvailability: 0.9, targetPerformance: 0.95, targetQuality: 0.99, clampPerformance: true, topN: 10 })
const filteredLines = computed(() => areaId.value ? lines.value.filter(l => l.areaId === areaId.value) : lines.value)
const trendRef = ref<HTMLDivElement>(), paretoRef = ref<HTMLDivElement>()
// 没有计划时间与产量时后端仍返回全 0 的 kpi；图表只剩坐标轴，需要盖一层说明
const hasProduction = computed(() => !!kpi.value && (kpi.value.plannedMs > 0 || kpi.value.total > 0))
let trendChart: echarts.ECharts | null = null, paretoChart: echarts.ECharts | null = null

// 无计划时间/产量时服务端仍返回全 0：0% 的 OEE 会被误读成「真的停了」，统一显示 —。
const pct = (v?: number | null) => !hasProduction.value || v == null ? '—' : (v * 100).toFixed(1) + '%'
const num = (v?: number | null) => !hasProduction.value || v == null ? '—' : Number(v.toFixed(2))
const count = (v: number) => hasProduction.value ? String(v) : '—'
const ratioPct = (v?: number | null) => !hasProduction.value || v == null ? '—' : `${Number(v.toFixed(2))}%`
const hours = (ms: number) => !hasProduction.value ? '—' : (ms / 3600000).toFixed(2)
const pctClass = (v: number | null | undefined, target: number) => v == null ? '' : v >= target ? 'ok' : v >= target * 0.85 ? 'warn' : 'bad'
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? String(id)
const fmtMin = (m: number) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`

function setQuick(h: number) { activeQuick.value = h; range.value = [new Date(Date.now() - h * 3600000), new Date()] }
async function refreshMeta() {
  try {
    const [a, l, p, s, r] = await Promise.all([areaApi.list(), oeeApi.lines(), oeeApi.products(), oeeApi.shifts(), oeeApi.reasons()])
    areas.value = a; lines.value = l; products.value = p; shifts.value = s; reasons.value = r
    metaError.value = ''
  } catch (error) {
    metaError.value = errorMessage(error, t('oee.loadFailed'))
  }
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
    queryError.value = ''
  } catch (error) {
    queryError.value = errorMessage(error, t('oee.loadFailed'))
  } finally { loading.value = false }
}
function renderCharts() {
  if (!report.value || !trendRef.value || !paretoRef.value) return
  trendChart ??= echarts.init(trendRef.value); paretoChart ??= echarts.init(paretoRef.value)
  const p = chartPalette()
  const trend = report.value.trend
  trendChart.setOption({
    textStyle: { color: p.text },
    tooltip: { trigger: 'axis' },
    legend: { data: ['OEE', 'A', 'P', 'Q'], textStyle: { color: p.text } },
    xAxis: { type: 'category', data: trend.map(x => x.label), axisLabel: { color: p.muted } },
    yAxis: { type: 'value', max: 100, axisLabel: { formatter: '{value}%', color: p.muted }, splitLine: { lineStyle: { color: p.split } } },
    series: [
      { name: 'OEE', type: 'line', data: trend.map(x => x.oee == null ? null : +(x.oee * 100).toFixed(2)) },
      { name: 'A', type: 'line', data: trend.map(x => x.availability == null ? null : +(x.availability * 100).toFixed(2)) },
      { name: 'P', type: 'line', data: trend.map(x => x.performance == null ? null : +(x.performance * 100).toFixed(2)) },
      { name: 'Q', type: 'line', data: trend.map(x => x.quality == null ? null : +(x.quality * 100).toFixed(2)) }
    ]
  })
  const pareto = report.value.kpi.downtimePareto
  paretoChart.setOption({
    textStyle: { color: p.text },
    tooltip: { trigger: 'axis' },
    xAxis: { type: 'category', data: pareto.map(x => x.code), axisLabel: { color: p.muted } },
    yAxis: [
      { type: 'value', name: 'h', axisLabel: { color: p.muted }, splitLine: { lineStyle: { color: p.split } } },
      { type: 'value', max: 100, axisLabel: { formatter: '{value}%', color: p.muted }, splitLine: { lineStyle: { color: p.split } } }
    ],
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
  const token = auth.token
  actionError.value = ''
  try {
    const r = await fetch(url, { headers: token ? { Authorization: `Bearer ${token}` } : {} })
    if (!r.ok) {
      const body = await r.text().catch(() => '')
      let detail = body
      try { detail = (JSON.parse(body) as { message?: string }).message ?? body } catch { /* 非 JSON 就按原文 */ }
      actionError.value = detail.slice(0, 200) || t('oee.loadFailed')
      return
    }
    const blob = await r.blob(); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `oee.${format === 'pdf' ? 'pdf' : 'xlsx'}`; a.click()
    URL.revokeObjectURL(a.href)
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
    // 服务端要求二者至少其一：理想节拍 > 0 或额定产量 > 0
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

function isConflict(error: unknown): boolean {
  return (error as { response?: { status?: number } } | undefined)?.response?.status === 409
}

/**
 * 统一保存流程：校验 → 提交 → 成功提示 → 刷新。失败时保留对话框并把服务端文案落到具体字段。
 * 409 是「已被他人修订」，必须明说结果：否则用户以为保存成功，继续在过期数据上编辑。
 */
async function saveWith(formEl: FormInstance | undefined, submit: () => Promise<unknown>, after: () => Promise<void> = refreshMeta): Promise<boolean> {
  if (saving.value) return false
  clearFieldErrors(fieldErrors); formError.value = ''
  try {
    await formEl?.validate()
  } catch {
    formError.value = t('oee.validationFailed')
    return false
  }
  saving.value = true
  try {
    await submit()
  } catch (error) {
    const { message, fields } = serverFormErrors(error, t('oee.saveFailed'))
    applyFieldErrors(fieldErrors, fields)
    formError.value = isConflict(error) ? t('oee.conflictHint', [message]) : message
    return false
  } finally {
    saving.value = false
  }
  ElMessage.success(t('oee.saved'))
  await after()
  return true
}

const lineDlg = ref(false), lineForm = ref<any>({}), lineIdEdit = ref<number | null>(null)
function editLine(row?: ProductionLine) {
  lineIdEdit.value = row?.id ?? null
  clearFieldErrors(fieldErrors); formError.value = ''
  lineForm.value = row ? { ...row } : { name: '', areaId: configurableAreas.value[0]?.id, idealCycleSec: 60, enabled: true, stateMapJson: '{}' }
  lineDlg.value = true
}
async function saveLine(): Promise<void> {
  if (await saveWith(lineFormRef.value, () => oeeApi.saveLine(lineIdEdit.value, { ...lineForm.value }))) lineDlg.value = false
}
// 删除主数据统一走这里：先确认，失败必须给出原因（通常是被历史数据引用），不能静默
async function confirmRemove(remove: () => Promise<unknown>, after: () => Promise<void>): Promise<void> {
  try {
    await ElMessageBox.confirm(t('oee.removeConfirm'), t('oee.remove'), {
      type: 'warning', confirmButtonText: t('oee.remove'), cancelButtonText: t('oee.cancel')
    })
  } catch {
    return
  }
  actionError.value = ''
  try {
    await remove()
    await after()
  } catch (error) {
    actionError.value = errorMessage(error, t('oee.removeFailed'))
  }
}

async function removeLine(id: number) { await confirmRemove(() => oeeApi.removeLine(id), refreshMeta) }

const productDlg = ref(false), productForm = ref<any>({}), productIdEdit = ref<number | null>(null)
// 服务端按 AreaId 判权：留空=全局产品（需全局 Configure），选中区域=需该区域 Configure。
// 因此默认值要跟着当前用户的实际权限走，否则区域级维护人员会被拒。
function defaultAreaId(): number | null {
  if (hasGlobal(auth.user, 'Configure')) return null
  return configurableAreas.value[0]?.id ?? null
}
function editProduct(row?: ProductionProduct) {
  productIdEdit.value = row?.id ?? null
  clearFieldErrors(fieldErrors); formError.value = ''
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
  clearFieldErrors(fieldErrors); formError.value = ''
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
  clearFieldErrors(fieldErrors); formError.value = ''
  reasonForm.value = row ? { ...row } : { code: '', name: '', category: 'Availability', isPlanned: false, enabled: true, areaId: defaultAreaId() }
  reasonDlg.value = true
}
async function saveReason(): Promise<void> {
  if (await saveWith(reasonFormRef.value, () => oeeApi.saveReason(reasonIdEdit.value, { ...reasonForm.value }))) reasonDlg.value = false
}
async function removeReason(id: number) { await confirmRemove(() => oeeApi.removeReason(id), refreshMeta) }

const dtDlg = ref(false), dtForm = ref<any>({})
function editDowntime() {
  clearFieldErrors(fieldErrors); formError.value = ''
  dtForm.value = { lineId: controllableLines.value[0]?.id, start: new Date(Date.now() - 3600000), end: new Date(), state: 'Fault', reasonId: null, note: '' }
  dtDlg.value = true
}
async function saveDt(): Promise<void> {
  const form = dtForm.value
  const ok = await saveWith(dtFormRef.value, () => oeeApi.createDowntime({
    lineId: form.lineId, startTs: form.start.getTime(), endTs: form.end?.getTime(),
    state: form.state, reasonId: form.reasonId, note: form.note, confirmed: true
  }), load)
  if (ok) dtDlg.value = false
}
async function confirmDt(id: number) { await oeeApi.confirmDowntime(id); await load() }
async function removeDt(id: number) { await confirmRemove(() => oeeApi.removeDowntime(id), load) }

const corrDlg = ref(false), corrForm = ref<any>({})
function editCorrection() {
  clearFieldErrors(fieldErrors); formError.value = ''
  corrForm.value = { lineId: controllableLines.value[0]?.id, start: range.value[0], end: range.value[1], goodDelta: 0, rejectDelta: 0, scrapDelta: 0, reason: '' }
  corrDlg.value = true
}
async function saveCorr(): Promise<void> {
  const form = corrForm.value
  const ok = await saveWith(corrFormRef.value, () => oeeApi.correct({
    lineId: form.lineId, periodFrom: form.start.getTime(), periodTo: form.end.getTime(),
    goodDelta: form.goodDelta, rejectDelta: form.rejectDelta, scrapDelta: form.scrapDelta, reason: form.reason
  }), load)
  if (ok) corrDlg.value = false
}

const thDlg = ref(false), thForm = ref<OeeThresholds | null>(null), thRev = ref(0)
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
    await load()
  } catch (error) {
    const message = errorMessage(error, t('oee.saveFailed'))
    formError.value = isConflict(error) ? t('oee.conflictHint', [message]) : message
  } finally {
    saving.value = false
  }
}

watch(mainTab, v => { if (v === 'config') refreshMeta() })
onMounted(async () => { await refreshMeta(); await load() })
</script>

<style scoped>
/* 导出与阈值放在页签行右侧：筛选行只留查询条件，避免按钮被挤成第二行 */
.oee-panel { position: relative; }
.oee-actions {
  position: absolute;
  top: 10px;
  right: var(--scada-space-5);
  display: flex;
  gap: 8px;
  z-index: 2;
}
@media (max-width: 1100px) {
  .oee-actions { position: static; justify-content: flex-end; margin-bottom: 8px; }
}
.toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
.wrap { flex-wrap: wrap; }
.spacer { flex: 1; }
/* KPI 与总览/报警分析共用白卡语言，状态只体现在左侧色条和数值颜色 */
.cards { display: grid; grid-template-columns: repeat(5, minmax(0, 1fr)); gap: 10px; margin: 12px 0; }
.card {
  background: var(--scada-panel);
  border: 1px solid var(--scada-border);
  border-left: 3px solid var(--scada-border-strong);
  border-radius: var(--scada-radius);
  padding: 12px 14px;
  box-shadow: var(--scada-shadow-sm);
}
.card .value {
  font-family: var(--scada-font-mono);
  font-variant-numeric: tabular-nums;
  font-size: 22px;
  font-weight: 600;
  color: var(--scada-text);
}
.card .label { font-size: 12px; color: var(--scada-text-muted); margin-top: 4px; }
.card.ok { border-left-color: var(--scada-success); }
.card.ok .value { color: var(--scada-success); }
.card.warn { border-left-color: var(--scada-warning); }
.card.warn .value { color: var(--scada-warning); }
.card.bad { border-left-color: var(--scada-danger); }
.card.bad .value { color: var(--scada-danger); }
.chart { height: 280px; }
.chart-wrap { position: relative; }
.chart-empty {
  position: absolute;
  inset: 0;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: var(--scada-space-6);
  text-align: center;
  color: var(--scada-text-muted);
  font-size: 13px;
  background: var(--scada-panel);
  border: 1px dashed var(--scada-border);
  border-radius: var(--scada-radius-sm);
}
.note { margin-bottom: 8px; }
.mt { margin-top: 8px; }

@media (max-width: 1100px) {
  .cards { grid-template-columns: repeat(3, minmax(0, 1fr)); }
}
@media (max-width: 768px) {
  .cards { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
</style>

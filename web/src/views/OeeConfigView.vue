<template>
  <div class="page" v-loading="loading">
    <el-alert v-if="metaError" :title="metaError" type="error" :closable="false" show-icon data-test="oee-meta-error" />
    <el-button v-if="metaError" data-test="oee-meta-retry" @click="refreshMeta()">{{ $t('common.retry') }}</el-button>
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="oee-action-error" />
    <div class="panel">
      <div class="toolbar">
        <h3>{{ $t(operationsOnly ? 'ux.events.title' : 'oee.configTitle') }}</h3>
        <span class="spacer" />
        <router-link v-if="operationsOnly" to="/oee"><el-button>{{ $t('navMenu.oee') }}</el-button></router-link>
        <router-link v-else-if="can(auth.user, 'Control')" to="/production/events"><el-button data-test="oee-production-events">{{ $t('ux.events.title') }}</el-button></router-link>
        <el-button v-if="!operationsOnly && isGlobalConfigure" :loading="thOpening" data-test="oee-thresholds" @click="openThresholds">{{ $t('oee.thresholds') }}</el-button>
      </div>
      <p class="tiny muted">{{ $t(operationsOnly ? 'ux.events.hint' : 'ux.events.configHint') }}</p>
      <el-tabs v-model="cfgTab">
        <el-tab-pane v-if="!operationsOnly" :label="$t('oee.lines')" name="lines">
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
        <el-tab-pane v-if="!operationsOnly" :label="$t('oee.products')" name="products">
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
        <el-tab-pane v-if="!operationsOnly" :label="$t('oee.shifts')" name="shifts">
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
        <el-tab-pane v-if="!operationsOnly" :label="$t('oee.reasons')" name="reasons">
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
        <el-tab-pane v-if="operationsOnly" :label="$t('oee.downtimes')" name="downtimes">
          <div class="toolbar event-filters">
            <el-select v-model="eventLineId" clearable :placeholder="$t('oee.allLines')" :aria-label="$t('oee.line')" data-test="production-event-line"><el-option v-for="line in lines" :key="line.id" :value="line.id" :label="line.name" /></el-select>
            <el-date-picker v-model="eventRange" type="datetimerange" :range-separator="$t('ux.events.rangeSeparator')" :start-placeholder="$t('oee.start')" :end-placeholder="$t('oee.end')" data-test="production-event-range" />
            <el-select v-model="eventConfirmation" :aria-label="$t('oee.confirmed')" data-test="production-event-confirmation">
              <el-option value="all" :label="$t('ux.events.allConfirmations')" /><el-option value="pending" :label="$t('ux.events.pendingConfirmation')" /><el-option value="confirmed" :label="$t('ux.events.confirmed')" />
            </el-select>
            <el-button type="primary" :loading="eventsLoading" data-test="production-event-query" @click="loadDowntimes">{{ $t('ux.events.query') }}</el-button>
          </div>
          <el-alert v-if="eventsError" :title="eventsError" type="error" :closable="false" show-icon data-test="production-events-error" />
          <el-alert v-if="eventsPending" :title="$t('ux.events.pending')" type="warning" :closable="false" show-icon />
          <el-alert v-if="downtimes.length >= 2000" :title="$t('ux.events.limit')" type="warning" :closable="false" show-icon />
          <el-button v-if="controllableLines.length" type="primary" size="small" data-test="oee-add-downtime" @click="editDowntime()">{{ $t('oee.add') }}</el-button>
          <el-button v-if="controllableLines.length" size="small" data-test="oee-add-correction" @click="editCorrection()">{{ $t('oee.correction') }}</el-button>
          <el-table v-loading="eventsLoading" :data="pagedDowntimes" size="small" class="mt" data-test="oee-downtimes" :empty-text="eventsError || undefined">
            <el-table-column prop="lineName" :label="$t('oee.line')" min-width="130" />
            <el-table-column :label="$t('ux.events.period')" min-width="190"><template #default="{ row }"><div>{{ formatTime(row.startTs) }}</div><div class="tiny muted">{{ row.endTs ? formatTime(row.endTs) : $t('ux.events.ongoing') }}</div></template></el-table-column>
            <el-table-column :label="$t('ux.events.duration')" width="100"><template #default="{ row }">{{ (Math.max(0, (row.endTs ?? now) - row.startTs) / 60000).toFixed(1) }}</template></el-table-column>
            <el-table-column :label="$t('oee.reason')" min-width="140" show-overflow-tooltip><template #default="{ row }">{{ reasons.find(reason => reason.id === row.reasonId)?.name || $t('ux.events.unassigned') }}</template></el-table-column>
            <el-table-column :label="$t('oee.state')" width="85"><template #default="{ row }">{{ labelOf(stateLabels, row.state) }}</template></el-table-column>
            <el-table-column :label="$t('oee.source')" width="75"><template #default="{ row }">{{ labelOf(sourceLabels, row.source) }}</template></el-table-column>
            <el-table-column :label="$t('oee.confirmed')" width="75"><template #default="{ row }"><el-tag :type="row.confirmed ? 'info' : 'warning'">{{ yesNo(row.confirmed) }}</el-tag></template></el-table-column>
            <el-table-column width="190"><template #default="{ row }"><div class="event-actions">
              <el-button v-if="canControlLine(row.lineId)" link type="primary" :disabled="eventsPending || eventsLoading" data-test="oee-dt-edit" @click="editDowntime(row)">{{ $t('oee.edit') }}</el-button>
              <el-button v-if="canControlLine(row.lineId) && !row.confirmed" link type="primary" :disabled="eventsPending || eventsLoading" :loading="confirming.includes(row.id)" data-test="oee-dt-confirm" @click="confirmDt(row.id)">{{ $t('oee.confirm') }}</el-button>
              <el-button v-if="canControlLine(row.lineId)" link type="danger" :disabled="eventsPending || eventsLoading" @click="removeDt(row.id)">{{ $t('oee.remove') }}</el-button>
            </div>
            </template></el-table-column>
          </el-table>
          <el-pagination v-if="downtimes.length" v-model:current-page="eventsPage" :page-size="50" :total="downtimes.length" layout="total, prev, pager, next" class="mt" />
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="lineDlg" :title="$t('oee.line')" width="520px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="lineFormRef" :model="lineForm" :rules="lineRules" label-width="120px" status-icon :disabled="saving">
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="lineForm.name" maxlength="100" data-test="oee-line-name" /></el-form-item>
        <el-form-item :label="$t('oee.area')" prop="areaId"><el-select v-model="lineForm.areaId" style="width:100%" data-test="oee-line-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.idealCycle')" prop="idealCycleSec" :error="fieldErrors.idealCycleSec"><el-input-number v-model="lineForm.idealCycleSec" :min="0.001" :step="1" /></el-form-item>
        <el-form-item :label="$t('oee.product')"><el-select v-model="lineForm.productId" clearable style="width:100%"><el-option v-for="p in products" :key="p.id" :value="p.id" :label="p.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.enabled')"><el-switch v-model="lineForm.enabled" /></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="lineDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-line-save" @click="saveLine">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="productDlg" :title="$t('oee.product')" width="520px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="productFormRef" :model="productForm" :rules="productRules" label-width="120px" status-icon :disabled="saving">
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="productForm.name" maxlength="100" data-test="oee-product-name" /></el-form-item>
        <el-form-item :label="$t('oee.code')" prop="code" :error="fieldErrors.code"><el-input v-model="productForm.code" maxlength="60" /></el-form-item>
        <el-form-item :label="$t('oee.area')"><el-select v-model="productForm.areaId" :clearable="isGlobalConfigure" :placeholder="$t('oee.global')" style="width:100%" data-test="oee-product-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.idealCycle')" prop="idealCycleSec" :error="fieldErrors.idealCycleSec"><el-input-number v-model="productForm.idealCycleSec" :min="0.001" /></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="productDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-product-save" @click="saveProduct">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="shiftDlg" :title="$t('oee.shift')" width="480px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="shiftFormRef" :model="shiftForm" :rules="shiftRules" label-width="120px" status-icon :disabled="saving">
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="shiftForm.name" maxlength="100" data-test="oee-shift-name" /></el-form-item>
        <el-form-item :label="$t('oee.area')" prop="areaId"><el-select v-model="shiftForm.areaId" style="width:100%" data-test="oee-shift-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.startMin')" prop="startMinute"><el-input-number v-model="shiftForm.startMinute" :min="0" :max="1439" /></el-form-item>
        <el-form-item :label="$t('oee.endMin')" prop="endMinute"><el-input-number v-model="shiftForm.endMinute" :min="0" :max="1439" /></el-form-item>
        <el-form-item :label="$t('oee.enabled')"><el-switch v-model="shiftForm.enabled" /></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="shiftDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-shift-save" @click="saveShift">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="reasonDlg" :title="$t('oee.reason')" width="480px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="reasonFormRef" :model="reasonForm" :rules="reasonRules" label-width="120px" status-icon :disabled="saving">
        <el-form-item :label="$t('oee.code')" prop="code" :error="fieldErrors.code"><el-input v-model="reasonForm.code" maxlength="30" data-test="oee-reason-code" /></el-form-item>
        <el-form-item :label="$t('oee.name')" prop="name" :error="fieldErrors.name"><el-input v-model="reasonForm.name" maxlength="100" data-test="oee-reason-name" /></el-form-item>
        <el-form-item :label="$t('oee.category')"><el-select v-model="reasonForm.category" style="width:100%" data-test="oee-reason-category"><el-option v-for="(label, key) in categoryLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.area')"><el-select v-model="reasonForm.areaId" :clearable="isGlobalConfigure" :placeholder="$t('oee.global')" style="width:100%" data-test="oee-reason-area"><el-option v-for="a in configurableAreas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.planned')"><el-switch v-model="reasonForm.isPlanned" /></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="reasonDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-reason-save" @click="saveReason">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="dtDlg" :title="$t('oee.downtime')" width="520px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="dtFormRef" :model="dtForm" :rules="dtRules" label-width="120px" status-icon :disabled="saving">
        <el-form-item :label="$t('oee.line')" prop="lineId"><el-select v-model="dtForm.lineId" :disabled="dtEditId !== null" style="width:100%" data-test="oee-dt-line"><el-option v-for="l in controllableLines" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.start')" prop="start"><el-date-picker v-model="dtForm.start" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.end')" prop="end" :error="fieldErrors.end"><el-date-picker v-model="dtForm.end" type="datetime" :disabled="dtOngoing" style="width:100%" /><el-checkbox v-model="dtOngoing">{{ $t('ux.events.ongoing') }}</el-checkbox></el-form-item>
        <el-form-item :label="$t('oee.state')"><el-select v-model="dtForm.state" style="width:100%" data-test="oee-dt-state"><el-option v-for="key in downtimeStates" :key="key" :value="key" :label="stateLabels[key]" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.reason')"><el-select v-model="dtForm.reasonId" clearable style="width:100%" data-test="oee-dt-reason"><el-option v-for="r in reasons" :key="r.id" :value="r.id" :label="`${r.code} ${r.name}`" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.note')"><el-input v-model="dtForm.note" maxlength="500" /></el-form-item>
        <el-form-item :label="$t('oee.confirmed')"><el-checkbox v-model="dtForm.confirmed">{{ $t('ux.events.confirmed') }}</el-checkbox></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="dtDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-dt-save" @click="saveDt">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="corrDlg" :title="$t('oee.correction')" width="520px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form ref="corrFormRef" :model="corrForm" :rules="corrRules" label-width="120px" status-icon :disabled="saving">
        <el-form-item :label="$t('oee.line')" prop="lineId"><el-select v-model="corrForm.lineId" style="width:100%" data-test="oee-corr-line"><el-option v-for="l in controllableLines" :key="l.id" :value="l.id" :label="l.name" /></el-select></el-form-item>
        <el-form-item :label="$t('oee.start')" prop="start"><el-date-picker v-model="corrForm.start" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.end')" prop="end" :error="fieldErrors.end"><el-date-picker v-model="corrForm.end" type="datetime" style="width:100%" /></el-form-item>
        <el-form-item :label="$t('oee.good')"><el-input-number v-model="corrForm.goodDelta" data-test="oee-corr-good" /></el-form-item>
        <el-form-item :label="$t('oee.reject')"><el-input-number v-model="corrForm.rejectDelta" /></el-form-item>
        <el-form-item :label="$t('oee.scrap')"><el-input-number v-model="corrForm.scrapDelta" /></el-form-item>
        <el-form-item :label="$t('oee.reason')" prop="reason" :error="fieldErrors.reason"><el-input v-model="corrForm.reason" maxlength="200" data-test="oee-corr-reason" /></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="corrDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-corr-save" @click="saveCorr">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
    <el-dialog v-model="thDlg" :title="$t('oee.thresholds')" width="480px" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="oee-form-error" />
      <el-form label-width="160px" v-if="thForm" :disabled="saving">
        <el-form-item :label="$t('oee.targetOee')"><el-input-number v-model="thForm.targetOee" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetA')"><el-input-number v-model="thForm.targetAvailability" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetP')"><el-input-number v-model="thForm.targetPerformance" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.targetQ')"><el-input-number v-model="thForm.targetQuality" :min="0" :max="1" :step="0.01" /></el-form-item>
        <el-form-item :label="$t('oee.clampP')"><el-switch v-model="thForm.clampPerformance" /></el-form-item>
      </el-form>
      <template #footer><el-button :disabled="saving" @click="thDlg=false">{{ $t('oee.cancel') }}</el-button><el-button type="primary" :loading="saving" data-test="oee-th-save" @click="saveTh">{{ $t('oee.save') }}</el-button></template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { oeeApi, type DowntimeReason, type OeeThresholds, type ProductionLine, type ProductionProduct, type ProductionShift, type DowntimeEvent, type LineState } from '../api/oee'
import { areaApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { formatTime } from '../utils'
import { can, canGlobal } from '../access/policies'
import { errorMessage } from '../api/http'
import { useEntityEditor } from '../composables/useEntityEditor'
import { useEditDialog } from '../composables/useEditDialog'
import { t } from '../i18n'
import { useNowTicker } from '../composables/useNowTicker'

const props = withDefaults(defineProps<{ operationsOnly?: boolean }>(), { operationsOnly: false })
const route = useRoute()
const { now } = useNowTicker()
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

const cfgTab = ref(props.operationsOnly ? 'downtimes' : 'lines')
const loading = ref(true)
const actionError = ref('')
const metaError = ref('')
let disposed = false
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
const confirming = ref<number[]>([])
const configurableAreas = computed(() => areas.value.filter(a => can(auth.user, 'Configure', a.id)))
const controllableLines = computed(() => lines.value.filter(l => can(auth.user, 'Control', l.areaId)))
function canControlLine(lineId: number): boolean { return controllableLines.value.some(l => l.id === lineId) }
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? String(id)
const fmtMin = (m: number) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`
const eventLineId = ref<number>(), eventRange = ref<[Date, Date] | null>([new Date(Date.now() - 7 * 86400000), new Date()])
const eventConfirmation = ref<'all' | 'pending' | 'confirmed'>('all')
const eventsLoading = ref(false), eventsError = ref(''), eventsPage = ref(1), displayedEventsKey = ref('')
let eventsQueryVersion = 0
const eventParams = computed(() => ({ from: eventRange.value?.[0].getTime() ?? NaN, to: eventRange.value?.[1].getTime() ?? NaN,
  lineId: eventLineId.value, confirmed: eventConfirmation.value === 'all' ? undefined : eventConfirmation.value === 'confirmed' }))
const eventsPending = computed(() => !!displayedEventsKey.value && displayedEventsKey.value !== JSON.stringify(eventParams.value))
const pagedDowntimes = computed(() => downtimes.value.slice((eventsPage.value - 1) * 50, eventsPage.value * 50))
function defaultEventLineId(): number | undefined {
  return eventLineId.value === undefined ? controllableLines.value[0]?.id : controllableLines.value.find(line => line.id === eventLineId.value)?.id
}

function applyEventRoute(): boolean {
  const { from, to, lineId } = route.query
  if (from !== undefined || to !== undefined) {
    if (typeof from !== 'string' || typeof to !== 'string' || !Number.isFinite(Number(from)) || !Number.isFinite(Number(to)) || Number(from) >= Number(to)) {
      eventsError.value = t('ux.events.invalidRange'); return false
    }
    eventRange.value = [new Date(Number(from)), new Date(Number(to))]
  }
  if (lineId !== undefined) {
    if (typeof lineId !== 'string' || !lines.value.some(line => line.id === Number(lineId))) {
      eventsError.value = t('oee.lineRequired'); return false
    }
    eventLineId.value = Number(lineId)
  }
  return true
}

async function refreshMeta() {
  try {
    const [a, l, p, s, r] = await Promise.all([areaApi.list(), oeeApi.lines(), oeeApi.products(), oeeApi.shifts(), oeeApi.reasons()])
    if (disposed) return
    areas.value = a; lines.value = l; products.value = p; shifts.value = s; reasons.value = r
    metaError.value = ''
  } catch (error) {
    if (!disposed) metaError.value = errorMessage(error, t('oee.loadFailed'))
  }
}

async function loadDowntimes() {
  const params = eventParams.value, version = ++eventsQueryVersion
  if (!Number.isFinite(params.from) || !Number.isFinite(params.to) || params.from >= params.to || params.to - params.from > 366 * 86400000) {
    eventsLoading.value = false; eventsError.value = t('ux.events.invalidRange'); return
  }
  eventsLoading.value = true
  eventsError.value = ''
  try {
    const result = await oeeApi.downtimes(params)
    if (!disposed && version === eventsQueryVersion) {
      downtimes.value = result; eventsPage.value = 1; displayedEventsKey.value = JSON.stringify(params)
    }
  } catch (error) {
    if (!disposed && version === eventsQueryVersion) eventsError.value = errorMessage(error, t('oee.loadFailed'))
  } finally { if (!disposed && version === eventsQueryVersion) eventsLoading.value = false }
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
  start: [{ trigger: 'change', validator: (_rule, _value, callback) => validateDowntimeRange(callback) }],
  end: [{ trigger: 'change', validator: (_rule, _value, callback) => validateDowntimeRange(callback) }]
}))
const corrRules = computed<FormRules>(() => ({
  lineId: [{ required: true, message: t('oee.lineRequired'), trigger: 'change' }],
  start: rangeRule(() => corrForm.value),
  end: rangeRule(() => corrForm.value),
  reason: textRule(t('oee.reasonRequired'))
}))

const lineDlg = ref(false), lineForm = ref<any>({}), lineIdEdit = ref<number | null>(null)
function editLine(row?: ProductionLine) {
  if (disposed || saving.value || thOpening.value) return
  lineIdEdit.value = row?.id ?? null
  resetErrors()
  lineForm.value = row ? { ...row } : { name: '', areaId: configurableAreas.value[0]?.id, idealCycleSec: 60, enabled: true, stateMapJson: '{}' }
  lineDlg.value = true
}
async function saveLine(): Promise<void> {
  if (disposed || !lineDlg.value) return
  if (await saveWith(lineFormRef.value, () => oeeApi.saveLine(lineIdEdit.value, { ...lineForm.value }), refreshMeta)) lineDlg.value = false
}

async function removeLine(id: number) { await confirmRemove(() => oeeApi.removeLine(id), refreshMeta) }

const productDlg = ref(false), productForm = ref<any>({}), productIdEdit = ref<number | null>(null)
function defaultAreaId(): number | null {
  if (canGlobal(auth.user, 'Configure')) return null
  return configurableAreas.value[0]?.id ?? null
}
function editProduct(row?: ProductionProduct) {
  if (disposed || saving.value || thOpening.value) return
  productIdEdit.value = row?.id ?? null
  resetErrors()
  productForm.value = row ? { ...row } : { name: '', code: '', areaId: defaultAreaId(), idealCycleSec: 60 }
  productDlg.value = true
}
async function saveProduct(): Promise<void> {
  if (disposed || !productDlg.value) return
  if (await saveWith(productFormRef.value, () => oeeApi.saveProduct(productIdEdit.value, { ...productForm.value }), refreshMeta)) productDlg.value = false
}
async function removeProduct(id: number) { await confirmRemove(() => oeeApi.removeProduct(id), refreshMeta) }

const shiftDlg = ref(false), shiftForm = ref<any>({}), shiftIdEdit = ref<number | null>(null)
function editShift(row?: ProductionShift) {
  if (disposed || saving.value || thOpening.value) return
  shiftIdEdit.value = row?.id ?? null
  resetErrors()
  shiftForm.value = row ? { ...row } : { name: '', areaId: configurableAreas.value[0]?.id, startMinute: 480, endMinute: 1200, days: 127, enabled: true }
  shiftDlg.value = true
}
async function saveShift(): Promise<void> {
  if (disposed || !shiftDlg.value) return
  if (await saveWith(shiftFormRef.value, () => oeeApi.saveShift(shiftIdEdit.value, { ...shiftForm.value }), refreshMeta)) shiftDlg.value = false
}
async function removeShift(id: number) { await confirmRemove(() => oeeApi.removeShift(id), refreshMeta) }

const reasonDlg = ref(false), reasonForm = ref<any>({}), reasonIdEdit = ref<number | null>(null)
function editReason(row?: DowntimeReason) {
  if (disposed || saving.value || thOpening.value) return
  reasonIdEdit.value = row?.id ?? null
  resetErrors()
  reasonForm.value = row ? { ...row } : { code: '', name: '', category: 'Availability', isPlanned: false, enabled: true, areaId: defaultAreaId() }
  reasonDlg.value = true
}
async function saveReason(): Promise<void> {
  if (disposed || !reasonDlg.value) return
  if (await saveWith(reasonFormRef.value, () => oeeApi.saveReason(reasonIdEdit.value, { ...reasonForm.value }), refreshMeta)) reasonDlg.value = false
}
async function removeReason(id: number) { await confirmRemove(() => oeeApi.removeReason(id), refreshMeta) }

interface DowntimeForm { lineId?: number; start: Date | null; end: Date | null; state: LineState; reasonId: number | null; note: string; confirmed: boolean }
const dtDlg = ref(false), dtForm = ref<DowntimeForm>({ start: null, end: null, state: 'Fault', reasonId: null, note: '', confirmed: true })
const dtEditId = ref<number | null>(null), dtOngoing = ref(false)
function validateDowntimeRange(callback: RuleCallback) {
  const { start, end } = dtForm.value
  if (!start || !Number.isFinite(start.getTime()) || !dtOngoing.value && (!end || !Number.isFinite(end.getTime()))) callback(new Error(t('oee.timeRequired')))
  else if (!dtOngoing.value && end && end.getTime() <= start.getTime()) callback(new Error(t('oee.endAfterStart')))
  else callback()
}
function editDowntime(row?: DowntimeEvent) {
  if (disposed || saving.value || thOpening.value) return
  resetErrors()
  dtEditId.value = row?.id ?? null
  dtOngoing.value = !!row && row.endTs == null
  dtForm.value = row
    ? { lineId: row.lineId, start: new Date(row.startTs), end: row.endTs == null ? null : new Date(row.endTs), state: row.state, reasonId: row.reasonId ?? null, note: row.note ?? '', confirmed: row.confirmed }
    : { lineId: defaultEventLineId(), start: new Date(Date.now() - 3600000), end: new Date(), state: 'Fault', reasonId: null, note: '', confirmed: true }
  dtDlg.value = true
}
async function saveDt(): Promise<void> {
  if (disposed || !dtDlg.value) return
  const form = dtForm.value
  if (!form.lineId || !form.start || !canControlLine(form.lineId)) { formError.value = t('oee.lineRequired'); return }
  const body = { lineId: form.lineId, startTs: form.start.getTime(), endTs: dtOngoing.value ? null : form.end?.getTime(),
    state: form.state, reasonId: form.reasonId, note: form.note, confirmed: form.confirmed }
  const ok = await saveWith(dtFormRef.value, () => dtEditId.value === null ? oeeApi.createDowntime(body) : oeeApi.updateDowntime(dtEditId.value, body), loadDowntimes)
  if (ok) dtDlg.value = false
}
async function confirmDt(id: number) {
  const row = downtimes.value.find(event => event.id === id)
  if (disposed || !row || row.confirmed || !canControlLine(row.lineId) || confirming.value.includes(id)) return
  confirming.value.push(id)
  actionError.value = ''
  try {
    await oeeApi.confirmDowntime(id)
    if (!disposed) await loadDowntimes()
  } catch (error) {
    if (!disposed) actionError.value = errorMessage(error, t('oee.saveFailed'))
  } finally { confirming.value = confirming.value.filter(value => value !== id) }
}
async function removeDt(id: number) { await confirmRemove(() => oeeApi.removeDowntime(id), loadDowntimes) }

const corrDlg = ref(false), corrForm = ref<any>({})
function editCorrection() {
  if (disposed || saving.value || thOpening.value) return
  resetErrors()
  const to = Date.now()
  corrForm.value = { lineId: defaultEventLineId(), start: new Date(to - 3600000), end: new Date(to), goodDelta: 0, rejectDelta: 0, scrapDelta: 0, reason: '' }
  corrDlg.value = true
}
async function saveCorr(): Promise<void> {
  if (disposed || !corrDlg.value) return
  const form = corrForm.value
  const ok = await saveWith(corrFormRef.value, () => oeeApi.correct({
    lineId: form.lineId, periodFrom: form.start.getTime(), periodTo: form.end.getTime(),
    goodDelta: form.goodDelta, rejectDelta: form.rejectDelta, scrapDelta: form.scrapDelta, reason: form.reason
  }), loadDowntimes)
  if (ok) corrDlg.value = false
}

const thDlg = ref(false), thOpening = ref(false), thForm = ref<OeeThresholds | null>(null), thRev = ref(0)
function isConflict(error: unknown): boolean {
  return (error as { response?: { status?: number } } | undefined)?.response?.status === 409
}
async function openThresholds() {
  if (disposed || saving.value || thOpening.value || !isGlobalConfigure.value) return
  thOpening.value = true
  actionError.value = ''
  formError.value = ''
  try {
    const r = await oeeApi.thresholds()
    if (disposed) return
    thForm.value = { ...r.thresholds }; thRev.value = r.revision; formError.value = ''; thDlg.value = true
  } catch (error) {
    if (!disposed) actionError.value = errorMessage(error, t('oee.loadFailed'))
  } finally { if (!disposed) thOpening.value = false }
}
async function saveTh(): Promise<void> {
  if (disposed || !isGlobalConfigure.value || !thDlg.value || !thForm.value || saving.value) return
  const thresholds = { ...thForm.value }, revision = thRev.value
  saving.value = true
  formError.value = ''
  try {
    await oeeApi.saveThresholds(thresholds, revision)
    if (disposed) return
    ElMessage.success(t('oee.saved'))
    thDlg.value = false
  } catch (error) {
    if (disposed) return
    const message = errorMessage(error, t('oee.saveFailed'))
    formError.value = isConflict(error) ? t('oee.conflictHint', [message]) : message
  } finally {
    saving.value = false
  }
}

onMounted(async () => { try { await refreshMeta(); if (props.operationsOnly && !metaError.value && applyEventRoute()) await loadDowntimes() } finally { loading.value = false } })
onBeforeUnmount(() => { disposed = true })

// 七个配置弹窗的编辑守卫（未保存确认 / Ctrl+Enter 保存）；返回值需绑定到弹窗的 before-close 与 keydown 才生效，当前尚未绑定。
const lineGuard = useEditDialog({ visible: lineDlg, saving, snapshot: () => JSON.stringify({ id: lineIdEdit.value, form: lineForm.value }), save: saveLine })
const productGuard = useEditDialog({ visible: productDlg, saving, snapshot: () => JSON.stringify({ id: productIdEdit.value, form: productForm.value }), save: saveProduct })
const shiftGuard = useEditDialog({ visible: shiftDlg, saving, snapshot: () => JSON.stringify({ id: shiftIdEdit.value, form: shiftForm.value }), save: saveShift })
const reasonGuard = useEditDialog({ visible: reasonDlg, saving, snapshot: () => JSON.stringify({ id: reasonIdEdit.value, form: reasonForm.value }), save: saveReason })
const dtGuard = useEditDialog({ visible: dtDlg, saving, snapshot: () => JSON.stringify(dtForm.value), save: saveDt })
const corrGuard = useEditDialog({ visible: corrDlg, saving, snapshot: () => JSON.stringify(corrForm.value), save: saveCorr })
const thGuard = useEditDialog({ visible: thDlg, saving, snapshot: () => JSON.stringify(thForm.value), save: saveTh })
</script>

<style scoped>
.toolbar { display: flex; gap: 8px; align-items: center; margin-bottom: 12px; }
.spacer { flex: 1; }
.mt { margin-top: 8px; }
.event-filters { flex-wrap: wrap; }
.event-filters :deep(.el-select) { width: 180px; }
.event-filters :deep(.el-date-editor) { max-width: 100%; }
.event-actions { display: flex; gap: 4px; flex-wrap: wrap; }
.event-actions :deep(.el-button + .el-button) { margin-left: 0; }
</style>
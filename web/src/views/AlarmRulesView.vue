<template>
  <div class="page page--fill">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterTag" clearable filterable remote :remote-method="searchFilterTags" :loading="filterTagLoading" :placeholder="$t('alarmRulesView.kb8ceed')" :aria-label="$t('alarmRulesView.kb8ceed')" style="width: 220px" data-test="rule-filter-tag" @visible-change="onFilterTagsOpen">
          <el-option v-for="tag in filterTagOptions" :key="tag.id" :label="tagOptionLabel(tag)" :value="tag.id" />
        </el-select>
        <span class="spacer" />
        <template v-if="selected.length">
          <el-text data-test="rule-batch-count">{{ $t('alarmRulesView.k805f4f', [selected.length]) }}</el-text>
          <el-button data-test="rule-batch-enable" :loading="batching" @click="batchSetEnabled(true)">{{ $t('alarmRulesView.kf5e360') }}</el-button>
          <el-button data-test="rule-batch-disable" :loading="batching" @click="batchSetEnabled(false)">{{ $t('alarmRulesView.k45f528') }}</el-button>
          <el-button type="danger" plain data-test="rule-batch-delete" :loading="batching" @click="batchDelete">{{ $t('alarmRulesView.k362aed') }}</el-button>
          <el-button v-if="batching" type="warning" plain :disabled="batchCancel" data-test="rule-batch-stop" @click="batchCancel = true">{{ $t('batchUx.stopBatch') }}</el-button>
        </template>
        <el-button v-if="can(auth.user, 'Configure')" data-test="rule-quad-create" @click="openQuad">{{ $t('alarmRulesView.k7a0a8e') }}</el-button>
        <el-button :icon="Download" :loading="exporting" data-test="rule-export-xlsx" @click="exportRules('xlsx')">{{ $t('alarmRulesView.exportXlsx') }}</el-button>
        <el-button :icon="Download" :loading="exporting" data-test="rule-export-csv" @click="exportRules('csv')">{{ $t('alarmRulesView.exportCsv') }}</el-button>
        <el-button v-if="canImport" :icon="Upload" :loading="importing" data-test="rule-import" @click="pickImport">{{ $t('alarmRulesView.importRules') }}</el-button>
        <input ref="importInput" type="file" accept=".xlsx,.csv" hidden data-test="rule-import-file" @change="onImportFile" />
        <el-button type="primary" :icon="Plus" data-test="rule-create" @click="openCreate">{{ $t('alarmRulesView.k945c08') }}</el-button>
      </div>
      <div class="catalog-filters">
        <el-input v-model="search" clearable :prefix-icon="Search" :placeholder="$t('alarmRulesView.searchPlaceholder')" :aria-label="$t('alarmRulesView.searchPlaceholder')" class="catalog-search" />
        <el-select v-model="filterState" :aria-label="$t('listFilters.status')" class="catalog-select" data-test="rule-filter-state">
          <el-option :label="$t('listFilters.allStates')" value="all" />
          <el-option :label="$t('alarmRulesView.kd4e9ca')" value="enabled" />
          <el-option :label="$t('alarmRulesView.kbe70be')" value="disabled" />
          <el-option :label="$t('alarmRulesView.ka3f7d9')" value="outOfService" />
        </el-select>
        <el-select v-model="filterCategory" :aria-label="$t('alarmRulesView.k31f7ba')" class="catalog-select" data-test="rule-filter-category">
          <el-option :label="$t('alarmRulesView.k8b5074')" value="all" />
          <el-option v-for="(label, value) in alarmCategoryLabels" :key="value" :label="label" :value="value" />
        </el-select>
        <el-button v-if="hasFilters" link type="primary" data-test="rule-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <span v-if="!loading || ruleTotal > 0 || rules.length" class="catalog-count muted" role="status" data-test="rule-result-count">{{ $t('listFilters.resultCount', [ruleTotal, scopeTotal]) }}</span>
        <el-button :icon="Refresh" :loading="loading" data-test="rule-refresh" @click="load">{{ $t('listFilters.refresh') }}</el-button>
      </div>
      <el-alert v-if="batchProgress" :title="batchCancel ? $t('batchUx.stopping') : batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" data-test="rule-batch-progress" />
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="rules-load-error" />
      <el-alert v-if="catalogError" :title="catalogError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="rules-catalog-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="rules-action-error" />

      <el-table ref="tableRef" v-loading="loading" :data="rules" size="small" height="100%" row-key="id" @selection-change="onSelect" @sort-change="onSortChange">
        <template #empty>
          <span v-if="loadError">{{ loadError }}</span>
          <template v-else-if="hasFilters">
            <span>{{ $t('listFilters.noMatches') }}</span>
            <el-button link type="primary" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
          </template>
          <span v-else>{{ $t('alarmRulesView.ka0230f') }}</span>
        </template>
        <el-table-column type="selection" width="42" :selectable="canSelectRule" />
        <el-table-column prop="tagName" :label="$t('alarmRulesView.k8cff1c')" width="125" sortable="custom" show-overflow-tooltip>
          <template #default="{ row }">{{ tagName(row.tagId) }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.ke4e46c')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ alarmTypeLabels[row.type] }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.k31f7ba')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ alarmCategoryLabels[row.category] ?? row.category }}</template>
        </el-table-column>
        <el-table-column :label="`${$t('alarmRulesView.ka05320')} / ${$t('alarmRulesView.k254849')}`" width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="isLevelRule(row)" class="mono">{{ row.threshold }}</span>
            <span v-else-if="row.type === 'Discrete'" class="mono">{{ activeWhenLabels[row.activeWhen ?? 'On'] }}<template v-if="row.bitIndex != null"> · bit {{ row.bitIndex }}</template></span>
            <span v-else-if="row.type === 'Expression'" class="mono" :title="row.expression ?? ''">{{ row.expression ? 'f(x)' : '—' }}</span>
            <span v-else class="muted">—</span>
            <el-tag :type="levelType(row.level)" size="small" style="margin-left: 6px">{{ levelLabel(row.level) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="`${$t('alarmRulesView.k27cd56')} / ${$t('alarmRulesView.k3b86db')}`" width="170" show-overflow-tooltip>
          <template #default="{ row }">
            <template v-if="isLevelRule(row)">
              <span class="mono">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</span>
              <span class="muted tiny">· {{ row.recoverDeadband }}</span>
            </template>
            <span v-else-if="showsTiming(row.type, row.activeWhen)" class="mono">{{ row.delaySec }}s / {{ row.offDelaySec ?? 0 }}s</span>
            <span v-else class="muted tiny">—</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kb37a4e')" width="120" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.suppressMode && row.suppressMode !== 'None'" class="tiny">{{ $t('alarmRulesView.k045924', [tagName(row.suppressTagId), row.suppressMode === 'WhenOn' ? 1 : 0]) }}</span>
            <span v-else class="muted tiny">-</span>
          </template>
        </el-table-column>
        <el-table-column prop="message" :label="$t('alarmRulesView.kb62f14')" min-width="150" show-overflow-tooltip sortable="custom">
          <template #default="{ row }">{{ localizedText(row.message, row.messageEn) || '—' }}</template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kd4e9ca')" width="78">
          <template #default="{ row }">
            <el-tag :class="{ 'tag-quiet': row.enabled }" :type="row.enabled ? undefined : 'warning'" size="small" :data-test="`rule-enabled-${row.id}`">{{ row.enabled ? $t('alarmRulesView.kd4e9ca') : $t('alarmRulesView.kbe70be') }}</el-tag>
            <el-tooltip v-if="row.outOfService" :content="row.outOfServiceReason || ''"><el-tag type="warning" size="small" data-test="oos-tag">{{ $t('alarmRulesView.ka3f7d9') }}</el-tag></el-tooltip>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmRulesView.kf3ea6d')" width="240" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canConfigureRule(row)" link type="primary" @click="openEdit(row)">{{ $t('alarmRulesView.ka7f814') }}</el-button>
            <el-button v-if="canConfigureRule(row) && !row.outOfService" link @click="setOutOfService(row, true)">{{ $t('alarmRulesView.kd989e5') }}</el-button>
            <el-button v-if="canConfigureRule(row) && row.outOfService" link @click="setOutOfService(row, false)">{{ $t('alarmRulesView.k79748c') }}</el-button>
            <el-popconfirm v-if="canConfigureRule(row)" :title="$t('alarmRulesView.k2343c0')" @confirm="remove(row)">
              <template #reference><el-button link type="danger">{{ $t('alarmRulesView.k3755f5') }}</el-button></template>
            </el-popconfirm>
            <el-dropdown v-if="canConfigureRule(row)" trigger="click">
              <el-button link data-test="rule-more">{{ $t('ux.tables.more') }}</el-button>
              <template #dropdown><el-dropdown-menu>
                <el-dropdown-item data-test="rule-simulate" @click="openSimulate(row)">{{ $t('alarmRulesView.simulate') }}</el-dropdown-item>
                <el-dropdown-item data-test="rule-copy" @click="openCopy(row)">{{ $t('alarmRulesView.k4edd1d') }}</el-dropdown-item>
              </el-dropdown-menu></template>
            </el-dropdown>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination :current-page="page" :page-size="PAGE_SIZE" :total="ruleTotal" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="rule-pagination" @update:current-page="page = $event" @current-change="onPageChange" />
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? $t('alarmRulesView.kc9caa8') : (copying ? $t('alarmRulesView.k24245b') : $t('alarmRulesView.ke76def'))" width="520px" data-test="rule-dialog" :before-close="editDialog.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" @keydown="editDialog.onKey" @opened="onDialogOpened">
      <el-alert v-if="saveError" :title="saveError" type="error" :closable="false" show-icon tabindex="-1" data-test="rule-save-error" />
      <el-alert
        v-if="!editingId"
        type="info"
        :closable="false"
        show-icon
        data-test="rule-defaults-tip"
        :title="$t('alarmRulesView.k4cad37')"
        style="margin-bottom: 12px"
      />
      <el-alert v-if="fieldErrors._form" :title="fieldErrors._form" type="error" :closable="false" show-icon style="margin-bottom: 12px" />
      <el-form ref="formRef" :model="form" :rules="validationRules" :disabled="saving" label-width="110px" status-icon @submit.prevent="save">
        <el-form-item :label="$t('alarmRulesView.k8cff1c')" prop="tagId" :error="fieldErrors.tagId">
          <el-select :model-value="form.tagId > 0 ? form.tagId : undefined" filterable remote :remote-method="searchRuleTags" :loading="ruleTagLoading" style="width: 100%" data-test="rule-tag" @update:model-value="onRuleTagPicked" @visible-change="onRuleTagsOpen">
            <el-option v-for="tag in ruleTagOptions" :key="tag.id" :label="tagOptionLabel(tag)" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k7655f4')" prop="type">
          <el-select v-model="form.type" style="width: 100%" data-test="rule-type" @change="onTypeChange">
            <el-option v-for="(label, value) in ruleTypeOptions" :key="value" :label="label" :value="value" />
            <el-option v-if="form.type === 'StateChange'" :label="alarmTypeLabels.StateChange" value="StateChange" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.type === 'Expression'" :label="$t('alarmRulesView.expression')" prop="expression" :error="fieldErrors.expression">
          <el-input v-model="form.expression" type="textarea" :rows="3" maxlength="4096" :placeholder="$t('alarmRulesView.expressionPlaceholder')" data-test="rule-expression" />
          <span class="tiny muted">{{ $t('alarmRulesView.expressionHint') }}</span>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k31f7ba')" prop="category">
          <el-select v-model="form.category" style="width: 100%" data-test="rule-category">
            <el-option v-for="(label, value) in alarmCategoryLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.type === 'Discrete'" :label="$t('alarmRulesView.kca6c9e')" prop="activeWhen">
          <el-radio-group v-model="form.activeWhen" data-test="rule-active-when">
            <el-radio-button value="On">{{ activeWhenLabels.On }}</el-radio-button>
            <el-radio-button value="Off">{{ activeWhenLabels.Off }}</el-radio-button>
            <el-radio-button value="Change">{{ activeWhenLabels.Change }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="form.type === 'Discrete'">
          <p class="tiny muted">{{ $t('alarmRulesView.k1cb89d') }}</p>
        </el-form-item>
        <el-form-item v-if="needsBitIndex" :label="$t('alarmRulesView.bitIndex')" prop="bitIndex" :error="fieldErrors.bitIndex">
          <el-input-number v-model="form.bitIndex" :min="0" :max="selectedBitMax" :controls="false" style="width: 100%" data-test="rule-bit-index" />
          <span class="tiny muted">{{ $t('alarmRulesView.bitIndexHint', [selectedBitMax]) }}</span>
        </el-form-item>
        <el-form-item v-if="isLevelType" :label="$t('alarmRulesView.ka05320')" prop="threshold" :error="fieldErrors.threshold">
          <el-input-number v-model="form.threshold" :controls="false" style="width: 100%" data-test="rule-threshold" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kd02c6b')" prop="consequence">
          <el-select v-model="form.consequence" style="width: 100%" data-test="rule-consequence">
            <el-option v-for="(label, value) in alarmConsequenceLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k14a01b')" prop="responseTimeSec" :error="fieldErrors.responseTimeSec">
          <el-input-number v-model="form.responseTimeSec" :min="0" :max="86400" style="width: 100%" data-test="rule-response-time" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k421fc5')" prop="autoPriority">
          <el-switch v-model="form.autoPriority" data-test="rule-auto-priority" />
          <span class="tiny muted" style="margin-left: 8px">{{ $t('alarmRulesView.k464d7d') }}</span>
        </el-form-item>
        <el-form-item v-if="form.autoPriority" :label="$t('alarmRulesView.k254849')">
          <el-tag :type="levelType(autoLevel)" data-test="rule-auto-level">{{ levelLabel(autoLevel) }}</el-tag>
        </el-form-item>
        <el-form-item v-else :label="$t('alarmRulesView.k254849')" prop="level">
          <el-radio-group v-model="form.level" data-test="rule-level">
            <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
            <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
            <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="formShowsTiming" :label="$t('alarmRulesView.kbaa5d6')" prop="delaySec" :error="fieldErrors.delaySec">
          <el-input-number v-model="form.delaySec" :min="0" :max="3600" style="width: 100%" data-test="rule-delay" />
        </el-form-item>
        <el-form-item v-if="formShowsTiming" :label="$t('alarmRulesView.kcf7b90')" prop="offDelaySec">
          <el-input-number v-model="form.offDelaySec" :min="0" :max="3600" style="width: 100%" />
        </el-form-item>
        <el-form-item v-if="isLevelType" :label="$t('alarmRulesView.k3b86db')" prop="recoverDeadband" :error="fieldErrors.recoverDeadband">
          <el-input-number v-model="form.recoverDeadband" :min="0" :step="0.1" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kb37a4e')" prop="suppressMode">
          <el-select v-model="form.suppressMode" style="width: 100%">
            <el-option v-for="(label, value) in suppressModeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.suppressMode !== 'None'" :label="$t('alarmRulesView.ka52424')" prop="suppressTagId" :error="fieldErrors.suppressTagId">
          <el-select v-model="form.suppressTagId" filterable remote clearable :remote-method="searchSuppressTags" :loading="suppressTagLoading" style="width: 100%" :placeholder="$t('alarmRulesView.kd680cd')" @visible-change="onSuppressTagsOpen">
            <el-option v-for="tag in suppressTagOptions" :key="tag.id" :label="tagOptionLabel(tag)" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kb62f14')" prop="message">
          <el-input v-model="form.message" :placeholder="$t('alarmRulesView.k7c59bc')" data-test="rule-message" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.messageEn')" prop="messageEn">
          <el-input v-model="form.messageEn" maxlength="500" :placeholder="$t('alarmRulesView.messageEnHint')" data-test="rule-message-en" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.cause')" prop="cause">
          <el-input v-model="form.cause" :placeholder="$t('alarmRulesView.causePlaceholder')" maxlength="200" data-test="rule-cause" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.causeEn')" prop="causeEn">
          <el-input v-model="form.causeEn" maxlength="200" :placeholder="$t('alarmRulesView.causeEnHint')" data-test="rule-cause-en" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.action')" prop="action">
          <el-input v-model="form.action" :placeholder="$t('alarmRulesView.actionPlaceholder')" maxlength="200" data-test="rule-action" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.actionEn')" prop="actionEn">
          <el-input v-model="form.actionEn" maxlength="200" :placeholder="$t('alarmRulesView.actionEnHint')" data-test="rule-action-en" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.kd4e9ca')" prop="enabled">
          <el-switch v-model="form.enabled" data-test="rule-enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <span class="edit-dialog-shortcut">{{ $t('editDialog.shortcut', [editDialog.modKey]) }}</span>
        <el-button :disabled="saving" @click="editDialog.close">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="rule-save" :loading="saving" :disabled="!canSave" @click="save">{{ $t('alarmRulesView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="quadVisible" :title="$t('alarmRulesView.k55a182')" width="640px" data-test="rule-quad-dialog" :close-on-click-modal="!quadSaving" :close-on-press-escape="!quadSaving" :show-close="!quadSaving" :before-close="quadGuard.beforeClose" @keydown="quadGuard.onKey">
      <el-alert v-if="quadError" :title="quadError" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-error" />
      <el-alert v-if="quadLoading" :title="$t('alarmRulesView.kbb9cb2')" type="info" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-loading" />
      <el-alert v-else-if="quadForm.tagId && quadRows.length === 0" :title="$t('alarmRulesView.ka49545')" type="warning" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-none" />
      <el-alert v-else-if="quadSkipNotice" :title="quadSkipNotice" type="info" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-skip" />
      <el-form :disabled="quadSaving" label-width="110px" @submit.prevent="saveQuad">
        <el-form-item :label="$t('alarmRulesView.k8cff1c')" required>
          <el-select :model-value="quadForm.tagId > 0 ? quadForm.tagId : undefined" filterable remote :remote-method="searchQuadTags" :loading="quadTagLoading" style="width: 100%" data-test="rule-quad-tag" @update:model-value="onQuadTagPicked" @visible-change="onQuadTagsOpen">
            <el-option v-for="tag in quadTagOptions" :key="tag.id" :label="tagOptionLabel(tag)" :value="tag.id" />
          </el-select>
        </el-form-item>
        <el-row :gutter="12">
          <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.kfe8edb')"><el-input-number v-model="quadForm.highHigh" :controls="false" style="width: 100%" data-test="rule-quad-hh" /></el-form-item></el-col>
          <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.k88deca')"><el-input-number v-model="quadForm.high" :controls="false" style="width: 100%" data-test="rule-quad-h" /></el-form-item></el-col>
          <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.kb44804')"><el-input-number v-model="quadForm.low" :controls="false" style="width: 100%" data-test="rule-quad-l" /></el-form-item></el-col>
          <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.k09df8e')"><el-input-number v-model="quadForm.lowLow" :controls="false" style="width: 100%" data-test="rule-quad-ll" /></el-form-item></el-col>
        </el-row>
        <el-form-item :label="$t('alarmRulesView.ke682b0')">
          <el-radio-group v-model="quadForm.criticalLevel" data-test="rule-quad-critical-level">
            <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
            <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
            <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.k318342')">
          <el-radio-group v-model="quadForm.highLevel" data-test="rule-quad-high-level">
            <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
            <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
            <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-row :gutter="12">
          <el-col :xs="24" :sm="12"><el-form-item :label="$t('alarmRulesView.k85d836')"><el-input-number v-model="quadForm.delaySec" :min="0" :max="3600" style="width: 100%" data-test="rule-quad-delay" /></el-form-item></el-col>
          <el-col :xs="24" :sm="12"><el-form-item :label="$t('alarmRulesView.k3b86db')"><el-input-number v-model="quadForm.recoverDeadband" :min="0" :step="0.1" style="width: 100%" data-test="rule-quad-deadband" /></el-form-item></el-col>
        </el-row>
        <el-form-item :label="$t('alarmRulesView.kd4e9ca')">
          <el-switch v-model="quadForm.enabled" data-test="rule-quad-enabled" />
        </el-form-item>
      </el-form>
      <el-alert v-if="quadOrderError" :title="quadOrderError" type="warning" :closable="false" show-icon style="margin-bottom: 8px" data-test="rule-quad-order" />
      <p class="tiny muted">{{ $t('alarmRulesView.k2377c1') }}</p>
      <el-table :data="quadRows" size="small" data-test="rule-quad-preview">
        <el-table-column :label="$t('alarmRulesView.k9f23cf')" min-width="120" show-overflow-tooltip><template #default="{ row }">{{ row.label }}</template></el-table-column>
        <el-table-column :label="$t('alarmRulesView.ka05320')" width="120" show-overflow-tooltip><template #default="{ row }"><span class="mono">{{ row.threshold ?? '-' }}</span></template></el-table-column>
        <el-table-column :label="$t('alarmRulesView.k5c42c0')" width="100" show-overflow-tooltip><template #default="{ row }">{{ levelLabel(row.level) }}</template></el-table-column>
      </el-table>
      <template #footer>
        <el-button :disabled="quadSaving" @click="quadGuard.close">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="quadSaving" :disabled="!canSaveQuad" data-test="rule-quad-save" @click="saveQuad">{{ $t('alarmRulesView.k4dcb41', [quadRows.length]) }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="simulate.visible" :title="$t('alarmRulesView.simulateTitle')" width="560px" data-test="rule-simulate-dialog" :close-on-click-modal="!simulate.loading" :close-on-press-escape="!simulate.loading" :show-close="!simulate.loading">
      <el-alert type="info" :closable="false" show-icon :title="$t('alarmRulesView.simulateNote')" style="margin-bottom: 12px" />
      <div v-if="simulate.rule" class="tiny muted" style="margin-bottom: 12px">
        #{{ simulate.rule.id }} · {{ tagName(simulate.rule.tagId) }} · {{ alarmTypeLabels[simulate.rule.type] }}
      </div>
      <el-alert v-if="simulate.error" :title="simulate.error" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="simulate-error" />
      <el-form label-width="120px" :disabled="simulate.loading" @submit.prevent>
        <el-form-item :label="$t('alarmRulesView.simulateValue')">
          <el-input-number v-model="simulate.value" :controls="false" style="width: 160px" data-test="simulate-value" />
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.simulateQuality')">
          <el-select v-model="simulate.quality" style="width: 160px" data-test="simulate-quality">
            <el-option v-for="(label, value) in qualityLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('alarmRulesView.simulatePrevious')">
          <el-input-number v-model="simulate.previousValue" :controls="false" style="width: 160px" data-test="simulate-previous" />
        </el-form-item>
        <template v-if="simulateRefIds.length">
          <el-form-item v-for="id in simulateRefIds" :key="id" :label="tagName(id)">
            <el-input-number v-model="simulate.references[id]" :controls="false" style="width: 140px" :data-test="`simulate-ref-${id}`" />
            <el-select v-model="simulate.referenceQualities[id]" style="width: 110px; margin-left: 8px" :data-test="`simulate-ref-quality-${id}`">
              <el-option v-for="(label, value) in qualityLabels" :key="value" :label="label" :value="value" />
            </el-select>
          </el-form-item>
        </template>
      </el-form>
      <el-alert v-if="simulate.result" :type="simulateResultType" :closable="false" show-icon :title="simulateResultTitle" data-test="simulate-result">
        <div class="tiny" style="white-space: pre-wrap">{{ simulate.result.indeterminate || localizedText(simulate.result.message, simulate.result.messageEn) }}</div>
        <div v-if="simulate.result.messageEn" class="tiny muted" style="white-space: pre-wrap">{{ simulate.result.messageEn }}</div>
        <div v-if="simulate.result.references.length" class="tiny muted">
          {{ simulate.result.references.map(r => `${r.tagName}=${r.value} (${r.quality})`).join(' · ') }}
        </div>
      </el-alert>
      <template #footer>
        <el-button :disabled="simulate.loading" @click="simulate.visible = false">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="simulate.loading" data-test="simulate-run" @click="runSimulate">{{ $t('alarmRulesView.simulateRun') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, toRefs, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules, type TableInstance } from 'element-plus'
import { Plus, Refresh, Search, Download, Upload } from '@element-plus/icons-vue'
import { ruleApi, deviceApi, tagApi } from '../api'
import type { AlarmRuleSimulateResult } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmRule, AlarmRuleInput, Tag } from '../types'
import { alarmTypeLabels, activeWhenLabels, alarmCategoryLabels, alarmConsequenceLabels, alarmPriorityLevel, downloadBlob, levelLabel, levelType, localizedText, qualityLabels, suppressModeLabels } from '../utils'
import { can, canGlobal } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { tr } from '../i18n'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { readLastNumber, rememberNumber } from '../composables/lastChoice'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'
import { positiveQueryId } from '../nav/query'
import { useCatalogQuery } from '../composables/useCatalogQuery'
import { focusFormError, useEditDialog } from '../composables/useEditDialog'

const auth = useAuthStore()
const route = useRoute(), router = useRouter()
const canImport = computed(() => canGlobal(auth.user, 'Configure'))
const importInput = ref<HTMLInputElement>()
const exporting = ref(false), importing = ref(false)
const rules = ref<AlarmRule[]>([])
const ruleTotal = ref(0)
const scopeTotal = ref(0)
const tags = ref<Tag[]>([])
const devices = ref<{ id: number; areaId?: number | null }[]>([])
let devicesPromise: Promise<void> | null = null
let devicesLoaded = false
function canConfigureTag(tag: Tag): boolean {
  if (tag.dataType === 'String') return false
  if (tag.kind !== 'Physical') return tag.areaId != null ? can(auth.user, 'Configure', tag.areaId) : can(auth.user, 'Configure', undefined)
  return can(auth.user, 'Configure', devices.value.find(d => d.id === tag.deviceId)?.areaId)
}
function canConfigureRule(rule: AlarmRule): boolean {
  const tag = tags.value.find(t => t.id === rule.tagId)
  return !!tag && canConfigureTag(tag)
}
function canSelectRule(rule: AlarmRule): boolean { return canConfigureRule(rule) }
const filterTag = computed({
  get: () => positiveQueryId(route.query.tagId),
  set: (value: number | undefined) => {
    const query = { ...route.query }
    if (value) query.tagId = String(value)
    else delete query.tagId
    delete query.page
    void router.replace({ query })
  }
})
const catalog = useCatalogQuery({ search: '', state: 'all', category: 'all' })
const { search, state: filterState, category: filterCategory } = toRefs(catalog.draft)
const hasFilters = computed(() => !!catalog.applied.value.search || catalog.applied.value.state !== 'all' || catalog.applied.value.category !== 'all' || !!catalog.draft.search.trim() || catalog.draft.state !== 'all' || catalog.draft.category !== 'all' || filterTag.value !== undefined)
const tableRef = ref<TableInstance>()
const PAGE_SIZE = 100
const page = computed({
  get: () => catalog.page.value,
  set: value => { catalog.page.value = value }
})
const sortKey = ref('')
const sortOrder = ref<'ascending' | 'descending' | null>(null)
function onPageChange(): void { selected.value = []; tableRef.value?.clearSelection() }
function onSortChange({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }): void {
  sortKey.value = order && (prop === 'tagName' || prop === 'message') ? prop : ''
  sortOrder.value = order
  if (catalog.page.value !== 1) catalog.page.value = 1
  onPageChange()
}
let searchTimer = 0
watch(() => catalog.draft.search, () => {
  window.clearTimeout(searchTimer)
  searchTimer = window.setTimeout(() => { void catalog.apply() }, 250)
})
watch(() => catalog.draft.state, () => { void catalog.apply() })
watch(() => catalog.draft.category, () => { void catalog.apply() })
watch([() => JSON.stringify(catalog.applied.value), () => catalog.page.value, filterTag, sortKey, sortOrder], () => {
  onPageChange()
  void load()
})
const loading = ref(false)
const loadError = ref('')
const catalogError = ref('')
const actionError = ref('')
const saveError = ref('')
const saving = ref(false)
const batching = ref(false)
const batchCancel = ref(false)
const batchProgress = ref('')
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const copying = ref(false)
const selected = ref<AlarmRule[]>([])
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})

const emptyForm = (): AlarmRuleInput => ({
  tagId: 0,
  type: 'High',
  activeWhen: 'On',
  threshold: 0,
  level: 'Warning',
  category: 'Process',
  consequence: 'Major',
  responseTimeSec: 600,
  autoPriority: true,
  delaySec: 3,
  recoverDeadband: 0,
  enabled: true,
  message: '',
  messageEn: '',
  cause: '',
  causeEn: '',
  action: '',
  actionEn: '',
  offDelaySec: 0,
  bitIndex: null,
  expression: '',
  suppressTagId: null,
  suppressMode: 'None'
})
const form = reactive<AlarmRuleInput>(emptyForm())
const levelTypes: AlarmRule['type'][] = ['High', 'HighHigh', 'Low', 'LowLow']
// 新建时不再提供旧的 StateChange；已有规则编辑时仍按原值显示，避免无谓地改动存量数据。
const ruleTypeOptions = computed(() => Object.entries(alarmTypeLabels).filter(([value]) => value !== 'StateChange'))
const discreteDataTypes = ['Bool', 'Int16', 'UInt16', 'Int32', 'UInt32']
const selectedTag = computed(() => tags.value.find(tag => tag.id === form.tagId))
const needsBitIndex = computed(() => form.type === 'Discrete' && selectedTag.value != null && selectedTag.value.dataType !== 'Bool')
const selectedBitMax = computed(() => selectedTag.value?.dataType === 'Int16' || selectedTag.value?.dataType === 'UInt16' ? 15 : 31)
const isLevelType = computed(() => isLevelRule(form))
const formShowsTiming = computed(() => showsTiming(form.type, form.activeWhen))
const autoLevel = computed(() => alarmPriorityLevel(form.consequence, form.responseTimeSec))
function isLevelRule(rule: { type: AlarmRule['type'] }): boolean { return levelTypes.includes(rule.type) }
function showsTiming(type: AlarmRule['type'], activeWhen?: AlarmRule['activeWhen']): boolean {
  return isLevelRule({ type }) || type === 'BadQuality' || type === 'Expression'
    || (type === 'Discrete' && (activeWhen ?? 'On') !== 'Change')
}
// 位报警默认即时判定：切换到位报警时清掉模拟量专用的延时与死区，仍可在表单里手动调整。
function onTypeChange(type: AlarmRule['type']): void {
  if (type !== 'Discrete') {
    form.bitIndex = null
    void searchRuleTags('')
    return
  }
  if (!selectedTag.value || !discreteDataTypes.includes(selectedTag.value.dataType) || !canConfigureTag(selectedTag.value)) form.tagId = 0
  form.activeWhen = 'On'
  form.delaySec = 0
  form.offDelaySec = 0
  form.recoverDeadband = 0
  form.bitIndex = selectedTag.value?.dataType === 'Bool' ? null : form.bitIndex ?? 0
  void searchRuleTags('')
}
function onTagChange(): void {
  if (form.type !== 'Discrete') return
  form.bitIndex = selectedTag.value?.dataType === 'Bool' ? null : form.bitIndex ?? 0
}

const TAG_PICKER_PAGE = 50
const filterTagOptions = ref<Tag[]>([])
const filterTagLoading = ref(false)
const ruleTagOptions = ref<Tag[]>([])
const ruleTagLoading = ref(false)
const suppressTagOptions = ref<Tag[]>([])
const suppressTagLoading = ref(false)
const quadTagOptions = ref<Tag[]>([])
const quadTagLoading = ref(false)

async function ensureDevices(): Promise<void> {
  if (devicesLoaded) return
  if (!devicesPromise) {
    devicesPromise = deviceApi.list()
      .then(list => { devices.value = list; devicesLoaded = true; catalogError.value = '' })
      .catch(error => {
        devicesPromise = null
        catalogError.value = errorMessage(error, tr('alarmRulesView.kdf92e4'))
        throw error
      })
  }
  await devicesPromise
}

function rememberTags(list: Tag[]): void {
  if (!list.length) return
  const map = new Map(tags.value.map(tag => [tag.id, tag]))
  for (const tag of list) map.set(tag.id, tag)
  tags.value = [...map.values()]
}

async function pinTag(id?: number | null): Promise<Tag | undefined> {
  if (!id) return
  const known = tags.value.find(tag => tag.id === id)
  if (known) return known
  try {
    const [tag] = await tagApi.list({ ids: [id] })
    if (tag) rememberTags([tag])
    return tag
  } catch {
    return undefined
  }
}

function tagOptionLabel(tag: Tag): string { return `${tag.name} (#${tag.id})` }

function allowRuleTag(tag: Tag): boolean {
  if (!canConfigureTag(tag)) return false
  return form.type !== 'Discrete' || discreteDataTypes.includes(tag.dataType)
}

function withPinned(items: Tag[], pinned: Tag | undefined, allow?: (tag: Tag) => boolean, force = false): Tag[] {
  const list = allow ? items.filter(allow) : items.slice()
  if (pinned && (force || !allow || allow(pinned)) && !list.some(tag => tag.id === pinned.id)) list.unshift(pinned)
  return list
}

function bindTagSearch(
  target: { value: Tag[] },
  loading: { value: boolean },
  accept?: () => ((tag: Tag) => boolean) | undefined,
  pinnedId?: () => number | undefined,
  forcePin?: () => boolean
): (query: string) => Promise<void> {
  let version = 0
  return async (query: string) => {
    const current = ++version
    loading.value = true
    try {
      await ensureDevices().catch(() => undefined)
      const page = await tagApi.page({ search: query.trim() || undefined, skip: 0, take: TAG_PICKER_PAGE })
      if (current !== version) return
      rememberTags(page.items)
      const pinned = await pinTag(pinnedId?.())
      if (current !== version) return
      target.value = withPinned(page.items, pinned, accept?.(), forcePin?.() ?? false)
    } catch {
      if (current !== version) return
      const pinned = tags.value.find(tag => tag.id === pinnedId?.())
      if (pinned) target.value = withPinned(target.value, pinned, accept?.(), forcePin?.() ?? false)
    } finally {
      if (current === version) loading.value = false
    }
  }
}

const searchFilterTags = bindTagSearch(filterTagOptions, filterTagLoading, undefined, () => filterTag.value, () => true)
const searchRuleTags = bindTagSearch(ruleTagOptions, ruleTagLoading, () => allowRuleTag, () => form.tagId || undefined, () => editingId.value != null || copying.value)
const searchSuppressTags = bindTagSearch(suppressTagOptions, suppressTagLoading, () => (tag: Tag) => tag.id !== form.tagId, () => form.suppressTagId ?? undefined, () => true)
const searchQuadTags = bindTagSearch(quadTagOptions, quadTagLoading, () => canConfigureTag, () => quadForm.tagId || undefined, () => false)

function onFilterTagsOpen(open: boolean): void { if (open) void searchFilterTags('') }
function onRuleTagsOpen(open: boolean): void { if (open) void searchRuleTags('') }
function onSuppressTagsOpen(open: boolean): void { if (open) void searchSuppressTags('') }
function onQuadTagsOpen(open: boolean): void { if (open) void searchQuadTags('') }
function onRuleTagPicked(value: number | undefined): void {
  form.tagId = value ?? 0
  onTagChange()
}
function onQuadTagPicked(value: number | undefined): void {
  quadForm.tagId = value ?? 0
  selectQuadTag()
}
const editDialog = useEditDialog({ visible: dialogVisible, saving, form: formRef, snapshot: () => JSON.stringify(form), initialField: 'threshold', save })

const validationRules = computed<FormRules>(() => ({
  tagId: [{ required: true, type: 'number', min: 1, message: tr('alarmRulesView.k1a545c'), trigger: 'change' }],
  suppressTagId: [{
    validator: (_r, _v, cb) => {
      if (form.suppressMode !== 'None' && !form.suppressTagId) cb(new Error(tr('alarmRulesView.kfb2726')))
      else cb()
    },
    trigger: 'change'
  }],
  delaySec: [{ type: 'number', min: 0, max: 3600, message: tr('alarmRulesView.k233836'), trigger: 'change' }],
  offDelaySec: [{ type: 'number', min: 0, max: 3600, message: tr('alarmRulesView.k04a0a0'), trigger: 'change' }],
  responseTimeSec: [
    { type: 'number', min: 0, max: 86400, message: tr('alarmRulesView.kf07468'), trigger: 'change' },
    {
      validator: (_r, _v, cb) => {
        if (form.autoPriority && form.responseTimeSec <= 0) cb(new Error(tr('alarmRulesView.kdf43a3')))
        else cb()
      },
      trigger: 'change'
    }
  ],
  recoverDeadband: [{ type: 'number', min: 0, message: tr('alarmRulesView.ka8a2b8'), trigger: 'change' }],
  expression: [{
    validator: (_r, _v, cb) => {
      if (form.type === 'Expression' && !form.expression?.trim()) cb(new Error(tr('alarmRulesView.expressionRequired')))
      else cb()
    },
    trigger: 'change'
  }],
  bitIndex: [{
    validator: (_r, _v, cb) => {
      if (needsBitIndex.value && form.bitIndex == null) cb(new Error(tr('alarmRulesView.bitIndexRequired')))
      else cb()
    },
    trigger: 'change'
  }]
}))
const canSave = computed(() => {
  if (!form.tagId) return false
  if (form.suppressMode !== 'None' && !form.suppressTagId) return false
  if (form.type === 'Expression' && !form.expression?.trim()) return false
  if (needsBitIndex.value && form.bitIndex == null) return false
  return true
})

// 四限批量生成：按测点量程预填高/低阈值，只补该测点尚未配置的限值类型。
type QuadRow = { type: AlarmRule['type']; level: AlarmRule['level']; threshold: number | null; label: string }
const quadTypeDefs: { type: AlarmRule['type']; thresholdKey: 'highHigh' | 'high' | 'low' | 'lowLow'; levelKey: 'criticalLevel' | 'highLevel' }[] = [
  { type: 'HighHigh', thresholdKey: 'highHigh', levelKey: 'criticalLevel' },
  { type: 'High', thresholdKey: 'high', levelKey: 'highLevel' },
  { type: 'Low', thresholdKey: 'low', levelKey: 'highLevel' },
  { type: 'LowLow', thresholdKey: 'lowLow', levelKey: 'criticalLevel' }
]
const quadVisible = ref(false), quadSaving = ref(false), quadLoading = ref(false), quadLoaded = ref(false), quadError = ref('')
const quadExisting = ref<AlarmRule[]>([])
const quadForm = reactive({
  tagId: 0,
  highHigh: 100,
  high: 90,
  low: 10,
  lowLow: 0,
  highLevel: 'Warning' as AlarmRule['level'],
  criticalLevel: 'Critical' as AlarmRule['level'],
  delaySec: 3,
  recoverDeadband: 0,
  enabled: true
})
const quadSkippedDefs = computed(() => {
  const types = new Set(quadExisting.value.map(rule => rule.type))
  return quadTypeDefs.filter(def => types.has(def.type))
})
const quadRows = computed<QuadRow[]>(() => {
  const types = new Set(quadExisting.value.map(rule => rule.type))
  return quadTypeDefs.filter(def => !types.has(def.type)).map(def => ({
    type: def.type,
    label: alarmTypeLabels[def.type],
    threshold: quadForm[def.thresholdKey],
    level: quadForm[def.levelKey]
  }))
})
const quadSkipNotice = computed(() => {
  const labels = quadSkippedDefs.value.map(def => alarmTypeLabels[def.type])
  return labels.length ? tr('alarmRulesView.kffb30c', [labels.length, labels.join('、')]) : ''
})
const quadOrderError = computed(() => {
  const { highHigh, high, low, lowLow } = quadForm
  if ([highHigh, high, low, lowLow].some(value => value == null || !Number.isFinite(value))) return tr('alarmRulesView.kd28e9b')
  if (highHigh <= high) return tr('alarmRulesView.k8cc9ce')
  if (high <= low) return tr('alarmRulesView.k4f69f6')
  if (low <= lowLow) return tr('alarmRulesView.kcb2b7f')
  return ''
})
const canSaveQuad = computed(() => quadForm.tagId > 0 && quadLoaded.value && !quadLoading.value && quadRows.value.length > 0 && !quadOrderError.value)
// 四元组一次改四条阈值，改到一半按 Esc 会全丢；与单条规则编辑弹窗用同一套守卫
const quadGuard = useEditDialog({
  visible: quadVisible,
  saving: quadSaving,
  snapshot: () => JSON.stringify(quadForm),
  save: saveQuad
})

function roundThreshold(value: number): number { return Number(value.toFixed(3)) }

function selectQuadTag(): void {
  const tag = tags.value.find(item => item.id === quadForm.tagId)
  if (tag?.minValue != null && tag.maxValue != null && tag.maxValue > tag.minValue) {
    const span = tag.maxValue - tag.minValue
    quadForm.lowLow = roundThreshold(tag.minValue)
    quadForm.low = roundThreshold(tag.minValue + span * 0.1)
    quadForm.high = roundThreshold(tag.minValue + span * 0.9)
    quadForm.highHigh = roundThreshold(tag.maxValue)
  }
  quadError.value = ''
  void loadQuadExisting()
}

async function loadQuadExisting(): Promise<void> {
  quadExisting.value = []
  quadLoaded.value = false
  if (!quadForm.tagId) return
  quadLoading.value = true
  try {
    quadExisting.value = await ruleApi.list(quadForm.tagId)
    quadLoaded.value = true
    quadError.value = ''
  } catch (error) {
    quadError.value = errorMessage(error, tr('alarmRulesView.kc2def9'))
  } finally {
    quadLoading.value = false
  }
}

async function openQuad(): Promise<void> {
  quadError.value = ''
  quadExisting.value = []
  quadLoaded.value = false
  await ensureDevices()
  const preferred = filterTag.value ?? readLastNumber('rule-tag') ?? undefined
  const tag = preferred ? await pinTag(preferred) : undefined
  quadForm.tagId = tag && canConfigureTag(tag) ? tag.id : 0
  quadTagOptions.value = quadForm.tagId && tag ? [tag] : []
  quadForm.highHigh = 100
  quadForm.high = 90
  quadForm.low = 10
  quadForm.lowLow = 0
  quadForm.highLevel = 'Warning'
  quadForm.criticalLevel = 'Critical'
  quadForm.delaySec = 3
  quadForm.recoverDeadband = 0
  quadForm.enabled = true
  // 先按量程回填阈值再打开弹窗：守卫的基线在 visible 变 true 时快照，
  // 顺序反过来会把「自动回填」误判成用户改动，一开窗就提示丢弃
  if (quadForm.tagId) selectQuadTag()
  quadVisible.value = true
}

async function saveQuad(): Promise<void> {
  if (!canSaveQuad.value || quadSaving.value) return
  const rows = quadRows.value.slice()
  quadSaving.value = true
  quadError.value = ''
  try {
    const report = await runBatch(rows, row => `${row.label} ${row.threshold}`, async row => {
      await ruleApi.create({
        ...emptyForm(),
        // 四限生成由操作员逐条指定等级；emptyForm 的自动优先级会让服务端把 Level 重算成同一个值。
        autoPriority: false,
        tagId: quadForm.tagId,
        type: row.type,
        threshold: row.threshold!,
        level: row.level,
        delaySec: quadForm.delaySec,
        recoverDeadband: quadForm.recoverDeadband,
        enabled: quadForm.enabled
      })
    })
    if (report.failed.length === 0) {
      rememberNumber('rule-tag', quadForm.tagId)
      quadVisible.value = false
      ElMessage.success(tr('alarmRulesView.kf8f201', [report.ok]))
      await load()
    } else {
      quadError.value = summarizeBatch(report, tr('alarmRulesView.k84b6d1'))
      await load()
      await loadQuadExisting()
    }
  } finally {
    quadSaving.value = false
  }
}

function onSelect(rows: AlarmRule[]): void { selected.value = rows }

function toInput(rule: AlarmRule, patch: Partial<AlarmRuleInput> = {}): AlarmRuleInput {
  return {
    tagId: rule.tagId,
    type: rule.type,
    activeWhen: rule.type === 'StateChange' ? 'Change' : rule.activeWhen ?? 'On',
    threshold: rule.threshold,
    level: rule.level,
    category: rule.category ?? 'Process',
    consequence: rule.consequence ?? 'Major',
    responseTimeSec: rule.responseTimeSec ?? 600,
    autoPriority: rule.autoPriority ?? false,
    delaySec: rule.delaySec,
    recoverDeadband: rule.recoverDeadband,
    enabled: rule.enabled,
    message: rule.message ?? '',
    messageEn: rule.messageEn ?? '',
    cause: rule.cause ?? '',
    causeEn: rule.causeEn ?? '',
    action: rule.action ?? '',
    actionEn: rule.actionEn ?? '',
    offDelaySec: rule.offDelaySec ?? 0,
    bitIndex: rule.bitIndex ?? null,
    expression: rule.expression ?? '',
    suppressTagId: rule.suppressTagId ?? null,
    suppressMode: rule.suppressMode ?? 'None',
    ...patch
  }
}

async function setOutOfService(rule: AlarmRule, outOfService: boolean): Promise<void> {
  let reason: string | undefined
  if (outOfService) {
    try {
      const result = await ElMessageBox.prompt(tr('alarmRulesView.k3db650'), tr('alarmRulesView.kbba488'), {
        inputValidator: (value: string) => (value && value.trim() ? true : tr('alarmRulesView.k75ebf5')),
        confirmButtonText: tr('alarmRulesView.kd989e5'),
        cancelButtonText: tr('alarmRulesView.k4d0b46')
      })
      reason = result.value.trim()
    } catch {
      return
    }
  }
  actionError.value = ''
  try {
    await ruleApi.outOfService(rule.id, outOfService, reason)
    ElMessage.success(outOfService ? tr('alarmRulesView.k6c7dcb') : tr('alarmRulesView.k6aa9ad'))
    actionError.value = ''
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.k09e424'))
  }
}

function tagName(id?: number | null): string {
  return tags.value.find((x) => x.id === id)?.name ?? `#${id}`
}

let loadVersion = 0, disposed = false
async function load(): Promise<void> {
  if (disposed) return
  const version = ++loadVersion
  loading.value = true
  selected.value = []
  try {
    const applied = catalog.applied.value
    const extra = !!applied.search.trim() || applied.state !== 'all' || applied.category !== 'all'
    const pageRequest = ruleApi.page({
      tagId: filterTag.value,
      search: applied.search,
      state: applied.state,
      category: applied.category,
      skip: Math.max(0, (catalog.page.value - 1) * PAGE_SIZE),
      take: PAGE_SIZE,
      sort: sortKey.value,
      order: sortOrder.value
    })
    const scopeRequest = extra
      ? ruleApi.page({ tagId: filterTag.value, skip: 0, take: 1 })
      : null
    const result = await pageRequest
    if (version !== loadVersion || disposed) return
    const maxPage = Math.max(1, Math.ceil(result.total / PAGE_SIZE))
    if (catalog.page.value > maxPage) {
      catalog.page.value = maxPage
      return
    }
    const ids = new Set<number>()
    for (const rule of result.items) {
      ids.add(rule.tagId)
      if (rule.suppressTagId) ids.add(rule.suppressTagId)
      for (const id of expressionTagIds(rule.expression)) ids.add(id)
    }
    const idList = [...ids].filter(id => id > 0).slice(0, 500)
    const [pageTags] = await Promise.all([
      idList.length ? tagApi.list({ ids: idList }) : Promise.resolve([] as Tag[]),
      ensureDevices().catch(() => undefined)
    ])
    if (version !== loadVersion || disposed) return
    rememberTags(pageTags)
    const scope = scopeRequest ? await scopeRequest : null
    if (version !== loadVersion || disposed) return
    rules.value = result.items
    ruleTotal.value = result.total
    scopeTotal.value = scope?.total ?? result.total
    loadError.value = ''
  } catch (error) {
    if (version !== loadVersion) return
    loadError.value = errorMessage(error, tr('alarmRulesView.k9174d4'))
  } finally {
    if (version === loadVersion) loading.value = false
  }
}

async function exportRules(format: 'xlsx' | 'csv'): Promise<void> {
  if (disposed || exporting.value) return
  exporting.value = true
  actionError.value = ''
  try {
    const blob = await ruleApi.export(format, filterTag.value)
    const stamp = new Date().toISOString().slice(0, 19).replace(/[-:T]/g, '')
    downloadBlob(blob, `alarm-rules-${stamp}.${format}`)
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.exportFailed'))
  } finally {
    exporting.value = false
  }
}

function pickImport(): void {
  importInput.value?.click()
}

async function onImportFile(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (disposed || !file || importing.value) return
  importing.value = true
  actionError.value = ''
  try {
    const result = await ruleApi.import(file)
    const summary = tr('alarmRulesView.importDone', [result.created, result.updated, result.skipped])
    if (result.errors.length) await ElMessageBox.alert(`${summary}\n${result.errors.join('\n')}`, tr('alarmRulesView.importRules'), { type: 'warning', confirmButtonText: tr('alarmRulesView.kcb63c6') })
    else ElMessage.success(summary)
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.importFailed'))
  } finally {
    importing.value = false
  }
}

// 模拟使用已保存的规则：只预览条件与文案，不改变实际报警状态。
const simulate = reactive({
  visible: false,
  rule: null as AlarmRule | null,
  value: 0,
  previousValue: null as number | null,
  quality: 'Good',
  references: {} as Record<number, number>,
  referenceQualities: {} as Record<number, string>,
  loading: false,
  result: null as AlarmRuleSimulateResult | null,
  error: ''
})
function expressionTagIds(expression?: string | null): number[] {
  const ids = [...(expression ?? '').matchAll(/#(\d+)/g)].map(match => Number(match[1]))
  return [...new Set(ids.filter(id => id > 0))]
}
const simulateRefIds = computed(() => expressionTagIds(simulate.rule?.expression))
const simulateResultType = computed(() => simulate.result?.indeterminate ? 'warning' : simulate.result?.triggered ? 'error' : 'success')
const simulateResultTitle = computed(() => simulate.result?.indeterminate
  ? tr('alarmRulesView.simulateIndeterminate')
  : simulate.result?.triggered ? tr('alarmRulesView.simulateTriggered') : tr('alarmRulesView.simulateNotTriggered'))

function openSimulate(rule: AlarmRule): void {
  const references: Record<number, number> = {}
  const qualities: Record<number, string> = {}
  for (const id of expressionTagIds(rule.expression)) { references[id] = 0; qualities[id] = 'Good' }
  Object.assign(simulate, {
    visible: true, rule, value: 0, previousValue: null, quality: 'Good', references, referenceQualities: qualities,
    loading: false, result: null, error: ''
  })
  if (rule.type === 'High' || rule.type === 'HighHigh') simulate.value = Number((rule.threshold + 1).toFixed(3))
  else if (rule.type === 'Low' || rule.type === 'LowLow') simulate.value = Number((rule.threshold - 1).toFixed(3))
  else if (rule.type === 'Discrete') simulate.value = rule.activeWhen === 'Off' ? 0 : rule.bitIndex != null ? 2 ** rule.bitIndex : 1
}

async function runSimulate(): Promise<void> {
  if (disposed || !simulate.rule || simulate.loading) return
  simulate.loading = true
  simulate.error = ''
  try {
    simulate.result = await ruleApi.simulate(simulate.rule.id, {
      value: simulate.value,
      quality: simulate.quality,
      previousValue: simulate.previousValue ?? undefined,
      references: simulate.references,
      referenceQualities: simulate.referenceQualities
    })
  } catch (error) {
    simulate.result = null
    simulate.error = errorMessage(error, tr('alarmRulesView.simulateFailed'))
  } finally {
    simulate.loading = false
  }
}
onBeforeUnmount(() => { disposed = true; loadVersion++; window.clearTimeout(searchTimer) })
async function resetFilters(): Promise<void> {
  window.clearTimeout(searchTimer)
  await catalog.apply(true)
  if (filterTag.value !== undefined) filterTag.value = undefined
}

async function openCreate(): Promise<void> {
  editingId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  Object.assign(form, emptyForm())
  await ensureDevices().catch(() => undefined)
  const preferred = filterTag.value ?? readLastNumber('rule-tag') ?? undefined
  const tag = preferred ? await pinTag(preferred) : undefined
  form.tagId = tag && allowRuleTag(tag) ? tag.id : 0
  ruleTagOptions.value = form.tagId && tag ? [tag] : []
  suppressTagOptions.value = []
  dialogVisible.value = true
}

async function openEdit(rule: AlarmRule): Promise<void> {
  editingId.value = rule.id
  copying.value = false
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  Object.assign(form, toInput(rule))
  await ensureDevices().catch(() => undefined)
  const tag = await pinTag(rule.tagId)
  const suppress = rule.suppressTagId ? await pinTag(rule.suppressTagId) : undefined
  ruleTagOptions.value = withPinned(tag ? [tag] : [], tag, allowRuleTag, true)
  suppressTagOptions.value = suppress ? [suppress] : []
  dialogVisible.value = true
}

async function openCopy(rule: AlarmRule): Promise<void> {
  editingId.value = null
  copying.value = true
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  Object.assign(form, toInput(rule, { message: rule.message ? tr('alarmRulesView.kc0769b', [rule.message]) : '' }))
  await ensureDevices().catch(() => undefined)
  const tag = await pinTag(rule.tagId)
  const suppress = rule.suppressTagId ? await pinTag(rule.suppressTagId) : undefined
  ruleTagOptions.value = withPinned(tag ? [tag] : [], tag, allowRuleTag, true)
  suppressTagOptions.value = suppress ? [suppress] : []
  dialogVisible.value = true
}

async function onDialogOpened(): Promise<void> {
  await nextTick()
  formRef.value?.clearValidate()
  await editDialog.focusInitial()
  void searchRuleTags('')
  if (form.suppressMode !== 'None') void searchSuppressTags('')
}

async function save(): Promise<void> {
  if (!dialogVisible.value || saving.value) return
  saving.value = true
  let focusError = false
  clearFieldErrors(fieldErrors)
  saveError.value = ''
  try {
    const valid = await formRef.value?.validate().catch(() => false)
    if (!valid) {
      saveError.value = tr('alarmRulesView.k02668e'); focusError = true; return
    }
    if (!canSave.value) {
      saveError.value = tr('alarmRulesView.k1a545c'); focusError = true; return
    }
    if (form.suppressMode === 'None') form.suppressTagId = null
    if (form.type !== 'Discrete') form.bitIndex = null
    if (form.type !== 'Expression') form.expression = ''
    if (editingId.value) await ruleApi.update(editingId.value, { ...form })
    else {
      await ruleApi.create({ ...form })
      rememberNumber('rule-tag', form.tagId)
    }
    ElMessage.success(tr('alarmRulesView.k7e68eb'))
    saveError.value = ''
    dialogVisible.value = false
    await load()
  } catch (error) {
    const mapped = serverFormErrors(error, tr('alarmRulesView.k40525a'), ['tagId', 'threshold', 'suppressTagId', 'delaySec', 'bitIndex', 'expression'])
    applyFieldErrors(fieldErrors, mapped.fields)
    saveError.value = mapped.message
    focusError = true
  } finally {
    saving.value = false
    if (focusError) await focusFormError(formRef.value)
  }
}

async function remove(rule: AlarmRule): Promise<void> {
  actionError.value = ''
  try {
    await ruleApi.remove(rule.id)
    actionError.value = ''
    ElMessage.success(tr('alarmRulesView.kfb5fe1'))
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmRulesView.k72250c'))
  }
}

async function reportBatch(report: Awaited<ReturnType<typeof runBatch>>, okText: string): Promise<void> {
  batchProgress.value = ''
  batchCancel.value = false
  const pending = report.total - report.ok - report.failed.length
  if (report.failed.length === 0) {
    if (report.canceled) ElMessage.info(tr('batchUx.stoppedOnly', [report.ok, report.total, pending]))
    else ElMessage.success(okText)
  } else {
    const summary = summarizeBatch(report, tr('alarmRulesView.kbce2ef'))
    const body = report.canceled ? `${summary}\n\n${tr('batchUx.stoppedSuffix', [pending])}` : summary
    await ElMessageBox.alert(body, tr('alarmRulesView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmRulesView.kcb63c6') })
  }
  await load()
}

async function batchSetEnabled(enabled: boolean): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (r) => `#${r.id} ${r.message || tagName(r.tagId)}`, async (r) => { await ruleApi.update(r.id, toInput(r, { enabled })) }, (p) => {
    batchProgress.value = tr('alarmRulesView.k4c5062', [enabled ? tr('alarmRulesView.kd4e9ca') : tr('alarmRulesView.kd989e5'), p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('alarmRulesView.k611dcc', [enabled ? tr('alarmRulesView.kd4e9ca') : tr('alarmRulesView.kd989e5'), report.ok]))
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(tr('alarmRulesView.k6db0fb', [rows.length]), tr('alarmRulesView.k362aed'), { type: 'warning', confirmButtonText: tr('alarmRulesView.k3755f5'), cancelButtonText: tr('alarmRulesView.k4d0b46') })
  } catch { return }
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (r) => `#${r.id} ${r.message || tagName(r.tagId)}`, async (r) => { await ruleApi.remove(r.id) }, (p) => {
    batchProgress.value = tr('alarmRulesView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('alarmRulesView.kad6890', [report.ok]))
}

onMounted(async () => {
  await load()
  if (filterTag.value) {
    const tag = await pinTag(filterTag.value)
    if (tag) filterTagOptions.value = withPinned(filterTagOptions.value, tag, undefined, true)
  }
  // 从测点页“配置报警”进入时直接打开新建对话框；消费掉 create 参数避免重复弹出。
  if (route.query.create === '1' && filterTag.value) {
    await ensureDevices().catch(() => undefined)
    const tag = tags.value.find(item => item.id === filterTag.value)
    if (tag && canConfigureTag(tag)) void openCreate()
    const query = { ...route.query }
    delete query.create
    void router.replace({ query })
  }
})
</script>

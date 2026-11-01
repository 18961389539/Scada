<template>
  <div class="page page--fill alarm-page">
    <AlarmFloodBanner :summary="summary" data-test="flood-banner" />
    <div class="panel alarm-panel" :aria-label="$t('alarmWorkbench.title')">
      <el-tabs v-model="tab" @tab-change="onTabChange">
        <el-tab-pane :label="$t('alarmsView.kc97e08')" name="live" />
        <el-tab-pane :label="$t('alarmsView.k894f96', [summary?.shelvedRules ?? shelves.length])" name="shelved" />
        <el-tab-pane :label="$t('alarmsView.k8e0241')" name="history" />
      </el-tabs>
      <div v-if="tab === 'live'" class="alarm-state-filters" role="group" :aria-label="$t('alarmWorkbench.states')">
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'active' }" :aria-pressed="stateFilter === 'active'" data-test="filter-active" @click="stateFilter = 'active'">{{ $t('alarmsView.filterActive') }}</el-button>
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'live' }" :aria-pressed="stateFilter === 'live'" data-test="filter-live" @click="stateFilter = 'live'">{{ $t('alarmWorkbench.liveScope') }}</el-button>
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'unack' }" :aria-pressed="stateFilter === 'unack'" data-test="filter-unack" @click="stateFilter = 'unack'">
          {{ $t('overviewStatus.activeUnack') }} <span class="alarm-filter-count" :class="{ 'has-unack': summary?.unack }">{{ summary?.unack ?? '--' }}</span>
        </el-button>
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'rtnUnack' }" :aria-pressed="stateFilter === 'rtnUnack'" data-test="filter-rtn" @click="stateFilter = 'rtnUnack'">
          {{ $t('overviewStatus.returned') }} <span class="alarm-filter-count" :class="{ 'has-returned': summary?.rtnUnack }">{{ summary?.rtnUnack ?? '--' }}</span>
        </el-button>
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'acked' }" :aria-pressed="stateFilter === 'acked'" data-test="filter-acked" @click="stateFilter = 'acked'">
          {{ $t('overviewStatus.acked') }} <span class="alarm-filter-count">{{ summary?.acked ?? '--' }}</span>
        </el-button>
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'shelved' }" :aria-pressed="stateFilter === 'shelved'" data-test="filter-shelved" @click="stateFilter = 'shelved'">
          {{ $t('alarmsView.k06d561') }} <span class="alarm-filter-count">{{ summary?.shelved ?? '--' }}</span>
        </el-button>
        <el-button size="small" class="alarm-state-filter" :class="{ 'is-current': stateFilter === 'suppressed' }" :aria-pressed="stateFilter === 'suppressed'" data-test="filter-suppressed" @click="stateFilter = 'suppressed'">
          {{ $t('alarmsView.kaa2e2b') }} <span class="alarm-filter-count">{{ summary?.suppressed ?? '--' }}</span>
        </el-button>
        <span class="alarm-rule-count tiny muted">{{ $t('alarmsView.k0e262f', [summary?.outOfServiceRules ?? '--']) }}</span>
      </div>
      <div class="toolbar alarm-toolbar">
        <div v-if="tab !== 'shelved'" class="alarm-query" role="group" :aria-label="$t('alarmWorkbench.filters')">
          <template v-if="tab === 'history'">
            <el-select v-model="rangePreset" clearable :aria-label="$t('common.timeRange')" :placeholder="$t('alarmsView.range24h')" class="alarm-range-preset" data-test="range-preset" @change="applyRangePreset">
              <el-option :label="$t('alarmsView.range1h')" :value="1" />
              <el-option :label="$t('alarmsView.range8h')" :value="8" />
              <el-option :label="$t('alarmsView.range24h')" :value="24" />
              <el-option :label="$t('alarmsView.range7d')" :value="168" />
            </el-select>
            <label class="alarm-sr-only" for="alarm-history-start">{{ $t('alarmsView.ke8868a') }}</label>
            <label class="alarm-sr-only" for="alarm-history-end">{{ $t('alarmsView.ka0bb9f') }}</label>
            <el-date-picker v-model="historyRange" :id="['alarm-history-start', 'alarm-history-end']" type="datetimerange" :start-placeholder="$t('alarmsView.ke8868a')" :end-placeholder="$t('alarmsView.ka0bb9f')" class="alarm-history-range" popper-class="alarm-date-popper" @change="rangePreset = ''" />
            <el-button :icon="Search" :loading="loading" @click="searchHistory">{{ $t('alarmsView.k711363') }}</el-button>
          </template>
          <el-select v-model="levelFilter" clearable :aria-label="$t('alarmsView.allLevels')" :placeholder="$t('alarmsView.allLevels')" class="alarm-level-filter" data-test="level-filter">
            <el-option v-for="l in levelOptions" :key="l" :label="levelLabel(l)" :value="l" />
          </el-select>
          <el-select v-model="categoryFilter" clearable :aria-label="$t('alarmsView.allCategories')" :placeholder="$t('alarmsView.allCategories')" class="alarm-category-filter" data-test="category-filter">
            <el-option v-for="(label, value) in alarmCategoryLabels" :key="value" :label="label" :value="value" />
          </el-select>
          <el-input ref="keywordInputRef" v-model="keyword" :prefix-icon="Search" clearable :aria-label="$t('alarmsView.kd812c3')" :placeholder="$t('alarmsView.kd812c3')" class="alarm-keyword" data-test="alarm-keyword" />
          <el-select v-if="tab === 'live'" v-model="liveSort" :aria-label="$t('alarmListReview.sort')" class="alarm-sort" data-test="alarm-sort">
            <el-option value="priority" :label="$t('alarmListReview.priorityFirst')" />
            <el-option value="time" :label="$t('alarmListReview.newestFirst')" />
          </el-select>
        </div>
        <div class="alarm-actions" role="group" :aria-label="$t('alarmWorkbench.actions')">
          <el-button v-if="tab === 'live' && canAck" type="primary" data-test="batch-ack" :disabled="!selectedAckable.length || loading" :loading="batchRunning" @click="batchAck">
            {{ $t('alarmsView.batchAck') }}<template v-if="selectedAckable.length"> ({{ selectedAckable.length }})</template>
          </el-button>
          <el-button v-if="tab === 'live' && batchRunning" type="warning" plain :disabled="batchCancel" data-test="batch-stop" @click="batchCancel = true">{{ $t('alarmsUx.stopBatch') }}</el-button>
          <el-button v-if="tab === 'live' && canAck" data-test="ack-visible" :disabled="!selectableAlarms.length || loading || batchRunning" @click="ackVisible">{{ $t('alarmsView.ackVisible') }} ({{ selectableAlarms.length }})</el-button>
          <el-tooltip v-if="tab === 'live' && canAck" :content="$t('alarmWorkbench.silenceHint')" placement="top">
            <el-button :icon="MuteNotification" data-test="silence-ann" @click="silenceAnnunciators">{{ $t('alarmsView.silenceAnn') }}</el-button>
          </el-tooltip>
          <el-tooltip :content="$t('shortcuts.openHint')" placement="top">
            <el-button class="alarm-kbd-help" data-test="alarm-kbd-help" @click="openShortcutHelp"><kbd>?</kbd></el-button>
          </el-tooltip>
          <el-button :icon="Refresh" :loading="loading" data-test="alarms-refresh" @click="reload">{{ $t('alarmsView.k38108e') }}</el-button>
        </div>
      </div>
      <p v-if="batchProgress" class="tiny muted alarm-progress" role="status" data-test="batch-progress">{{ batchCancel ? $t('alarmsUx.stopping') : batchProgress }}</p>
      <div v-if="tab !== 'shelved'" class="alarm-list-summary">
        <el-tag v-if="route.query.tagId" closable type="info" data-test="alarm-tag-filter" @close="clearTagFilter">{{ $t('realtimeMonitor.tagFilter', [route.query.tagId]) }}</el-tag>
        <span class="tiny muted" role="status" data-test="alarm-result-count">
          <template v-if="loading">{{ $t('alarmsView.loadingResults') }}</template>
          <template v-else>{{ $t('alarmsView.pageCount', [tab === 'live' ? stateLabel : $t('alarmsView.k8e0241'), alarms.length, (tab === 'live' ? liveTotal : historyTotal) ?? '--']) }}<template v-if="tab === 'live'"> · {{ $t('alarmsView.selectionCount', [selectableAlarms.length, selectedAckable.length]) }}</template></template>
        </span>
        <el-button v-if="hasFilters" link type="primary" data-test="alarm-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <el-button v-if="selectedAckable.length && tab === 'live'" link data-test="alarm-clear-selection" @click="clearSelection">{{ $t('alarmsView.clearSelection') }}</el-button>
        <span class="alarm-kbd-tip tiny muted">{{ $t(tab === 'live' ? 'shortcuts.alarmsTip' : 'shortcuts.historyTip') }}</span>
      </div>
      <div v-if="tab === 'history' && loadedHistoryRange" class="alarm-history-context tiny muted" role="status">
        <span data-test="alarm-history-range">{{ $t('alarmsView.loadedRange', [formatTime(loadedHistoryRange[0]), formatTime(loadedHistoryRange[1])]) }}</span>
        <span v-if="historyRangeDirty && !loading" class="alarm-range-changed" data-test="alarm-range-changed">{{ $t('alarmWorkbench.rangeChanged') }}</span>
      </div>
      <el-alert v-if="batchOutcome && tab === 'live'" :type="batchOutcome.failed.length ? 'warning' : batchOutcome.canceled ? 'info' : 'success'" :closable="true" show-icon :title="$t('alarmsView.batchResult', [batchOutcome.ok, batchOutcome.failed.length, batchOutcome.skipped])" data-test="alarm-batch-result" @close="batchOutcome = null">
        <p v-if="batchOutcome.canceled" class="tiny" data-test="alarm-batch-stopped">{{ $t('alarmsUx.batchStopped', [batchOutcome.total - batchOutcome.ok - batchOutcome.failed.length]) }}</p>
        <div v-if="batchOutcome.failed.length" class="batch-failures">
          <div v-for="(failure, i) in batchOutcome.failed" :key="i" class="tiny">{{ failure.label }}：{{ failure.error }}</div>
        </div>
        <el-button v-if="batchOutcome.failed.length" link type="primary" :disabled="!retryRows.length || batchRunning || loading" data-test="alarm-retry-failed" @click="confirmAndAck(retryRows, 'selected')">{{ $t('alarmsView.retryFailed', [retryRows.length]) }}</el-button>
      </el-alert>

      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon class="alarm-feedback" data-test="alarms-load-error">
        <el-button link type="primary" :loading="loading" data-test="alarms-list-retry" @click="reload">{{ $t('common.retry') }}</el-button>
      </el-alert>
      <el-alert v-if="summaryError" :title="summaryError" type="warning" :closable="false" show-icon class="alarm-feedback" data-test="alarms-summary-error">
        <el-button link type="primary" data-test="alarms-summary-retry" @click="loadSummary">{{ $t('common.retry') }}</el-button>
      </el-alert>
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon class="alarm-feedback" data-test="alarms-action-error" />
      <el-alert v-if="sourceCatalogError" :title="$t('alarmListReview.sourceCatalogFailed')" :description="sourceCatalogError" type="warning" :closable="false" show-icon class="source-catalog-warning" data-test="alarm-source-error">
        <el-button link type="primary" :loading="sourceCatalogLoading" data-test="alarm-source-retry" @click="loadSourceCatalog">{{ $t('common.retry') }}</el-button>
      </el-alert>

      <el-alert
        v-if="historyCapped && tab === 'history'"
        :title="$t('alarmsView.historyCapped')"
        type="warning"
        :closable="false"
        show-icon
        style="margin-bottom: 8px"
        data-test="alarms-history-capped"
      />
      <div v-if="tab === 'shelved'" class="alarm-table-wrap" role="region" :aria-label="$t('alarmWorkbench.table')" :aria-busy="loading" data-test="alarm-table-region">
      <el-table v-loading="loading" :data="shelves" size="small" height="100%" scrollbar-always-on :scrollbar-tabindex="0" :empty-text="loading ? $t('alarmsView.loadingResults') : loadError && !shelves.length ? loadError : $t('alarmsView.k0d51d2')">
        <el-table-column :label="$t('alarmsView.ked904c')" width="90" show-overflow-tooltip><template #default="{ row }">#{{ row.ruleId }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k8cff1c')" width="90" show-overflow-tooltip><template #default="{ row }">#{{ row.tagId }}</template></el-table-column>
        <el-table-column prop="reason" :label="$t('alarmsView.k1ff9c3')" min-width="200" show-overflow-tooltip />
        <el-table-column prop="user" :label="$t('alarmsView.k06858d')" width="100" show-overflow-tooltip />
        <el-table-column :label="$t('alarmsView.k79cc87')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.k8066e0')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(row.untilTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmsView.kf3ea6d')" width="100">
          <template #default="{ row }">
            <el-button v-if="canAckAlarm(row)" link type="primary" :disabled="batchRunning" @click="unshelve(row.ruleId)">{{ $t('alarmsView.kcd308e') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
      </div>

      <div v-else class="alarm-table-wrap alarm-desktop-table" role="region" :aria-label="$t('alarmWorkbench.table')" :aria-busy="loading" data-test="alarm-table-region">
      <el-table
        ref="tableRef"
        v-loading="loading"
        :data="alarms"
        row-key="id"
        size="small"
        height="100%"
        scrollbar-always-on
        :scrollbar-tabindex="0"
        :row-class-name="rowClass"
        :empty-text="emptyText"
        @selection-change="onSelectionChange"
      >
        <template #empty>
          <span v-if="loadError">{{ loadError }}</span>
          <template v-else>
            <span>{{ emptyText }}</span>
            <el-button v-if="hasFilters" link type="primary" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
          </template>
        </template>
        <el-table-column v-if="tab === 'live' && canAck" type="selection" width="42" :reserve-selection="true" :selectable="canSelectAlarm" />
        <el-table-column :label="`${$t('alarmsView.k62e951')} / ${$t('alarmsView.k254849')}`" width="150">
          <template #default="{ row }">
            <div class="state-cell">
              <el-tag :type="alarmStateType(row.state)" size="small" data-test="alarm-state">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
              <el-tag :type="levelType(row.level)" size="small" effect="plain">{{ levelLabel(row.level) }}</el-tag>
              <el-tag v-if="row.category" size="small" effect="plain" type="info" data-test="alarm-category">{{ alarmCategoryLabels[row.category] ?? row.category }}</el-tag>
            </div>
            <div class="flags">
              <el-tooltip v-if="row.firstUp" :content="$t('alarmsView.firstUpHint')" placement="top">
                <el-tag size="small" effect="dark" type="warning" data-test="first-up">{{ $t('alarmsView.firstUp') }}</el-tag>
              </el-tooltip>
              <el-tooltip v-if="row.firstOut" :content="$t('alarmsView.firstOutHint')" placement="top">
                <el-tag size="small" effect="dark" type="danger" data-test="first-out">{{ $t('alarmsView.firstOut') }}</el-tag>
              </el-tooltip>
              <el-tag v-if="row.shelved" size="small" effect="plain">{{ $t('alarmsView.k06d561') }}</el-tag>
              <el-tooltip v-if="row.suppressed" :content="row.suppressReason || $t('alarmsView.kb37a4e')"><el-tag size="small" effect="plain" type="info">{{ $t('alarmsView.kaa2e2b') }}</el-tag></el-tooltip>
              <el-tag v-if="row.chattering" size="small" effect="plain" type="warning">{{ $t('alarmsView.ka60f92') }}</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kf3dd64')" min-width="260">
          <template #default="{ row }">
            <el-tag v-if="row.gatewayId" size="small" effect="plain" class="gateway-tag">{{ $t('alarmsView.k38b4cc', [row.gatewayId]) }}</el-tag>
            <div class="alarm-message">{{ alarmMessage(row) }}</div>
            <div class="alarm-source" data-test="alarm-source">
              <span>{{ $t('alarmListReview.source') }}：{{ alarmSource(row) }}</span>
              <router-link v-if="row.tagId && can(auth.user, 'View')" :to="{ path: '/trends', query: { tagId: String(row.tagId) } }" class="alarm-trend-link" data-test="alarm-trend">{{ $t('overviewStatus.trend') }}</router-link>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.k521523')" width="112" show-overflow-tooltip><template #default="{ row }">{{ alarmValueText(row) }}</template></el-table-column>
        <el-table-column :label="`${$t('alarmsView.k79cc87')} / ${$t('alarmsView.k25722a')}`" width="165" show-overflow-tooltip>
          <template #default="{ row }">
            <span>{{ formatTime(row.startTs) }}</span>
            <div class="tiny muted">{{ duration(row) }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.ke93ea4')" width="175" show-overflow-tooltip>
          <template #default="{ row }">
            {{ row.endTs ? formatTime(row.endTs) : $t('alarmsView.k559948') }}
            <div v-if="row.endReason" class="tiny muted" style="white-space: normal">{{ row.endReason }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kb56d9a')" width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.ackTs" class="tiny">{{ row.ackUser }}<br />{{ formatTime(row.ackTs) }}</span>
            <span v-else class="muted tiny">{{ $t('alarmsView.k0642fa') }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kf3ea6d')" width="190" fixed="right">
          <template #default="{ row }">
            <div class="alarm-row-actions">
            <el-button v-if="!row.ackTs && canAckAlarm(row)" link type="primary" :disabled="batchRunning" data-test="ack" @click="openAck(row)">{{ $t('alarmsView.kb56d9a') }}</el-button>
            <el-button v-if="canAckAlarm(row)" link :disabled="batchRunning" @click="openNote(row)">{{ $t('alarmsView.ke03614') }}</el-button>
            <el-button v-if="canAckAlarm(row) && row.ruleId && !row.endTs && !row.shelved" link type="warning" :disabled="batchRunning" @click="openShelve(row)">{{ $t('alarmsView.k06d561') }}</el-button>
            <el-button v-if="canAckAlarm(row) && row.ruleId && row.shelved" link type="warning" :disabled="batchRunning" @click="unshelve(row.ruleId)">{{ $t('alarmsView.kcd308e') }}</el-button>
            <el-button link data-test="timeline" @click="openTimeline(row)">{{ $t('alarmsView.k9058a1') }}</el-button>
            </div>
          </template>
        </el-table-column>
      </el-table>
      </div>
      <!-- 手机/平板用卡片替代 7 列表格：横滑表格在现场基本不可读 -->
      <div v-if="tab !== 'shelved'" class="alarm-mobile-list" data-test="alarm-mobile-cards">
        <article
          v-for="row in alarms"
          :key="row.id"
          class="alarm-card"
          :class="{ 'is-critical': row.level === 'Critical' && !row.endTs && !row.shelved }"
          :data-test="'alarm-card-' + row.id"
        >
          <div class="alarm-card-head">
            <el-tag :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
            <el-tag :type="alarmStateType(row.state)" size="small" effect="plain">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
            <el-tag v-if="row.shelved" size="small" effect="plain">{{ $t('alarmsView.k06d561') }}</el-tag>
            <el-tag v-if="row.suppressed" size="small" effect="plain" type="info">{{ $t('alarmsView.kaa2e2b') }}</el-tag>
            <span class="alarm-card-duration tiny muted">{{ duration(row) }}</span>
          </div>
          <p class="alarm-card-message">{{ alarmMessage(row) }}</p>
          <div class="alarm-card-meta tiny">
            <span>{{ $t('alarmListReview.source') }}：{{ alarmSource(row) }}</span>
            <span>{{ $t('alarmsView.k521523') }}：{{ alarmValueText(row) }}</span>
            <span>{{ formatTime(row.startTs) }}</span>
          </div>
          <div class="alarm-card-meta tiny">
            <span v-if="row.ackTs">{{ row.ackUser }} · {{ formatTime(row.ackTs) }}</span>
            <span v-else class="alarm-card-unack">{{ $t('alarmsView.k0642fa') }}</span>
            <router-link v-if="row.tagId && can(auth.user, 'View')" :to="{ path: '/trends', query: { tagId: String(row.tagId) } }" class="alarm-trend-link">{{ $t('overviewStatus.trend') }}</router-link>
          </div>
          <div class="alarm-card-actions">
            <el-button v-if="!row.ackTs && canAckAlarm(row)" size="small" type="primary" :disabled="batchRunning" :data-test="`alarm-card-ack-${row.id}`" @click="openAck(row)">{{ $t('alarmsView.kb56d9a') }}</el-button>
            <el-button v-if="canAckAlarm(row)" size="small" :disabled="batchRunning" @click="openNote(row)">{{ $t('alarmsView.ke03614') }}</el-button>
            <el-button size="small" @click="openTimeline(row)">{{ $t('alarmsView.k9058a1') }}</el-button>
          </div>
        </article>
        <p v-if="!alarms.length" class="muted tiny">{{ emptyText }}</p>
      </div>
      <div class="alarm-footer">
        <span v-if="tab === 'shelved' ? shelves.length : alarms.length" class="alarm-scroll-hint tiny muted">{{ $t('alarmWorkbench.scrollHint') }}</span>
        <div v-if="tab === 'live'" class="alarm-pager">
          <el-pagination
            :current-page="livePage"
            :page-size="livePageSize"
            :total="liveTotal ?? 0"
            :disabled="loading || batchRunning"
            :pager-count="5"
            layout="total, slot, prev, pager, next"
            hide-on-single-page
            data-test="alarm-live-pagination"
            @current-change="onLivePageChange"
          >
            <el-select :model-value="livePageSize" :disabled="loading || batchRunning" :aria-label="$t('alarmWorkbench.pageSize')" class="alarm-page-size" data-test="alarm-live-page-size" @change="onLiveSizeChange">
              <el-option v-for="size in [50, 100, 200, 500]" :key="size" :value="size" :label="$t('alarmWorkbench.pageSizeOption', [size])" />
            </el-select>
          </el-pagination>
        </div>
        <div v-if="tab === 'history'" class="alarm-pager">
        <el-pagination
          :current-page="historyPage"
          :page-size="historyPageSize"
          :total="historyTotal"
          :disabled="loading"
          :pager-count="5"
          layout="total, slot, prev, pager, next, jumper"
          data-test="alarm-history-pagination"
          @current-change="onHistoryPageChange"
        >
          <el-select :model-value="historyPageSize" :disabled="loading" :aria-label="$t('alarmWorkbench.pageSize')" class="alarm-page-size" data-test="alarm-page-size" @change="onHistorySizeChange">
            <el-option v-for="size in [50, 100, 200, 500]" :key="size" :value="size" :label="$t('alarmWorkbench.pageSizeOption', [size])" />
          </el-select>
        </el-pagination>
        </div>
      </div>
    </div>

    <el-dialog v-model="ackDialog.visible" :title="ackDialog.mode === 'ack' ? $t('alarmsView.kff8fce') : $t('alarmsView.kb82157')" width="460px" :close-on-click-modal="!ackDialog.saving" :close-on-press-escape="!ackDialog.saving" :show-close="!ackDialog.saving">
      <el-alert v-if="ackError" :title="ackError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="ack-error" />
      <div v-if="ackDialog.alarm" class="alarm-dialog-context">
        <el-tag :type="levelType(ackDialog.alarm.level)" size="small">{{ levelLabel(ackDialog.alarm.level) }}</el-tag>
        <strong>{{ alarmMessage(ackDialog.alarm) }}</strong>
        <span class="tiny muted">{{ $t('alarmListReview.source') }}：{{ alarmSource(ackDialog.alarm) }}</span>
        <span class="tiny muted" data-test="ack-dialog-value">{{ $t('alarmsView.k521523') }}：{{ alarmValueText(ackDialog.alarm) }}</span>
      </div>
      <el-input v-model="ackDialog.note" :disabled="ackDialog.saving" type="textarea" :rows="3" maxlength="1000" show-word-limit :aria-label="ackDialog.mode === 'ack' ? $t('alarmsView.k7eb18b') : $t('alarmsView.k11abee')" :placeholder="ackDialog.mode === 'ack' ? $t('alarmsView.k7eb18b') : $t('alarmsView.k11abee')" data-test="note-input" />
      <template #footer>
        <el-button :disabled="ackDialog.saving" @click="ackDialog.visible = false">{{ $t('alarmsView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="ackDialog.saving" data-test="note-submit" @click="submitAck">{{ ackDialog.mode === 'ack' ? $t('alarmsView.kb56d9a') : $t('alarmsView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="shelveDialog.visible" :title="$t('alarmsView.ke90a39')" width="460px" :close-on-click-modal="!shelveDialog.saving" :close-on-press-escape="!shelveDialog.saving" :show-close="!shelveDialog.saving">
      <el-form :disabled="shelveDialog.saving" label-width="80px" @submit.prevent>
        <el-form-item :label="$t('alarmsView.k9f45b3')">{{ shelveDialog.alarm ? alarmMessage(shelveDialog.alarm) : '' }}</el-form-item>
        <el-form-item :label="$t('alarmsView.k29d055')" for="alarm-shelve-duration">
          <el-select id="alarm-shelve-duration" v-model="shelveDialog.minutes" style="width: 160px">
            <el-option v-for="m in [15, 30, 60, 120, 240, 480]" :key="m" :label="m >= 60 ? $t('alarmsView.k5247a4', [m / 60]) : $t('alarmsView.kc16d73', [m])" :value="m" />
          </el-select>
          <span class="tiny muted" style="margin-left: 8px">{{ $t('alarmsView.shelveHint') }}</span>
        </el-form-item>
        <el-form-item :label="$t('alarmsView.k1ff9c3')" required :error="shelveError">
          <el-input v-model="shelveDialog.reason" type="textarea" :rows="2" maxlength="500" :aria-label="$t('alarmsView.k7fc9ae')" :placeholder="$t('alarmsView.k7fc9ae')" data-test="shelve-reason" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="shelveDialog.saving" @click="shelveDialog.visible = false">{{ $t('alarmsView.k4d0b46') }}</el-button>
        <el-button type="warning" :disabled="!shelveDialog.reason.trim()" :loading="shelveDialog.saving" data-test="shelve-submit" @click="submitShelve">{{ $t('alarmsView.k06d561') }}</el-button>
      </template>
    </el-dialog>

    <el-drawer v-model="timelineVisible" :title="$t('alarmsView.kb2da68')" size="min(440px, 100%)" data-test="alarm-timeline">
      <div v-if="timelineAlarm" class="alarm-dialog-context">
        <el-tag :type="levelType(timelineAlarm.level)" size="small">{{ levelLabel(timelineAlarm.level) }}</el-tag>
        <strong>{{ timelineAlarm ? alarmMessage(timelineAlarm) : '' }}</strong>
        <span class="tiny muted">{{ $t('alarmListReview.source') }}：{{ alarmSource(timelineAlarm) }}</span>
      </div>
      <div v-if="timeline?.guidance?.cause || timeline?.guidance?.action || timeline?.guidance?.causeEn || timeline?.guidance?.actionEn" class="alarm-dialog-context" data-test="alarm-guidance">
        <div v-if="timeline?.guidance?.cause || timeline?.guidance?.causeEn"><span class="muted">{{ $t('alarmsView.cause') }}：</span>{{ localizedText(timeline?.guidance?.cause, timeline?.guidance?.causeEn) }}</div>
        <div v-if="timeline?.guidance?.action || timeline?.guidance?.actionEn"><span class="muted">{{ $t('alarmsView.action') }}：</span>{{ localizedText(timeline?.guidance?.action, timeline?.guidance?.actionEn) }}</div>
      </div>
      <div v-if="timelineLoading" v-loading="true" style="min-height: 100px" data-test="alarm-timeline-loading" />
      <el-alert v-if="timelineError" :title="timelineError" type="error" :closable="false" show-icon data-test="alarm-timeline-error">
        <el-button link type="primary" :loading="timelineLoading" data-test="alarm-timeline-retry" @click="retryTimeline">{{ $t('common.retry') }}</el-button>
      </el-alert>
      <template v-if="timeline">
        <div class="tiny" style="margin-bottom: 12px">
          <el-tag :type="alarmStateType(timeline.alarm.state)" size="small">{{ alarmStateLabels[timeline.alarm.state ?? 'Normal'] }}</el-tag>
        </div>
        <el-timeline>
          <el-timeline-item :timestamp="formatTime(timeline.alarm.startTs)" type="danger">{{ $t('alarmsView.k476f9e', [alarmValueText(timeline.alarm)]) }}</el-timeline-item>
          <el-timeline-item v-for="item in timeline.activities" :key="item.id" :timestamp="formatTime(item.ts)" data-test="timeline-item">
            <b>{{ alarmActionLabels[item.action] ?? item.action }}</b>
            <span v-if="item.user" class="muted"> · {{ item.user }}</span>
            <div v-if="item.note" class="tiny">{{ item.note }}</div>
          </el-timeline-item>
        </el-timeline>
        <div v-if="timeline.notifications.length" class="tiny muted">{{ $t('alarmsView.k9768ca', [timeline.notifications.length, timeline.notifications.filter((x) => !x.success).length]) }}</div>
      </template>
    </el-drawer>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type InputInstance, type TableInstance } from 'element-plus'
import { MuteNotification, Refresh, Search } from '@element-plus/icons-vue'
import { annunciatorApi } from '../api/annunciators'
import { alarmApi, deviceApi, ruleApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmCategory, AlarmEvent, AlarmLevel, AlarmListState, AlarmPageQuery, AlarmShelve, AlarmSummary, AlarmTimeline, Device, Tag } from '../types'
import { alarmActionLabels, alarmCategoryLabels, alarmMessage, alarmStateLabels, alarmStateType, canAckAlarmEvent, formatTime, formatValue, levelLabel, levelType, localizedText } from '../utils'
import { can } from '../access/policies'
import { runBatch, summarizeBatch, type BatchReport } from '../composables/batchRunner'
import { serverFormErrors } from '../composables/formErrors'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { onHubEvent } from '../realtime/hub'
import { tr } from '../i18n'
import { positiveQueryId } from '../nav/query'
import AlarmFloodBanner from '../components/AlarmFloodBanner.vue'
import { openShortcutHelp, registerShortcuts } from '../shortcuts/registry'
import { modalOpen } from '../ui/overlay'

const auth = useAuthStore()
const canAck = computed(() => can(auth.user, 'AckAlarm'))
const deviceAreas = ref<Map<number, number>>(new Map())
const sourceDevices = ref(new Map<number, Device>())
const sourceTags = ref(new Map<number, Tag>())
const sourceCatalogLoading = ref(false), sourceCatalogError = ref('')
function alarmSource(alarm: AlarmEvent): string {
  const tag = alarm.tagId ? sourceTags.value.get(alarm.tagId) : undefined
  const deviceId = alarm.deviceId ?? tag?.deviceId
  const device = deviceId ? sourceDevices.value.get(deviceId) : undefined
  return [device?.name ?? (deviceId ? tr('alarmListReview.deviceId', [deviceId]) : ''),
    tag?.name ?? (alarm.tagId ? tr('alarmListReview.tagId', [alarm.tagId]) : '')]
    .filter(Boolean).join(' / ') || tr('overviewStatus.systemEvent')
}
// 报警值必须带单位：一屏里压力、温度、流量混排，只写「500.00」无法判断量级。
// 数字量报警显示 0/1 而不套两位小数——「1.00」会让开关量看着像模拟量。
function alarmValueText(alarm: AlarmEvent): string {
  const tag = alarm.tagId ? sourceTags.value.get(alarm.tagId) : undefined
  if (tag?.dataType === 'Bool') return alarm.value ? '1' : '0'
  const text = formatValue(alarm.value)
  return tag?.unit ? `${text} ${tag.unit}` : text
}
async function loadSourceCatalog(): Promise<void> {
  if (disposed || sourceCatalogLoading.value) return
  sourceCatalogLoading.value = true
  try {
    const [devices, tags] = await Promise.allSettled([deviceApi.list(), tagApi.list()])
    if (disposed) return
    if (devices.status === 'fulfilled') {
      sourceDevices.value = new Map(devices.value.map(device => [device.id, device]))
      deviceAreas.value = new Map(devices.value.filter(device => device.areaId != null).map(device => [device.id, device.areaId!]))
    }
    if (tags.status === 'fulfilled') sourceTags.value = new Map(tags.value.map(tag => [tag.id, tag]))
    sourceCatalogError.value = [devices, tags].flatMap(result => result.status === 'rejected' ? [errorMessage(result.reason)] : []).join(' · ')
  } finally { sourceCatalogLoading.value = false }
}
function canAckAlarm(row: AlarmEvent): boolean {
  return canAckAlarmEvent(auth.user, row, deviceAreas.value)
}
function canSilencePanel(areaId: number | null): boolean {
  if (!canAck.value) return false
  return areaId == null || can(auth.user, 'AckAlarm', areaId)
}
const tab = ref<'live' | 'shelved' | 'history'>('live')
const alarms = ref<AlarmEvent[]>([])
const HISTORY_PAGE_SIZE = 100
const livePageSize = ref(100)
const livePage = ref(1)
const liveTotal = ref<number | null>(null)
const historyPage = ref(1)
const historyPageSize = ref(HISTORY_PAGE_SIZE)
const historyTotal = ref(0)
const historyCapped = ref(false)
const shelves = ref<AlarmShelve[]>([])
const summary = ref<AlarmSummary | null>(null)
const summaryError = ref('')
const loading = ref(false)
const loadError = ref('')
const actionError = ref('')
const ackError = ref('')
const keyword = ref('')
// 默认只看「全部活动」（未恢复、未暂挂/抑制）。已恢复未确认的存量最多，
// 全量默认展示会把首屏刷成几千行；需要时点对应状态筛选即可。
const stateFilter = ref<AlarmListState | 'live'>('active')
const liveSort = ref<'priority' | 'time'>('priority')
const route = useRoute()
const router = useRouter()
// 清除测点筛选与写回地址栏是两条异步导航：置位期间 syncUrl 不再把 tagId 加回去
let tagFilterClearing = false
function clearTagFilter(): void {
  const query = { ...route.query }
  delete query.tagId
  tagFilterClearing = true
  void router.replace({ query }).finally(() => { tagFilterClearing = false })
}
watch(() => route.query.state, value => {
  const states = ['active', 'live', 'unack', 'rtnUnack', 'acked', 'shelved', 'suppressed'] as const
  stateFilter.value = states.find(state => state === value) ?? 'active'
}, { immediate: true })
const historyRange = ref<[Date, Date] | null>([new Date(Date.now() - 24 * 3600_000), new Date()])
const ackDialog = reactive({ visible: false, mode: 'ack' as 'ack' | 'note', alarm: null as AlarmEvent | null, note: '', saving: false })
const shelveDialog = reactive({ visible: false, alarm: null as AlarmEvent | null, minutes: 60, reason: '', saving: false })
const timeline = ref<AlarmTimeline | null>(null)
const timelineAlarm = ref<AlarmEvent | null>(null)
const timelineVisible = ref(false)
const timelineLoading = ref(false), timelineError = ref('')
const shelveError = ref('')
// 批量确认：多选行 + 进度状态；等级与关键词过滤同时服务实时页和历史页
const selected = ref<AlarmEvent[]>([])
const batchRunning = ref(false)
const batchProgress = ref('')
const tableRef = ref<TableInstance>()
const keywordInputRef = ref<InputInstance>()
// 键盘聚焦用 id 锚定而不是行号：30s 轮询刷新后行号会变，id 不会
const focusedId = ref<number | null>(null)
const focusedIndex = computed(() => focusedId.value === null ? -1 : alarms.value.findIndex(alarm => alarm.id === focusedId.value))
const focusedAlarm = computed(() => focusedIndex.value < 0 ? null : alarms.value[focusedIndex.value] ?? null)
const levelOptions = ['Critical', 'Warning', 'Info'] as const
const levelFilter = ref<AlarmLevel | ''>('')
const categoryFilter = ref<AlarmCategory | ''>('')
const rangePreset = ref<number | ''>('')
const loadedHistoryRange = ref<[number, number] | null>(null)
const historyRangeDirty = computed(() => !!loadedHistoryRange.value &&
  (!historyRange.value || historyRange.value.some((date, index) => date.getTime() !== loadedHistoryRange.value?.[index])))
const batchOutcome = ref<(BatchReport & { failedIds: number[]; skipped: number }) | null>(null)
// 批量确认是逐条串行的：几十上百条在慢速网络下要跑很久，期间页面所有按钮都被禁用。
// 给一个「停止」，让操作员能腾出手处理新的严重报警，已确认的照常计成功。
const batchCancel = ref(false)

// 地址栏是视图状态的唯一来源：刷新、复制链接、交接班描述都依赖它。
// 这里必须在下面的合并 watch 建立之前完成初始化，否则逐项赋值会各触发一次重查。
const initialQuery = route.query
const initialLevel = initialQuery.level
const initialCategory = initialQuery.category
const initialKeyword = initialQuery.keyword
const initialPage = positiveQueryId(initialQuery.page)
if (typeof initialKeyword === 'string') keyword.value = initialKeyword
if (typeof initialLevel === 'string' && levelOptions.some(level => level === initialLevel)) levelFilter.value = initialLevel as AlarmLevel
if (typeof initialCategory === 'string' && initialCategory in alarmCategoryLabels) categoryFilter.value = initialCategory as AlarmCategory
if (initialQuery.sort === 'time' || initialQuery.sort === 'priority') liveSort.value = initialQuery.sort
if (initialPage) livePage.value = initialPage

// 活动报警的“已持续”必须每秒走动：只在推送或轮询时重算会让人误判报警刚发生
const { now } = useNowTicker()

let pushTimer: number | undefined
let filterTimer: number | undefined
let disposeAlarmHandler: (() => void) | undefined
let disposeShortcuts: (() => void) | undefined
let listSeq = 0, summarySeq = 0, timelineSeq = 0, disposed = false

// 报警声音统一由 AppLayout 经 handleAlarmPush 播放（走 alarmAudio 的静音与过滤）。
// 这里曾有一份自建 AudioContext 的发声实现：它绕过了静音开关，并且与 AppLayout 的订阅
// 重复消费同一条 alarm 事件，导致静音失效、报警页上双重发声。请勿再加回来。

const stateLabel = computed(() => tr({
  active: 'alarmsView.filterActive', live: 'alarmWorkbench.liveScope', unack: 'overviewStatus.activeUnack', rtnUnack: 'overviewStatus.returned',
  acked: 'overviewStatus.acked', shelved: 'alarmsView.k06d561', suppressed: 'alarmsView.kaa2e2b'
}[stateFilter.value]))
const hasFilters = computed(() => !!route.query.tagId || !!keyword.value || !!levelFilter.value || !!categoryFilter.value || tab.value === 'live' && stateFilter.value !== 'active')
const emptyText = computed(() => {
  if (loadError.value) return loadError.value
  if (loading.value || liveTotal.value === null && tab.value === 'live') return tr('alarmsView.loadingResults')
  if (keyword.value.trim() || levelFilter.value || categoryFilter.value || route.query.tagId) return tr('alarmsView.noFilteredResults')
  return tab.value === 'live' ? tr('alarmsView.noStateResults', [stateLabel.value]) : tr('alarmsView.noHistoryResults')
})
function canSelectAlarm(alarm: AlarmEvent): boolean { return !alarm.ackTs && canAckAlarm(alarm) }
const selectableAlarms = computed(() => alarms.value.filter(canSelectAlarm))
const selectedAckable = computed(() => {
  const ids = new Set(selected.value.map(alarm => alarm.id))
  return selectableAlarms.value.filter(alarm => ids.has(alarm.id))
})
const retryRows = computed(() => {
  const ids = new Set(batchOutcome.value?.failedIds ?? [])
  return selectableAlarms.value.filter(alarm => ids.has(alarm.id))
})
function clearSelection(): void { selected.value = []; tableRef.value?.clearSelection() }
function resetFilters(): void { keyword.value = ''; levelFilter.value = ''; categoryFilter.value = ''; clearTagFilter(); if (tab.value === 'live') stateFilter.value = 'active' }

// 把当前筛选/排序/页码写回地址栏。用 replace 保留历史栈，值未变化时不动 URL。
function syncUrl(): void {
  if (disposed) return
  const query: Record<string, string> = {}
  if (!tagFilterClearing && typeof route.query.tagId === 'string') query.tagId = route.query.tagId
  if (tab.value === 'live' && stateFilter.value !== 'active') query.state = stateFilter.value
  if (levelFilter.value) query.level = levelFilter.value
  if (categoryFilter.value) query.category = categoryFilter.value
  if (keyword.value.trim()) query.keyword = keyword.value.trim()
  if (tab.value === 'live' && liveSort.value !== 'priority') query.sort = liveSort.value
  if (tab.value === 'live' && livePage.value > 1) query.page = String(livePage.value)
  const current = route.query
  const unchanged = Object.keys(query).length === Object.keys(current).length &&
    Object.entries(query).every(([key, value]) => current[key] === value)
  if (unchanged) return
  void router.replace({ query })
}

watch([keyword, levelFilter, categoryFilter, stateFilter, () => route.query.tagId, liveSort], (values, previous) => {
  listSeq++
  if (filterTimer !== undefined) window.clearTimeout(filterTimer)
  clearSelection()
  livePage.value = 1
  historyPage.value = 1
  alarms.value = []
  liveTotal.value = null
  historyTotal.value = 0
  syncUrl()
  if (tab.value === 'shelved') return
  loading.value = true
  if (values[0] !== previous[0]) {
    filterTimer = window.setTimeout(() => { filterTimer = undefined; void reload() }, 250)
  } else {
    filterTimer = undefined
    void reload()
  }
})
watch(selectableAlarms, list => {
  const ids = new Set(list.map(alarm => alarm.id))
  if (selected.value.some(alarm => !ids.has(alarm.id))) clearSelection()
})

function onSelectionChange(rows: AlarmEvent[]): void {
  const ids = new Set(selectableAlarms.value.map(alarm => alarm.id))
  selected.value = rows.filter(alarm => ids.has(alarm.id))
}

// ---- 键盘操作 ----
// HMI 惯例：↑↓/JK 选行、A 确认、N 备注、T/Enter 时间线、X 勾选、/ 搜索。
// 这里只做「进入已有流程」的快捷键：确认仍走弹窗，绝不因按键直接改变现场状态。
function isTypingTarget(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null
  return !!element && (element.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(element.tagName))
}

function overlayOpen(): boolean {
  return ackDialog.visible || shelveDialog.visible || timelineVisible.value || modalOpen()
}

// 用 Tab 走到按钮/链接上时，Enter/Space 归浏览器原生激活，不能被行快捷键抢走
function onInteractiveElement(): boolean {
  const element = document.activeElement as HTMLElement | null
  return !!element && /^(BUTTON|A)$/.test(element.tagName)
}

function focusRow(index: number): void {
  if (index < 0 || index >= alarms.value.length) return
  focusedId.value = alarms.value[index].id
  void nextTick(() => {
    const root = tableRef.value?.$el as HTMLElement | undefined
    root?.querySelectorAll<HTMLElement>('.el-table__body-wrapper tbody tr')[index]?.scrollIntoView({ block: 'nearest' })
  })
}

function moveFocus(delta: number): void {
  if (!alarms.value.length) return
  if (focusedIndex.value < 0) { focusRow(delta > 0 ? 0 : alarms.value.length - 1); return }
  focusRow(Math.min(alarms.value.length - 1, Math.max(0, focusedIndex.value + delta)))
}

function toggleSelection(alarm: AlarmEvent): void {
  tableRef.value?.toggleRowSelection(alarm, !selected.value.some(item => item.id === alarm.id))
}

function onKeyboard(e: KeyboardEvent): void {
  if (disposed || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return
  // 手机/平板隐藏了桌面表格，行聚焦不可见，键盘流程只保留桌面端
  if (window.matchMedia?.('(max-width: 768px)').matches) return
  if (isTypingTarget(e.target) || overlayOpen()) return
  if (tab.value === 'shelved') return
  if (e.key === '/') { e.preventDefault(); keywordInputRef.value?.focus(); return }
  if (e.key === 'ArrowDown' || e.key === 'j' || e.key === 'J') { e.preventDefault(); moveFocus(1); return }
  if (e.key === 'ArrowUp' || e.key === 'k' || e.key === 'K') { e.preventDefault(); moveFocus(-1); return }
  if (e.key === 'Home') { e.preventDefault(); focusRow(0); return }
  if (e.key === 'End') { e.preventDefault(); focusRow(alarms.value.length - 1); return }
  if (e.key === 'Escape') {
    if (focusedId.value !== null) { e.preventDefault(); focusedId.value = null }
    return
  }
  const alarm = focusedAlarm.value
  if (!alarm) return
  if (e.key === 'Enter') {
    if (onInteractiveElement()) return
    e.preventDefault()
    openTimeline(alarm)
    return
  }
  if (e.key === 't' || e.key === 'T') { e.preventDefault(); openTimeline(alarm); return }
  if (e.key === 'a' || e.key === 'A') {
    if (alarm.ackTs || !canAckAlarm(alarm)) return
    e.preventDefault()
    openAck(alarm)
    return
  }
  if (e.key === 'n' || e.key === 'N') {
    if (!canAckAlarm(alarm)) return
    e.preventDefault()
    openNote(alarm)
    return
  }
  if (e.key === 'x' || e.key === 'X' || e.key === ' ') {
    if (e.key === ' ' && onInteractiveElement()) return
    if (tab.value !== 'live' || alarm.ackTs || !canAckAlarm(alarm)) return
    e.preventDefault()
    toggleSelection(alarm)
  }
}

// 只确认未确认的选中项：已确认的跳过，避免整批因个别 400 失败。
// 用最新列表回填，防止选中后列表刷新留下过期对象。
async function batchAck(): Promise<void> {
  await confirmAndAck(selectedAckable.value, 'selected')
}

async function confirmAndAck(targets: AlarmEvent[], mode: 'selected' | 'visible'): Promise<void> {
  if (disposed || batchRunning.value || loading.value || tab.value !== 'live' || !targets.length) return
  const ids = new Set(targets.map(alarm => alarm.id))
  batchRunning.value = true
  batchCancel.value = false
  try {
    const needsNote = targets.some(alarm => alarm.level === 'Critical')
    let batchNote = ''
    // 范围说明（多少条、含不含隐藏项）和「不可撤销」在两种弹窗里都要给：
    // 只弹一个要求填说明的输入框，会让操作员在不知道自己要确认几条的情况下签字。
    const scope = tr(mode === 'visible' ? 'alarmsView.ackVisibleConfirm' : 'alarmsView.batchAckConfirm', [ids.size])
    if (needsNote) {
      // 严重报警确认必须留下处理说明，批量确认时统一填写一次。
      try {
        const result = await ElMessageBox.prompt(`${scope} ${tr('alarmsView.k8ea5fe')}`, tr('alarmsView.confirmTitle'), {
          inputType: 'textarea',
          inputValidator: (value: string) => (value && value.trim() ? true : tr('alarmsView.k8ea5fe')),
          confirmButtonText: tr(mode === 'visible' ? 'alarmsView.ackVisible' : 'alarmsView.batchAck'),
          cancelButtonText: tr('alarmsView.k4d0b46')
        })
        batchNote = result.value.trim()
      } catch { return }
    } else {
      try {
        await ElMessageBox.confirm(
          scope,
          tr('alarmsView.confirmTitle'),
          { type: 'warning', confirmButtonText: tr(mode === 'visible' ? 'alarmsView.ackVisible' : 'alarmsView.batchAck'), cancelButtonText: tr('alarmsView.k4d0b46') }
        )
      } catch { return }
    }
    if (disposed || tab.value !== 'live') return
    const rows = selectableAlarms.value.filter(alarm => ids.has(alarm.id))
    const failedIds: number[] = []
    actionError.value = ''
    batchOutcome.value = null
    const report = await runBatch(
      rows,
      (a) => `#${a.id} ${alarmMessage(a)}`,
      async alarm => {
        try { await alarmApi.ack(alarm.id, batchNote) }
        catch (error) { failedIds.push(alarm.id); throw error }
      },
      (p) => { batchProgress.value = tr('alarmsView.batchAckProgress', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : '']) },
      () => batchCancel.value
    )
    if (disposed) return
    batchOutcome.value = { ...report, failedIds, skipped: ids.size - rows.length }
    if (report.failed.length === 0 && !report.canceled) ElMessage.success(tr('alarmsView.batchAckOk', [report.ok, report.total]))
    await reload()
  } finally {
    batchRunning.value = false
    batchProgress.value = ''
  }
}

function applyRangePreset(hours: number | ''): void {
  if (typeof hours !== 'number') return
  historyRange.value = [new Date(Date.now() - hours * 3600_000), new Date()]
  searchHistory()
}

function rowClass({ row, rowIndex }: { row: AlarmEvent; rowIndex: number }): string {
  const classes: string[] = []
  if (row.level === 'Critical' && !row.endTs && !row.shelved) classes.push('alarm-row-critical')
  if (rowIndex === focusedIndex.value) classes.push('alarm-row-focused')
  return classes.join(' ')
}

// 行被刷新掉（确认后转历史、过滤变化）时顺势释放聚焦，避免指针停在别的行上
watch(alarms, list => {
  if (focusedId.value !== null && !list.some(alarm => alarm.id === focusedId.value)) focusedId.value = null
})

function duration(alarm: AlarmEvent): string {
  const end = alarm.endTs ?? now.value
  const seconds = Math.max(0, Math.floor((end - alarm.startTs) / 1000))
  if (seconds < 60) return tr('alarmsView.k99598b', [seconds])
  if (seconds < 3600) return tr('alarmsView.k76dfb6', [Math.floor(seconds / 60), seconds % 60])
  return tr('alarmsView.k1cf4ee', [Math.floor(seconds / 3600), Math.floor((seconds % 3600) / 60)])
}

async function loadSummary(): Promise<void> {
  const seq = ++summarySeq
  try {
    const result = await alarmApi.summary()
    if (seq === summarySeq && !disposed) { summary.value = result; summaryError.value = '' }
  } catch (error) {
    if (seq === summarySeq && !disposed) {
      summary.value = null
      summaryError.value = errorMessage(error, tr('overviewView.summaryFailed'))
    }
  }
}

function queryFilters(): Pick<AlarmPageQuery, 'level' | 'category' | 'keyword' | 'tagId'> {
  const tagId = positiveQueryId(route.query.tagId)
  if (route.query.tagId !== undefined && !tagId) throw new Error(tr('alarmsView.invalidTag'))
  return { level: levelFilter.value || undefined, category: categoryFilter.value || undefined, keyword: keyword.value.trim() || undefined, tagId: tagId ?? undefined }
}

// background=true 用于推送与定时刷新：不点亮全表遮罩（遮罩会挡住确认操作）。失败留在页面上，不再额外弹提示。
async function load(background = false): Promise<void> {
  if (disposed || tab.value === 'history') return
  const seq = ++listSeq, mode = tab.value
  if (!background) loading.value = true
  try {
    const [result, shelf] = await Promise.all([alarmApi.queryPage({
      live: true, priorityFirst: liveSort.value === 'priority', state: stateFilter.value === 'live' ? undefined : stateFilter.value, ...queryFilters(),
      offset: (livePage.value - 1) * livePageSize.value, limit: livePageSize.value
    }), ruleApi.shelves()])
    if (disposed || seq !== listSeq || tab.value !== mode) return
    if (livePage.value > 1 && !result.items.length && result.total <= (livePage.value - 1) * livePageSize.value) {
      livePage.value = Math.max(1, Math.ceil(result.total / livePageSize.value))
      // 页码被纠正后同步地址栏，否则刷新会回到已经空掉的页
      syncUrl()
      await load(background)
      return
    }
    alarms.value = result.items
    liveTotal.value = result.total
    shelves.value = shelf
    loadError.value = ''
  } catch (error) {
    if (disposed || seq !== listSeq || tab.value !== mode) return
    loadError.value = errorMessage(error, tr('alarmsView.k620b67'))
  } finally {
    if (seq === listSeq) loading.value = false
  }
  if (!disposed && seq === listSeq) await loadSummary()
}

// 报警洪峰时推送会连成一片，每条都触发一次全量重查会把列表刷成请求风暴。
// 这里做在途去重 + 400ms 合批：同一波事件只查一次，落下的那波用 queued 补上。
let backgroundBusy = false
let backgroundQueued = false

async function backgroundRefresh(): Promise<void> {
  if (disposed || tab.value === 'history' || filterTimer !== undefined) return
  if (backgroundBusy) {
    backgroundQueued = true
    return
  }
  backgroundBusy = true
  try {
    await load(true)
  } finally {
    backgroundBusy = false
  }
  if (backgroundQueued) {
    backgroundQueued = false
    await backgroundRefresh()
  }
}

function schedulePushRefresh(): void {
  if (tab.value === 'history') return
  if (pushTimer !== undefined) window.clearTimeout(pushTimer)
  pushTimer = window.setTimeout(() => {
    pushTimer = undefined
    void backgroundRefresh()
  }, 400)
}

async function loadHistory(useDraftRange = false): Promise<void> {
  if (disposed || tab.value !== 'history') return
  const seq = ++listSeq
  loading.value = true
  try {
    const range = !useDraftRange && loadedHistoryRange.value
      ? loadedHistoryRange.value
      : (historyRange.value ?? [new Date(Date.now() - 24 * 3600_000), new Date()]).map(date => date.getTime())
    const [from, to] = range
    if (!Number.isFinite(from) || !Number.isFinite(to) || from >= to) {
      loadError.value = tr('trendsView.invalidRange')
      return
    }
    const pageSize = historyPageSize.value
    const result = await alarmApi.queryPage({
      from,
      to,
      ...queryFilters(),
      offset: (historyPage.value - 1) * pageSize,
      limit: pageSize
    })
    if (disposed || seq !== listSeq || tab.value !== 'history') return
    alarms.value = result.items
    historyTotal.value = result.total
    historyCapped.value = result.capped
    loadedHistoryRange.value = [from, to]
    loadError.value = ''
  } catch (error) {
    if (disposed || seq !== listSeq || tab.value !== 'history') return
    loadError.value = errorMessage(error, tr('alarmsView.k620b67'))
  } finally {
    if (seq === listSeq) loading.value = false
  }
}

function searchHistory(): void {
  historyPage.value = 1
  void loadHistory(true)
}

function onLivePageChange(page: number): void {
  if (disposed || tab.value !== 'live' || livePage.value === page) return
  livePage.value = page
  clearSelection()
  alarms.value = []
  syncUrl()
  void load()
}

// 洪峰时希望一屏看更多：直播页与历史页共用同一组可选条数
function onLiveSizeChange(size: number): void {
  if (disposed || tab.value !== 'live' || livePageSize.value === size) return
  livePageSize.value = size
  livePage.value = 1
  clearSelection()
  alarms.value = []
  syncUrl()
  void load()
}

function onHistoryPageChange(page: number): void {
  if (disposed || tab.value !== 'history' || historyPage.value === page) return
  historyPage.value = page
  clearSelection()
  alarms.value = []
  void loadHistory()
}

function onHistorySizeChange(size: number): void {
  if (disposed || tab.value !== 'history' || historyPageSize.value === size) return
  historyPageSize.value = size
  historyPage.value = 1
  clearSelection()
  alarms.value = []
  void loadHistory()
}

// 切到历史页时把默认区间刷新到“最近 24 小时至现在”，避免页面停留后漏掉新报警。
function onTabChange(): Promise<void> {
  listSeq++
  if (filterTimer !== undefined) window.clearTimeout(filterTimer)
  filterTimer = undefined
  clearSelection()
  focusedId.value = null
  livePage.value = 1
  alarms.value = []
  liveTotal.value = null
  loadError.value = ''
  loadedHistoryRange.value = null
  rangePreset.value = ''
  historyCapped.value = false
  void nextTick(() => tableRef.value?.scrollTo({ left: 0, top: 0 }))
  // 选中行按 row-key 保留在表格内部，切页时要显式清掉，避免跨页误批量
  if (tab.value === 'history') {
    historyRange.value = [new Date(Date.now() - 24 * 3600_000), new Date()]
    historyPage.value = 1
  }
  syncUrl()
  return reload()
}

async function ackVisible(): Promise<void> {
  await confirmAndAck(selectableAlarms.value, 'visible')
}

async function silenceAnnunciators(): Promise<void> {
  actionError.value = ''
  let list: Awaited<ReturnType<typeof annunciatorApi.list>>
  try {
    list = await annunciatorApi.list()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmsView.silenceAnnFail'))
    return
  }
  const ackable = list.filter((p) => canSilencePanel(p.areaId))
  if (!ackable.length) {
    actionError.value = tr('alarmsView.silenceAnnNone')
    return
  }
  // 静音会停掉现场声光，属安全相关操作，必须先确认。只包含当前用户有确认权的面板。
  try {
    await ElMessageBox.confirm(tr('alarmsView.silenceAnnConfirm', [ackable.length]), tr('alarmsView.confirmTitle'), {
      type: 'warning',
      confirmButtonText: tr('alarmsView.silenceAnn'),
      cancelButtonText: tr('alarmsView.k4d0b46')
    })
  } catch {
    return
  }
  // 逐个面板静音，部分失败时列出明细，不静默吞错
  const report = await runBatch(ackable, (p) => p.name, async (p) => { await annunciatorApi.silence(p.id) })
  if (report.failed.length === 0) ElMessage.success(tr('alarmsView.silenceAnnOk'))
  else await ElMessageBox.alert(summarizeBatch(report, ''), tr('alarmsView.confirmTitle'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmsView.k4d0b46') })
}
function reload(): Promise<void> {
  return tab.value === 'history' ? loadHistory() : load()
}

function openAck(alarm: AlarmEvent): void {
  if (disposed || ackDialog.saving || batchRunning.value) return
  ackError.value = ''
  Object.assign(ackDialog, { visible: true, mode: 'ack', alarm, note: '', saving: false })
}

function openNote(alarm: AlarmEvent): void {
  if (disposed || ackDialog.saving || batchRunning.value) return
  ackError.value = ''
  Object.assign(ackDialog, { visible: true, mode: 'note', alarm, note: '', saving: false })
}

async function submitAck(): Promise<void> {
  if (disposed || !ackDialog.visible || !ackDialog.alarm || ackDialog.saving || batchRunning.value) return
  const { alarm, mode } = ackDialog, note = ackDialog.note.trim()
  ackError.value = ''
  if (mode === 'note' && !note) {
    ackError.value = tr('alarmsView.kc31ec2')
    return
  }
  if (mode === 'ack' && alarm.level === 'Critical' && !note) {
    ackError.value = tr('alarmsView.k8ea5fe')
    return
  }
  ackDialog.saving = true
  try {
    if (mode === 'ack') await alarmApi.ack(alarm.id, note)
    else await alarmApi.note(alarm.id, note)
    if (disposed) return
    ElMessage.success(mode === 'ack' ? tr('alarmsView.kd9fea6') : tr('alarmsView.kbad4a8'))
    ackDialog.visible = false
    await reload()
  } catch (error) {
    if (!disposed) ackError.value = errorMessage(error, tr('alarmsView.k09e424'))
  } finally {
    ackDialog.saving = false
  }
}

function openShelve(alarm: AlarmEvent): void {
  if (disposed || shelveDialog.saving || batchRunning.value) return
  shelveError.value = ''
  Object.assign(shelveDialog, { visible: true, alarm, minutes: 60, reason: '', saving: false })
}

async function submitShelve(): Promise<void> {
  if (disposed || !shelveDialog.visible || !shelveDialog.alarm?.ruleId || shelveDialog.saving || batchRunning.value) return
  const ruleId = shelveDialog.alarm.ruleId, minutes = shelveDialog.minutes, reason = shelveDialog.reason.trim()
  shelveError.value = ''
  if (!reason) {
    shelveError.value = tr('alarmsView.shelveReasonRequired')
    return
  }
  shelveDialog.saving = true
  try {
    await ruleApi.shelve(ruleId, minutes, reason)
    if (disposed) return
    ElMessage.success(tr('alarmsView.ka7f94c'))
    shelveDialog.visible = false
    await reload()
  } catch (error) {
    // 服务端 400 尽量落到原因字段旁，避免只弹一条全局提示
    if (!disposed) shelveError.value = serverFormErrors(error, tr('alarmsView.k2fe047')).message
  } finally {
    shelveDialog.saving = false
  }
}

async function unshelve(ruleId: number): Promise<void> {
  // 解除后该规则的新报警会重新通知，属易误触操作
  try {
    await ElMessageBox.confirm(tr('alarmsView.unshelveConfirm'), tr('alarmsView.confirmTitle'), {
      type: 'warning',
      confirmButtonText: tr('alarmsView.kcd308e'),
      cancelButtonText: tr('alarmsView.k4d0b46')
    })
  } catch {
    return
  }
  actionError.value = ''
  try {
    await ruleApi.unshelve(ruleId)
    ElMessage.success(tr('alarmsView.kdaf18d'))
    await reload()
  } catch (error) {
    actionError.value = errorMessage(error, tr('alarmsView.k7286b4'))
  }
}

async function openTimeline(alarm: AlarmEvent): Promise<void> {
  if (disposed) return
  const seq = ++timelineSeq
  timelineAlarm.value = alarm
  timeline.value = null; timelineError.value = ''; timelineLoading.value = true; timelineVisible.value = true
  try {
    const result = await alarmApi.timeline(alarm.id)
    if (!disposed && seq === timelineSeq) timeline.value = result
  } catch (error) {
    if (!disposed && seq === timelineSeq) timelineError.value = errorMessage(error, tr('alarmsView.k760fd5'))
  } finally { if (!disposed && seq === timelineSeq) timelineLoading.value = false }
}
function retryTimeline(): void {
  if (timelineVisible.value && timelineAlarm.value && !timelineLoading.value) void openTimeline(timelineAlarm.value)
}
watch(timelineVisible, visible => { if (!visible) { timelineSeq++; timelineLoading.value = false; timeline.value = null; timelineAlarm.value = null } }, { flush: 'sync' })

function registerAlarmHandler(): void {
  // 只负责刷新数据；发声由 AppLayout 统一处理，避免重复消费同一条 alarm 事件。
  disposeAlarmHandler = onHubEvent<{ action: string; alarm: AlarmEvent }>('alarm', () => {
    schedulePushRefresh()
  })
}

// 30s 兜底轮询：推送丢包时列表不至于长期停摆；隐藏页自动暂停，回到前台补一次。
usePolling(backgroundRefresh, 30000)

onMounted(async () => {
  disposeShortcuts = registerShortcuts({
    id: 'alarms',
    titleKey: 'shortcuts.alarms',
    items: [
      { keys: ['↓', 'J'], descKey: 'shortcuts.alarmDown' },
      { keys: ['↑', 'K'], descKey: 'shortcuts.alarmUp' },
      { keys: ['A'], descKey: 'shortcuts.alarmAck' },
      { keys: ['N'], descKey: 'shortcuts.alarmNote' },
      { keys: ['T'], descKey: 'shortcuts.alarmTimeline' },
      { keys: ['X'], descKey: 'shortcuts.alarmSelect' },
      { keys: ['/'], descKey: 'shortcuts.alarmSearch' },
      { keys: ['Esc'], descKey: 'shortcuts.closeLayer' }
    ]
  })
  window.addEventListener('keydown', onKeyboard)
  void loadSourceCatalog()
  await load()
  if (disposed) return
  registerAlarmHandler()
})

onBeforeUnmount(() => {
  disposed = true
  listSeq++
  summarySeq++
  timelineSeq++
  disposeShortcuts?.()
  window.removeEventListener('keydown', onKeyboard)
  if (pushTimer !== undefined) window.clearTimeout(pushTimer)
  if (filterTimer !== undefined) window.clearTimeout(filterTimer)
  disposeAlarmHandler?.()
})
</script>

<style scoped>
.alarm-panel {
  min-width: 0;
}

.alarm-panel :deep(.el-tabs__header) {
  flex: none;
  margin-bottom: var(--scada-space-3);
}

.alarm-state-filters {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  flex: none;
  gap: 6px;
  margin-bottom: var(--scada-space-3);
}
.alarm-state-filters .el-button { margin-left: 0; }
.alarm-state-filter {
  min-height: 30px;
  padding: 5px 10px;
  border-color: var(--scada-border);
  color: var(--scada-text-secondary);
  background: var(--scada-panel-muted);
}
.alarm-state-filter.is-current {
  color: var(--scada-primary);
  background: var(--scada-primary-soft);
  border-color: var(--scada-primary);
  box-shadow: inset 0 -2px 0 var(--scada-primary);
  font-weight: 600;
}
.alarm-state-filter:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: 2px; }
.alarm-filter-count { margin-left: 6px; font-variant-numeric: tabular-nums; font-weight: 600; color: var(--scada-text-secondary); }
.alarm-filter-count.has-unack { color: var(--scada-danger); }
.alarm-filter-count.has-returned { color: var(--scada-warning); }
.alarm-rule-count { margin-left: auto; white-space: nowrap; }
.alarm-toolbar {
  flex: none;
  align-items: flex-start;
  gap: 12px;
  margin-bottom: var(--scada-space-2);
}
.alarm-query, .alarm-actions {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}
.alarm-query { flex: 1; min-width: 0; }
.alarm-query .el-button, .alarm-actions .el-button { margin-left: 0; }
.alarm-actions { margin-left: auto; }
.alarm-level-filter { width: 112px; }
.alarm-category-filter { width: 120px; }
.alarm-keyword { width: 200px; }
.alarm-sort { width: 150px; }
.alarm-range-preset { width: 130px; }
.alarm-query :deep(.alarm-history-range) { flex: 0 1 390px; width: 390px; min-width: 240px; max-width: 100%; }

.alarm-table-wrap {
  flex: 1;
  min-height: 160px;
  min-width: 0;
}
.alarm-list-summary { display: flex; align-items: center; flex-wrap: wrap; flex: none; gap: 8px 12px; margin: 0 0 10px; }
/* 快捷键提示靠右：只在桌面显示，移动端没有键盘流程 */
.alarm-kbd-tip { margin-left: auto; white-space: nowrap; }
.alarm-kbd-help { padding: 5px 9px; }
.alarm-kbd-help kbd { font-family: var(--scada-font-mono, ui-monospace, monospace); font-size: 13px; }
@media (max-width: 768px) { .alarm-kbd-tip, .alarm-kbd-help { display: none; } }
.alarm-footer { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; flex: none; gap: 8px 16px; margin-top: 8px; }
.alarm-pager { display: flex; justify-content: flex-end; max-width: 100%; margin-left: auto; }
.alarm-pager :deep(.el-pagination) { flex-wrap: wrap; justify-content: flex-end; row-gap: 8px; }
.alarm-page-size { width: 128px; margin-left: 8px; margin-right: 16px; }
.alarm-history-context { display: flex; flex-direction: column; flex: none; gap: 4px; margin: 0 0 10px; }
.alarm-range-changed { color: var(--scada-warning); }
.alarm-progress { flex: none; margin: 0 0 8px; overflow-wrap: anywhere; }
.alarm-feedback { flex: none; margin-bottom: 8px; }
.batch-failures { max-height: 120px; overflow: auto; margin: 4px 0; overflow-wrap: anywhere; }
.alarm-dialog-context { display: flex; align-items: flex-start; flex-wrap: wrap; gap: 8px; margin-bottom: 16px; overflow-wrap: anywhere; }
.alarm-dialog-context > strong { flex: 1; min-width: 0; color: var(--scada-text); }
.alarm-dialog-context > .muted { flex-basis: 100%; }
.alarm-row-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 2px 10px; }
.alarm-row-actions .el-button { min-height: 28px; padding: 2px 0; margin-left: 0; }
.alarm-panel :deep(.alarm-row-critical) { --el-table-tr-bg-color: var(--scada-alarm-critical-bg); }
.alarm-panel :deep(.alarm-row-critical .alarm-message) { font-weight: 600; }
/* 键盘聚焦：左侧色条 + 底色。表格行会裁掉 outline，所以不用 outline 表达聚焦 */
.alarm-panel :deep(.alarm-row-focused) { --el-table-tr-bg-color: var(--scada-primary-soft); }
.alarm-panel :deep(.alarm-row-focused td:first-child) { box-shadow: inset 3px 0 0 var(--scada-primary); }
.alarm-panel :deep(.el-table:not(.is-scrolling-none) .el-table__cell.el-table-fixed-column--right.is-first-column::before) {
  width: 12px;
  background: linear-gradient(90deg, transparent, color-mix(in srgb, var(--scada-text) 14%, transparent));
}
.alarm-sr-only { position: absolute; width: 1px; height: 1px; padding: 0; overflow: hidden; clip-path: inset(50%); white-space: nowrap; border: 0; }

.state-cell {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-wrap: wrap;
}

.gateway-tag {
  margin-right: 6px;
}
.alarm-message { overflow-wrap: anywhere; }
.alarm-source { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 12px; margin-top: 4px; font-size: var(--scada-fs-caption); color: var(--scada-text-secondary); }
.alarm-source > span { overflow-wrap: anywhere; }
.alarm-trend-link { color: var(--scada-primary); white-space: nowrap; }
.alarm-trend-link:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: 2px; }
/* 触控设备：文字链接只有一行高（26×23），手指点不中；撑开点击区，视觉尺寸不变 */
@media (any-pointer: coarse) {
  .alarm-trend-link { display: inline-flex; align-items: center; min-height: 32px; padding: 0 8px; margin: 0 -8px; }
}
.source-catalog-warning { margin-bottom: 8px; flex-shrink: 0; }

.flags {
  display: flex;
  gap: 2px;
  margin-top: 2px;
}

.alarm-mobile-list { display: none; }
.alarm-card {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 12px 0;
  border-bottom: 1px solid var(--scada-border);
}
.alarm-card:last-child { border-bottom: 0; }
.alarm-card.is-critical { padding-left: 10px; border-left: 3px solid var(--scada-danger); }
.alarm-card-head { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; }
.alarm-card-duration { margin-left: auto; font-variant-numeric: tabular-nums; }
.alarm-card-message { margin: 0; font-size: 14px; font-weight: 600; line-height: 1.45; color: var(--scada-text); overflow-wrap: anywhere; }
.alarm-card-meta { display: flex; flex-wrap: wrap; gap: 4px 12px; color: var(--scada-text-secondary); overflow-wrap: anywhere; }
.alarm-card-unack { color: var(--scada-danger); font-weight: 600; }
.alarm-card-actions { display: flex; flex-wrap: wrap; gap: 8px; margin-top: 4px; }
.alarm-card-actions .el-button { margin-left: 0; }

@media (max-width: 768px), (max-height: 700px) {
  .alarm-page { height: auto; min-height: 100%; }
  .alarm-panel { flex: none; }
  .alarm-table-wrap { flex: none; height: clamp(280px, 50vh, 520px); }
}
@media (max-width: 768px) {
  .alarm-desktop-table { display: none; }
  .alarm-mobile-list { display: flex; flex-direction: column; flex: none; }
  .alarm-scroll-hint { display: none; }
  .alarm-query { flex-basis: 100%; }
  .alarm-keyword { flex: 1; min-width: 140px; }
  .alarm-actions { justify-content: flex-end; width: 100%; }
  .alarm-rule-count { margin-left: 0; }
  .alarm-scroll-hint { width: 100%; }
  :global(.alarm-date-popper .el-date-range-picker) { width: calc(100vw - 24px); max-height: calc(100vh - 24px); overflow: auto; }
  :global(.alarm-date-popper .el-picker-panel__body) { min-width: 0; }
  :global(.alarm-date-popper .el-date-range-picker__content) { width: 100%; padding: 12px; }
  :global(.alarm-date-popper .el-date-range-picker__content.is-left) { border-right: 0; border-bottom: 1px solid var(--scada-border); }
  :global(.alarm-date-popper .el-date-range-picker__time-header) { display: flex; flex-direction: column; gap: 8px; }
  :global(.alarm-date-popper .el-date-range-picker__editors-wrap) { width: 100%; }
  :global(.alarm-date-popper .el-date-range-picker__time-header > .el-icon) { display: none; }
  :global(.alarm-date-popper .el-picker-panel__footer) { position: sticky; bottom: 0; background: var(--scada-panel); }
}
</style>

<template>
  <div class="page page--fill alarm-page">
    <AlarmFloodBanner :summary="summary" locate data-test="flood-banner" @locate="locateFirstOut" />
    <el-alert v-if="pendingNotice" :title="pendingNotice === 'critical' ? $t('alarmUx.pendingCritical') : $t('alarmUx.pendingUpdate')" type="warning" :closable="false" show-icon class="alarm-pending" data-test="alarm-pending-push">
      <el-button link type="primary" data-test="alarm-pending-apply" @click="applyPendingPush">{{ $t('alarmUx.pendingApply') }}</el-button>
    </el-alert>
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
            <el-date-picker v-model="historyRange" :id="['alarm-history-start', 'alarm-history-end']" type="datetimerange" :start-placeholder="$t('alarmsView.ke8868a')" :end-placeholder="$t('alarmsView.ka0bb9f')" :aria-label="$t('common.timeRange')" class="alarm-history-range" popper-class="alarm-date-popper" @change="rangePreset = ''" />
            <el-button :icon="Search" :loading="loading" @click="searchHistory">{{ $t('alarmsView.k711363') }}</el-button>
          </template>
          <el-button size="small" data-test="alarm-filters" :aria-expanded="filtersOpen" @click="filtersOpen = !filtersOpen">{{ $t('alarmUx.filters') }}</el-button>
          <div v-show="filtersOpen" class="alarm-filter-panel">
            <el-select v-model="levelFilter" clearable :aria-label="$t('alarmsView.allLevels')" :placeholder="$t('alarmsView.allLevels')" class="alarm-level-filter" data-test="level-filter">
              <el-option v-for="l in levelOptions" :key="l" :label="levelLabel(l)" :value="l" />
            </el-select>
            <el-select v-model="categoryFilter" clearable :aria-label="$t('alarmsView.allCategories')" :placeholder="$t('alarmsView.allCategories')" class="alarm-category-filter" data-test="category-filter">
              <el-option v-for="(label, value) in alarmCategoryLabels" :key="value" :label="label" :value="value" />
            </el-select>
            <el-select v-if="tab === 'live'" v-model="liveSort" :aria-label="$t('alarmListReview.sort')" class="alarm-sort" data-test="alarm-sort">
              <el-option value="priority" :label="$t('alarmListReview.priorityFirst')" />
              <el-option value="time" :label="$t('alarmListReview.newestFirst')" />
            </el-select>
          </div>
          <el-input ref="keywordInputRef" v-model="keyword" :prefix-icon="Search" clearable :aria-label="$t('alarmsView.kd812c3')" :placeholder="$t('alarmsView.kd812c3')" class="alarm-keyword" data-test="alarm-keyword" />
        </div>
        <div class="alarm-actions" role="group" :aria-label="$t('alarmWorkbench.actions')">
          <el-button v-if="tab === 'live' && canAck" type="primary" data-test="batch-ack" :disabled="!selectedAckable.length || loading" :loading="batchRunning" @click="batchAck">
            {{ $t('alarmsView.batchAck') }}<template v-if="selectedAckable.length"> ({{ selectedAckable.length }})</template>
          </el-button>
          <el-button v-if="tab === 'live' && batchRunning" type="warning" plain :disabled="batchCancel" data-test="batch-stop" @click="batchCancel = true">{{ $t('alarmsUx.stopBatch') }}</el-button>
          <el-button v-if="tab === 'live' && canAck" data-test="ack-visible" :disabled="!visibleAckable.length || loading || batchRunning" @click="ackVisible">{{ $t('alarmsView.ackVisible') }} ({{ visibleAckable.length }})</el-button>
          <p v-if="silenceUntil" class="alarm-silence-until tiny" data-test="alarm-silence-until">{{ $t('alarmUx.silenceUntil', [formatTime(silenceUntil)]) }}</p>
          <el-dropdown trigger="click">
            <el-button data-test="alarm-more">{{ $t('alarmUx.more') }}</el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item v-if="tab === 'live' && canAck" data-test="silence-ann" @click="silenceAnnunciators">{{ $t('alarmsView.silenceAnn') }}</el-dropdown-item>
                <el-dropdown-item data-test="alarm-kbd-help" @click="openShortcutHelp">{{ $t('alarmUx.shortcuts') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
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

      <PageStatusBar :issues="pageIssues" @action="onStatusAction" />

      <div v-if="tab === 'shelved'" class="alarm-table-wrap" role="region" :aria-label="$t('alarmWorkbench.table')" :aria-busy="loading" data-test="alarm-table-region">
      <el-table v-loading="loading" :data="shelves" size="small" height="100%" scrollbar-always-on :scrollbar-tabindex="0" :empty-text="loading ? $t('alarmsView.loadingResults') : loadError && !shelves.length ? loadError : $t('alarmsView.k0d51d2')">
        <el-table-column :label="$t('alarmListReview.source')" min-width="180" show-overflow-tooltip><template #default="{ row }">{{ shelfSource(row) }}</template></el-table-column>
        <el-table-column prop="reason" :label="$t('alarmsView.k1ff9c3')" min-width="200" show-overflow-tooltip />
        <el-table-column prop="user" :label="$t('alarmsView.k06858d')" width="100" show-overflow-tooltip />
        <el-table-column :label="$t('alarmsView.k79cc87')" width="170" show-overflow-tooltip><template #default="{ row }">{{ formatTime(row.startTs) }}</template></el-table-column>
        <el-table-column :label="$t('alarmUx.shelfLeft')" width="170" show-overflow-tooltip>
          <template #default="{ row }">
            <div data-test="shelf-remaining">{{ shelfRemaining(row) }}</div>
            <div class="tiny muted">{{ formatTime(row.untilTs) }}</div>
          </template>
        </el-table-column>
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
              <el-tag :class="{ 'tag-quiet': !alarmStateType(row.state) }" :type="alarmStateType(row.state)" size="small" data-test="alarm-state">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
              <el-tag class="alarm-level-tag" :data-level="row.level" :type="levelType(row.level)" size="small" effect="plain">{{ levelLabel(row.level) }}</el-tag>
            </div>
            <div class="flags">
              <el-tooltip v-if="row.firstUp" :content="$t('alarmsView.firstUpHint')" placement="top">
                <el-tag size="small" effect="dark" type="warning" data-test="first-up">{{ $t('alarmsView.firstUp') }}</el-tag>
              </el-tooltip>
              <el-tooltip v-if="row.firstOut" :content="$t('alarmsView.firstOutHint')" placement="top">
                <el-tag size="small" effect="dark" type="danger" data-test="first-out">{{ $t('alarmsView.firstOut') }}</el-tag>
              </el-tooltip>
              <el-tag v-if="row.shelved" class="flag-muted" size="small" effect="plain" type="info">{{ $t('alarmsView.k06d561') }}</el-tag>
              <el-tooltip v-if="row.suppressed" :content="row.suppressReason || $t('alarmsView.kb37a4e')"><el-tag class="flag-muted" size="small" effect="plain" type="info">{{ $t('alarmsView.kaa2e2b') }}</el-tag></el-tooltip>
              <el-tag v-if="row.chattering" class="flag-muted" size="small" effect="plain" type="warning">{{ $t('alarmsView.ka60f92') }}</el-tag>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('alarmsView.kf3dd64')" min-width="260">
          <template #default="{ row }">
            <el-tag v-if="row.gatewayId" class="tag-quiet gateway-tag" :type="undefined" size="small" effect="plain">{{ $t('alarmsView.k38b4cc', [row.gatewayId]) }}</el-tag>
            <div class="alarm-message" :data-alarm-id="row.id">{{ alarmMessage(row) }}</div>
            <div class="alarm-source" data-test="alarm-source">
              <span v-if="row.category" class="alarm-category" data-test="alarm-category">{{ alarmCategoryLabels[row.category] ?? row.category }}</span>
              <span>{{ $t('alarmListReview.source') }}：{{ alarmSource(row) }}</span>
              <router-link v-if="row.tagId && can(auth.user, 'View')" :to="trendQuery(row)" class="alarm-trend-link" data-test="alarm-trend">{{ $t('overviewStatus.trend') }}</router-link>
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
            <el-button link data-test="alarm-row-more" :aria-expanded="rowMenuId === row.id" @click.stop="rowMenuId = rowMenuId === row.id ? null : row.id">{{ $t('alarmUx.rowMore') }}</el-button>
            <div v-if="rowMenuId === row.id" class="alarm-row-menu">
              <el-button v-if="canAckAlarm(row)" link :disabled="batchRunning" data-test="alarm-note" @click="chooseRowAction(() => openNote(row))">{{ $t('alarmsView.ke03614') }}</el-button>
              <el-button v-if="canAckAlarm(row) && row.ruleId && !row.endTs && !row.shelved" link type="warning" :disabled="batchRunning" data-test="alarm-shelve" @click="chooseRowAction(() => openShelve(row))">{{ $t('alarmsView.k06d561') }}</el-button>
              <el-button v-if="canAckAlarm(row) && row.ruleId && row.shelved" link type="warning" :disabled="batchRunning" data-test="alarm-unshelve" @click="chooseRowAction(() => unshelve(row.ruleId))">{{ $t('alarmsView.kcd308e') }}</el-button>
              <el-button v-if="row.tagId && can(auth.user, 'View')" link data-test="alarm-open-screen" @click="chooseRowAction(() => openAlarmScreen(row))">{{ $t('alarmUx.openScreen') }}</el-button>
              <el-button link data-test="timeline" @click="chooseRowAction(() => openTimeline(row))">{{ $t('alarmsView.k9058a1') }}</el-button>
            </div>
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
            <el-tag class="alarm-level-tag" :data-level="row.level" :type="levelType(row.level)" size="small">{{ levelLabel(row.level) }}</el-tag>
            <el-tag :class="{ 'tag-quiet': !alarmStateType(row.state) }" :type="alarmStateType(row.state)" size="small" effect="plain">{{ alarmStateLabels[row.state ?? 'Normal'] }}</el-tag>
            <el-tag v-if="row.shelved" size="small" effect="plain" type="info">{{ $t('alarmsView.k06d561') }}</el-tag>
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
            <router-link v-if="row.tagId && can(auth.user, 'View')" :to="trendQuery(row)" class="alarm-trend-link">{{ $t('overviewStatus.trend') }}</router-link>
          </div>
          <div class="alarm-card-actions">
            <el-button v-if="!row.ackTs && canAckAlarm(row)" size="small" type="primary" :disabled="batchRunning" :data-test="`alarm-card-ack-${row.id}`" @click="openAck(row)">{{ $t('alarmsView.kb56d9a') }}</el-button>
            <el-dropdown trigger="click">
              <el-button size="small" data-test="alarm-card-more">{{ $t('alarmUx.rowMore') }}</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item v-if="canAckAlarm(row)" :disabled="batchRunning" @click="openNote(row)">{{ $t('alarmsView.ke03614') }}</el-dropdown-item>
                  <el-dropdown-item v-if="canAckAlarm(row) && row.ruleId && !row.endTs && !row.shelved" :disabled="batchRunning" @click="openShelve(row)">{{ $t('alarmsView.k06d561') }}</el-dropdown-item>
                  <el-dropdown-item v-if="row.tagId && can(auth.user, 'View')" @click="openAlarmScreen(row)">{{ $t('alarmUx.openScreen') }}</el-dropdown-item>
                  <el-dropdown-item @click="openTimeline(row)">{{ $t('alarmsView.k9058a1') }}</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
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
        <el-tag class="alarm-level-tag" :data-level="ackDialog.alarm.level" :type="levelType(ackDialog.alarm.level)" size="small">{{ levelLabel(ackDialog.alarm.level) }}</el-tag>
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
        <el-tag class="alarm-level-tag" :data-level="timelineAlarm.level" :type="levelType(timelineAlarm.level)" size="small">{{ levelLabel(timelineAlarm.level) }}</el-tag>
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
          <el-tag :class="{ 'tag-quiet': !alarmStateType(timeline.alarm.state) }" :type="alarmStateType(timeline.alarm.state)" size="small">{{ alarmStateLabels[timeline.alarm.state ?? 'Normal'] }}</el-tag>
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
    <el-dialog v-model="screenPicker" :title="$t('alarmUx.pickScreen')" width="420px" data-test="alarm-screen-picker">
      <div class="alarm-screen-picker">
        <el-button v-for="screen in screenMatches" :key="screen.id" :data-test="`alarm-screen-${screen.id}`" @click="goScreen(screen.id)">{{ screen.name }}</el-button>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { Refresh, Search } from '@element-plus/icons-vue'
import AlarmFloodBanner from '../components/AlarmFloodBanner.vue'
import PageStatusBar, { type PageIssue } from '../components/PageStatusBar.vue'
import { useAlarmsView } from '../composables/useAlarmsView'
const { auth, canAck, deviceAreas, sourceDevices, sourceTags, sourceCatalogLoading, sourceCatalogError, alarmSource, trendQuery, alarmValueText, loadSourceCatalog, canAckAlarm, canSilencePanel, tab, alarms, HISTORY_PAGE_SIZE, livePageSize, livePage, liveTotal, historyPage, historyPageSize, historyTotal, historyCapped, shelves, summary, summaryError, loading, loadError, actionError, ackError, keyword, stateFilter, liveSort, route, router, clearTagFilter, pageIssues, onStatusAction, historyFromQuery, historyToQuery, historyRange, ackDialog, shelveDialog, timeline, timelineAlarm, timelineVisible, timelineLoading, timelineError, shelveError, selected, batchRunning, batchProgress, tableRef, keywordInputRef, focusedId, focusedIndex, focusedAlarm, levelOptions, levelFilter, categoryFilter, rangePreset, loadedHistoryRange, historyRangeDirty, batchOutcome, batchCancel, initialQuery, initialLevel, initialCategory, initialKeyword, initialPage, now, stateLabel, hasFilters, emptyText, canSelectAlarm, selectableAlarms, visibleAckable, filtersOpen, rowMenuId, pendingNotice, silenceUntil, screenPicker, screenMatches, selectedAckable, retryRows, clearSelection, resetFilters, syncUrl, onSelectionChange, isTypingTarget, editingOpen, overlayOpen, onInteractiveElement, focusRow, moveFocus, toggleSelection, onKeyboard, batchAck, confirmAndAck, applyRangePreset, rowClass, chooseRowAction, shelfSource, shelfRemaining, collectScreenTags, ensureScreenIndex, openAlarmScreen, goScreen, focusAlarm, locateFirstOut, duration, loadSummary, queryFilters, load, backgroundRefresh, schedulePushRefresh, applyPendingPush, loadHistory, searchHistory, onLivePageChange, onLiveSizeChange, onHistoryPageChange, onHistorySizeChange, onTabChange, ackVisible, silenceAnnunciators, reload, openAck, openNote, submitAck, openShelve, submitShelve, unshelve, openTimeline, retryTimeline, registerAlarmHandler, ref, alarmActionLabels, alarmCategoryLabels, alarmMessage, alarmStateLabels, alarmStateType, formatTime, levelLabel, levelType, localizedText, can, openShortcutHelp } = useAlarmsView()
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
.batch-failures { max-height: 120px; overflow: auto; margin: 4px 0; overflow-wrap: anywhere; }
.alarm-dialog-context { display: flex; align-items: flex-start; flex-wrap: wrap; gap: 8px; margin-bottom: 16px; overflow-wrap: anywhere; }
.alarm-dialog-context > strong { flex: 1; min-width: 0; color: var(--scada-text); }
.alarm-dialog-context > .muted { flex-basis: 100%; }
.alarm-filter-panel { display: flex; flex-wrap: wrap; gap: 8px; }
.alarm-pending { flex: none; margin-bottom: var(--scada-space-3); }
.alarm-silence-until { margin: 0; align-self: center; }
.alarm-screen-picker { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; }
.alarm-row-actions { display: flex; align-items: center; flex-wrap: wrap; gap: 2px 10px; }
.alarm-row-menu { display: flex; flex-direction: column; align-items: flex-start; }
.alarm-row-actions .el-button { min-height: 28px; padding: 2px 0; margin-left: 0; }
.alarm-panel :deep(.alarm-row-critical .alarm-message) { font-weight: 600; }
/* 等级只留左侧 3px 色条：整行铺色会把整张表染成红黄蓝，反而看不出哪条最急。 */
.alarm-panel :deep(.alarm-row-critical td:first-child) { box-shadow: inset 3px 0 0 var(--scada-alarm-critical); }
.alarm-panel :deep(.alarm-row-warning td:first-child) { box-shadow: inset 3px 0 0 var(--scada-alarm-warning); }
.alarm-panel :deep(.alarm-row-info td:first-child) { box-shadow: inset 3px 0 0 var(--scada-alarm-info); }
.alarm-panel :deep(.el-table .el-table__cell) { padding: 5px 0; }
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
.alarm-category { padding: 0 6px; border-radius: 4px; background: var(--scada-panel-muted); color: var(--scada-text-secondary); font-size: 11px; line-height: 18px; }
.alarm-trend-link { color: var(--scada-primary); white-space: nowrap; }
.alarm-trend-link:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: 2px; }
/* 触控设备：文字链接只有一行高（26×23），手指点不中；撑开点击区，视觉尺寸不变 */
@media (any-pointer: coarse) {
  .alarm-trend-link { display: inline-flex; align-items: center; min-height: 32px; padding: 0 8px; margin: 0 -8px; }
}

.flags {
  display: flex;
  gap: 2px;
  margin-top: 2px;
}
.flags :deep(.el-tag) { height: 17px; padding: 0 5px; font-size: 11px; line-height: 15px; }
.flags :deep(.el-tag.flag-muted) {
  --el-tag-bg-color: transparent;
  --el-tag-border-color: transparent;
  --el-tag-text-color: var(--scada-text-muted);
  padding: 0 2px;
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

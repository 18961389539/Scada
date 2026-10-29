<template>
  <div class="page">
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="layouts-load-error" />
    <el-button v-if="loadError" data-test="layouts-retry" @click="load()">{{ $t('common.retry') }}</el-button>
    <el-alert v-if="stationsError" :title="stationsError" type="error" :closable="false" show-icon data-test="stations-load-error" />
    <el-alert v-if="screensError" :title="screensError" type="warning" :closable="false" show-icon data-test="layouts-screens-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="layouts-action-error" />
    <el-button v-if="deleteConflict" data-test="layouts-conflict-reload" :loading="loading" @click="reloadAfterDeleteConflict">{{ $t('layoutSafety.reloadLists') }}</el-button>
    <el-alert v-if="recoveryError" :title="recoveryError" type="warning" :closable="false" show-icon data-test="layout-recovery-error" />
    <section v-if="recoveries.length" class="panel local-drafts" data-test="layout-local-drafts">
      <strong>{{ $t('layoutRecovery.available', [recoveries.length]) }}</strong><p class="tiny">{{ $t('layoutRecovery.help') }}</p>
      <div class="local-draft-list"><div v-for="draft in recoveries" :key="draft.id" class="local-draft-row" data-test="layout-recovery-item"><span>{{ draft.content.name || $t('layoutRecovery.unnamed') }} · {{ formatTime(draft.savedAt) }}</span><div><el-button size="small" :disabled="visible || restoring" data-test="layout-recover" @click="restoreLocalDraft(draft)">{{ $t('layoutRecovery.restore') }}</el-button><el-button size="small" :disabled="visible || restoring" data-test="layout-recovery-discard" @click="discardLocalDraft(draft)">{{ $t('layoutRecovery.discard') }}</el-button></div></div></div>
    </section>
    <div class="panel local-workstation-summary" data-test="local-workstation-summary">
      <div class="local-summary-info">
        <strong>{{ $t('layoutCatalog.thisWorkstation') }} · {{ localStation?.name || localName || $t('layoutsView.ke6c8c4') }}</strong>
        <template v-if="localStation && !stationsError && !loadError">
          <span>{{ $t('layoutCatalog.assignedLayout') }}：{{ stationLayoutLabel(localStation.layoutId) }}<template v-if="localLayout?.publishedRevision != null"> · V{{ localLayout.publishedRevision }}</template></span>
          <el-tag size="small" :type="localStation.allowControl ? 'success' : 'warning'">{{ $t(localStation.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag>
        </template>
        <span class="local-summary-message" :class="{ warning: !localReady }">{{ localSummaryMessage }}</span>
      </div>
      <div class="local-summary-actions">
        <el-button type="primary" :disabled="!localReady" data-test="layout-open-local" @click="router.push('/console')">{{ $t('layoutCatalog.enterRuntime') }}</el-button>
        <el-button v-if="localStation && !stationsError" data-test="layout-edit-local" @click="editStation(localStation)">{{ $t('layoutCatalog.editAssignment') }}</el-button>
        <el-button v-else @click="focusStations">{{ $t('layoutCatalog.configureWorkstation') }}</el-button>
      </div>
    </div>
    <div class="panel" v-loading="loading">
    <div class="toolbar"><h3>{{ $t('layoutsView.k0dd591') }}</h3><span class="spacer" />
      <el-button type="primary" data-test="layout-new" :disabled="!!loadError" @click="edit()">{{ $t('layoutsView.kd6308f') }}</el-button><el-button :loading="loading" @click="load()">{{ $t('layoutsView.k38108e') }}</el-button></div>
    <div class="layout-filters">
      <el-input v-model="layoutSearch" clearable :placeholder="$t('layoutCatalog.search')" :aria-label="$t('layoutCatalog.search')" data-test="layouts-search" />
      <el-select v-model="statusFilter" :aria-label="$t('layoutCatalog.statusFilter')" data-test="layouts-status-filter"><el-option v-for="value in ['all', 'published', 'unpublished', 'changes']" :key="value" :value="value" :label="$t(`layoutCatalog.status_${value}`)" /></el-select>
      <el-select v-model="usageFilter" :disabled="!!stationsError" :aria-label="$t('layoutCatalog.usageFilter')" data-test="layouts-usage-filter"><el-option v-for="value in ['all', 'used', 'unused']" :key="value" :value="value" :label="$t(`layoutCatalog.usage_${value}`)" /></el-select>
      <span class="tiny" data-test="layouts-result-count">{{ $t('layoutCatalog.resultCount', [filteredLayouts.length, layouts.length]) }}</span>
      <el-button v-if="layoutSearch || statusFilter !== 'all' || usageFilter !== 'all'" size="small" link data-test="layouts-reset-filters" @click="resetFilters">{{ $t('layoutCatalog.resetFilters') }}</el-button>
    </div>
    <div v-if="isMobile" class="catalog-cards" data-test="layout-cards">
      <p v-if="!filteredLayouts.length" class="card-empty" role="status" data-test="layout-cards-empty">{{ loadError && !layouts.length ? loadError : layouts.length ? $t('layoutCatalog.noMatches') : $t('layoutsView.kd0ba6b') }}</p>
      <article v-for="row in filteredLayouts" :key="row.id" class="catalog-card" data-test="layout-card" :data-layout-id="row.id" :aria-label="row.name">
        <h4>{{ row.name }}</h4>
        <div class="layout-state" data-test="layout-lifecycle-status"><el-tag :type="row.publishedRevision != null ? 'success' : 'info'" size="small">{{ row.publishedRevision != null ? $t('layoutLifecycle.publishedVersion', [row.publishedRevision]) : $t('layoutLifecycle.unpublished') }}</el-tag><el-tag v-if="row.publishedRevision != null && row.hasDraftChanges" type="warning" size="small">{{ $t('layoutLifecycle.draftChanges') }}</el-tag><span v-if="row.publishedName && row.publishedName !== row.name" class="tiny">{{ $t('layoutLifecycle.runtimeName', [row.publishedName]) }}</span></div>
        <div class="card-preview"><span class="tiny">{{ $t('layoutCatalog.draftStructure') }} · {{ monitorSummary(row) }}</span><LayoutThumbnail :document="row.document" :screen-names="screenNames" /></div>
        <span v-if="stationsError" class="tiny" data-test="layout-usage">{{ $t('layoutImprovements.usageUnknown') }}</span>
        <details v-else class="card-usage" data-test="layout-card-usage">
          <summary data-test="layout-usage">{{ $t('layoutImprovements.workstationCount', [layoutStations(row.id).length]) }}</summary>
          <p v-if="!layoutStations(row.id).length" class="tiny">{{ $t('layoutCatalog.noWorkstations') }}</p>
          <div v-for="station in layoutStations(row.id)" :key="station.id" class="usage-station"><div><strong>{{ station.name }}</strong> <el-tag v-if="localStation?.id === station.id" size="small" type="success">{{ $t('layoutImprovements.thisStation') }}</el-tag><p v-if="station.description" class="tiny">{{ station.description }}</p></div><el-tag size="small" :type="station.allowControl ? 'success' : 'warning'">{{ $t(station.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag></div>
        </details>
        <p class="tiny card-updated">r{{ row.revision }} · {{ row.updatedBy }} · {{ formatTime(row.updatedTs) }}</p>
        <LayoutCatalogActions :ref="(instance: unknown) => rememberPopup(`menu-${row.id}`, instance)" :layout="row" mobile @open="router.push(`/console/${row.id}`)" @edit="edit(row)" @copy="edit(row, true)" @publish="lifecycle?.openPublish(row.id)" @history="lifecycle?.openHistory(row.id)" @remove="remove(row)" />
      </article>
    </div>
    <el-table v-else :data="filteredLayouts" size="small" data-test="layouts" :empty-text="loadError && !layouts.length ? loadError : layouts.length ? $t('layoutCatalog.noMatches') : $t('layoutsView.kd0ba6b')">
      <el-table-column :label="$t('layoutsView.k1be7ae')" min-width="280"><template #default="{ row }">
        <strong class="layout-name">{{ row.name }}</strong><span class="tiny">{{ $t('layoutCatalog.draftStructure') }}</span>
        <LayoutThumbnail :document="row.document" :screen-names="screenNames" compact />
        <span class="tiny monitor-summary">{{ monitorSummary(row) }}</span>
      </template></el-table-column>
      <el-table-column :label="$t('layoutLifecycle.status')" min-width="160"><template #default="{ row }"><div class="layout-state" data-test="layout-lifecycle-status"><el-tag :type="row.publishedRevision != null ? 'success' : 'info'" size="small">{{ row.publishedRevision != null ? $t('layoutLifecycle.publishedVersion', [row.publishedRevision]) : $t('layoutLifecycle.unpublished') }}</el-tag><el-tag v-if="row.publishedRevision != null && row.hasDraftChanges" type="warning" size="small">{{ $t('layoutLifecycle.draftChanges') }}</el-tag><span v-if="row.publishedName && row.publishedName !== row.name" class="tiny">{{ $t('layoutLifecycle.runtimeName', [row.publishedName]) }}</span></div></template></el-table-column>
      <el-table-column :label="$t('layoutImprovements.usage')" min-width="115"><template #default="{ row }">
        <span v-if="stationsError" class="tiny" data-test="layout-usage">{{ $t('layoutImprovements.usageUnknown') }}</span>
        <el-popover v-else :ref="(instance: unknown) => rememberPopup(`usage-${row.id}`, instance)" placement="bottom" :width="320" :popper-style="{ maxWidth: 'calc(100vw - 24px)' }" trigger="click" @keydown.esc.stop="closePopups">
          <template #reference><el-button link size="small" data-test="layout-usage" :aria-label="$t('layoutCatalog.usageFor', [row.name])">{{ $t('layoutImprovements.workstationCount', [layoutStations(row.id).length]) }}</el-button></template>
          <div class="layout-usage-detail" :data-test="`layout-usage-detail-${row.id}`"><strong>{{ $t('layoutCatalog.usageFor', [row.name]) }}</strong><p v-if="!layoutStations(row.id).length" class="tiny">{{ $t('layoutCatalog.noWorkstations') }}</p>
            <div v-for="station in layoutStations(row.id)" :key="station.id" class="usage-station"><div><strong>{{ station.name }}</strong> <el-tag v-if="localStation?.id === station.id" size="small" type="success">{{ $t('layoutImprovements.thisStation') }}</el-tag><p v-if="station.description" class="tiny">{{ station.description }}</p></div><el-tag size="small" :type="station.allowControl ? 'success' : 'warning'">{{ $t(station.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag></div>
          </div>
        </el-popover>
      </template></el-table-column>
      <el-table-column :label="$t('layoutsView.kc9c775')" min-width="150"><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}<br />{{ formatTime(row.updatedTs) }}</span></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kf3ea6d')" min-width="245"><template #default="{ row }"><LayoutCatalogActions :ref="(instance: unknown) => rememberPopup(`menu-${row.id}`, instance)" :layout="row" @open="router.push(`/console/${row.id}`)" @edit="edit(row)" @copy="edit(row, true)" @publish="lifecycle?.openPublish(row.id)" @history="lifecycle?.openHistory(row.id)" @remove="remove(row)" /></template></el-table-column>
    </el-table>
  </div>
  <div ref="stationsPanel" class="panel station-panel" tabindex="-1">
    <div class="toolbar"><h3>{{ $t('layoutsView.k90ec9a') }}</h3><el-tag size="small" :type="localInvalid ? 'danger' : localName ? 'success' : 'info'" data-test="local-station">{{ $t('layoutsView.kfedb9d', [localName || $t('layoutsView.ke6c8c4')]) }}</el-tag>
      <el-button v-if="localName" size="small" link @click="setLocal(null)">{{ $t('layoutsView.kb398ea') }}</el-button><span class="spacer" />
      <el-button data-test="station-new" @click="editStation()">{{ $t('layoutsView.kb321d8') }}</el-button></div>
    <p class="page-desc">{{ $t('layoutsView.kc0704a') }}</p>
    <el-alert v-if="localInvalid" :title="$t('layoutImprovements.localInvalid')" type="warning" :closable="false" show-icon data-test="local-station-invalid" />
    <div v-if="isMobile" class="catalog-cards" data-test="station-cards">
      <p v-if="!stations.length" class="card-empty" role="status" data-test="station-cards-empty">{{ stationsError || loadError || $t('layoutsView.kfe2078') }}</p>
      <article v-for="row in stations" :key="row.id" class="catalog-card" data-test="station-card" :data-station-id="row.id" :aria-label="row.name">
        <div class="card-heading"><h4>{{ row.name }}</h4><el-tag v-if="isLocalWorkstation(row)" size="small" type="success">{{ $t('layoutImprovements.thisStation') }}</el-tag></div>
        <p v-if="row.description" class="card-description">{{ row.description }}</p>
        <dl class="card-fields"><dt>{{ $t('layoutsView.kb0961e') }}</dt><dd>{{ stationLayoutLabel(row.layoutId) }}</dd><dt>{{ $t('layoutsView.k223826') }}</dt><dd><el-tag size="small" :type="row.allowControl ? 'success' : 'warning'">{{ row.allowControl ? $t('layoutsView.k4c0c0a') : $t('layoutsView.ka18e06') }}</el-tag></dd></dl>
        <div class="station-card-actions"><el-button size="small" type="primary" @click="editStation(row)">{{ $t('layoutsView.ka7f814') }}</el-button><el-button size="small" :disabled="isLocalWorkstation(row)" :data-test="`station-local-${row.name}`" @click="setLocal(row)">{{ $t('layoutsView.kbffbf3') }}</el-button><el-button size="small" type="danger" plain @click="removeStation(row)">{{ $t('layoutsView.k3755f5') }}</el-button></div>
      </article>
    </div>
    <el-table v-else :data="stations" size="small" data-test="stations" :empty-text="(stationsError || loadError) && !stations.length ? (stationsError || loadError) : $t('layoutsView.kfe2078')">
      <el-table-column :label="$t('layoutsView.k1be7ae')" min-width="140"><template #default="{ row }">{{ row.name }} <el-tag v-if="isLocalWorkstation(row)" size="small" type="success">{{ $t('layoutImprovements.thisStation') }}</el-tag></template></el-table-column>
      <el-table-column prop="description" :label="$t('layoutsView.k26670d')" min-width="160" />
      <el-table-column :label="$t('layoutsView.kb0961e')" min-width="140"><template #default="{ row }">{{ stationLayoutLabel(row.layoutId) }}</template></el-table-column>
      <el-table-column :label="$t('layoutsView.k223826')" width="90"><template #default="{ row }"><el-tag size="small" :type="row.allowControl ? 'success' : 'warning'">{{ row.allowControl ? $t('layoutsView.k4c0c0a') : $t('layoutsView.ka18e06') }}</el-tag></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kf3ea6d')" width="230"><template #default="{ row }">
        <el-button size="small" :data-test="`station-local-${row.name}`" @click="setLocal(row)">{{ $t('layoutsView.kbffbf3') }}</el-button>
        <el-button size="small" @click="editStation(row)">{{ $t('layoutsView.ka7f814') }}</el-button><el-button size="small" type="danger" @click="removeStation(row)">{{ $t('layoutsView.k3755f5') }}</el-button>
      </template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="visible" :title="form.id ? $t('layoutsView.kf7d900') : $t('layoutsView.kd6308f')" class="layout-editor" :class="{ 'mobile-layout-editor': isMobile }" :before-close="closeLayout" data-test="layout-dialog" @keydown="editorShortcut">
    <el-alert v-if="layoutError" :title="layoutError" type="error" :closable="false" show-icon data-test="layout-save-error" />
    <el-alert v-if="recoveryError" :title="recoveryError" type="warning" :closable="false" show-icon />
    <el-button v-if="layoutConflict" :disabled="saving" data-test="layout-draft-reload" @click="reloadSavedDraft">{{ $t('layoutLifecycle.reloadDraft') }}</el-button>
    <p v-if="recoverySavedAt && layoutDirty" class="tiny" data-test="layout-local-saved">{{ $t('layoutRecovery.savedAt', [formatTime(recoverySavedAt)]) }}</p>
    <el-form label-width="80px" size="small" inline class="editor-primary-form" @submit.prevent>
      <el-form-item :label="$t('layoutsView.k1be7ae')"><el-input v-model="form.name" maxlength="100" style="width: 220px" data-test="layout-name" @change="recordHistory" /></el-form-item>
      <el-form-item class="editor-primary-actions"><el-button data-test="layout-undo" :disabled="historyIndex <= 0" @click="undo">{{ $t('layoutImprovements.undo') }}</el-button><el-button data-test="layout-redo" :disabled="historyIndex >= history.length - 1" @click="redo">{{ $t('layoutImprovements.redo') }}</el-button><el-button data-test="layout-runtime-preview" :disabled="!!draftShapeError || !!layoutIssues.length" @click="runtimePreviewVisible = true">{{ $t('layoutPreview.runtimePreview') }}</el-button></el-form-item>
    </el-form>
    <details class="editor-layout-settings" :open="!isMobile || layoutSettingsOpen" data-test="layout-settings" @toggle="layoutSettingsOpen = ($event.target as HTMLDetailsElement).open">
      <summary class="mobile-section-summary">{{ $t('layoutGeometry.layoutSettings') }} <el-tag v-if="copiedFrom || legacyConverted" size="small" type="warning">{{ $t('layoutGeometry.hasNotice') }}</el-tag></summary>
      <el-alert :title="$t('layoutLifecycle.draftHelp')" type="info" :closable="false" show-icon />
      <el-alert v-if="copiedFrom" :title="$t('layoutCatalog.copyHelp', [copiedFrom])" type="info" :closable="false" data-test="layout-copy-help" />
      <el-alert v-if="legacyConverted" :title="$t('layoutPreview.legacyConverted', [legacyConverted])" type="warning" :closable="false" show-icon data-test="layout-banner-converted" />
      <el-form size="small" inline @submit.prevent><el-form-item><el-select v-model="templateKey" class="template-select" :aria-label="$t('layoutCatalog.template')" data-test="layout-template"><el-option v-for="key in layoutTemplateKeys" :key="key" :value="key" :label="$t(`layoutCatalog.template_${key}`)" /></el-select><el-button data-test="layout-preset" @click="replacePreset">{{ $t('layoutCatalog.applyTemplate') }}</el-button><el-button data-test="layout-add-monitor" :disabled="form.document.monitors.length >= 8" @click="addMonitor()">{{ $t('layoutsView.k968c70') }} ({{ form.document.monitors.length }}/8)</el-button></el-form-item></el-form>
      <p class="drag-hint editor-shortcuts" data-test="layout-editor-shortcuts">{{ $t('layoutEditing.shortcuts') }}</p>
      <details class="template-preview" data-test="layout-template-preview"><summary>{{ $t('layoutCatalog.templatePreview') }} · {{ $t(`layoutCatalog.template_${templateKey}`) }}</summary><p class="tiny">{{ $t('layoutCatalog.templateHelp') }}</p><LayoutThumbnail :document="templateDocument" :screen-names="screenNames" /></details>
      <p class="drag-hint">{{ $t('layoutEditing.monitorOrderHelp') }}</p>
    </details>
    <el-alert v-if="!screensError && !screens.length && !isMobile" :title="$t('layoutImprovements.noPublishedScreens')" type="warning" :closable="false" show-icon />
    <details v-if="layoutIssues.length" class="layout-issues" :open="!isMobile || issuesOpen" role="status" data-test="layout-region-issues" @toggle="issuesOpen = ($event.target as HTMLDetailsElement).open">
      <summary><strong>{{ $t('layoutLifecycle.draftIssues', [layoutIssues.length]) }}</strong></summary>
      <button v-for="(issue, index) in layoutIssues" :key="index" class="issue-link" type="button" data-test="layout-issue" @click="focusIssue(issue)">{{ issue.message }}</button>
    </details>
    <el-tabs v-model="monitorTab" type="card">
      <el-tab-pane v-for="(m, mi) in form.document.monitors" :key="mi" :name="String(mi)">
        <template #label><span>{{ m.name || $t('layoutsView.k47eff4', [mi + 1]) }} <el-tag v-if="monitorIssues(m, mi).length" type="danger" size="small">{{ monitorIssues(m, mi).length }}</el-tag></span></template>
        <details class="monitor-settings" :open="!isMobile || monitorSettingsOpen" data-test="layout-monitor-settings" @toggle="monitorTab === String(mi) && (monitorSettingsOpen = ($event.target as HTMLDetailsElement).open)">
        <summary class="mobile-section-summary">{{ $t('layoutGeometry.monitorSettings') }} · {{ $t('layoutGeometry.gridSummary', [m.columns, m.rows, m.regions.length]) }}</summary>
        <div class="toolbar monitor-toolbar">
          <label>{{ $t('layoutsView.k1be7ae') }} <el-input v-model="m.name" size="small" maxlength="40" style="width: 140px" data-test="monitor-name" @change="recordHistory" /></label>
          <label>{{ $t('layoutsView.kad3ee6') }} <el-input-number :key="`columns-${gridControlVersion}`" :model-value="m.columns" :min="1" :max="24" :disabled="!!pendingGrid" size="small" data-test="monitor-columns" @change="changeGridSize(m, $event, m.rows)" /></label>
          <label>{{ $t('layoutsView.k2a2b77') }} <el-input-number :key="`rows-${gridControlVersion}`" :model-value="m.rows" :min="1" :max="24" :disabled="!!pendingGrid" size="small" data-test="monitor-rows" @change="changeGridSize(m, m.columns, $event)" /></label>
          <label>{{ $t('layoutPreview.targetWidth') }} <el-input-number :model-value="m.previewWidth ?? 1920" :min="320" :max="7680" size="small" data-test="monitor-preview-width" @change="setPreviewSize(m, $event, m.previewHeight ?? 1080)" /></label>
          <label>{{ $t('layoutPreview.targetHeight') }} <el-input-number :model-value="m.previewHeight ?? 1080" :min="240" :max="4320" size="small" data-test="monitor-preview-height" @change="setPreviewSize(m, m.previewWidth ?? 1920, $event)" /></label>
          <el-button size="small" :data-test="`layout-add-region-${mi}`" :disabled="m.regions.length >= 16 || !freeCell(m)" @click="addRegion(m)">{{ $t('layoutsView.k39b1a6') }} ({{ m.regions.length }}/16)</el-button>
          <el-button size="small" data-test="layout-paste-region" :disabled="!!pasteIssue(m, regionClipboard)" :title="pasteIssue(m, regionClipboard)" @click="pasteRegion(m)">{{ $t('layoutEditing.paste') }}</el-button>
          <el-button size="small" data-test="layout-copy-monitor" :disabled="form.document.monitors.length >= 8" @click="duplicateMonitor(mi)">{{ $t('layoutCatalog.copyMonitor') }}</el-button>
          <el-button size="small" data-test="layout-monitor-previous" :disabled="mi === 0" @click="moveMonitor(mi, -1)">{{ $t('layoutEditing.monitorPrevious') }}</el-button>
          <el-button size="small" data-test="layout-monitor-next" :disabled="mi === form.document.monitors.length - 1" @click="moveMonitor(mi, 1)">{{ $t('layoutEditing.monitorNext') }}</el-button>
          <el-button v-if="form.document.monitors.length > 1" size="small" type="danger" link data-test="layout-remove-monitor" @click="removeMonitor(mi)">{{ $t('layoutsView.k33160a') }}</el-button>
        </div>
        <p v-if="regionClipboard" class="drag-hint" data-test="layout-region-clipboard" role="status">{{ $t('layoutEditing.copied', [regionClipboard.id]) }}<span v-if="pasteIssue(m, regionClipboard)"> · {{ pasteIssue(m, regionClipboard) }}</span></p>
        <p v-if="!freeCell(m) && m.regions.length < 16" class="drag-hint">{{ $t('layoutImprovements.noSpace') }}</p>
        </details>
        <div v-if="monitorTab === String(mi)" class="editor">
          <div class="canvas-panel">
            <div class="canvas-toolbar"><strong>{{ $t('layoutImprovements.preview') }}</strong><label>{{ $t('layoutImprovements.aspectRatio') }} <el-select v-model="previewRatio" size="small" data-test="layout-preview-ratio"><el-option v-for="ratio in ['16:9', '16:10', '4:3', '9:16']" :key="ratio" :label="ratio" :value="ratio" /></el-select></label></div>
            <p id="layout-keyboard-help" class="drag-hint keyboard-help">{{ $t('layoutImprovements.keyboardHint') }}</p>
            <p v-if="!isMobile" class="drag-hint">{{ $t('layoutPreview.structureHelp') }}</p>
            <div class="preview-frame"><div class="preview-console" :style="previewFrameStyle(m)" data-test="layout-preview-shell">
            <div class="preview-topbar">{{ $t('layoutPreview.consoleToolbar') }}</div>
            <div class="preview-alarm" data-test="layout-fixed-banner">{{ $t('layoutPreview.fixedAlarm') }}</div>
            <div class="preview" :style="gridStyle(m)" data-test="layout-preview" :aria-label="$t('layoutImprovements.preview')">
            <div
              v-for="(r, ri) in m.regions"
              :key="ri"
              :class="[`k-${r.kind}`, { 'is-selected': selectedRegionIndex === ri, 'is-overlap': regionOverlaps(m, ri), 'is-out': outOfBounds(m, r), 'is-invalid': r.kind === 'screen' && !validScreen(r), 'is-dragging': drag?.region === r }]"
              :style="regionStyle(r)"
              data-test="layout-region"
              :data-region-id="r.id"
              role="button" tabindex="0" :aria-pressed="selectedRegionIndex === ri" :aria-label="$t('layoutImprovements.regionLabel', [r.id, r.title || kinds[r.kind]])" aria-describedby="layout-keyboard-help"
              @focus="selectedRegionIndex = ri" @click="selectedRegionIndex = ri" @pointerdown.stop="startDrag($event, m, r, ri)" @keydown="regionKeydown($event, m, r, ri)"
            >
              <span class="region-id">{{ r.id }}</span><strong>{{ r.title || kinds[r.kind] }}</strong><small v-if="r.kind === 'screen'">{{ screenLabel(r) }}</small>
              <span class="resize" data-test="layout-region-resize" aria-hidden="true" @pointerdown.stop="startResize($event, m, r, ri)" />
            </div>
            </div></div></div>
            <div class="region-picker" :aria-label="$t('layoutImprovements.regions')"><el-button v-for="(r, ri) in m.regions" :key="ri" size="small" :type="selectedRegionIndex === ri ? 'primary' : 'default'" :aria-pressed="selectedRegionIndex === ri" :data-test="`layout-pick-region-${ri}`" @click="selectRegion(ri)">{{ r.id || '?' }}</el-button></div>
            <LayoutGeometryTools :monitor="m" :selected-index="selectedRegionIndex" @apply="applyGeometry(m, $event)" />
          </div>
          <details class="region-properties" :open="!isMobile || regionPropertiesOpen" :aria-label="$t('layoutImprovements.properties')" data-test="layout-region-properties" @toggle="regionPropertiesOpen = ($event.target as HTMLDetailsElement).open">
            <summary class="mobile-section-summary">{{ $t('layoutImprovements.properties') }} · {{ selectedRegion?.id || '?' }}</summary>
            <div class="region-properties-body">
            <template v-if="selectedRegion">
              <h4 v-if="!isMobile">{{ $t('layoutImprovements.properties') }} · {{ selectedRegion.id || '?' }}</h4>
              <p class="tiny" data-test="region-share">{{ $t('layoutCatalog.regionShare', [Math.round(selectedRegion.colSpan / m.columns * 100), Math.round(selectedRegion.rowSpan / m.rows * 100)]) }}</p>
              <el-form label-position="top" size="small">
                <el-form-item :label="$t('layoutsView.k9f42da')"><el-input v-model="selectedRegion.id" maxlength="40" data-test="region-id" @change="recordHistory" /></el-form-item>
                <el-form-item :label="$t('layoutsView.ke4e46c')"><el-select v-model="selectedRegion.kind" data-test="region-kind" @change="recordHistory"><el-option v-for="(label, k) in editableKinds" :key="k" :value="k" :label="label" /></el-select></el-form-item>
                <el-form-item v-if="selectedRegion.kind === 'screen'" :label="$t('layoutsView.kbfffd0')" :error="!screensError && !validScreen(selectedRegion) ? $t('layoutImprovements.invalidScreen') : ''">
                  <el-select v-model="selectedRegion.screenId" filterable data-test="region-screen" :disabled="!!screensError" @change="recordHistory"><el-option v-if="selectedRegion.screenId && !validScreen(selectedRegion)" :value="selectedRegion.screenId" :label="screenLabel(selectedRegion)" disabled /><el-option v-for="s in screens" :key="s.id" :value="s.id" :label="s.name" /></el-select>
                </el-form-item>
                <el-form-item :label="$t('layoutsView.k748d7d')"><el-input v-model="selectedRegion.title" maxlength="60" data-test="region-title" @change="recordHistory" /></el-form-item>
                <div class="position-fields">
                  <el-form-item :label="$t('layoutsView.k2a2b77')"><el-input-number v-model="selectedRegion.row" :min="1" :max="Math.max(m.rows, selectedRegion.row || 1)" controls-position="right" data-test="region-row" @change="recordHistory" /></el-form-item>
                  <el-form-item :label="$t('layoutsView.kad3ee6')"><el-input-number v-model="selectedRegion.col" :min="1" :max="Math.max(m.columns, selectedRegion.col || 1)" controls-position="right" data-test="region-col" @change="recordHistory" /></el-form-item>
                  <el-form-item :label="$t('layoutImprovements.rowSpan')"><el-input-number v-model="selectedRegion.rowSpan" :min="1" :max="Math.max(1, m.rows - selectedRegion.row + 1, selectedRegion.rowSpan || 1)" controls-position="right" data-test="region-row-span" @change="recordHistory" /></el-form-item>
                  <el-form-item :label="$t('layoutImprovements.colSpan')"><el-input-number v-model="selectedRegion.colSpan" :min="1" :max="Math.max(1, m.columns - selectedRegion.col + 1, selectedRegion.colSpan || 1)" controls-position="right" data-test="region-col-span" @change="recordHistory" /></el-form-item>
                </div>
              </el-form>
              <div class="region-actions">
                <el-button size="small" data-test="layout-copy-region" @click="copyRegion">{{ $t('layoutEditing.copy') }}</el-button>
                <el-button size="small" data-test="layout-duplicate-region" :disabled="!!pasteIssue(m, selectedRegion)" :title="pasteIssue(m, selectedRegion)" @click="duplicateRegion(m)">{{ $t('layoutEditing.duplicate') }}</el-button>
                <el-button type="danger" plain size="small" :disabled="m.regions.length <= 1" :title="m.regions.length <= 1 ? $t('layoutEditing.keepRegion') : ''" data-test="layout-remove-region" @click="removeRegion(m, selectedRegionIndex)">{{ $t('layoutImprovements.deleteRegion') }}</el-button>
              </div>
            </template>
            <p v-else class="drag-hint">{{ $t('layoutImprovements.selectRegion') }}</p>
            </div>
          </details>
        </div>
      </el-tab-pane>
    </el-tabs>
    <template #footer><el-button :disabled="saving" @click="closeLayout()">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" data-test="layout-save" :loading="saving" :disabled="!!draftShapeError || !form.name.trim() || layoutConflict" @click="save()">{{ $t('layoutLifecycle.saveDraft') }}</el-button><p v-if="draftShapeError" class="draft-shape-error">{{ draftShapeError }}</p></template>
  </el-dialog>

  <el-dialog :model-value="!!pendingGrid" :title="$t('layoutGeometry.resizeTitle')" class="grid-resize-dialog" append-to-body data-test="layout-grid-resize" @update:model-value="!$event && cancelGridResize()">
    <template v-if="pendingGrid">
      <p>{{ $t('layoutGeometry.resizeAffected', [pendingGrid.monitor.columns, pendingGrid.monitor.rows, pendingGrid.columns, pendingGrid.rows]) }}</p>
      <ul class="resize-affected" data-test="grid-resize-affected"><li v-for="(id, index) in pendingGrid.affected" :key="index">{{ id }}</li></ul>
      <p class="tiny">{{ $t('layoutGeometry.resizeHelp') }}</p>
      <LayoutThumbnail v-if="pendingGridPreview" :document="pendingGridPreview" :screen-names="screenNames" :limit="1" data-test="grid-resize-preview" />
      <el-alert v-else :title="$t('layoutGeometry.fitBlocked')" type="warning" :closable="false" show-icon />
    </template>
    <template #footer><el-button data-test="grid-resize-cancel" @click="cancelGridResize">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="warning" plain data-test="grid-resize-keep" @click="applyGridResize(false)">{{ $t('layoutGeometry.keepPositions') }}</el-button><el-button type="primary" data-test="grid-resize-fit" :disabled="!pendingGrid?.fitted" @click="applyGridResize(true)">{{ $t('layoutGeometry.fitAndResize') }}</el-button></template>
  </el-dialog>

  <el-dialog v-model="stationVisible" :title="stationForm.id ? $t('layoutsView.k460078') : $t('layoutsView.kb321d8')" class="station-editor" :before-close="closeStation">
    <el-alert v-if="stationError" :title="stationError" type="error" :closable="false" show-icon data-test="station-save-error" />
    <el-button v-if="stationConflict" :disabled="stationSaving" data-test="station-conflict-reload" @click="reloadStation">{{ $t('layoutSafety.reloadStation') }}</el-button>
    <el-form label-width="90px" size="small">
      <el-form-item :label="$t('layoutsView.k1be7ae')"><el-input v-model="stationForm.name" maxlength="60" :placeholder="$t('layoutsView.k803edb')" data-test="station-name" /></el-form-item>
      <el-form-item :label="$t('layoutsView.k26670d')"><el-input v-model="stationForm.description" maxlength="200" show-word-limit /></el-form-item>
      <el-form-item :label="$t('layoutsView.kb0961e')"><el-select v-model="stationForm.layoutId" clearable data-test="station-layout"><el-option v-if="stationForm.layoutId != null && !publishedLayouts.some(l => l.id === stationForm.layoutId)" :value="stationForm.layoutId" :label="stationLayoutLabel(stationForm.layoutId)" disabled /><el-option v-for="l in publishedLayouts" :key="l.id" :value="l.id" :label="l.publishedName || l.name" /></el-select><p class="station-publish-help">{{ $t('layoutLifecycle.stationPublishedOnly') }}</p><el-alert v-if="stationForm.layoutId != null && !publishedLayouts.some(l => l.id === stationForm.layoutId)" :title="$t('layoutPreview.legacyBinding')" type="warning" :closable="false" show-icon data-test="station-unpublished-binding" /></el-form-item>
      <el-form-item :label="$t('layoutsView.k54d7df')"><el-switch v-model="stationForm.allowControl" data-test="station-control" /></el-form-item>
    </el-form>
    <template #footer><el-button :disabled="stationSaving" @click="closeStation()">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" :loading="stationSaving" :disabled="stationConflict" data-test="station-save" @click="saveStation()">{{ $t('layoutsView.kfadf24') }}</el-button></template>
  </el-dialog>
  <LayoutVersionHistory ref="lifecycle" @changed="load" />
  <el-dialog v-model="runtimePreviewVisible" :title="$t('layoutPreview.runtimePreview')" class="layout-runtime-dialog" append-to-body destroy-on-close data-test="layout-runtime-dialog">
    <LayoutRuntimePreview v-if="runtimePreviewVisible" :document="form.document" :name="form.name" :initial-monitor="Number(monitorTab)" />
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { onBeforeRouteLeave, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { getLocalWorkstation, hmiLayoutApi, isLocalWorkstation, setLocalWorkstation, WORKSTATION_ID_KEY, type HmiLayout, type HmiLayoutDocument, type HmiMonitor, type HmiRegion, type HmiWorkstation, type RegionKind } from '../api/hmiLayouts'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { useActionError } from '../composables/useActionError'
import type { ScreenSummary } from '../screens/model'
import { tr } from '../i18n'
import { formatTime } from '../utils'
import LayoutVersionHistory from '../components/console/LayoutVersionHistory.vue'
import LayoutRuntimePreview from '../components/console/LayoutRuntimePreview.vue'
import { contentMonitor, previewSize } from '../components/console/layoutGeometry'
import LayoutThumbnail from '../components/console/LayoutThumbnail.vue'
import LayoutCatalogActions from '../components/console/LayoutCatalogActions.vue'
import LayoutGeometryTools from '../components/console/LayoutGeometryTools.vue'
import { copiedRegionId, findRegionSpace, fitRegionsToGrid, geometryChanged, type RegionGeometry } from '../components/console/layoutEditing'
import { isMobile } from '../mobile'
import { createLayoutTemplate, layoutTemplateKeys, type LayoutTemplateKey } from '../components/console/layoutTemplates'
import { useAuthStore } from '../stores/auth'
import { layoutContentKey, normalizeDraftContent, readLocalLayoutDrafts, removeLocalLayoutDraft, writeLocalLayoutDraft, type LayoutDraftContent, type LocalLayoutDraft } from '../components/console/layoutRecovery'

const router = useRouter()
const popups = new Map<string, { handleClose?: () => void; hide?: () => void }>()
function rememberPopup(key: string, instance: unknown): void { if (instance) popups.set(key, instance as { handleClose?: () => void; hide?: () => void }); else popups.delete(key) }
function closePopups(): void { for (const popup of popups.values()) { popup.handleClose?.(); popup.hide?.() } }
onMounted(() => window.addEventListener('resize', closePopups))
onBeforeUnmount(() => { window.removeEventListener('resize', closePopups); popups.clear() })
const lifecycle = ref<InstanceType<typeof LayoutVersionHistory> | null>(null)
const layouts = ref<HmiLayout[]>([]), stations = ref<HmiWorkstation[]>([]), screens = ref<ScreenSummary[]>([]), loadError = ref(''), layoutError = ref(''), stationError = ref('')
const { actionError, guard: runGuarded } = useActionError()
const deleteConflict = ref(false), stationConflict = ref(false)
const isConflict = (reason: unknown) => (reason as { response?: { status?: number } })?.response?.status === 409
async function guardDelete(action: () => Promise<unknown>): Promise<boolean> {
  deleteConflict.value = false
  return runGuarded(async () => { try { await action() } catch (reason) { deleteConflict.value = isConflict(reason); throw reason } }, tr('layoutsView.k72250c'))
}
async function reloadAfterDeleteConflict(): Promise<void> { await load(); if (!loadError.value && !stationsError.value) { deleteConflict.value = false; actionError.value = '' } }
const stationsError = ref(''), screensError = ref(''), loading = ref(false), saving = ref(false), stationSaving = ref(false)
const kinds: Record<RegionKind, string> = { screen: tr('layoutsView.k8a1840'), alarmBanner: tr('layoutsView.k77911d'), alarmList: tr('layoutsView.k459887'), navigation: tr('layoutsView.k6be0ed') }
const editableKinds = { screen: kinds.screen, alarmList: kinds.alarmList, navigation: kinds.navigation }
const getBinding = () => ({ ...getLocalWorkstation(), hasId: localStorage.getItem(WORKSTATION_ID_KEY) != null })
const local = ref(getBinding())
const localName = computed(() => local.value.name || (local.value.id ? `#${local.value.id}` : local.value.hasId ? '—' : ''))
const localInvalid = computed(() => !!localName.value && !loading.value && !stationsError.value && !stations.value.some(s => local.value.hasId ? s.id === local.value.id : s.name === local.value.name))
const layoutStations = (id: number) => stations.value.filter(s => s.layoutId === id)
const publishedLayouts = computed(() => layouts.value.filter(layout => layout.publishedRevision != null))
const screenNames = computed(() => new Map(screens.value.map(screen => [screen.id, screen.name])))
const localStation = computed(() => stations.value.find(station => local.value.hasId ? station.id === local.value.id : station.name === local.value.name))
const localLayout = computed(() => layouts.value.find(layout => layout.id === localStation.value?.layoutId))
const localReady = computed(() => !loading.value && !stationsError.value && !loadError.value && !!localStation.value && localLayout.value?.publishedRevision != null)
const localSummaryMessage = computed(() => tr(`layoutCatalog.local_${loading.value ? 'loading' : stationsError.value || loadError.value ? 'error' : localInvalid.value ? 'invalid' : !localStation.value ? 'unregistered' : localStation.value.layoutId == null ? 'unassigned' : !localLayout.value ? 'missing' : localLayout.value.publishedRevision == null ? 'unpublished' : 'ready'}`))
const layoutSearch = ref(''), statusFilter = ref('all'), usageFilter = ref('all')
const filteredLayouts = computed(() => {
  const query = layoutSearch.value.trim().toLocaleLowerCase()
  return layouts.value.filter(layout => (!query || [layout.name, layout.publishedName].some(name => name?.toLocaleLowerCase().includes(query)))
    && (statusFilter.value === 'all' || (statusFilter.value === 'published' ? layout.publishedRevision != null : statusFilter.value === 'unpublished' ? layout.publishedRevision == null : layout.publishedRevision != null && layout.hasDraftChanges))
    && (stationsError.value || usageFilter.value === 'all' || (usageFilter.value === 'used' ? layoutStations(layout.id).length > 0 : !layoutStations(layout.id).length)))
})
function resetFilters(): void { layoutSearch.value = ''; statusFilter.value = 'all'; usageFilter.value = 'all' }
function monitorSummary(layout: HmiLayout): string { return tr('layoutCatalog.monitorSummary', [layout.document.monitors.length, layout.document.monitors.reduce((total, monitor) => total + contentMonitor(monitor).regions.length, 0)]) }
const stationsPanel = ref<HTMLElement | null>(null)
function focusStations(): void { stationsPanel.value?.scrollIntoView({ behavior: 'smooth', block: 'start' }); stationsPanel.value?.focus({ preventScroll: true }) }
function stationLayoutLabel(id: number | null): string {
  const layout = layouts.value.find(item => item.id === id)
  return !layout ? id == null ? '—' : `#${id}` : layout.publishedRevision == null ? tr('layoutLifecycle.unpublishedBinding', [layout.name]) : layout.publishedName || layout.name
}

async function load(): Promise<void> {
  if (loading.value) return
  loading.value = true
  const [layoutResult, stationResult, screenResult] = await Promise.allSettled([hmiLayoutApi.list(true), hmiLayoutApi.workstations(), screenApi.list()])
  if (layoutResult.status === 'fulfilled') { layouts.value = layoutResult.value; loadError.value = '' }
  else loadError.value = errorMessage(layoutResult.reason, tr('layoutsView.kf6b7a4'))
  if (stationResult.status === 'fulfilled') {
    stations.value = stationResult.value; stationsError.value = ''
    const match = stations.value.find(isLocalWorkstation)
    if (match) setLocalWorkstation(match)
    syncLocal()
  }
  else { stationsError.value = errorMessage(stationResult.reason, tr('layoutMonitor.stationsFailed')); usageFilter.value = 'all' }
  if (screenResult.status === 'fulfilled') { screens.value = screenResult.value.filter(s => s.publishedRevision != null); screensError.value = '' }
  else screensError.value = errorMessage(screenResult.reason, tr('layoutMonitor.screensFailed'))
  loading.value = false
}

const visible = ref(false), monitorTab = ref('0')
const layoutSettingsOpen = ref(false), monitorSettingsOpen = ref(false), regionPropertiesOpen = ref(false), issuesOpen = ref(false)
function collapseMobilePanels(): void { layoutSettingsOpen.value = false; monitorSettingsOpen.value = false; regionPropertiesOpen.value = false; issuesOpen.value = false }
watch(isMobile, mobile => { if (mobile) collapseMobilePanels() })
const layoutConflict = ref(false)
const form = reactive<{ id?: number; name: string; revision: number; document: HmiLayoutDocument }>({ name: '', revision: 0, document: { monitors: [] } })
const selectedRegionIndex = ref(0), runtimePreviewVisible = ref(false), legacyConverted = ref(0)
const regionClipboard = ref<HmiRegion | null>(null)
const copiedFrom = ref(''), templateKey = ref<LayoutTemplateKey>('dual')
const templateDocument = computed(() => createLayoutTemplate(templateKey.value, screens.value[0]?.id ?? null))
const ratioSizes: Record<string, [number, number]> = { '16:9': [1920, 1080], '16:10': [1920, 1200], '4:3': [1280, 960], '9:16': [1080, 1920] }
const previewRatio = computed({
  get: () => {
    const monitor = form.document.monitors[Number(monitorTab.value)]
    if (!monitor) return '16:9'
    const size = previewSize(monitor)
    return Object.entries(ratioSizes).find(([, [width, height]]) => Math.abs(width / height - size.width / size.height) < .001)?.[0] ?? `${size.width}:${size.height}`
  },
  set: (ratio: string) => {
    const monitor = form.document.monitors[Number(monitorTab.value)], size = ratioSizes[ratio]
    if (monitor && size) setPreviewSize(monitor, size[0], size[1])
  }
})
function setPreviewSize(monitor: HmiMonitor, width: number | undefined, height: number | undefined): void {
  if (width == null || height == null) return
  monitor.previewWidth = width; monitor.previewHeight = height; recordHistory()
}
const gridControlVersion = ref(0)
interface GridResize { monitor: HmiMonitor; columns: number; rows: number; affected: string[]; fitted: RegionGeometry[] | null; baseline: string }
const pendingGrid = ref<GridResize | null>(null)
const pendingGridPreview = computed<HmiLayoutDocument | null>(() => {
  const pending = pendingGrid.value
  if (!pending?.fitted) return null
  return { monitors: [{ ...pending.monitor, columns: pending.columns, rows: pending.rows, regions: pending.monitor.regions.map((region, index) => ({ ...region, ...pending.fitted![index] })) }] }
})
function cancelGridResize(): void { pendingGrid.value = null; gridControlVersion.value++ }
function changeGridSize(monitor: HmiMonitor, columns: number | undefined, rows: number | undefined): void {
  if (pendingGrid.value || columns == null || rows == null || ![columns, rows].every(value => Number.isInteger(value) && value >= 1 && value <= 24)) { gridControlVersion.value++; return }
  if (monitor.columns === columns && monitor.rows === rows) return
  endDrag(); recordHistory()
  const affected = monitor.regions.filter(region => outOfBounds({ ...monitor, columns, rows }, region)).map(region => region.id || '?')
  if (affected.length) {
    pendingGrid.value = { monitor, columns, rows, affected, fitted: fitRegionsToGrid(monitor, columns, rows), baseline: JSON.stringify(monitor) }
  } else { monitor.columns = columns; monitor.rows = rows; recordHistory(); gridControlVersion.value++ }
}
function applyGridResize(fit: boolean): void {
  const pending = pendingGrid.value
  if (!pending || fit && !pending.fitted) return
  if (form.document.monitors.includes(pending.monitor) && JSON.stringify(pending.monitor) === pending.baseline) {
    endDrag(); recordHistory()
    pending.monitor.columns = pending.columns; pending.monitor.rows = pending.rows
    if (fit) pending.fitted!.forEach((geometry, index) => Object.assign(pending.monitor.regions[index], geometry))
    recordHistory()
  }
  cancelGridResize()
}
function applyGeometry(monitor: HmiMonitor, geometry: RegionGeometry[]): void {
  if (!geometryChanged(monitor.regions, geometry)) return
  endDrag(); recordHistory()
  geometry.forEach((position, index) => Object.assign(monitor.regions[index], { row: position.row, col: position.col, rowSpan: position.rowSpan, colSpan: position.colSpan }))
  recordHistory()
}
const selectedRegion = computed(() => form.document.monitors[Number(monitorTab.value)]?.regions[selectedRegionIndex.value])
watch(monitorTab, () => { selectedRegionIndex.value = 0 }, { flush: 'sync' })
interface EditorHistoryEntry { content: string; monitorIndex: number; regionIndex: number }
const history = ref<EditorHistoryEntry[]>([]), historyIndex = ref(0)
const snapshot = () => JSON.stringify({ name: form.name, document: form.document })
const historyEntry = (): EditorHistoryEntry => ({ content: snapshot(), monitorIndex: Number(monitorTab.value), regionIndex: selectedRegionIndex.value })
function recordHistory(): void {
  if (!visible.value || drag.value) return
  const current = snapshot()
  if (current === history.value[historyIndex.value]?.content) { Object.assign(history.value[historyIndex.value], historyEntry()); return }
  history.value.splice(historyIndex.value + 1)
  history.value.push(historyEntry())
  if (history.value.length > 100) history.value.shift()
  historyIndex.value = history.value.length - 1
}
function restoreHistory(index: number): void {
  endDrag()
  const entry = history.value[index]
  const state = JSON.parse(entry.content) as { name: string; document: HmiLayoutDocument }
  form.name = state.name; form.document = state.document; historyIndex.value = index
  monitorTab.value = String(Math.min(entry.monitorIndex, form.document.monitors.length - 1))
  selectedRegionIndex.value = Math.max(0, Math.min(entry.regionIndex, (form.document.monitors[Number(monitorTab.value)]?.regions.length ?? 0) - 1))
  if (!layoutConflict.value) layoutError.value = ''
  void selectRegion(selectedRegionIndex.value)
}
function undo(): void { endDrag(); recordHistory(); if (historyIndex.value > 0) restoreHistory(historyIndex.value - 1) }
function redo(): void { endDrag(); recordHistory(); if (historyIndex.value < history.value.length - 1) restoreHistory(historyIndex.value + 1) }
function editorShortcut(event: KeyboardEvent): void {
  if (event.defaultPrevented || event.isComposing || event.altKey || saving.value || runtimePreviewVisible.value) return
  const target = event.target as HTMLElement, key = event.key.toLowerCase(), modifier = event.ctrlKey || event.metaKey
  if (modifier && key === 's') { event.preventDefault(); target.closest<HTMLElement>('input, textarea')?.blur(); void nextTick().then(save); return }
  if (target.closest('input, textarea, [contenteditable]:not([contenteditable="false"]), [role="combobox"], [role="listbox"], [role="menu"]')) return
  if (modifier && key === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); return }
  if (modifier && key === 'y') { event.preventDefault(); redo(); return }
  const monitor = form.document.monitors[Number(monitorTab.value)]
  if (!monitor) return
  if (modifier && !event.shiftKey && ['c', 'v', 'd'].includes(key)) {
    if (key === 'c' && document.getSelection()?.toString()) return
    event.preventDefault()
    if (key === 'c') copyRegion()
    else if (key === 'v') pasteRegion(monitor)
    else duplicateRegion(monitor)
  } else if (!modifier && !event.shiftKey && ['Delete', 'Backspace'].includes(event.key) && selectedRegion.value) {
    event.preventDefault()
    if (monitor.regions.length <= 1) ElMessage.warning(tr('layoutEditing.keepRegion'))
    else removeRegion(monitor, selectedRegionIndex.value)
  }
}
const layoutBaseline = ref(''), stationBaseline = ref('')
const layoutDirty = computed(() => visible.value && JSON.stringify(form) !== layoutBaseline.value)
const stationDirty = computed(() => stationVisible.value && JSON.stringify(stationForm) !== stationBaseline.value)
const auth = useAuthStore()
const recoveryOwner = computed(() => auth.user ? `${auth.user.id}:${auth.user.userName}` : '')
const recoveries = ref<LocalLayoutDraft[]>([]), recoveryError = ref(''), recoverySavedAt = ref(0), restoring = ref(false)
let recoveryTimer: ReturnType<typeof setTimeout> | undefined, recoveryId = '', sessionOwner = '', recoveryBase: LayoutDraftContent | null = null, lastRecoveryContent = ''
let restoredSource: LocalLayoutDraft | null = null
function refreshRecoveries(): void {
  const result = readLocalLayoutDrafts(recoveryOwner.value)
  recoveries.value = result.drafts.filter(draft => draft.id !== recoveryId)
  if (result.failed) recoveryError.value = tr('layoutRecovery.storageFailed')
}
function persistLocalDraft(): boolean {
  clearTimeout(recoveryTimer)
  if (!visible.value || !layoutDirty.value || !recoveryBase || !sessionOwner || sessionOwner !== recoveryOwner.value) return false
  const content = snapshot()
  if (content === lastRecoveryContent) return true
  const savedAt = Date.now()
  const stored = writeLocalLayoutDraft(sessionOwner, { schema: 1, id: recoveryId, layoutId: form.id ?? null, baseRevision: form.revision, base: recoveryBase, content: normalizeDraftContent(JSON.parse(content) as LayoutDraftContent), savedAt, copiedFrom: copiedFrom.value })
  if (stored) { recoverySavedAt.value = savedAt; lastRecoveryContent = content }
  else recoveryError.value = tr('layoutRecovery.storageFailed')
  return stored
}
function forgetLocalDraft(): void {
  clearTimeout(recoveryTimer)
  if (sessionOwner && recoveryId && !removeLocalLayoutDraft(sessionOwner, recoveryId)) recoveryError.value = tr('layoutRecovery.storageFailed')
  if (sessionOwner && restoredSource && !removeLocalLayoutDraft(sessionOwner, restoredSource.id, restoredSource.savedAt)) recoveryError.value = tr('layoutRecovery.storageFailed')
  recoveryId = ''; sessionOwner = ''; restoredSource = null; recoverySavedAt.value = 0; lastRecoveryContent = ''; refreshRecoveries()
}
watch([visible, snapshot], () => {
  clearTimeout(recoveryTimer)
  if (!visible.value) return
  if (layoutDirty.value) recoveryTimer = setTimeout(persistLocalDraft, 400)
  else { if (sessionOwner && recoveryId && !removeLocalLayoutDraft(sessionOwner, recoveryId)) recoveryError.value = tr('layoutRecovery.storageFailed'); recoverySavedAt.value = 0; lastRecoveryContent = '' }
}, { flush: 'post' })
watch(recoveryOwner, refreshRecoveries, { immediate: true })
async function discardLocalDraft(draft: LocalLayoutDraft): Promise<void> {
  if (!await confirmDiscard()) return
  if (!removeLocalLayoutDraft(recoveryOwner.value, draft.id, draft.savedAt)) recoveryError.value = tr('layoutRecovery.storageFailed')
  refreshRecoveries()
}
async function restoreLocalDraft(draft: LocalLayoutDraft): Promise<void> {
  if (visible.value || restoring.value) return
  restoring.value = true; recoveryError.value = ''
  try {
    let current: HmiLayout | undefined
    if (draft.layoutId != null) {
      try { current = await hmiLayoutApi.get(draft.layoutId, true) }
      catch (reason) { if ((reason as { response?: { status?: number } }).response?.status !== 404) throw reason }
    }
    const changed = draft.layoutId != null && (!current || current.revision !== draft.baseRevision || layoutContentKey(current) !== layoutContentKey(draft.base))
    if (changed) {
      try { await ElMessageBox.confirm(tr(current ? 'layoutRecovery.serverChanged' : 'layoutRecovery.sourceDeleted', [draft.baseRevision, current?.revision ?? '—']), tr('layoutRecovery.restoreCopy'), { type: 'warning', confirmButtonText: tr('layoutRecovery.restoreCopy') }) } catch { return }
    }
    edit(current, changed)
    form.name = changed ? copyName(draft.content.name || tr('layoutRecovery.unnamed'), layouts.value.map(layout => layout.name), 100) : draft.content.name
    form.document = { monitors: JSON.parse(JSON.stringify(draft.content.document.monitors)).map(contentMonitor) }
    copiedFrom.value = changed ? draft.base.name : draft.copiedFrom ?? ''
    restoredSource = draft; recordHistory()
    if (persistLocalDraft()) {
      if (!removeLocalLayoutDraft(sessionOwner, draft.id, draft.savedAt)) recoveryError.value = tr('layoutRecovery.storageFailed')
      restoredSource = null
    }
    refreshRecoveries()
  } catch (reason) { recoveryError.value = errorMessage(reason, tr('layoutRecovery.restoreFailed')) }
  finally { restoring.value = false }
}
function flushHiddenDraft(): void { if (document.visibilityState === 'hidden') persistLocalDraft() }
onMounted(() => { window.addEventListener('storage', refreshRecoveries); document.addEventListener('visibilitychange', flushHiddenDraft) })
onBeforeUnmount(() => { persistLocalDraft(); clearTimeout(recoveryTimer); window.removeEventListener('storage', refreshRecoveries); document.removeEventListener('visibilitychange', flushHiddenDraft) })
async function confirmDiscard(): Promise<boolean> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.discardChanges'), tr('layoutMonitor.unsaved'), { type: 'warning' }); return true } catch { return false }
}
async function closeLayout(done?: () => void): Promise<void> {
  if (saving.value || (layoutDirty.value && !await confirmDiscard())) return
  endDrag(); forgetLocalDraft(); if (done) done(); else visible.value = false
}
async function closeStation(done?: () => void): Promise<void> {
  if (stationSaving.value || (stationDirty.value && !await confirmDiscard())) return
  if (done) done(); else stationVisible.value = false
}
onBeforeRouteLeave(async () => { if (!(layoutDirty.value || stationDirty.value)) return true; if (!await confirmDiscard()) return false; forgetLocalDraft(); return true })
function beforeUnload(event: BeforeUnloadEvent): void { persistLocalDraft(); if (layoutDirty.value || stationDirty.value) { event.preventDefault(); event.returnValue = '' } }
function edit(row?: HmiLayout, asCopy = false): void {
  endDrag()
  cancelGridResize(); collapseMobilePanels()
  copiedFrom.value = asCopy && row ? row.name : ''; templateKey.value = 'dual'
  Object.assign(form, row ? { id: asCopy ? undefined : row.id, name: asCopy ? copyName(row.name, layouts.value.map(layout => layout.name), 100) : row.name, revision: asCopy ? 0 : row.revision, document: JSON.parse(JSON.stringify(row.document)) } : { id: undefined, name: '', revision: 0, document: { monitors: [] } })
  if (!form.document.monitors.length) preset()
  layoutError.value = ''; layoutConflict.value = false
  layoutBaseline.value = JSON.stringify(form)
  clearTimeout(recoveryTimer); recoveryId = window.crypto?.randomUUID?.() ?? `${Date.now()}-${Math.random().toString(36).slice(2)}`; sessionOwner = recoveryOwner.value
  recoveryBase = normalizeDraftContent(JSON.parse(snapshot()) as LayoutDraftContent); recoverySavedAt.value = 0; lastRecoveryContent = ''; restoredSource = null
  legacyConverted.value = form.document.monitors.filter(m => m.regions.some(r => r.kind === 'alarmBanner')).length
  if (legacyConverted.value) form.document.monitors = form.document.monitors.map(contentMonitor)
  monitorTab.value = '0'; selectedRegionIndex.value = 0; regionClipboard.value = null; visible.value = true
  history.value = [historyEntry()]; historyIndex.value = 0
}
async function reloadSavedDraft(): Promise<void> {
  if (!form.id || saving.value || (layoutDirty.value && !await confirmDiscard())) return
  try { const current = await hmiLayoutApi.get(form.id, true); forgetLocalDraft(); edit(current) }
  catch (reason) { layoutError.value = errorMessage(reason, tr('layoutLifecycle.loadFailed')) }
}
async function replacePreset(): Promise<void> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.replacePreset'), tr('layoutCatalog.applyTemplate'), { type: 'warning' }) } catch { return }
  endDrag(); preset(); monitorTab.value = '0'; selectedRegionIndex.value = 0; recordHistory()
}
async function removeMonitor(index: number): Promise<void> {
  try { await ElMessageBox.confirm(tr('layoutMonitor.removeMonitor'), tr('layoutsView.k33160a'), { type: 'warning' }) } catch { return }
  endDrag(); form.document.monitors.splice(index, 1); monitorTab.value = '0'; selectedRegionIndex.value = 0; recordHistory()
}
function preset(): void {
  legacyConverted.value = 0
  form.document = createLayoutTemplate(templateKey.value, screens.value[0]?.id ?? null)
}
function copyName(base: string, existing: string[], limit: number): string {
  for (let index = 1; ; index++) {
    const suffix = ` ${tr('layoutCatalog.copySuffix')}${index === 1 ? '' : ` ${index}`}`
    const name = base.slice(0, Math.max(0, limit - suffix.length)) + suffix
    if (!existing.includes(name)) return name
  }
}
function duplicateMonitor(index: number): void {
  if (form.document.monitors.length >= 8) return
  endDrag()
  const monitor = JSON.parse(JSON.stringify(form.document.monitors[index])) as HmiMonitor
  monitor.name = copyName(monitor.name, form.document.monitors.map(item => item.name), 40)
  form.document.monitors.push(monitor); monitorTab.value = String(form.document.monitors.length - 1); selectedRegionIndex.value = 0; recordHistory()
}
function moveMonitor(index: number, direction: -1 | 1): void {
  const destination = index + direction
  if (destination < 0 || destination >= form.document.monitors.length) return
  endDrag(); recordHistory()
  const regionIndex = selectedRegionIndex.value
  const [monitor] = form.document.monitors.splice(index, 1)
  form.document.monitors.splice(destination, 0, monitor)
  monitorTab.value = String(destination); selectedRegionIndex.value = regionIndex; recordHistory()
}
function pasteIssue(monitor: HmiMonitor, region: HmiRegion | null | undefined): string {
  if (!region) return tr('layoutEditing.clipboardEmpty')
  if (monitor.regions.length >= 16) return tr('layoutEditing.regionLimit')
  return findRegionSpace(monitor, region) ? '' : tr('layoutEditing.noRoom')
}
function copyRegion(): void {
  if (!selectedRegion.value) return
  endDrag(); regionClipboard.value = JSON.parse(JSON.stringify(selectedRegion.value)) as HmiRegion
}
function insertRegionCopy(monitor: HmiMonitor, source: HmiRegion | null | undefined): void {
  endDrag(); recordHistory()
  const issue = pasteIssue(monitor, source)
  if (issue || !source) { ElMessage.warning(issue); return }
  const position = findRegionSpace(monitor, source)!
  const region = JSON.parse(JSON.stringify(source)) as HmiRegion
  Object.assign(region, position, { id: copiedRegionId(region.id, monitor) })
  monitor.regions.push(region); selectedRegionIndex.value = monitor.regions.length - 1; recordHistory()
  void selectRegion(selectedRegionIndex.value)
}
function pasteRegion(monitor: HmiMonitor): void { insertRegionCopy(monitor, regionClipboard.value) }
function duplicateRegion(monitor: HmiMonitor): void { insertRegionCopy(monitor, selectedRegion.value) }
function addMonitor(): void {
  if (form.document.monitors.length >= 8) return
  endDrag(); form.document.monitors.push({ name: '', columns: 12, rows: 12, regions: [{ id: 'main', kind: 'screen', screenId: screens.value[0]?.id ?? null, row: 1, col: 1, rowSpan: 12, colSpan: 12 }] })
  monitorTab.value = String(form.document.monitors.length - 1); selectedRegionIndex.value = 0; recordHistory()
}
function freeCell(m: HmiMonitor): { row: number; col: number } | null {
  for (let row = 1; row <= m.rows; row++) for (let col = 1; col <= m.columns; col++) {
    if (!m.regions.some(r => row >= r.row && row < r.row + r.rowSpan && col >= r.col && col < r.col + r.colSpan)) return { row, col }
  }
  return null
}
function addRegion(m: HmiMonitor): void {
  const position = freeCell(m)
  if (m.regions.length >= 16 || !position) return
  let n = m.regions.length + 1; while (m.regions.some(r => r.id === `r${n}`)) n++
  m.regions.push({ id: `r${n}`, kind: 'screen', screenId: screens.value[0]?.id ?? null, ...position, rowSpan: 1, colSpan: 1 })
  selectedRegionIndex.value = m.regions.length - 1; recordHistory()
  void selectRegion(selectedRegionIndex.value)
}
function removeRegion(m: HmiMonitor, index: number): void {
  if (m.regions.length <= 1) return
  endDrag(); m.regions.splice(index, 1); selectedRegionIndex.value = Math.min(index, m.regions.length - 1); recordHistory()
}
async function selectRegion(index: number): Promise<void> {
  selectedRegionIndex.value = index
  await nextTick()
  const region = document.querySelectorAll<HTMLElement>('[data-test="layout-preview"] [data-test="layout-region"]')[index]
  ;(region ?? document.querySelector<HTMLElement>('.layout-editor'))?.focus({ preventScroll: true })
}

type DragMode = 'move' | 'resize'
interface DragState {
  mode: DragMode
  monitor: HmiMonitor
  region: HmiRegion
  startX: number
  startY: number
  row: number
  col: number
  rowSpan: number
  colSpan: number
  cellW: number
  cellH: number
}
const drag = ref<DragState | null>(null)
const clamp = (value: number, min: number, max: number) => Math.min(Math.max(value, min), max)

function gridStyle(m: HmiMonitor): Record<string, string> {
  return { gridTemplateColumns: `repeat(${Math.max(1, m.columns)}, minmax(0, 1fr))`, gridTemplateRows: `repeat(${Math.max(1, m.rows)}, minmax(0, 1fr))`, '--grid-cols': String(Math.max(1, m.columns)), '--grid-rows': String(Math.max(1, m.rows)) }
}
function previewFrameStyle(m: HmiMonitor): Record<string, string> {
  const { width, height } = previewSize(m)
  return { aspectRatio: `${width} / ${height}`, maxWidth: `${(isMobile.value ? 34 : 62) * width / height}dvh` }
}
function regionStyle(r: HmiRegion): Record<string, string> {
  return { gridColumn: `${r.col} / span ${r.colSpan}`, gridRow: `${r.row} / span ${r.rowSpan}` }
}
function overlaps(a: HmiRegion, b: HmiRegion): boolean {
  return a.row < b.row + b.rowSpan && b.row < a.row + a.rowSpan && a.col < b.col + b.colSpan && b.col < a.col + a.colSpan
}
function outOfBounds(m: HmiMonitor, r: HmiRegion): boolean {
  return ![r.row, r.col, r.rowSpan, r.colSpan].every(n => Number.isInteger(n) && n > 0) || r.row + r.rowSpan - 1 > m.rows || r.col + r.colSpan - 1 > m.columns
}
function regionOverlaps(m: HmiMonitor, index: number): boolean {
  return m.regions.some((other, i) => i !== index && overlaps(m.regions[index], other))
}
interface LayoutIssue { monitorIndex: number; regionIndex?: number; target?: string; message: string }
function validScreen(r: HmiRegion): boolean { return screens.value.some(s => s.id === r.screenId) }
function screenLabel(r: HmiRegion): string { return screens.value.find(s => s.id === r.screenId)?.name || tr('layoutImprovements.unavailableScreen', [r.screenId ?? '—']) }
function monitorIssues(m: HmiMonitor, monitorIndex: number): LayoutIssue[] {
  const issues: LayoutIssue[] = []
  const add = (message: string, regionIndex?: number, target?: string) => issues.push({ monitorIndex, regionIndex, target, message: tr('layoutsView.monitorIssue', [m.name || monitorIndex + 1, message]) })
  if ((m.name?.length ?? 0) > 40) add(tr('layoutImprovements.monitorNameLimit'), undefined, 'monitor-name')
  if (![m.columns, m.rows].every(n => Number.isInteger(n) && n >= 1 && n <= 24)) add(tr('layoutImprovements.gridLimit'), undefined, 'monitor-columns')
  if (!validPreviewSize(m)) add(tr('layoutPreview.sizeRule'), undefined, 'monitor-preview-width')
  if (m.regions.length < 1 || m.regions.length > 16) add(tr('layoutImprovements.regionLimit'))
  const ids = new Set<string>()
  m.regions.forEach((r, i) => {
    if (!r.id?.trim() || r.id.length > 40 || ids.has(r.id)) add(tr('layoutImprovements.regionIdRule', [r.id || '?']), i, 'region-id')
    ids.add(r.id)
    if (!Object.prototype.hasOwnProperty.call(kinds, r.kind)) add(tr('layoutImprovements.invalidKind', [r.id]), i, 'region-kind')
    if (outOfBounds(m, r)) add(tr('layoutsView.regionOut', [r.id, m.columns, m.rows]), i, 'region-row')
    if (r.kind === 'screen' && (!r.screenId || (!screensError.value && !validScreen(r)))) add(tr('layoutsView.regionScreen', [r.id]), i, 'region-screen')
    if ((r.title?.length ?? 0) > 60) add(tr('layoutImprovements.titleLimit', [r.id]), i, 'region-title')
    const previous = m.regions.findIndex((other, oi) => oi < i && overlaps(other, r))
    if (previous >= 0) add(tr('layoutsView.regionOverlap', [r.id, m.regions[previous].id]), i)
  })
  return issues
}
const layoutIssues = computed<LayoutIssue[]>(() => {
  const issues = form.document.monitors.flatMap((m, i) => monitorIssues(m, i))
  if (form.document.monitors.length < 1 || form.document.monitors.length > 8) issues.unshift({ monitorIndex: 0, message: tr('layoutImprovements.monitorLimit') })
  return issues
})
const draftShapeError = computed(() => {
  if (!form.name.trim() || form.name.length > 100) return tr('layoutLifecycle.nameRule')
  if (form.document.monitors.length < 1 || form.document.monitors.length > 8) return tr('layoutImprovements.monitorLimit')
  for (const monitor of form.document.monitors) {
    if (!validPreviewSize(monitor)) return tr('layoutPreview.sizeRule')
    if ((monitor.name?.length ?? 0) > 40) return tr('layoutImprovements.monitorNameLimit')
    if (![monitor.rows, monitor.columns].every(n => Number.isInteger(n) && n >= 1 && n <= 24)) return tr('layoutImprovements.gridLimit')
    if (monitor.regions.length > 16) return tr('layoutLifecycle.draftRegionLimit')
    for (const region of monitor.regions) {
      if ((region.id?.length ?? 0) > 40 || (region.title?.length ?? 0) > 60 || !Object.prototype.hasOwnProperty.call(kinds, region.kind)) return tr('layoutLifecycle.draftShapeRule')
      if (![region.row, region.col, region.rowSpan, region.colSpan].every(n => Number.isInteger(n) && n >= -2147483648 && n <= 2147483647)) return tr('layoutLifecycle.geometryInteger')
    }
  }
  return ''
})
function validPreviewSize(monitor: HmiMonitor): boolean {
  if (monitor.previewWidth == null && monitor.previewHeight == null) return true
  return Number.isInteger(monitor.previewWidth) && Number.isInteger(monitor.previewHeight) && monitor.previewWidth! >= 320 && monitor.previewWidth! <= 7680 && monitor.previewHeight! >= 240 && monitor.previewHeight! <= 4320
}
async function focusIssue(issue: LayoutIssue): Promise<void> {
  monitorTab.value = String(issue.monitorIndex)
  if (issue.target?.startsWith('monitor-')) monitorSettingsOpen.value = true
  if (issue.regionIndex != null) regionPropertiesOpen.value = true
  await nextTick()
  if (issue.regionIndex != null) selectedRegionIndex.value = issue.regionIndex
  await nextTick()
  if (issue.target) {
    const control = [...document.querySelectorAll<HTMLElement>(`.layout-editor [data-test="${issue.target}"]`)].map(el => el.matches('input') ? el : el.querySelector<HTMLElement>('input')).find(el => el && el.getClientRects().length)
    if (control) { control.focus(); control.scrollIntoView({ block: 'nearest' }); return }
  }
  if (issue.regionIndex != null) await selectRegion(issue.regionIndex)
}

function beginDrag(e: PointerEvent, m: HmiMonitor, r: HmiRegion, mode: DragMode, index: number): void {
  if (e.button !== 0) return
  endDrag()
  selectedRegionIndex.value = index
  const preview = (e.currentTarget as HTMLElement).closest('.preview') as HTMLElement | null
  if (!preview) return
  const rect = preview.getBoundingClientRect()
  const css = getComputedStyle(preview)
  const gap = parseFloat(css.columnGap) || 0
  const rowGap = parseFloat(css.rowGap) || 0
  recordHistory()
  drag.value = {
    mode, monitor: m, region: r,
    startX: e.clientX, startY: e.clientY,
    row: r.row, col: r.col, rowSpan: r.rowSpan, colSpan: r.colSpan,
    cellW: (rect.width - parseFloat(css.paddingLeft) - parseFloat(css.paddingRight) + gap) / Math.max(1, m.columns),
    cellH: (rect.height - parseFloat(css.paddingTop) - parseFloat(css.paddingBottom) + rowGap) / Math.max(1, m.rows)
  }
  window.addEventListener('pointermove', onDragMove)
  window.addEventListener('pointerup', endDrag, { once: true })
  window.addEventListener('pointercancel', endDrag, { once: true })
  ;(e.currentTarget as HTMLElement).closest<HTMLElement>('[data-test="layout-region"]')?.focus({ preventScroll: true })
  e.preventDefault()
}
function startDrag(e: PointerEvent, m: HmiMonitor, r: HmiRegion, index: number): void { beginDrag(e, m, r, 'move', index) }
function startResize(e: PointerEvent, m: HmiMonitor, r: HmiRegion, index: number): void { beginDrag(e, m, r, 'resize', index) }
function onDragMove(e: PointerEvent): void {
  const d = drag.value
  if (!d) return
  const dx = Math.round((e.clientX - d.startX) / d.cellW)
  const dy = Math.round((e.clientY - d.startY) / d.cellH)
  if (d.mode === 'move') {
    d.region.col = clamp(d.col + dx, 1, d.monitor.columns - d.region.colSpan + 1)
    d.region.row = clamp(d.row + dy, 1, d.monitor.rows - d.region.rowSpan + 1)
  } else {
    d.region.colSpan = clamp(d.colSpan + dx, 1, d.monitor.columns - d.region.col + 1)
    d.region.rowSpan = clamp(d.rowSpan + dy, 1, d.monitor.rows - d.region.row + 1)
  }
}
function endDrag(): void {
  const wasDragging = !!drag.value
  drag.value = null
  window.removeEventListener('pointermove', onDragMove)
  window.removeEventListener('pointerup', endDrag)
  window.removeEventListener('pointercancel', endDrag)
  if (wasDragging) recordHistory()
}
function regionKeydown(event: KeyboardEvent, m: HmiMonitor, r: HmiRegion, index: number): void {
  selectedRegionIndex.value = index
  if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); return }
  const direction = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[event.key]
  if (!direction || event.ctrlKey || event.metaKey || event.altKey) return
  event.preventDefault()
  if (event.shiftKey) {
    r.colSpan = clamp(r.colSpan + direction[0], 1, Math.max(1, m.columns - r.col + 1))
    r.rowSpan = clamp(r.rowSpan + direction[1], 1, Math.max(1, m.rows - r.row + 1))
  } else {
    r.col = clamp(r.col + direction[0], 1, Math.max(1, m.columns - r.colSpan + 1))
    r.row = clamp(r.row + direction[1], 1, Math.max(1, m.rows - r.rowSpan + 1))
  }
  recordHistory()
}
onBeforeUnmount(endDrag)
async function save(): Promise<void> {
  if (saving.value || draftShapeError.value || layoutConflict.value) return
  saving.value = true
  try {
    await hmiLayoutApi.save({ ...form, name: form.name.trim(), document: JSON.parse(JSON.stringify(form.document)) })
    forgetLocalDraft()
    ElMessage.success(tr('layoutLifecycle.draftSaved'))
    visible.value = false
    await load()
  } catch (reason) {
    layoutConflict.value = (reason as { response?: { status?: number } }).response?.status === 409
    layoutError.value = layoutConflict.value ? tr('layoutLifecycle.draftConflict') : errorMessage(reason, tr('layoutsView.k40525a'))
  } finally { saving.value = false }
}
async function remove(row: HmiLayout): Promise<void> {
  const assigned = layoutStations(row.id)
  const impact = stationsError.value ? tr('layoutImprovements.usageUnknown') : assigned.length ? tr('layoutImprovements.deleteImpact', [assigned.map(s => s.name).join('、')]) : ''
  try { await ElMessageBox.confirm([tr('layoutsView.kb8c7a3', [row.name]), impact].filter(Boolean).join('\n'), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guardDelete(() => hmiLayoutApi.remove(row.id, row.revision))) await load()
}

const stationVisible = ref(false)
const stationForm = reactive<{ id?: number; name: string; description: string | null; layoutId: number | null; allowControl: boolean; expectedUpdatedTs?: number }>({ name: '', description: null, layoutId: null, allowControl: true })
function editStation(row?: HmiWorkstation): void {
  Object.assign(stationForm, { id: row?.id, name: row?.name ?? '', description: row?.description ?? null, layoutId: row ? row.layoutId : publishedLayouts.value[0]?.id ?? null, allowControl: row?.allowControl ?? true, expectedUpdatedTs: row?.updatedTs })
  stationBaseline.value = JSON.stringify(stationForm); stationError.value = ''; stationConflict.value = false; stationVisible.value = true
}
async function reloadStation(): Promise<void> {
  if (!stationForm.id || stationSaving.value || (stationDirty.value && !await confirmDiscard())) return
  try {
    const current = await hmiLayoutApi.workstations(), station = current.find(item => item.id === stationForm.id)
    stations.value = current; stationsError.value = ''
    const match = current.find(isLocalWorkstation)
    if (match) { setLocalWorkstation(match); syncLocal() }
    if (!station) { stationError.value = tr('layoutSafety.stationDeleted'); return }
    editStation(station)
  } catch (reason) { stationError.value = errorMessage(reason, tr('layoutMonitor.stationsFailed')) }
}
async function saveStation(): Promise<void> {
  if (stationSaving.value || stationConflict.value) return
  stationForm.name = stationForm.name.trim()
  if (!/^[A-Za-z0-9_.-]{1,60}$/.test(stationForm.name)) { stationError.value = tr('layoutsView.stationNameRule'); return }
  if ((stationForm.description?.length ?? 0) > 200) { stationError.value = tr('layoutImprovements.descriptionLimit'); return }
  if (stationForm.layoutId != null && !publishedLayouts.value.some(l => l.id === stationForm.layoutId)) { stationError.value = tr('layoutPreview.legacyBinding'); return }
  stationSaving.value = true
  const wasLocal = stations.value.some(s => s.id === stationForm.id && isLocalWorkstation(s))
  let savedStation: HmiWorkstation | undefined
  const saved = await runGuarded(async () => {
    try { savedStation = await hmiLayoutApi.saveWorkstation({ ...stationForm, layoutId: stationForm.layoutId ?? null }) }
    catch (reason) { stationConflict.value = isConflict(reason); throw reason }
  }, tr('layoutsView.k40525a'), stationError)
  stationSaving.value = false
  if (saved) {
    if (wasLocal && savedStation) { setLocalWorkstation(savedStation); syncLocal() }
    ElMessage.success(tr('layoutsView.kcdfab9')); stationVisible.value = false; await load()
  }
}
async function removeStation(row: HmiWorkstation): Promise<void> {
  try { await ElMessageBox.confirm(tr('layoutsView.k54ad42', [row.name]), tr('layoutsView.k3755f5'), { type: 'warning' }) } catch { return }
  if (await guardDelete(() => hmiLayoutApi.removeWorkstation(row.id, row.updatedTs))) {
    if (isLocalWorkstation(row)) { setLocalWorkstation(null); syncLocal() }
    await load()
  }
}
function setLocal(station: HmiWorkstation | null): void { setLocalWorkstation(station); syncLocal(); ElMessage.success(station ? tr('layoutsView.k0a2885', [station.name]) : tr('layoutsView.k7507bc')) }
function syncLocal(): void { local.value = getBinding() }
onMounted(() => { void load(); window.addEventListener('beforeunload', beforeUnload) })
onBeforeUnmount(() => window.removeEventListener('beforeunload', beforeUnload))
onMounted(() => window.addEventListener('storage', syncLocal))
onBeforeUnmount(() => window.removeEventListener('storage', syncLocal))
</script>

<style scoped>
.mobile-section-summary { display: none; cursor: pointer; font-size: 13px; line-height: 1.5; min-height: 44px; box-sizing: border-box; padding: 12px; overflow-wrap: anywhere; }
summary:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.editor-primary-actions :deep(.el-form-item__content) { display: flex; gap: 8px; }
.editor-primary-actions :deep(.el-button) { margin: 0; }
.resize-affected { max-height: 160px; overflow: auto; padding-left: 22px; overflow-wrap: anywhere; }
:global(.grid-resize-dialog) { width: min(560px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.grid-resize-dialog .el-dialog__body) { overflow: auto; min-height: 0; }
:global(.grid-resize-dialog .el-dialog__footer) { display: flex; flex-wrap: wrap; justify-content: end; gap: 8px; }
:global(.grid-resize-dialog .el-dialog__footer .el-button) { margin: 0; min-height: 36px; }
.editor { display: grid; grid-template-columns: minmax(0, 1fr) 270px; gap: 18px; align-items: start; }
.layout-state { display: flex; flex-wrap: wrap; align-items: center; gap: 5px; }.layout-state .tiny { width: 100%; overflow-wrap: anywhere; }
.catalog-cards { display: grid; gap: 14px; }
.catalog-card { min-width: 0; padding: 16px; border: 1px solid var(--scada-border); border-radius: 10px; background: var(--scada-panel); overflow-wrap: anywhere; }
.catalog-card h4 { margin: 0; font-size: 16px; line-height: 1.5; }
.catalog-card > .layout-state { margin-top: 10px; }
.card-heading { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.card-preview { padding: 12px 0; }.card-preview > .tiny { display: block; margin-bottom: 6px; }
.card-updated { margin: 10px 0 14px; line-height: 1.6; }
.card-usage { border-block: 1px solid var(--scada-border); }
.card-usage summary { min-height: 44px; align-content: center; cursor: pointer; font-size: 13px; color: var(--scada-primary); }
.card-usage summary:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: 2px; }
.card-empty { text-align: center; padding: 24px 12px; margin: 0; color: var(--scada-text-muted); overflow-wrap: anywhere; }
.card-description { font-size: 13px; line-height: 1.6; margin: 8px 0; color: var(--scada-text-muted); }
.card-fields { display: grid; grid-template-columns: auto minmax(0, 1fr); align-items: start; gap: 10px 12px; margin: 14px 0; font-size: 13px; line-height: 1.6; }
.card-fields dt { color: var(--scada-text-muted); }.card-fields dd { margin: 0; }
.station-card-actions, .region-actions { display: flex; flex-wrap: wrap; gap: 8px; }
.station-card-actions :deep(.el-button), .region-actions :deep(.el-button) { margin: 0; }
.station-card-actions :deep(.el-button) { min-height: 44px; padding-inline: 15px; font-size: 13px; }
.local-workstation-summary { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 14px; margin-bottom: 16px; }
.local-draft-list { max-height: 200px; overflow: auto; }.local-draft-row { display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 8px; padding: 8px 0; overflow-wrap: anywhere; }.local-draft-row > span { min-width: 0; }
.local-summary-info { display: flex; align-items: center; flex-wrap: wrap; gap: 8px 16px; min-width: 0; overflow-wrap: anywhere; font-size: 13px; }
.local-summary-message { color: var(--scada-text-muted); }.local-summary-message.warning { color: var(--scada-warning); }
.local-summary-actions { display: flex; flex-wrap: wrap; gap: 8px; }.local-summary-actions :deep(.el-button) { margin: 0; }
.station-panel { margin-top: 16px; }
.layout-filters { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; margin-bottom: 14px; }
.layout-filters > .el-input { width: 240px; }.layout-filters > .el-select { width: 150px; }
.layout-name { display: block; overflow-wrap: anywhere; }.monitor-summary { display: block; margin-top: 4px; }
.layout-usage-detail { max-height: 300px; overflow: auto; overflow-wrap: anywhere; }.usage-station { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px; padding: 10px 0; border-bottom: 1px solid var(--scada-border); }.usage-station p { margin: 4px 0 0; }
.template-select { width: 170px; margin-right: 8px; }.template-preview { padding: 10px; border: 1px solid var(--scada-border); border-radius: 6px; margin-bottom: 14px; font-size: 12px; }.template-preview summary { cursor: pointer; }.template-preview .layout-thumbnails { margin-top: 8px; }
.station-publish-help, .draft-shape-error { font-size: 12px; line-height: 1.6; margin: 8px 0 0; }.station-publish-help { color: var(--scada-text-muted); }.draft-shape-error { color: var(--scada-warning); }
.canvas-panel { min-width: 0; }
.region-properties { min-width: 0; border: 1px solid var(--scada-border); border-radius: 8px; padding: 16px; background: var(--scada-panel); }
.region-properties h4 { margin: 0 0 16px; overflow-wrap: anywhere; }
.region-properties :deep(.el-select), .position-fields :deep(.el-input-number) { width: 100%; }
.position-fields { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0 12px; }
.monitor-toolbar { flex-wrap: wrap; gap: 12px; margin: 4px 0 14px; }
.monitor-toolbar label, .canvas-toolbar label { display: inline-flex; align-items: center; gap: 8px; font-size: 13px; }
.monitor-toolbar :deep(.el-input-number) { width: 94px; }
.canvas-toolbar { display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; }
.canvas-toolbar :deep(.el-select) { width: 95px; }
.preview-frame { display: flex; justify-content: center; padding: 10px; background: #0a101a; border: 1px solid var(--scada-border); border-radius: 8px; }
.preview-console { width: 100%; min-width: 0; display: flex; flex-direction: column; overflow: hidden; }
.preview-topbar, .preview-alarm { flex: 0 0 auto; padding: 5px 8px; color: #dce6f0; font-size: 11px; background: #101b2d; }
.preview-alarm { background: #1a2533; border-block: 1px solid #3b5878; }
:global(.layout-runtime-dialog) { width: min(1400px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.layout-runtime-dialog .el-dialog__body) { overflow: auto; min-height: 0; }
.region-picker { display: flex; flex-wrap: wrap; gap: 6px; margin: 10px 0; }
.region-picker :deep(.el-button) { margin: 0; max-width: 100%; overflow: hidden; text-overflow: ellipsis; }
.layout-issues { margin-bottom: 12px; padding: 10px 12px; border: 1px solid var(--scada-warning); border-radius: 6px; max-height: 150px; overflow: auto; }
.layout-issues strong { display: block; font-size: 13px; }
.issue-link { display: block; border: 0; background: none; color: var(--scada-warning); text-align: left; padding: 4px 0; cursor: pointer; text-decoration: underline; overflow-wrap: anywhere; }
.issue-link:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: 2px; }
:global(.layout-editor) { width: min(1440px, calc(100vw - 24px)); max-height: calc(100dvh - 32px); margin: 16px auto; display: flex; flex-direction: column; }
:global(.layout-editor .el-dialog__body) { overflow: auto; min-height: 0; }
:global(.station-editor) { width: min(480px, calc(100vw - 24px)); }
@media (max-width: 900px) {
  .editor { grid-template-columns: minmax(0, 1fr); }
  .region-properties { padding: 12px; }
}
:global(.layout-editor.mobile-layout-editor) { width: 100vw !important; max-width: 100vw; height: 100dvh; max-height: 100dvh; margin: 0 !important; padding: 12px; border-radius: 0; box-sizing: border-box; }
:global(.mobile-layout-editor .el-dialog__body) { flex: 1; }
:global(.mobile-layout-editor .el-dialog__header), :global(.mobile-layout-editor .el-dialog__footer) { flex-shrink: 0; }
:global(.mobile-layout-editor .el-dialog__footer .el-button) { min-height: 44px; }
:global(.mobile-layout-editor .mobile-section-summary) { display: list-item; list-style-position: inside; }
:global(.mobile-layout-editor .editor-primary-form) { display: flex; flex-wrap: wrap; gap: 8px; margin-bottom: 8px; }
:global(.mobile-layout-editor .editor-primary-form .el-form-item) { margin: 0; }
:global(.mobile-layout-editor .editor-primary-form > :first-child) { flex: 1 1 100%; }
:global(.mobile-layout-editor .editor-primary-form > :first-child .el-form-item__label) { width: 48px !important; }
:global(.mobile-layout-editor .editor-primary-form > :first-child .el-input) { width: 100% !important; }
:global(.mobile-layout-editor .editor-primary-actions .el-button) { min-height: 44px; }
:global(.mobile-layout-editor .editor-layout-settings), :global(.mobile-layout-editor .monitor-settings) { border: 1px solid var(--scada-border); border-radius: 8px; margin-bottom: 8px; }
:global(.mobile-layout-editor .editor-layout-settings[open]) { padding: 0 10px 10px; }
:global(.mobile-layout-editor .editor-layout-settings[open] > .mobile-section-summary) { padding-inline: 2px; }
:global(.mobile-layout-editor .monitor-settings[open] .monitor-toolbar) { padding-inline: 10px; }
:global(.mobile-layout-editor .monitor-settings > .drag-hint) { padding-inline: 10px; }
:global(.mobile-layout-editor .layout-issues) { padding: 0 10px; margin-bottom: 8px; }
:global(.mobile-layout-editor .layout-issues > summary) { min-height: 44px; align-content: center; cursor: pointer; }
:global(.mobile-layout-editor .layout-issues strong) { display: inline; }
:global(.mobile-layout-editor .keyboard-help) { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
:global(.mobile-layout-editor .canvas-toolbar) { margin-bottom: 8px; }
:global(.mobile-layout-editor .preview-frame) { padding: 6px; }
:global(.mobile-layout-editor .region-properties) { padding: 0; width: 100%; box-sizing: border-box; }
:global(.mobile-layout-editor .region-properties[open] .region-properties-body) { padding: 0 12px 12px; }
:global(.mobile-layout-editor .region-picker) { flex-wrap: nowrap; overflow-x: auto; padding: 2px; }
:global(.mobile-layout-editor .region-picker .el-button) { flex-shrink: 0; min-height: 44px; }
@media (max-width: 600px) {
  .layout-filters > .el-input { width: 100%; }
  .layout-filters > .el-select { flex: 1; min-width: 0; }
  .monitor-toolbar :deep(.el-button), .region-actions :deep(.el-button), .region-picker :deep(.el-button) { min-height: 44px; }
  .monitor-toolbar :deep(.el-button) { margin: 0; }
  :global(.layout-editor .el-form-item) { display: flex; margin-right: 0; }
  :global(.layout-editor .el-form-item__content) { min-width: 0; flex-wrap: wrap; gap: 8px; }
  :global(.layout-editor .el-input) { max-width: 100%; }
}
.preview { width: 100%; display: grid; gap: 3px; background-color: #0d1622; background-image: linear-gradient(to right, #203149 1px, transparent 1px), linear-gradient(to bottom, #203149 1px, transparent 1px); background-size: calc(100% / var(--grid-cols)) calc(100% / var(--grid-rows)); padding: 4px; border-radius: 4px; box-sizing: border-box; }
.preview > div { min-width: 0; min-height: 0; background: #22344d; color: #dce6f0; font-size: 13px; padding: 6px 8px; overflow: hidden; border-radius: 3px; position: relative; cursor: move; touch-action: none; user-select: none; }
.preview > div.is-selected { outline: 2px solid #8fc2ff; outline-offset: -2px; z-index: 1; }
.preview > div:focus-visible { outline: 3px solid #b7dbff; outline-offset: -3px; }
.preview > div strong { display: block; font-weight: 500; }
.preview .region-id { display: block; opacity: .7; font-size: 11px; }
.preview > div.is-out { outline: 2px solid var(--scada-danger); outline-offset: -2px; }
.preview > div.is-overlap, .preview > div.is-invalid { box-shadow: inset 0 0 0 2px var(--scada-danger); }
.preview > div.is-dragging { opacity: 0.78; }
.preview .resize {
  position: absolute;
  right: 0;
  bottom: 0;
  width: 18px;
  height: 18px;
  cursor: nwse-resize;
  touch-action: none;
  background: linear-gradient(135deg, transparent 48%, #8fb3d9 48%);
}
.drag-hint { margin: 0 0 10px; font-size: 12px; color: var(--scada-text-muted); line-height: 1.6; }
.preview small { display: block; color: #8fb3d9; }
.preview { flex: 1; min-height: 0; }
.preview .k-alarmBanner { background: #7a1010; } .preview .k-alarmList { background: #5c3b10; } .preview .k-navigation { background: #2c3e50; }
</style>

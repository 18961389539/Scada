<template>
  <div class="page">
    <p class="page-desc" data-test="workbench-setup-help">{{ $t('workbench.configHelp') }}</p>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="layouts-load-error" />
    <el-button v-if="loadError" data-test="layouts-retry" @click="load()">{{ $t('common.retry') }}</el-button>
    <el-alert v-if="stationsError" :title="stationsError" type="error" :closable="false" show-icon data-test="stations-load-error" />
    <el-alert v-if="screensError" :title="screensError" type="warning" :closable="false" show-icon data-test="layouts-screens-error" />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="layouts-action-error" />
    <el-button v-if="deleteConflict" data-test="layouts-conflict-reload" :loading="loading" @click="reloadAfterDeleteConflict">{{ $t('layoutSafety.reloadLists') }}</el-button>
    <el-alert v-if="recoveryError" :title="recoveryError" type="warning" :closable="false" show-icon data-test="layout-recovery-error" />
    <section v-if="savedDraft" class="panel saved-draft-next" data-test="layout-saved-next" role="status">
      <span>{{ $t('workbench.savedHint', [savedDraft.name]) }}</span>
      <WorkbenchTooltip :content="$t('workbenchTips.publishReview')"><el-button type="primary" data-test="layout-saved-publish" @click="lifecycle?.openPublish(savedDraft.id)">{{ $t('workbench.publishNow') }}</el-button></WorkbenchTooltip>
    </section>
    <section v-if="recoveries.length" class="panel local-drafts" data-test="layout-local-drafts">
      <strong>{{ $t('layoutRecovery.available', [recoveries.length]) }}</strong><p class="tiny">{{ $t('layoutRecovery.help') }}</p>
      <div class="local-draft-list"><div v-for="draft in recoveries" :key="draft.id" class="local-draft-row" data-test="layout-recovery-item"><span>{{ draft.content.name || $t('layoutRecovery.unnamed') }} · {{ formatTime(draft.savedAt) }}</span><div><WorkbenchTooltip :content="$t('workbenchTips.restore')" :disabled-trigger="(visible || restoring)"><el-button size="small" :disabled="visible || restoring" data-test="layout-recover" @click="restoreLocalDraft(draft)">{{ $t('layoutRecovery.restore') }}</el-button></WorkbenchTooltip><WorkbenchTooltip :content="$t('workbenchTips.discardLocal')" :disabled-trigger="(visible || restoring)"><el-button size="small" :disabled="visible || restoring" data-test="layout-recovery-discard" @click="discardLocalDraft(draft)">{{ $t('layoutRecovery.discard') }}</el-button></WorkbenchTooltip></div></div></div>
    </section>
    <div class="panel local-workstation-summary" data-test="local-workstation-summary">
      <div class="local-summary-info">
        <strong>{{ $t('layoutCatalog.thisWorkstation') }} · {{ localStation?.name || localName || $t('layoutsView.ke6c8c4') }}</strong>
        <template v-if="localStation && !stationsError && !loadError">
          <span>{{ $t('layoutCatalog.assignedLayout') }}：{{ stationLayoutLabel(localStation.layoutId) }}<template v-if="localLayout?.publishedRevision != null"> · V{{ localLayout.publishedRevision }}</template></span>
          <el-tag size="small" :class="{ 'tag-quiet': localStation.allowControl }" :type="localStation.allowControl ? undefined : 'warning'">{{ $t(localStation.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag>
        </template>
        <span class="local-summary-message" :class="{ warning: !localReady }">{{ localSummaryMessage }}</span>
      </div>
      <div class="local-summary-actions">
        <WorkbenchTooltip :content="$t('workbenchTips.enterLocal')" :disabled-trigger="(!localReady)"><el-button type="primary" :disabled="!localReady" data-test="layout-open-local" @click="router.push('/console')">{{ $t('layoutCatalog.enterRuntime') }}</el-button></WorkbenchTooltip>
        <WorkbenchTooltip v-if="localStation && !stationsError" :content="$t('workbenchTips.stationLayout')"><el-button data-test="layout-edit-local" @click="editStation(localStation)">{{ $t('layoutCatalog.editAssignment') }}</el-button></WorkbenchTooltip>
        <el-button v-else @click="focusStations">{{ $t('layoutCatalog.configureWorkstation') }}</el-button>
      </div>
    </div>
    <div class="panel" v-loading="loading">
    <div class="toolbar"><h3>{{ $t('workbench.configurationTitle') }}</h3><span class="spacer" />
      <WorkbenchTooltip :content="$t('workbenchTips.quickCreate')" :disabled-trigger="(!!loadError || loading)"><el-button type="primary" data-test="layout-quick-create" :disabled="!!loadError || loading" @click="quickCreate?.open()">{{ $t('workbench.create') }}</el-button></WorkbenchTooltip>
      <WorkbenchTooltip :content="$t('workbenchTips.advancedCreate')" :disabled-trigger="(!!loadError)"><el-button data-test="layout-new" :disabled="!!loadError" @click="edit()">{{ $t('layoutsView.kd6308f') }}</el-button></WorkbenchTooltip><el-button :loading="loading" @click="load()">{{ $t('layoutsView.k38108e') }}</el-button></div>
    <div class="layout-filters">
      <WorkbenchTooltip :content="$t('workbenchTips.search')"><el-input v-model="layoutSearch" clearable :placeholder="$t('layoutCatalog.search')" :aria-label="$t('layoutCatalog.search')" data-test="layouts-search" /></WorkbenchTooltip>
      <WorkbenchTooltip :content="$t('workbenchTips.statusFilter')"><el-select v-model="statusFilter" :aria-label="$t('layoutCatalog.statusFilter')" data-test="layouts-status-filter"><el-option v-for="value in ['all', 'published', 'unpublished', 'changes']" :key="value" :value="value" :label="$t(`layoutCatalog.status_${value}`)" /></el-select></WorkbenchTooltip>
      <WorkbenchTooltip :content="$t('workbenchTips.usageFilter')"><el-select v-model="usageFilter" :disabled="!!stationsError" :aria-label="$t('layoutCatalog.usageFilter')" data-test="layouts-usage-filter"><el-option v-for="value in ['all', 'used', 'unused']" :key="value" :value="value" :label="$t(`layoutCatalog.usage_${value}`)" /></el-select></WorkbenchTooltip>
      <span class="tiny" data-test="layouts-result-count">{{ $t('layoutCatalog.resultCount', [filteredLayouts.length, layouts.length]) }}</span>
      <el-button v-if="layoutSearch || statusFilter !== 'all' || usageFilter !== 'all'" size="small" link data-test="layouts-reset-filters" @click="resetFilters">{{ $t('layoutCatalog.resetFilters') }}</el-button>
    </div>
    <div v-if="isMobile" class="catalog-cards" data-test="layout-cards">
      <p v-if="!filteredLayouts.length" class="card-empty" role="status" data-test="layout-cards-empty">{{ loadError && !layouts.length ? loadError : layouts.length ? $t('layoutCatalog.noMatches') : $t('layoutsView.kd0ba6b') }}</p>
      <article v-for="row in filteredLayouts" :key="row.id" class="catalog-card" data-test="layout-card" :data-layout-id="row.id" :aria-label="row.name">
        <h4>{{ row.name }}</h4>
        <div class="layout-state" data-test="layout-lifecycle-status"><el-tag :class="{ 'tag-quiet': row.publishedRevision != null }" :type="row.publishedRevision != null ? undefined : 'info'" size="small">{{ row.publishedRevision != null ? $t('layoutLifecycle.publishedVersion', [row.publishedRevision]) : $t('layoutLifecycle.unpublished') }}</el-tag><el-tag v-if="row.publishedRevision != null && row.hasDraftChanges" type="warning" size="small">{{ $t('layoutLifecycle.draftChanges') }}</el-tag><span v-if="row.publishedName && row.publishedName !== row.name" class="tiny">{{ $t('layoutLifecycle.runtimeName', [row.publishedName]) }}</span></div>
        <div class="card-preview"><span class="tiny">{{ $t('layoutCatalog.draftStructure') }} · {{ monitorSummary(row) }}</span><LayoutThumbnail :document="row.document" :screen-names="screenNames" /></div>
        <span v-if="stationsError" class="tiny" data-test="layout-usage">{{ $t('layoutImprovements.usageUnknown') }}</span>
        <details v-else class="card-usage" data-test="layout-card-usage">
          <summary data-test="layout-usage">{{ $t('layoutImprovements.workstationCount', [layoutStations(row.id).length]) }}</summary>
          <p v-if="!layoutStations(row.id).length" class="tiny">{{ $t('layoutCatalog.noWorkstations') }}</p>
          <div v-for="station in layoutStations(row.id)" :key="station.id" class="usage-station"><div><strong>{{ station.name }}</strong> <el-tag v-if="localStation?.id === station.id" size="small" type="info">{{ $t('layoutImprovements.thisStation') }}</el-tag><p v-if="station.description" class="tiny">{{ station.description }}</p></div><el-tag size="small" :class="{ 'tag-quiet': station.allowControl }" :type="station.allowControl ? undefined : 'warning'">{{ $t(station.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag></div>
        </details>
        <p class="tiny card-updated">r{{ row.revision }} · {{ row.updatedBy }} · {{ formatTime(row.updatedTs) }}</p>
        <LayoutCatalogActions :ref="(instance: unknown) => rememberPopup(`menu-${row.id}`, instance)" :layout="row" mobile @open="router.push(`/console/${row.id}`)" @edit="edit(row)" @copy="edit(row, true)" @publish="lifecycle?.openPublish(row.id)" @history="lifecycle?.openHistory(row.id)" @remove="remove(row)" />
      </article>
    </div>
    <el-table v-else :data="filteredLayouts" size="small" data-test="layouts" :empty-text="loadError && !layouts.length ? loadError : layouts.length ? $t('layoutCatalog.noMatches') : $t('layoutsView.kd0ba6b')">
      <el-table-column :label="$t('layoutsView.k1be7ae')" min-width="280" show-overflow-tooltip><template #default="{ row }">
        <strong class="layout-name">{{ row.name }}</strong><span class="tiny">{{ $t('layoutCatalog.draftStructure') }}</span>
        <LayoutThumbnail :document="row.document" :screen-names="screenNames" compact />
        <span class="tiny monitor-summary">{{ monitorSummary(row) }}</span>
      </template></el-table-column>
      <el-table-column :label="$t('layoutLifecycle.status')" min-width="160" show-overflow-tooltip><template #default="{ row }"><div class="layout-state" data-test="layout-lifecycle-status"><el-tag :class="{ 'tag-quiet': row.publishedRevision != null }" :type="row.publishedRevision != null ? undefined : 'info'" size="small">{{ row.publishedRevision != null ? $t('layoutLifecycle.publishedVersion', [row.publishedRevision]) : $t('layoutLifecycle.unpublished') }}</el-tag><el-tag v-if="row.publishedRevision != null && row.hasDraftChanges" type="warning" size="small">{{ $t('layoutLifecycle.draftChanges') }}</el-tag><span v-if="row.publishedName && row.publishedName !== row.name" class="tiny">{{ $t('layoutLifecycle.runtimeName', [row.publishedName]) }}</span></div></template></el-table-column>
      <el-table-column :label="$t('layoutImprovements.usage')" min-width="115"><template #default="{ row }">
        <span v-if="stationsError" class="tiny" data-test="layout-usage">{{ $t('layoutImprovements.usageUnknown') }}</span>
        <el-popover v-else :ref="(instance: unknown) => rememberPopup(`usage-${row.id}`, instance)" placement="bottom" :width="320" :popper-style="{ maxWidth: 'calc(100vw - 24px)' }" trigger="click" @keydown.esc.stop="closePopups">
          <template #reference><el-button link size="small" data-test="layout-usage" :aria-label="$t('layoutCatalog.usageFor', [row.name])">{{ $t('layoutImprovements.workstationCount', [layoutStations(row.id).length]) }}</el-button></template>
          <div class="layout-usage-detail" :data-test="`layout-usage-detail-${row.id}`"><strong>{{ $t('layoutCatalog.usageFor', [row.name]) }}</strong><p v-if="!layoutStations(row.id).length" class="tiny">{{ $t('layoutCatalog.noWorkstations') }}</p>
            <div v-for="station in layoutStations(row.id)" :key="station.id" class="usage-station"><div><strong>{{ station.name }}</strong> <el-tag v-if="localStation?.id === station.id" size="small" type="info">{{ $t('layoutImprovements.thisStation') }}</el-tag><p v-if="station.description" class="tiny">{{ station.description }}</p></div><el-tag size="small" :class="{ 'tag-quiet': station.allowControl }" :type="station.allowControl ? undefined : 'warning'">{{ $t(station.allowControl ? 'layoutCatalog.controlAllowed' : 'layoutsView.ka18e06') }}</el-tag></div>
          </div>
        </el-popover>
      </template></el-table-column>
      <el-table-column :label="$t('layoutsView.kc9c775')" min-width="150" show-overflow-tooltip><template #default="{ row }"><span class="tiny">r{{ row.revision }} · {{ row.updatedBy }}<br />{{ formatTime(row.updatedTs) }}</span></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kf3ea6d')" min-width="245" show-overflow-tooltip><template #default="{ row }"><LayoutCatalogActions :ref="(instance: unknown) => rememberPopup(`menu-${row.id}`, instance)" :layout="row" @open="router.push(`/console/${row.id}`)" @edit="edit(row)" @copy="edit(row, true)" @publish="lifecycle?.openPublish(row.id)" @history="lifecycle?.openHistory(row.id)" @remove="remove(row)" /></template></el-table-column>
    </el-table>
  </div>
  <div ref="stationsPanel" class="panel station-panel" tabindex="-1">
    <div class="toolbar"><h3>{{ $t('layoutsView.k90ec9a') }}</h3><el-tag size="small" :class="{ 'tag-quiet': !localInvalid && !!localName }" :type="localInvalid ? 'danger' : localName ? undefined : 'info'" data-test="local-station">{{ $t('layoutsView.kfedb9d', [localName || $t('layoutsView.ke6c8c4')]) }}</el-tag>
      <WorkbenchTooltip v-if="localName" :content="$t('workbenchTips.clearLocal')"><el-button size="small" link @click="setLocal(null)">{{ $t('layoutsView.kb398ea') }}</el-button></WorkbenchTooltip><span class="spacer" />
      <WorkbenchTooltip :content="$t('workbenchTips.stationName')"><el-button data-test="station-new" @click="editStation()">{{ $t('layoutsView.kb321d8') }}</el-button></WorkbenchTooltip></div>
    <p class="page-desc">{{ $t('layoutsView.kc0704a') }}</p>
    <el-alert v-if="localInvalid" :title="$t('layoutImprovements.localInvalid')" type="warning" :closable="false" show-icon data-test="local-station-invalid" />
    <div v-if="isMobile" class="catalog-cards" data-test="station-cards">
      <p v-if="!stations.length" class="card-empty" role="status" data-test="station-cards-empty">{{ stationsError || loadError || $t('layoutsView.kfe2078') }}</p>
      <article v-for="row in stations" :key="row.id" class="catalog-card" data-test="station-card" :data-station-id="row.id" :aria-label="row.name">
        <div class="card-heading"><h4>{{ row.name }}</h4><el-tag v-if="isLocalWorkstation(row)" size="small" type="info">{{ $t('layoutImprovements.thisStation') }}</el-tag></div>
        <p v-if="row.description" class="card-description">{{ row.description }}</p>
        <dl class="card-fields"><dt>{{ $t('layoutsView.kb0961e') }}</dt><dd>{{ stationLayoutLabel(row.layoutId) }}</dd><dt>{{ $t('layoutsView.k223826') }}</dt><dd><el-tag size="small" :type="row.allowControl ? undefined : 'warning'" :class="{ 'tag-quiet': row.allowControl }">{{ row.allowControl ? $t('layoutsView.k4c0c0a') : $t('layoutsView.ka18e06') }}</el-tag></dd></dl>
        <div class="station-card-actions"><el-button size="small" type="primary" @click="editStation(row)">{{ $t('layoutsView.ka7f814') }}</el-button><WorkbenchTooltip :content="$t('workbenchTips.localStation')" :disabled-trigger="(isLocalWorkstation(row))"><el-button size="small" :disabled="isLocalWorkstation(row)" :data-test="`station-local-${row.name}`" @click="setLocal(row)">{{ $t('layoutsView.kbffbf3') }}</el-button></WorkbenchTooltip><el-button size="small" type="danger" plain @click="removeStation(row)">{{ $t('layoutsView.k3755f5') }}</el-button></div>
      </article>
    </div>
    <el-table v-else :data="stations" size="small" data-test="stations" :empty-text="(stationsError || loadError) && !stations.length ? (stationsError || loadError) : $t('layoutsView.kfe2078')">
      <el-table-column :label="$t('layoutsView.k1be7ae')" min-width="140" show-overflow-tooltip><template #default="{ row }">{{ row.name }} <el-tag v-if="isLocalWorkstation(row)" size="small" type="info">{{ $t('layoutImprovements.thisStation') }}</el-tag></template></el-table-column>
      <el-table-column prop="description" :label="$t('layoutsView.k26670d')" min-width="160" show-overflow-tooltip />
      <el-table-column :label="$t('layoutsView.kb0961e')" min-width="140" show-overflow-tooltip><template #default="{ row }">{{ stationLayoutLabel(row.layoutId) }}</template></el-table-column>
      <el-table-column :label="$t('layoutsView.k223826')" width="90" show-overflow-tooltip><template #default="{ row }"><el-tag size="small" :type="row.allowControl ? undefined : 'warning'" :class="{ 'tag-quiet': row.allowControl }">{{ row.allowControl ? $t('layoutsView.k4c0c0a') : $t('layoutsView.ka18e06') }}</el-tag></template></el-table-column>
      <el-table-column :label="$t('layoutsView.kf3ea6d')" width="230"><template #default="{ row }">
        <WorkbenchTooltip :content="$t('workbenchTips.localStation')"><el-button size="small" :data-test="`station-local-${row.name}`" @click="setLocal(row)">{{ $t('layoutsView.kbffbf3') }}</el-button></WorkbenchTooltip>
        <el-button size="small" @click="editStation(row)">{{ $t('layoutsView.ka7f814') }}</el-button><el-button size="small" type="danger" @click="removeStation(row)">{{ $t('layoutsView.k3755f5') }}</el-button>
      </template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="visible" :title="form.id ? $t('layoutsView.kf7d900') : $t('layoutsView.kd6308f')" class="layout-editor" :class="{ 'mobile-layout-editor': isMobile }" :before-close="closeLayout" data-test="layout-dialog" @keydown="editorShortcut">
    <p v-if="!isMobile" class="page-desc">{{ $t('workbench.advancedHelp') }}</p>
    <el-alert v-if="layoutError" :title="layoutError" type="error" :closable="false" show-icon data-test="layout-save-error" />
    <el-alert v-if="recoveryError" :title="recoveryError" type="warning" :closable="false" show-icon />
    <el-button v-if="layoutConflict" :disabled="saving" data-test="layout-draft-reload" @click="reloadSavedDraft">{{ $t('layoutLifecycle.reloadDraft') }}</el-button>
    <p v-if="recoverySavedAt && layoutDirty" class="tiny" data-test="layout-local-saved">{{ $t('layoutRecovery.savedAt', [formatTime(recoverySavedAt)]) }}</p>
    <el-form label-width="80px" size="small" inline class="editor-primary-form" @submit.prevent>
      <el-form-item :label="$t('layoutsView.k1be7ae')"><WorkbenchTooltip :content="$t('workbenchTips.layoutName')"><el-input v-model="form.name" maxlength="100" style="width: 220px" data-test="layout-name" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
      <el-form-item class="editor-primary-actions"><WorkbenchTooltip :content="$t('workbenchTips.undo')" :disabled-trigger="(historyIndex <= 0)"><el-button data-test="layout-undo" :disabled="historyIndex <= 0" @click="undo">{{ $t('layoutImprovements.undo') }}</el-button></WorkbenchTooltip><WorkbenchTooltip :content="$t('workbenchTips.redo')" :disabled-trigger="(historyIndex >= history.length - 1)"><el-button data-test="layout-redo" :disabled="historyIndex >= history.length - 1" @click="redo">{{ $t('layoutImprovements.redo') }}</el-button></WorkbenchTooltip><WorkbenchTooltip :content="$t('layoutPreview.readOnlyHelp')" :disabled-trigger="(!!draftShapeError || !!layoutIssues.length)"><el-button data-test="layout-runtime-preview" :disabled="!!draftShapeError || !!layoutIssues.length" @click="runtimePreviewVisible = true">{{ $t('layoutPreview.runtimePreview') }}</el-button></WorkbenchTooltip></el-form-item>
    </el-form>
    <div class="editor-mode" data-test="layout-editor-mode">
      <WorkbenchTooltip :content="$t('workbenchTips.advanced')" wrap-trigger><el-button :type="editorAdvanced ? 'primary' : 'default'" :aria-expanded="editorAdvanced" data-test="layout-advanced-toggle" @click="toggleAdvancedSettings">{{ $t(editorAdvanced ? 'workbench.hideAdvanced' : 'workbench.showAdvanced') }}</el-button></WorkbenchTooltip>
      <span class="editor-mode-help">{{ $t(isMobile ? 'workbench.basicEditMobileHelp' : 'workbench.basicEditHelp') }}</span>
    </div>
    <details class="editor-layout-settings" :open="!isMobile || layoutSettingsOpen" data-test="layout-settings" @toggle="layoutSettingsOpen = ($event.target as HTMLDetailsElement).open">
      <summary class="mobile-section-summary">{{ $t('layoutGeometry.layoutSettings') }} <el-tag v-if="copiedFrom || legacyConverted" size="small" type="warning">{{ $t('layoutGeometry.hasNotice') }}</el-tag></summary>
      <el-alert :title="$t('layoutLifecycle.draftHelp')" type="info" :closable="false" show-icon />
      <el-alert v-if="copiedFrom" :title="$t('layoutCatalog.copyHelp', [copiedFrom])" type="info" :closable="false" data-test="layout-copy-help" />
      <el-alert v-if="legacyConverted" :title="$t('layoutPreview.legacyConverted', [legacyConverted])" type="warning" :closable="false" show-icon data-test="layout-banner-converted" />
      <el-form size="small" inline @submit.prevent><el-form-item><WorkbenchTooltip :content="$t('workbenchTips.template')"><el-select v-model="templateKey" class="template-select" :aria-label="$t('layoutCatalog.template')" data-test="layout-template"><el-option v-for="key in layoutTemplateKeys" :key="key" :value="key" :label="$t(`layoutCatalog.template_${key}`)" /></el-select></WorkbenchTooltip><WorkbenchTooltip :content="$t('workbenchTips.template')"><el-button data-test="layout-preset" @click="replacePreset">{{ $t('layoutCatalog.applyTemplate') }}</el-button></WorkbenchTooltip><WorkbenchTooltip v-if="(editorAdvanced)" :content="$t('workbenchTips.addMonitor')" :disabled-trigger="(form.document.monitors.length >= 8)"><el-button data-test="layout-add-monitor" :disabled="form.document.monitors.length >= 8" @click="addMonitor()">{{ $t('layoutsView.k968c70') }} ({{ form.document.monitors.length }}/8)</el-button></WorkbenchTooltip></el-form-item></el-form>
      <p v-show="editorAdvanced" class="drag-hint editor-shortcuts" data-test="layout-editor-shortcuts">{{ $t('layoutEditing.shortcuts') }}</p>
      <details class="template-preview" data-test="layout-template-preview"><summary>{{ $t('layoutCatalog.templatePreview') }} · {{ $t(`layoutCatalog.template_${templateKey}`) }}</summary><p class="tiny">{{ $t('layoutCatalog.templateHelp') }}</p><LayoutThumbnail :document="templateDocument" :screen-names="screenNames" /></details>
      <p v-show="editorAdvanced" class="drag-hint">{{ $t('layoutEditing.monitorOrderHelp') }}</p>
    </details>
    <el-alert v-if="!screensError && !screens.length && !isMobile" :title="$t('layoutImprovements.noPublishedScreens')" type="warning" :closable="false" show-icon />
    <details v-if="layoutIssues.length" class="layout-issues" :open="!isMobile || issuesOpen" role="status" data-test="layout-region-issues" @toggle="issuesOpen = ($event.target as HTMLDetailsElement).open">
      <summary><strong>{{ $t('layoutLifecycle.draftIssues', [layoutIssues.length]) }}</strong></summary>
      <WorkbenchTooltip v-for="(issue, index) in layoutIssues" :key="index" :content="$t('workbenchTips.issue')"><button class="issue-link" type="button" data-test="layout-issue" @click="focusIssue(issue)">{{ issue.message }}</button></WorkbenchTooltip>
    </details>
    <el-tabs v-model="monitorTab" type="card">
      <el-tab-pane v-for="(m, mi) in form.document.monitors" :key="mi" :name="String(mi)">
        <template #label><span>{{ m.name || $t('layoutsView.k47eff4', [mi + 1]) }} <el-tag v-if="monitorIssues(m, mi).length" type="danger" size="small">{{ monitorIssues(m, mi).length }}</el-tag></span></template>
        <details class="monitor-settings" :open="!isMobile || monitorSettingsOpen" data-test="layout-monitor-settings" @toggle="monitorTab === String(mi) && (monitorSettingsOpen = ($event.target as HTMLDetailsElement).open)">
        <summary class="mobile-section-summary">{{ $t('layoutGeometry.monitorSettings') }} · {{ $t('layoutGeometry.gridSummary', [m.columns, m.rows, m.regions.length]) }}</summary>
        <div class="toolbar monitor-toolbar">
          <label>{{ $t('layoutsView.k1be7ae') }} <WorkbenchTooltip :content="$t('workbenchTips.monitorName')"><el-input v-model="m.name" size="small" maxlength="40" style="width: 140px" data-test="monitor-name" @change="recordHistory" /></WorkbenchTooltip></label>
          <label v-show="editorAdvanced">{{ $t('layoutsView.kad3ee6') }} <WorkbenchTooltip :content="$t('workbenchTips.gridColumns')"><el-input-number :key="`columns-${gridControlVersion}`" :model-value="m.columns" :min="1" :max="24" :disabled="!!pendingGrid" size="small" data-test="monitor-columns" @change="changeGridSize(m, $event, m.rows)" /></WorkbenchTooltip></label>
          <label v-show="editorAdvanced">{{ $t('layoutsView.k2a2b77') }} <WorkbenchTooltip :content="$t('workbenchTips.gridRows')"><el-input-number :key="`rows-${gridControlVersion}`" :model-value="m.rows" :min="1" :max="24" :disabled="!!pendingGrid" size="small" data-test="monitor-rows" @change="changeGridSize(m, m.columns, $event)" /></WorkbenchTooltip></label>
          <label v-show="editorAdvanced">{{ $t('layoutPreview.targetWidth') }} <WorkbenchTooltip :content="$t('workbenchTips.targetSize')"><el-input-number :model-value="m.previewWidth ?? 1920" :min="320" :max="7680" size="small" data-test="monitor-preview-width" @change="setPreviewSize(m, $event, m.previewHeight ?? 1080)" /></WorkbenchTooltip></label>
          <label v-show="editorAdvanced">{{ $t('layoutPreview.targetHeight') }} <WorkbenchTooltip :content="$t('workbenchTips.targetSize')"><el-input-number :model-value="m.previewHeight ?? 1080" :min="240" :max="4320" size="small" data-test="monitor-preview-height" @change="setPreviewSize(m, m.previewWidth ?? 1920, $event)" /></WorkbenchTooltip></label>
          <WorkbenchTooltip :content="$t('workbenchTips.addRegion')" :disabled-trigger="(m.regions.length >= 16 || !freeCell(m))"><el-button size="small" :data-test="`layout-add-region-${mi}`" :disabled="m.regions.length >= 16 || !freeCell(m)" @click="addRegion(m)">{{ $t('layoutsView.k39b1a6') }} ({{ m.regions.length }}/16)</el-button></WorkbenchTooltip>
          <WorkbenchTooltip :content="pasteIssue(m, regionClipboard) || $t('workbenchTips.pasteRegion')" :disabled-trigger="(!!pasteIssue(m, regionClipboard))"><el-button size="small" data-test="layout-paste-region" :disabled="!!pasteIssue(m, regionClipboard)" @click="pasteRegion(m)">{{ $t('layoutEditing.paste') }}</el-button></WorkbenchTooltip>
          <WorkbenchTooltip v-if="(editorAdvanced)" :content="$t('workbenchTips.copyMonitor')" :disabled-trigger="(form.document.monitors.length >= 8)"><el-button size="small" data-test="layout-copy-monitor" :disabled="form.document.monitors.length >= 8" @click="duplicateMonitor(mi)">{{ $t('layoutCatalog.copyMonitor') }}</el-button></WorkbenchTooltip>
          <WorkbenchTooltip v-if="(editorAdvanced)" :content="$t('workbenchTips.orderMonitor')" :disabled-trigger="(mi === 0)"><el-button size="small" data-test="layout-monitor-previous" :disabled="mi === 0" @click="moveMonitor(mi, -1)">{{ $t('layoutEditing.monitorPrevious') }}</el-button></WorkbenchTooltip>
          <WorkbenchTooltip v-if="(editorAdvanced)" :content="$t('workbenchTips.orderMonitor')" :disabled-trigger="(mi === form.document.monitors.length - 1)"><el-button size="small" data-test="layout-monitor-next" :disabled="mi === form.document.monitors.length - 1" @click="moveMonitor(mi, 1)">{{ $t('layoutEditing.monitorNext') }}</el-button></WorkbenchTooltip>
          <WorkbenchTooltip v-if="(form.document.monitors.length > 1) && (editorAdvanced)" :content="$t('workbenchTips.deleteMonitor')"><el-button size="small" type="danger" link data-test="layout-remove-monitor" @click="removeMonitor(mi)">{{ $t('layoutsView.k33160a') }}</el-button></WorkbenchTooltip>
        </div>
        <p v-if="regionClipboard" class="drag-hint" data-test="layout-region-clipboard" role="status">{{ $t('layoutEditing.copied', [regionClipboard.id]) }}<span v-if="pasteIssue(m, regionClipboard)"> · {{ pasteIssue(m, regionClipboard) }}</span></p>
        <p v-if="!freeCell(m) && m.regions.length < 16" class="drag-hint">{{ $t('layoutImprovements.noSpace') }}</p>
        </details>
        <div v-if="monitorTab === String(mi)" class="editor">
          <div class="canvas-panel">
            <div class="canvas-toolbar"><strong>{{ $t('layoutImprovements.preview') }}</strong><label v-show="editorAdvanced">{{ $t('layoutImprovements.aspectRatio') }} <WorkbenchTooltip :content="$t('workbenchTips.targetSize')"><el-select v-model="previewRatio" size="small" data-test="layout-preview-ratio"><el-option v-for="ratio in ['16:9', '16:10', '4:3', '9:16']" :key="ratio" :label="ratio" :value="ratio" /></el-select></WorkbenchTooltip></label></div>
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
            <LayoutGeometryTools v-show="editorAdvanced" :monitor="m" :selected-index="selectedRegionIndex" @apply="applyGeometry(m, $event)" />
          </div>
          <details class="region-properties" :open="!isMobile || regionPropertiesOpen" :aria-label="$t('layoutImprovements.properties')" data-test="layout-region-properties" @toggle="regionPropertiesOpen = ($event.target as HTMLDetailsElement).open">
            <summary class="mobile-section-summary">{{ $t('layoutImprovements.properties') }} · {{ selectedRegion?.id || '?' }}</summary>
            <div class="region-properties-body">
            <template v-if="selectedRegion">
              <h4 v-if="!isMobile">{{ $t('layoutImprovements.properties') }} · {{ selectedRegion.id || '?' }}</h4>
              <p class="tiny" data-test="region-share">{{ $t('layoutCatalog.regionShare', [Math.round(selectedRegion.colSpan / m.columns * 100), Math.round(selectedRegion.rowSpan / m.rows * 100)]) }}</p>
              <el-form label-position="top" size="small">
                <el-form-item v-show="editorAdvanced" :label="$t('layoutsView.k9f42da')"><WorkbenchTooltip :content="$t('workbenchTips.regionId')"><el-input v-model="selectedRegion.id" maxlength="40" data-test="region-id" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
                <el-form-item :label="$t('layoutsView.ke4e46c')"><WorkbenchTooltip :content="$t('workbenchTips.regionKind')"><el-select v-model="selectedRegion.kind" data-test="region-kind" @change="onRegionKind(selectedRegion)"><el-option v-for="(label, k) in editableKinds" :key="k" :value="k" :label="label" /></el-select></WorkbenchTooltip></el-form-item>
                <el-form-item v-if="selectedRegion.kind === 'screen'" :label="$t('layoutsView.kbfffd0')" :error="!screensError && !validScreen(selectedRegion) ? $t('layoutImprovements.invalidScreen') : ''">
                  <WorkbenchTooltip :content="$t('workbenchTips.screenBinding')"><el-select v-model="selectedRegion.screenId" filterable data-test="region-screen" :disabled="!!screensError" @change="recordHistory"><el-option v-if="selectedRegion.screenId && !validScreen(selectedRegion)" :value="selectedRegion.screenId" :label="screenLabel(selectedRegion)" disabled /><el-option v-for="s in screens" :key="s.id" :value="s.id" :label="s.name" /></el-select></WorkbenchTooltip>
                </el-form-item>
                <template v-if="selectedRegion.kind === 'alarmList'">
                  <el-form-item :label="$t('layoutScope.filter')">
                    <el-select :model-value="alarmScope(selectedRegion)" data-test="region-alarm-scope" @change="setAlarmScope(selectedRegion, $event)">
                      <el-option value="all" :label="$t('layoutScope.all')" />
                      <el-option value="area" :label="$t('layoutScope.area')" />
                      <el-option value="device" :label="$t('layoutScope.device')" />
                      <el-option value="screen" :label="$t('layoutScope.screen')" />
                    </el-select>
                  </el-form-item>
                  <el-form-item v-if="alarmScope(selectedRegion) === 'area'" :label="$t('layoutScope.area')">
                    <el-select v-model="selectedRegion.alarmAreaId" filterable data-test="region-alarm-area" @change="recordHistory"><el-option v-for="area in areas" :key="area.id" :value="area.id" :label="area.name" /></el-select>
                  </el-form-item>
                  <el-form-item v-if="alarmScope(selectedRegion) === 'device'" :label="$t('layoutScope.device')">
                    <el-select v-model="selectedRegion.alarmDeviceId" filterable data-test="region-alarm-device" @change="recordHistory"><el-option v-for="device in devices" :key="device.id" :value="device.id" :label="device.name" /></el-select>
                  </el-form-item>
                  <el-form-item v-if="alarmScope(selectedRegion) === 'screen'" :label="$t('layoutScope.screen')">
                    <el-select v-model="selectedRegion.alarmScreenId" filterable data-test="region-alarm-screen" :disabled="!!screensError" @change="recordHistory"><el-option v-for="screen in screens" :key="screen.id" :value="screen.id" :label="screen.name" /></el-select>
                  </el-form-item>
                  <p v-if="scopeCatalogError" class="draft-shape-error" data-test="region-scope-error">{{ scopeCatalogError }}</p>
                </template>
                <el-form-item :label="$t('layoutsView.k748d7d')"><WorkbenchTooltip :content="$t('workbenchTips.regionTitle')"><el-input v-model="selectedRegion.title" maxlength="60" data-test="region-title" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
                <div v-show="editorAdvanced" class="position-fields">
                  <el-form-item :label="$t('layoutsView.k2a2b77')"><WorkbenchTooltip :content="$t('workbenchTips.regionRow')"><el-input-number v-model="selectedRegion.row" :min="1" :max="Math.max(m.rows, selectedRegion.row || 1)" controls-position="right" data-test="region-row" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
                  <el-form-item :label="$t('layoutsView.kad3ee6')"><WorkbenchTooltip :content="$t('workbenchTips.regionColumn')"><el-input-number v-model="selectedRegion.col" :min="1" :max="Math.max(m.columns, selectedRegion.col || 1)" controls-position="right" data-test="region-col" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
                  <el-form-item :label="$t('layoutImprovements.rowSpan')"><WorkbenchTooltip :content="$t('workbenchTips.regionRowSpan')"><el-input-number v-model="selectedRegion.rowSpan" :min="1" :max="Math.max(1, m.rows - selectedRegion.row + 1, selectedRegion.rowSpan || 1)" controls-position="right" data-test="region-row-span" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
                  <el-form-item :label="$t('layoutImprovements.colSpan')"><WorkbenchTooltip :content="$t('workbenchTips.regionColumnSpan')"><el-input-number v-model="selectedRegion.colSpan" :min="1" :max="Math.max(1, m.columns - selectedRegion.col + 1, selectedRegion.colSpan || 1)" controls-position="right" data-test="region-col-span" @change="recordHistory" /></WorkbenchTooltip></el-form-item>
                </div>
              </el-form>
              <div class="region-actions">
                <WorkbenchTooltip :content="$t('workbenchTips.copyRegion')"><el-button size="small" data-test="layout-copy-region" @click="copyRegion">{{ $t('layoutEditing.copy') }}</el-button></WorkbenchTooltip>
                <WorkbenchTooltip :content="pasteIssue(m, selectedRegion) || $t('workbenchTips.duplicateRegion')" :disabled-trigger="(!!pasteIssue(m, selectedRegion))"><el-button size="small" data-test="layout-duplicate-region" :disabled="!!pasteIssue(m, selectedRegion)" @click="duplicateRegion(m)">{{ $t('layoutEditing.duplicate') }}</el-button></WorkbenchTooltip>
                <WorkbenchTooltip :content="m.regions.length <= 1 ? $t('layoutEditing.keepRegion') : $t('workbenchTips.deleteRegion')" :disabled-trigger="(m.regions.length <= 1)"><el-button type="danger" plain size="small" :disabled="m.regions.length <= 1" data-test="layout-remove-region" @click="removeRegion(m, selectedRegionIndex)">{{ $t('layoutImprovements.deleteRegion') }}</el-button></WorkbenchTooltip>
              </div>
            </template>
            <p v-else class="drag-hint">{{ $t('layoutImprovements.selectRegion') }}</p>
            </div>
          </details>
        </div>
      </el-tab-pane>
    </el-tabs>
    <template #footer><el-button :disabled="saving" @click="closeLayout()">{{ $t('layoutsView.k4d0b46') }}</el-button><WorkbenchTooltip :content="$t('workbenchTips.saveDraft')" :disabled-trigger="(!!draftShapeError || !form.name.trim() || layoutConflict) || (saving)"><el-button data-test="layout-save" :loading="saving" :disabled="!!draftShapeError || !form.name.trim() || layoutConflict" @click="save()">{{ $t('layoutLifecycle.saveDraft') }}</el-button></WorkbenchTooltip><WorkbenchTooltip :content="$t('workbenchTips.saveReview')" :disabled-trigger="(!!draftShapeError || !form.name.trim() || layoutConflict || !!layoutIssues.length || !!screensError) || (saving)"><el-button type="primary" data-test="layout-save-publish" :loading="saving" :disabled="!!draftShapeError || !form.name.trim() || layoutConflict || !!layoutIssues.length || !!screensError" @click="save(true)">{{ $t('workbench.saveAndReview') }}</el-button></WorkbenchTooltip><p v-if="draftShapeError" class="draft-shape-error">{{ draftShapeError }}</p></template>
  </el-dialog>

  <el-dialog :model-value="!!pendingGrid" :title="$t('layoutGeometry.resizeTitle')" class="grid-resize-dialog" append-to-body data-test="layout-grid-resize" @update:model-value="!$event && cancelGridResize()">
    <template v-if="pendingGrid">
      <p>{{ $t('layoutGeometry.resizeAffected', [pendingGrid.monitor.columns, pendingGrid.monitor.rows, pendingGrid.columns, pendingGrid.rows]) }}</p>
      <ul class="resize-affected" data-test="grid-resize-affected"><li v-for="(id, index) in pendingGrid.affected" :key="index">{{ id }}</li></ul>
      <p class="tiny">{{ $t('layoutGeometry.resizeHelp') }}</p>
      <LayoutThumbnail v-if="pendingGridPreview" :document="pendingGridPreview" :screen-names="screenNames" :limit="1" data-test="grid-resize-preview" />
      <el-alert v-else :title="$t('layoutGeometry.fitBlocked')" type="warning" :closable="false" show-icon />
    </template>
    <template #footer><el-button data-test="grid-resize-cancel" @click="cancelGridResize">{{ $t('layoutsView.k4d0b46') }}</el-button><WorkbenchTooltip :content="$t('layoutGeometry.resizeHelp')"><el-button type="warning" plain data-test="grid-resize-keep" @click="applyGridResize(false)">{{ $t('layoutGeometry.keepPositions') }}</el-button></WorkbenchTooltip><WorkbenchTooltip :content="$t('layoutGeometry.fitHelp')" :disabled-trigger="(!pendingGrid?.fitted)"><el-button type="primary" data-test="grid-resize-fit" :disabled="!pendingGrid?.fitted" @click="applyGridResize(true)">{{ $t('layoutGeometry.fitAndResize') }}</el-button></WorkbenchTooltip></template>
  </el-dialog>

  <el-dialog v-model="stationVisible" :title="stationForm.id ? $t('layoutsView.k460078') : $t('layoutsView.kb321d8')" class="station-editor" :before-close="closeStation">
    <el-alert v-if="stationError" :title="stationError" type="error" :closable="false" show-icon data-test="station-save-error" />
    <el-button v-if="stationConflict" :disabled="stationSaving" data-test="station-conflict-reload" @click="reloadStation">{{ $t('layoutSafety.reloadStation') }}</el-button>
    <el-form label-width="90px" size="small">
      <el-form-item :label="$t('layoutsView.k1be7ae')"><WorkbenchTooltip :content="$t('workbenchTips.stationName')"><el-input v-model="stationForm.name" maxlength="60" :placeholder="$t('layoutsView.k803edb')" data-test="station-name" /></WorkbenchTooltip></el-form-item>
      <el-form-item :label="$t('layoutsView.k26670d')"><WorkbenchTooltip :content="$t('workbenchTips.stationDescription')"><el-input v-model="stationForm.description" maxlength="200" show-word-limit /></WorkbenchTooltip></el-form-item>
      <el-form-item :label="$t('layoutsView.kb0961e')"><WorkbenchTooltip :content="$t('workbenchTips.stationLayout')"><el-select v-model="stationForm.layoutId" clearable data-test="station-layout"><el-option v-if="stationForm.layoutId != null && !publishedLayouts.some(l => l.id === stationForm.layoutId)" :value="stationForm.layoutId" :label="stationLayoutLabel(stationForm.layoutId)" disabled /><el-option v-for="l in publishedLayouts" :key="l.id" :value="l.id" :label="l.publishedName || l.name" /></el-select></WorkbenchTooltip><p class="station-publish-help">{{ $t('layoutLifecycle.stationPublishedOnly') }}</p><el-alert v-if="stationForm.layoutId != null && !publishedLayouts.some(l => l.id === stationForm.layoutId)" :title="$t('layoutPreview.legacyBinding')" type="warning" :closable="false" show-icon data-test="station-unpublished-binding" /></el-form-item>
      <el-form-item :label="$t('layoutsView.k54d7df')"><WorkbenchTooltip :content="$t('workbenchTips.stationControl')"><el-switch v-model="stationForm.allowControl" data-test="station-control" /></WorkbenchTooltip></el-form-item>
    </el-form>
    <template #footer><el-button :disabled="stationSaving" @click="closeStation()">{{ $t('layoutsView.k4d0b46') }}</el-button><el-button type="primary" :loading="stationSaving" :disabled="stationConflict" data-test="station-save" @click="saveStation()">{{ $t('layoutsView.kfadf24') }}</el-button></template>
  </el-dialog>
  <QuickLayoutCreate ref="quickCreate" @saved="rememberSavedDraft" @published="id => router.push(`/console/${id}`)" />
  <LayoutVersionHistory ref="lifecycle" @changed="load" />
  <el-dialog v-model="runtimePreviewVisible" :title="$t('layoutPreview.runtimePreview')" class="layout-runtime-dialog" append-to-body destroy-on-close data-test="layout-runtime-dialog">
    <LayoutRuntimePreview v-if="runtimePreviewVisible" :document="form.document" :name="form.name" :initial-monitor="Number(monitorTab)" />
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import WorkbenchTooltip from '../components/console/WorkbenchTooltip.vue'
import LayoutVersionHistory from '../components/console/LayoutVersionHistory.vue'
import LayoutRuntimePreview from '../components/console/LayoutRuntimePreview.vue'
import QuickLayoutCreate from '../components/console/QuickLayoutCreate.vue'
import LayoutThumbnail from '../components/console/LayoutThumbnail.vue'
import LayoutCatalogActions from '../components/console/LayoutCatalogActions.vue'
import LayoutGeometryTools from '../components/console/LayoutGeometryTools.vue'
import { useLayoutsView } from '../composables/useLayoutsView'
const { router, popups, rememberPopup, closePopups, lifecycle, quickCreate, layouts, stations, screens, areas, devices, loadError, layoutError, stationError, scopeCatalogError, scopeCatalogReady, lastSavedId, savedDraft, rememberSavedDraft, actionError, runGuarded, deleteConflict, stationConflict, isConflict, guardDelete, reloadAfterDeleteConflict, stationsError, screensError, loading, saving, stationSaving, editableKinds, getBinding, local, localName, localInvalid, layoutStations, publishedLayouts, screenNames, localStation, localLayout, localReady, localSummaryMessage, layoutSearch, statusFilter, usageFilter, filteredLayouts, resetFilters, monitorSummary, stationsPanel, focusStations, stationLayoutLabel, load, visible, monitorTab, editorAdvanced, toggleAdvancedSettings, layoutSettingsOpen, monitorSettingsOpen, regionPropertiesOpen, issuesOpen, collapseMobilePanels, layoutConflict, form, selectedRegionIndex, runtimePreviewVisible, legacyConverted, regionClipboard, copiedFrom, templateKey, templateDocument, previewRatio, setPreviewSize, gridControlVersion, pendingGrid, pendingGridPreview, cancelGridResize, changeGridSize, applyGridResize, applyGeometry, selectedRegion, history, historyIndex, snapshot, historyEntry, recordHistory, restoreHistory, undo, redo, editorShortcut, layoutBaseline, stationBaseline, layoutDirty, stationDirty, auth, recoveryOwner, recoveries, recoveryError, recoverySavedAt, restoring, refreshRecoveries, persistLocalDraft, forgetLocalDraft, discardLocalDraft, restoreLocalDraft, flushHiddenDraft, confirmDiscard, closeLayout, closeStation, beforeUnload, edit, reloadSavedDraft, replacePreset, removeMonitor, preset, copyName, duplicateMonitor, moveMonitor, pasteIssue, copyRegion, insertRegionCopy, pasteRegion, duplicateRegion, addMonitor, freeCell, addRegion, removeRegion, selectRegion, drag, clamp, gridStyle, previewFrameStyle, regionStyle, overlaps, outOfBounds, regionOverlaps, alarmScope, setAlarmScope, onRegionKind, loadScopeCatalog, validScreen, screenLabel, monitorIssues, layoutIssues, draftShapeError, validPreviewSize, focusIssue, beginDrag, startDrag, startResize, onDragMove, endDrag, regionKeydown, save, remove, stationVisible, stationForm, editStation, reloadStation, saveStation, removeStation, setLocal, syncLocal, ref, isLocalWorkstation, formatTime, isMobile, layoutTemplateKeys, kinds } = useLayoutsView()
</script>

<style scoped>
.saved-draft-next { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; font-size: 14px; line-height: 1.6; overflow-wrap: anywhere; }
.saved-draft-next > span { flex: 1 1 240px; }
.mobile-section-summary { display: none; cursor: pointer; font-size: 13px; line-height: 1.5; min-height: 44px; box-sizing: border-box; padding: 12px; overflow-wrap: anywhere; }
summary:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.editor-primary-actions :deep(.el-form-item__content) { display: flex; gap: 8px; }
.editor-primary-actions :deep(.el-button) { margin: 0; }
.editor-mode { display: flex; align-items: center; flex-wrap: wrap; gap: 10px; margin-bottom: 14px; }
.editor-mode-help { flex: 1 1 260px; font-size: 12px; line-height: 1.6; color: var(--scada-text-muted); }
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
:global(.mobile-layout-editor .editor-mode .el-button) { min-height: 44px; }
:global(.mobile-layout-editor .editor-mode-help) { flex-basis: 100px; }
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
/* 320px 宽时三个底部按钮会换行，挤掉首屏画布；压缩按钮内边距让它们保持一行。 */
@media (max-width: 360px) {
  :global(.mobile-layout-editor .el-dialog__footer .el-button) { padding-inline: 8px; font-size: 12px; }
  :global(.mobile-layout-editor .editor-mode) { margin-bottom: 8px; }
  :global(.mobile-layout-editor .preview-frame) { padding: 4px; }
}
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

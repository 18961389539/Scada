<template>
  <div class="designer" v-loading="loading" :class="{ busy, embedded }">
    <div class="designer-toolbar" :inert="busy">
      <div class="mobile-panel-tools">
        <el-tooltip :content="$t('screenDesignerView.componentPanel')"><el-button :icon="MenuIcon" :aria-label="$t('screenDesignerView.componentPanel')" :aria-pressed="mobilePanel === 'palette'" data-test="toggle-component-panel" @click="mobilePanel = mobilePanel === 'palette' ? null : 'palette'" /></el-tooltip>
        <el-tooltip :content="$t('screenDesignerView.propertyPanel')"><el-button :icon="Setting" :aria-label="$t('screenDesignerView.propertyPanel')" :aria-pressed="mobilePanel === 'properties'" data-test="toggle-property-panel" @click="mobilePanel = mobilePanel === 'properties' ? null : 'properties'" /></el-tooltip>
      </div>
      <template v-if="!embedded"><el-button @click="router.push('/screens')">{{ $t('screenDesignerView.screens') }}</el-button>
        <el-button :disabled="!publishedRevision || !!loadError" data-test="designer-open-runtime" @click="router.push(runtimePath)">{{ $t(returnToRuntime ? 'screenDesignerView.returnToRuntime' : 'screenDesignerView.openRuntime') }}</el-button>
        <el-input v-model="name" :disabled="busy" :aria-label="$t('screenDesignerView.screenName')" maxlength="100" style="width: 180px" @change="commit" />
        <el-tag :class="{ 'tag-quiet': !dirty }" :type="dirty ? 'warning' : undefined" size="small">{{ dirty ? $t('screenDesignerView.unsaved') : $t('screenDesignerView.saved') }}</el-tag>
        <span class="revision">{{ $t('screenDesignerView.draftRPublished', [revision, publishedRevision ? `r${publishedRevision}` : $t('screenDesignerView.none')]) }}</span></template>
      <strong v-else>{{ $t('screenDesignerView.deviceFaceplateTemplate') }} <small class="muted">{{ $t('screenDesignerView.templateBindingHint') }}</small></strong>
      <span class="spacer" />
      <el-button :disabled="!history.canUndo" @click="undo">{{ $t('screenDesignerView.undo') }}</el-button><el-button :disabled="!history.canRedo" @click="redo">{{ $t('screenDesignerView.redo') }}</el-button>
      <template v-if="!embedded"><el-button @click="validate">{{ $t('screenDesignerView.validate') }}</el-button><el-button @click="previewVisible = true">{{ $t('screenDesignerView.preview') }}</el-button>
        <el-button v-if="!loading && !loadError" :disabled="busy" data-test="designer-version-history" @click="versionsVisible = true">{{ $t('screenVersions.title') }}</el-button>
        <el-button :loading="busy" @click="save">{{ $t('screenDesignerView.saveDraft') }}</el-button>
        <el-tooltip :content="$t('screenDesignerView.unsavedChangesAreSavedBeforePublishing')" placement="bottom" :show-after="200">
          <el-button type="primary" :loading="busy" data-test="designer-publish" @click="publish">{{ $t('screenDesignerView.publish') }}</el-button>
        </el-tooltip></template>
    </div>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon />
    <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="designer-action-error" />
    <el-alert v-if="rollbackSucceeded" :title="$t('screenVersions.rollbackSuccess')" type="success" :closable="false" show-icon data-test="designer-rollback-success" />
    <el-alert v-if="rollbackWarning" :title="rollbackWarning" type="warning" :closable="false" show-icon data-test="designer-rollback-warning" />
    <el-alert v-if="recoverableDraft" :title="$t('screenDesignerView.localDraftFound', [formatTime(recoverableDraft.draft.savedAt)])" type="warning" :closable="false" show-icon data-test="designer-local-recovery">
      <p v-if="draftBaseChanged" class="draft-conflict">{{ $t('screenDesignerView.localDraftChanged') }}</p>
      <el-button size="small" :disabled="busy" data-test="restore-local-draft" @click="restoreLocalDraft">{{ $t('screenDesignerView.restoreLocalDraft') }}</el-button>
      <el-button size="small" :disabled="busy" data-test="discard-local-draft" @click="discardLocalDraft">{{ $t('screenDesignerView.discardLocalDraft') }}</el-button>
    </el-alert>
    <el-alert v-if="draftError" :title="draftError" type="warning" :closable="false" show-icon data-test="designer-backup-error" />
    <el-alert v-if="guardError" :title="guardError" type="warning" :closable="false" show-icon data-test="designer-guard-error" />
    <el-alert v-else-if="library.error" :title="library.error" type="error" :closable="false" show-icon data-test="designer-library-error">
      <el-button link type="primary" data-test="designer-library-retry" @click="library.ensure()">{{ $t('screenDesignerView.retryLibrary') }}</el-button>
    </el-alert>
    <div v-if="!loadError" class="designer-body" :inert="busy">
      <button v-if="mobilePanel" class="panel-scrim" :aria-label="$t('screenDesignerView.closePanel')" data-test="close-designer-panel" @click="mobilePanel = null" />
      <aside class="palette" :class="{ 'mobile-open': mobilePanel === 'palette' }">
        <div class="mobile-panel-head">
          <strong>{{ $t('screenDesignerView.componentPanel') }}</strong>
          <el-button class="mobile-panel-close" :icon="Close" circle size="small" :aria-label="$t('screenDesignerView.closePanel')" data-test="close-component-panel" @click="mobilePanel = null" />
        </div>
        <div class="palette-tabs" role="tablist">
          <button type="button" role="tab" data-test="designer-tab-components" :aria-selected="paletteTab === 'components'" @click="paletteTab = paletteTab === 'components' ? 'both' : 'components'">{{ $t('screenUx.components') }}</button>
          <button type="button" role="tab" data-test="designer-tab-layers" :aria-selected="paletteTab === 'layers'" @click="paletteTab = paletteTab === 'layers' ? 'both' : 'layers'">{{ $t('screenUx.layers') }}</button>
        </div>
        <div v-show="paletteTab !== 'layers'" class="palette-pane">
        <el-input v-model="componentQuery" type="search" clearable :aria-label="$t('screenDesignerView.searchComponents')" :placeholder="$t('screenDesignerView.searchComponents')" />
        <p v-if="componentQuery.trim()" class="search-summary" role="status">{{ $t('screenDesignerView.searchCount', [componentCount, componentTotal]) }}</p>
        <p v-if="!componentCount" class="search-summary">{{ $t('screenDesignerView.noComponents') }}</p>
        <div v-if="filteredPalette.length" class="section-title">{{ $t('screenDesignerView.industrialElements') }} <small>{{ $t('screenDesignerView.dragOntoCanvasOrClickToAdd') }}</small></div>
        <div v-if="filteredPalette.length" class="palette-grid"><button v-for="item in filteredPalette" :key="item.type" draggable="true" :data-palette="item.type"
          @dragstart="startPaletteDrag(item.type, $event)" @click="add(item.type)"><span>{{ item.icon }}</span>{{ item.label }}</button></div>
        <div v-if="filteredSymbols.length" class="section-title">{{ $t('screenDesignerView.iSA101SymbolLibrary') }} <small>{{ $t('screenDesignerView.grayscaleStatusColorHint') }}</small></div>
        <el-collapse v-if="filteredSymbols.length" v-model="expandedGroups" class="symbol-groups" data-test="symbol-library">
          <el-collapse-item v-for="(group, cat) in symbolGroups" :key="cat" :title="symbolCategoryLabel(cat)" :name="cat">
            <div class="symbol-grid"><button v-for="sym in group" :key="sym.key" draggable="true" :data-symbol="sym.key" :title="symbolName(sym)"
              @dragstart="startSymbolDrag(sym.key, $event)" @click="addSymbol(sym.key)"><span class="sym-preview" v-html="sym.svg" />{{ symbolName(sym) }}</button></div>
          </el-collapse-item>
        </el-collapse>
        <template v-if="filteredUserSymbols.length">
          <div class="section-title">{{ $t('screenDesignerView.userSymbols') }}</div>
          <div class="symbol-grid"><button v-for="sym in filteredUserSymbols" :key="sym.key" draggable="true" :data-symbol="`user:${sym.key}`" :title="sym.name"
            @dragstart="startSymbolDrag(`user:${sym.key}`, $event)" @click="addSymbol(`user:${sym.key}`)"><span class="sym-preview" v-html="sym.svg" />{{ sym.name }}</button></div>
        </template>
        <template v-if="!embedded && (!componentQuery.trim() || filteredTemplates.length)">
          <div class="section-title">{{ $t('screenDesignerView.templateFaceplate') }} <small>{{ $t('screenDesignerView.templateSyncHint') }}</small></div>
          <div class="layers" data-test="template-palette"><button v-for="item in filteredTemplates" :key="item.key" :data-template="item.key" @click="addTemplate(item)">{{ item.name }}<small>{{ $t('screenDesignerView.placeholderCount', [item.placeholders.length]) }}</small></button>
            <p v-if="!templates.length" class="muted tiny">{{ library.error || $t('screenDesignerView.saveAsTemplateHint') }}</p></div>
        </template>
        </div>
        <div v-show="paletteTab !== 'components'" class="palette-pane">
        <div class="section-title">{{ $t('screenDesignerView.layers') }} <small role="status">{{ $t('screenDesignerView.searchCount', [filteredLayers.length, document.nodes.length]) }}</small></div>
        <el-input v-model="layerQuery" type="search" clearable :aria-label="$t('screenDesignerView.searchLayers')" :placeholder="$t('screenDesignerView.searchLayers')" />
        <div class="layers" data-test="designer-layers">
          <template v-for="entry in layerEntries" :key="entry.key">
            <div v-if="entry.kind === 'group'" class="layer-group" role="button" tabindex="0" :data-group-id="entry.id" :aria-expanded="collapsedGroups[entry.id] ? 'false' : 'true'" @click="toggleGroup(entry.id)" @keydown.enter.prevent="toggleGroup(entry.id)">{{ $t('screenUx.groupCount', [entry.nodes.length]) }}</div>
            <div v-for="node in entry.nodes" v-show="entry.kind === 'node' || !collapsedGroups[entry.id]" :key="node.id" class="layer-row" :class="{ active: selectedIds.includes(node.id), 'layer-hidden': node.hidden }">
              <button class="layer-select" :aria-pressed="selectedIds.includes(node.id)" :data-layer-id="node.id" :title="`${layerTitle(node)} (#${node.id})`" draggable="true"
                @dragstart="startLayerDrag(node.id, $event)" @dragover.prevent @drop="dropLayer(node.id, $event)" @click="selectLayer(node.id, $event.shiftKey)">
                <input v-if="renamingId === node.id" v-model="renameDraft" class="layer-rename" data-test="layer-rename" :aria-label="$t('screenUx.renameLayer')" @click.stop @keydown.enter.stop.prevent="commitRename(node)" @keydown.esc.stop.prevent="renamingId = null" @blur="commitRename(node)" />
                <span v-else @dblclick.stop="startRename(node)">{{ layerTitle(node) }}</span><small>#{{ node.id.slice(0, 6) }}</small></button>
              <span class="layer-flags">
                <span class="layer-flag" role="button" tabindex="0" :aria-label="node.hidden ? $t('screenDesignerView.showLayer') : $t('screenDesignerView.hideLayer')" :title="node.hidden ? $t('screenDesignerView.showLayer') : $t('screenDesignerView.hideLayer')" @click.stop="toggleNodeFlag(node, 'hidden')" @keydown.enter.stop="toggleNodeFlag(node, 'hidden')"><el-icon><View v-if="node.hidden" /><Hide v-else /></el-icon></span>
                <span class="layer-flag" role="button" tabindex="0" :aria-label="node.locked ? $t('screenDesignerView.unlockLayer') : $t('screenDesignerView.lockLayer')" :title="node.locked ? $t('screenDesignerView.unlockLayer') : $t('screenDesignerView.lockLayer')" @click.stop="toggleNodeFlag(node, 'locked')" @keydown.enter.stop="toggleNodeFlag(node, 'locked')"><el-icon><Unlock v-if="node.locked" /><Lock v-else /></el-icon></span>
              </span>
            </div>
          </template>
          <button v-if="filteredLayers.length > visibleLayers.length" type="button" class="layer-more" data-test="layers-load-more" @click="layerLimit += 100">{{ $t('screenDesignerView.loadMoreLayers', [filteredLayers.length - visibleLayers.length]) }}</button>
          <p v-if="!filteredLayers.length" class="search-summary">{{ $t(document.nodes.length ? 'screenDesignerView.noLayers' : 'screenDesignerView.emptyLayers') }}</p></div>
        </div>
        <el-button class="palette-shortcuts" link type="primary" data-test="designer-shortcuts" @click="openShortcutHelp">{{ $t('screenUx.openShortcuts') }}</el-button>
        <el-button class="mobile-panel-done" type="primary" size="small" data-test="close-component-panel-done" @click="mobilePanel = null">{{ $t('screenDesignerView.done') }}</el-button>
      </aside>
      <main class="workspace">
        <div class="canvas-toolbar"><el-select :model-value="zoom" :aria-label="$t('screenDesignerView.editorZoom')" style="width: 100px" @update:model-value="changeZoom"><el-option v-for="z in zoomOptions" :key="z" :label="`${Math.round(z * 100)}%`" :value="z" /></el-select>
          <el-button size="small" @click="fitCanvas">{{ $t('screenDesignerView.fitCanvas') }}</el-button>
          <el-button size="small" :disabled="!selectedIds.length" @click="revealSelection">{{ $t('screenDesignerView.revealSelection') }}</el-button>
          <el-checkbox v-model="snap">{{ $t('screenDesignerView.snapToGrid') }}</el-checkbox><span class="spacer" />
          <el-button v-if="!embedded" size="small" :disabled="!selectedIds.length" data-test="save-as-template" @click="openTemplateDialog">{{ $t('screenDesignerView.saveAsTemplate') }}</el-button>
          <el-dropdown trigger="click" @command="arrange">
            <el-button size="small" :icon="Operation" :disabled="selectedIds.length < 2" data-test="designer-arrange">{{ $t('screenDesignerView.arrange') }}<el-icon class="el-icon--right"><ArrowDown /></el-icon></el-button>
            <template #dropdown><el-dropdown-menu>
              <el-dropdown-item v-for="(operation, index) in arrangeOperations" :key="operation" :command="operation" :disabled="operation.startsWith('space-') && selectedIds.length < 3" :divided="index === 6 || index === 8" :data-test="`arrange-${operation}`">{{ $t(`screenDesignerView.arrange_${operation}`) }}</el-dropdown-item>
            </el-dropdown-menu></template>
          </el-dropdown>
          <el-tooltip :content="$t('screenDesignerView.layerForward')"><el-button size="small" :icon="ArrowUp" :aria-label="$t('screenDesignerView.layerForward')" :disabled="!selectedIds.length" data-test="layer-forward" @click="moveLayer(true)" /></el-tooltip>
          <el-tooltip :content="$t('screenDesignerView.layerBackward')"><el-button size="small" :icon="ArrowDown" :aria-label="$t('screenDesignerView.layerBackward')" :disabled="!selectedIds.length" data-test="layer-backward" @click="moveLayer(false)" /></el-tooltip>
          <el-button size="small" :disabled="selectedIds.length < 2" data-test="designer-group" @click="groupSelection">{{ $t('screenDesignerView.group') }}</el-button>
          <el-button size="small" :disabled="!groupedSelection" data-test="designer-ungroup" @click="ungroupSelection">{{ $t('screenDesignerView.ungroup') }}</el-button>
          <el-tooltip :content="$t('screenDesignerView.lockSelection')"><el-button size="small" :icon="Lock" :aria-label="$t('screenDesignerView.lockSelection')" :disabled="!selectedIds.length" data-test="designer-lock" @click="toggleSelectionFlag('locked')" /></el-tooltip>
          <el-tooltip :content="$t('screenDesignerView.hideSelection')"><el-button size="small" :icon="Hide" :aria-label="$t('screenDesignerView.hideSelection')" :disabled="!selectedIds.length" data-test="designer-hide" @click="toggleSelectionFlag('hidden')" /></el-tooltip>
          <el-button size="small" :disabled="!selectedIds.length" @click="duplicate">{{ $t('screenDesignerView.copy') }}</el-button><el-button size="small" :disabled="!selectedIds.length" @click="reorder(false)">{{ $t('screenDesignerView.moveDown') }}</el-button>
          <el-button size="small" :disabled="!selectedIds.length" @click="reorder(true)">{{ $t('screenDesignerView.moveUp') }}</el-button><el-button size="small" type="danger" :disabled="!selectedIds.length" @click="remove">{{ $t('screenDesignerView.delete') }}</el-button>
        </div>
        <div ref="stage" class="stage" tabindex="0" role="region" :aria-label="$t('screenDesignerView.canvas')" data-testid="designer-stage">
          <ScreenCanvas :document="document" editing :zoom="zoom" :library="library.items" :library-failed="!!library.error" :recipe-templates="recipeTemplates" :selected-ids="selectedIds" :selection-box="selectionBox" :guides="guides" @background="beginSelection" @drop="drop"
            @node-pointer-down="beginDrag" @waypoint="beginWaypoint" />
        </div>
        <div class="status-line"><button type="button" class="status-link" data-test="canvas-size" @click="openCanvasSettings">{{ document.width }} × {{ document.height }}</button><span>{{ $t('screenDesignerView.elementsSelected', [selectedIds.length]) }}</span><span v-if="document.nodes.length >= 900" data-test="designer-node-limit">{{ $t('screenUx.nearNodeLimit', [document.nodes.length]) }}</span><span v-if="savedAt" data-test="designer-saved-at">{{ $t('screenDesignerView.savedAt', [savedAt]) }}</span><span v-if="localSavedAt" data-test="designer-local-saved">{{ $t('screenDesignerView.localDraftSaved', [formatTime(localSavedAt)]) }}</span></div>
      </main>
      <aside class="properties" ref="propertiesPanel" :class="{ 'mobile-open': mobilePanel === 'properties' }" @change="commit">
        <ScreenPropertiesPanel :api="panelApi" />
      </aside>
    </div>
    <el-dialog v-model="templateVisible" :title="$t('screenDesignerView.saveAsTemplate')" width="560px" data-test="template-dialog">
      <el-alert v-if="templateError" :title="templateError" type="error" :closable="false" show-icon data-test="template-create-error" />
      <el-form label-width="90px" size="small">
        <el-form-item :label="$t('screenDesignerView.key')"><el-input v-model="templateForm.key" :placeholder="$t('screenDesignerView.eGPumpFaceplate')" data-test="template-key" /></el-form-item>
        <el-form-item :label="$t('screenDesignerView.name')"><el-input v-model="templateForm.name" data-test="template-name" /></el-form-item>
        <el-form-item :label="$t('screenDesignerView.category')"><el-input v-model="templateForm.category" /></el-form-item>
        <el-form-item v-for="p in templateForm.placeholders" :key="p.tagId" :label="`#${p.tagId}`"><el-input v-model="p.key" :data-test="`template-ph-${p.tagId}`"><template #prepend>{{ $t('screenDesignerView.placeholders') }}</template></el-input>
          <span class="tiny muted">{{ tagLabel(tags.find(t => t.id === p.tagId) ?? ({ id: p.tagId, name: '?' } as Tag)) }}</span></el-form-item>
        <el-form-item><el-checkbox v-model="templateForm.replace">{{ $t('screenDesignerView.replaceSelectionWithTemplate') }}</el-checkbox></el-form-item>
      </el-form>
      <template #footer><el-button @click="templateVisible = false">{{ $t('screenDesignerView.cancel') }}</el-button><el-button type="primary" data-test="template-create" @click="createTemplate">{{ $t('screenDesignerView.createTemplate') }}</el-button></template>
    </el-dialog>
    <ScreenVersionsDialog v-if="!embedded && !loading && !loadError" v-model="versionsVisible" :screen-id="id" :revision="revision" :published-revision="publishedRevision" :dirty="dirty" :busy="busy" :current-document="document" @busy="setVersionsBusy" @rolled-back="applyRollback" />
    <el-dialog v-model="previewVisible" :title="$t('screenDesignerView.previewTitle', [name])" width="94%" destroy-on-close><ScreenRuntime v-if="previewVisible" :preview-document="document" :preview-name="name" /></el-dialog>
    <el-dialog v-model="issuesVisible" :title="$t('screenDesignerView.screenCheckResults')" width="560px" data-test="designer-issues">
      <el-alert v-if="issuesError" :title="issuesError" type="error" :closable="false" show-icon data-test="designer-issues-error" />
      <template v-else><el-result v-if="!issues.length" icon="success" :title="$t('screenDesignerView.validationPassed')" :sub-title="$t('screenDesignerView.validationPassedHint')" />
      <template v-else><ul class="issue-list"><li v-for="(issue, i) in issues" :key="i"><button @click="focusIssue(issue)">{{ issueLabel(issue.nodeId) }}</button>：{{ issue.message }}</li></ul>
      <p class="muted tiny" data-test="designer-issues-hint">{{ $t('screenDesignerView.selectAnItemToLocateTheShapeOnTheCanvas') }}</p></template></template></el-dialog>
    <el-dialog v-model="publishDiffVisible" :title="$t('screenUx.publishDiffTitle')" width="560px" data-test="designer-publish-diff" @closed="settlePublishDiff(false)">
      <p>{{ $t('screenUx.publishDiffBody', [publishDiff.added.length, publishDiff.removed.length, publishDiff.changed.length]) }}</p>
      <ul class="issue-list">
        <li v-for="label in publishDiff.added.slice(0, 8)" :key="'a-' + label">+ {{ label }}</li>
        <li v-for="label in publishDiff.removed.slice(0, 8)" :key="'r-' + label">- {{ label }}</li>
        <li v-for="label in publishDiff.changed.slice(0, 8)" :key="'c-' + label">~ {{ label }}</li>
      </ul>
      <p v-if="publishDiffExtra" class="muted tiny">{{ $t('screenUx.publishDiffMore', [publishDiffExtra]) }}</p>
      <template #footer>
        <el-button @click="publishDiffVisible = false">{{ $t('screenDesignerView.cancel') }}</el-button>
        <el-button type="primary" data-test="publish-diff-confirm" @click="settlePublishDiff(true)">{{ $t('screenDesignerView.publishImpactConfirm') }}</el-button>
      </template>
    </el-dialog>
    <el-dialog v-model="conflictVisible" :title="$t('screenDesignerView.conflictTitle')" width="620px" data-test="designer-conflict">
      <el-alert :title="$t('screenDesignerView.conflictHint', [conflict?.server.revision ?? 0, revision])" type="warning" :closable="false" show-icon />
      <p class="muted tiny">{{ $t('screenDesignerView.conflictSummary', [conflictRows.length]) }}</p>
      <p v-if="conflictNeedsChoice" class="muted tiny" data-test="conflict-need-choice">{{ $t('screenUx.conflictNeedChoice') }}</p>
      <ul v-if="conflictRows.length" class="issue-list conflict-list">
        <li v-for="row in conflictRows" :key="row.key">
          <el-tag size="small" :type="row.kind === 'local' ? 'info' : row.kind === 'server' ? 'danger' : 'warning'">{{ $t(`screenDesignerView.conflict_${row.kind}`) }}</el-tag>
          {{ row.label }}
          <el-checkbox v-if="row.kind !== 'modified'" v-model="row.keep">{{ $t('screenUx.conflictKeep') }}</el-checkbox>
          <el-radio-group v-else v-model="row.choice" size="small">
            <el-radio value="local">{{ $t('screenUx.conflictUseLocal') }}</el-radio>
            <el-radio value="server">{{ $t('screenUx.conflictUseServer') }}</el-radio>
          </el-radio-group>
        </li>
      </ul>
      <template #footer>
        <el-button @click="conflictVisible = false">{{ $t('screenDesignerView.cancel') }}</el-button>
        <el-button data-test="conflict-reload" @click="reloadServerDraft">{{ $t('screenDesignerView.conflictReload') }}</el-button>
        <el-button type="danger" plain data-test="conflict-overwrite" @click="overwriteServerDraft">{{ $t('screenDesignerView.conflictOverwrite') }}</el-button>
        <el-button type="primary" data-test="conflict-merge" :disabled="conflictNeedsChoice" @click="mergeConflict">{{ $t('screenUx.conflictMerge') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import ScreenCanvas from '../components/ScreenCanvas.vue'
import ScreenPropertiesPanel from '../components/ScreenPropertiesPanel.vue'
import ScreenRuntime from '../components/ScreenRuntime.vue'
import ScreenVersionsDialog from '../components/ScreenVersionsDialog.vue'
import { ArrowDown, ArrowUp, Close, Hide, Lock, Menu as MenuIcon, Operation, Setting, Unlock, View } from '@element-plus/icons-vue'
import { useScreenDesigner } from '../composables/useScreenDesigner'
import type { ScreenDocument } from '../screens/model'
import type { Tag } from '../types'
const props = withDefaults(defineProps<{ embedded?: boolean; modelValue?: ScreenDocument; bindingTags?: Tag[]; disabled?: boolean }>(), { embedded: false, disabled: false })
const emit = defineEmits<{ 'update:modelValue': [document: ScreenDocument]; save: [] }>()
const { route, router, id, returnToRuntime, runtimePath, document, name, revision, publishedRevision, loading, loadError, actionError, guardError, busy, zoom, snap, selectedIds, tags, devices, screens, stage, propertiesPanel, recipeTemplates, recipes, sequences, calcJobs, previewVisible, issuesVisible, issues, issuesError, savedAt, publishDiffVisible, publishDiff, publishDiffExtra, conflictVisible, conflict, conflictRows, conflictNeedsChoice, versionsVisible, versionsSubmitting, rollbackSucceeded, rollbackWarning, setVersionsBusy, applyRollback, history, savedSnapshot, selected, selectedNodes, isControlSelected, dataBindingTypes, typeSettingTypes, labelTypes, hasDataSection, hasActionSection, hasTypeSection, showEnglishText, showFontSize, batchShowFontSize, textStyleNodeTypes, shapeStyleNodeTypes, showTextStyle, showShapeStyle, showStyleSection, setAlign, setStyleFlag, setStyleNumber, controlRangeTypes, stateLimit, multiTagTypes, applyNumber, applyAction, clearActionTargets, setActionType, setBatchActionType, batchActionType, batchShowsTarget, batchShowsCommand, pipeEnds, pipeEndUpdates, pipePortUpdates, setPipePort, setPipeEnd, addPipePoint, addCommonStates, addState, addPanelCommand, beginWaypoint, mobilePanel, guides, marquee, selectionBox, snapshot, dirty, savedNodes, nodeChanged, selectedNodeChanged, selectedChangedCount, recoverableDraft, draftError, localSavedAt, draftBaseChanged, findLocalDraft, clearLocalBackup, persistLocalDraft, restoreLocalDraft, discardLocalDraft, setLeavingDiscarded, disposeDraftTimer, notify, commit, commitField, fieldLabel, resetAppearance, resetData, resetAction, resetAnimation, setAnimType, restore, undo, redo, tagLabel, typeLabel, bindingChanged, setControlTag, setResetTag, syncControlTag, groupMembers, select, block, add, library, locale, userSymbols, templates, symbolName, symbolCategoryLabel, componentQuery, layerQuery, expandedGroups, paletteTab, collapsedGroups, renamingId, renameDraft, layerTitle, toggleGroup, startRename, commitRename, openCanvasSettings, matches, filteredPalette, filteredSymbols, filteredUserSymbols, filteredTemplates, componentTotal, componentCount, symbolGroups, tagNames, tagsById, selectedIssues, fieldError, focusField, fieldForIssue, displayedTagIds, tagValues, tagValuesError, startTagValues, loadRecipeCatalog, loadSequenceCatalog, loadScriptCatalog, tagLiveText, tagLiveClass, tagLiveTitle, animationSummary, filteredLayers, layerLimit, visibleLayers, layerEntries, zoomOptions, fitCanvas, changeZoom, revealSelection, selectLayer, englishText, pushNode, nextPos, addSymbol, addTemplate, syncBindings, startSymbolDrag, templateVisible, templateError, templateForm, openTemplateDialog, createTemplate, startPaletteDrag, drop, beginDrag, canvasPoint, beginSelection, startTransform, snapAlignment, updateGuides, transformNodes, finishTransform, syncInteractionTargets, initializeInteractions, detachConnections, remove, copy, paste, duplicate, reorder, moveLayer, groupedSelection, groupSelection, ungroupSelection, toggleNodeFlag, toggleSelectionFlag, startLayerDrag, dropLayer, reorderLayer, arrangeOperations, arrange, resizeCanvas, closePanel, panelApi, failureIssues, issueOwner, issueLabel, issueDigest, showIssues, openConflict, conflictLabel, sameNode, conflictRowsFor, reloadServerDraft, overwriteServerDraft, mergeConflict, diffLabels, settlePublishDiff, confirmPublishDiff, save, validate, confirmPublishUsage, publish, focusIssue, keydown, beforeUnload, confirmLeave, requireDocument, ref, palette, formatTime, openShortcutHelp } = useScreenDesigner(props, emit)
</script>

<style scoped>
.designer { margin: 0; height: calc(100vh - var(--scada-header-height)); min-height: 600px; display: flex; flex-direction: column; background: var(--scada-bg); }
.designer.embedded { height: 720px; border: 1px solid var(--scada-border); }
.spacer { flex: 1; }
.designer-toolbar { display: flex; gap: 8px; align-items: center; padding: 12px; background: var(--scada-panel); border-bottom: 1px solid var(--scada-border); flex-wrap: wrap; }
.revision { color: var(--scada-text-muted); font-size: 12px; } .designer-body { display: grid; grid-template-columns: 180px minmax(300px, 1fr) 240px; flex: 1; min-height: 0; }
.palette, .properties { overflow: auto; background: var(--scada-panel); padding: 14px; } .palette { display: flex; flex-direction: column; height: 100%; min-height: 0; overflow: hidden; border-right: 1px solid var(--scada-border); } .properties { border-left: 1px solid var(--scada-border); }
.palette-tabs { display: flex; gap: 6px; margin-bottom: 8px; } .palette-tabs button { flex: 1; border: 1px solid var(--scada-border); border-radius: 6px; background: transparent; color: var(--scada-text-secondary); font: inherit; font-size: 12px; padding: 4px 0; cursor: pointer; }
.palette-tabs button[aria-selected="true"] { border-color: var(--scada-primary); color: var(--scada-primary); background: var(--scada-primary-soft); }
.palette-pane { flex: 1; min-height: 0; overflow: auto; } .palette-pane .layers { max-height: none; }
.palette-shortcuts { align-self: flex-start; margin-top: 8px; }
.layer-group { padding: 6px 8px; font-size: 12px; color: var(--scada-text); cursor: pointer; } .layer-rename { width: 100%; min-width: 0; font: inherit; }
.status-link { border: 0; padding: 0; background: transparent; color: inherit; font: inherit; cursor: pointer; } .status-link:hover { color: var(--scada-primary); }
.conflict-list { max-height: 360px; overflow: auto; } .conflict-list li { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; }
.section-title { font-weight: 600; margin-bottom: 14px; color: var(--scada-text); } .section-title small { display: block; color: var(--scada-text-muted); font-weight: 400; font-size: 11px; margin-top: 4px; }
.palette-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; margin-bottom: 24px; } .palette-grid button { border: 1px solid var(--scada-border); border-radius: 6px; padding: 8px 2px; background: var(--scada-panel-muted); color: var(--scada-text-secondary); cursor: grab; font: inherit; font-size: 12px; }
.palette-grid button:hover { border-color: var(--scada-primary); background: var(--scada-primary-soft); }.palette-grid span { display: block; font-size: 24px; height: 30px; color: var(--scada-primary); }
.symbol-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; margin-bottom: 12px; }
.symbol-grid button { border: 1px solid var(--scada-border); border-radius: 6px; padding: 4px 2px; background: #4a5560; color: #f1f3f5; cursor: grab; font: inherit; font-size: 11px; }
.symbol-grid button:hover { border-color: var(--scada-primary); }
.sym-preview { display: block; height: 34px; color: var(--scada-text-muted); } .sym-preview :deep(svg) { height: 34px; width: 100%; }
.symbol-groups { margin-bottom: 16px; } .symbol-groups :deep(.el-collapse-item__header) { height: 30px; font-size: 12px; }
.search-summary { font-size: 11px; color: var(--scada-text-muted); line-height: 1.6; }.palette > .el-input { margin-bottom: 12px; }
.layers { max-height: 270px; overflow: auto; }
.layer-row { display: flex; align-items: center; gap: 4px; }
.layer-select { flex: 1; min-width: 0; display: flex; align-items: center; justify-content: space-between; gap: 6px; width: 100%; padding: 8px; border: 0; text-align: left; background: transparent; color: var(--scada-text-secondary); cursor: pointer; }
.layer-select span { min-width: 0; overflow: hidden; white-space: nowrap; text-overflow: ellipsis; }
.layer-select small { flex-shrink: 0; font-size: 9px; color: var(--scada-text-muted); }
.layer-row.active .layer-select { background: var(--scada-primary-soft); color: var(--scada-primary); }
.layer-row.layer-hidden .layer-select > span { opacity: .5; text-decoration: line-through; }
.palette button:focus-visible, .stage:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.layers .layer-flags { display: flex; flex-shrink: 0; gap: 2px; overflow: visible; }
.layers .layer-flag { display: inline-flex; align-items: center; justify-content: center; width: 20px; height: 20px; overflow: visible; border-radius: 3px; color: var(--scada-text-muted); }
.layers .layer-flag:hover { background: var(--scada-panel-muted); color: var(--scada-primary); }
.layer-more { display: flex; align-items: center; justify-content: center; width: 100%; padding: 8px; border: 0; background: transparent; color: var(--scada-primary); font-size: 12px; cursor: pointer; }
.conflict-list { max-height: 300px; overflow: auto; }

.layers small { font-size: 9px; color: var(--scada-text-muted); } .keyboard-help { font-size: 11px; line-height: 1.8; color: var(--scada-text-muted); padding-top: 20px; }.workspace { display: flex; flex-direction: column; min-width: 0; min-height: 0; }.canvas-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 10px; background: var(--scada-panel-muted); }.canvas-toolbar :deep(.el-button + .el-button) { margin-left: 0; }.canvas-toolbar :deep(.el-select) { flex-shrink: 0; }
.stage { position: relative; isolation: isolate; flex: 1; overflow: auto; padding: 20px; background: var(--scada-bg-subtle); min-height: 0; }.stage :deep(svg.screen-canvas) { box-shadow: 0 4px 20px #1f334c26; }
.stage :deep(.moveable-area), .stage :deep(.moveable-line) { pointer-events: none; }
.stage :deep(.designer-selecto) { display: none !important; }
.stage :deep(svg.editing) { touch-action: none; user-select: none; }
.status-line { padding: 7px 14px; display: flex; flex-wrap: wrap; gap: 8px 24px; font-size: 11px; color: var(--scada-text-muted); background: var(--scada-panel); }
.draft-conflict { margin: 4px 0 8px; }
.mobile-panel-tools, .panel-scrim, .mobile-panel-close, .mobile-panel-done, .mobile-panel-head { display: none; }
.properties :deep(.el-select) { width: 100%; }
.busy .designer-body { pointer-events: none; opacity: .75; }.busy .designer-toolbar { pointer-events: none; }.issue-list { line-height: 2; padding-left: 18px; }.issue-list button { border: 0; background: transparent; color: var(--scada-primary); cursor: pointer; }
@media (max-width: 1250px) { .designer-body { grid-template-columns: 140px minmax(260px, 1fr) 210px; }.palette, .properties { padding: 10px; }.revision { display: none; } }
@media (max-width: 900px) {
  .designer { height: calc(100dvh - 56px); min-height: 500px; min-width: 0; }
  .designer-body { position: relative; grid-template-columns: minmax(0, 1fr); }
  .mobile-panel-tools { display: flex; gap: 6px; }
  .designer-toolbar { padding: 8px; gap: 6px; }
  .designer-toolbar :deep(.el-button + .el-button) { margin-left: 0; }
  .canvas-toolbar { padding: 8px; gap: 6px; }
  .palette, .properties { display: none; }
  .palette.mobile-open, .properties.mobile-open { display: block; position: absolute; top: 0; bottom: 0; width: min(290px, calc(100% - 40px)); z-index: 4; }
  .palette.mobile-open { left: 0; }.properties.mobile-open { right: 0; }
  .panel-scrim { display: block; position: absolute; inset: 0; z-index: 3; border: 0; background: #0005; cursor: pointer; }
  .mobile-panel-close { display: inline-flex; flex-shrink: 0; }
  .mobile-panel-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; margin-bottom: 8px; }
  .mobile-panel-done { display: flex; width: 100%; margin-top: 14px; position: sticky; bottom: 0; }
}
</style>

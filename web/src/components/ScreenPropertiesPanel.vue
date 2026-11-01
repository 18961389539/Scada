<template>
  <div class="properties-panel">
    <div class="section-title">
      <div class="property-heading-text">
        {{ selectedNodes.length > 1 ? $t('screenDesignerView.elementsSelected', [selectedNodes.length]) : selected ? $t('screenDesignerView.elementProperties') : $t('screenDesignerView.canvasProperties') }}
        <small v-if="selectedNodes.length === 1 && selected">{{ typeLabel(selected) }}<template v-if="selected.text && selected.text !== typeLabel(selected)"> · {{ selected.text }}</template> · #{{ selected.id.slice(0, 6) }}</small>
        <small v-if="selectedNodes.length > 1 && selectedChangedCount" data-test="selection-dirty">{{ $t('screenDesignerView.dirtyCount', [selectedChangedCount]) }}</small>
        <small v-else-if="selectedNodes.length === 1 && selectedNodeChanged" data-test="node-dirty">{{ $t('screenDesignerView.unsaved') }}</small>
        <small v-if="tagValuesError" class="tag-values-error" data-test="designer-live-unavailable">{{ $t('screenDesignerView.liveUnavailable') }}</small>
      </div>
      <el-button class="mobile-panel-close" :icon="Close" circle size="small" :aria-label="$t('screenDesignerView.closePanel')" data-test="close-property-panel" @click="closePanel" />
    </div>
    <el-form label-position="top" size="small">
      <template v-if="selectedNodes.length > 1">
        <p class="muted tiny">{{ $t('screenDesignerView.multiHint') }}</p>
        <el-divider>{{ $t('screenDesignerView.sharedProperties') }}</el-divider>
        <div class="field-pair">
          <el-form-item>
            <template #label>{{ $t('screenDesignerView.defaultColor') }}<span v-if="mixedOf('color')" class="mixed-value"> · {{ $t('screenDesignerView.mixedValue') }}</span></template>
            <ColorField compact :model-value="commonOf('color') ?? '#7394b8'" :aria-label="$t('screenDesignerView.defaultColor')" @update:model-value="(v: string) => applyAll('color', v)" />
          </el-form-item>
          <el-form-item>
            <template #label>{{ $t('screenDesignerView.activeColor') }}<span v-if="mixedOf('activeColor')" class="mixed-value"> · {{ $t('screenDesignerView.mixedValue') }}</span></template>
            <ColorField compact :model-value="commonOf('activeColor') ?? '#34d399'" :aria-label="$t('screenDesignerView.activeColor')" @update:model-value="(v: string) => applyAll('activeColor', v)" />
          </el-form-item>
        </div>
        <el-form-item :label="$t('screenDesignerView.rotation')">
          <el-input-number :model-value="commonOf('rotation')" :min="-360" :max="360" :placeholder="mixedOf('rotation') ? $t('screenDesignerView.mixedValue') : ''" @change="applyNumber('rotation', $event)" />
        </el-form-item>
        <el-form-item v-if="batchShowFontSize">
          <template #label>{{ $t('screenDesignerView.fontSize') }}<span v-if="mixedOf('fontSize')" class="mixed-value"> · {{ $t('screenDesignerView.mixedValue') }}</span></template>
          <el-input-number :model-value="commonOf('fontSize') ?? undefined" :min="8" :max="96" :step="1" :placeholder="mixedOf('fontSize') ? $t('screenDesignerView.mixedValue') : ''" @change="(v: number | undefined) => applyAll('fontSize', v ?? null)" />
        </el-form-item>
        <div class="field-pair">
          <el-form-item>
            <template #label>{{ $t('screenDesignerView.unit') }}<span v-if="mixedOf('unit')" class="mixed-value"> · {{ $t('screenDesignerView.mixedValue') }}</span></template>
            <el-input :model-value="commonOf('unit')" maxlength="32" :placeholder="mixedOf('unit') ? $t('screenDesignerView.mixedValue') : ''" @change="applyAll('unit', $event)" />
          </el-form-item>
          <el-form-item>
            <template #label>{{ $t('screenDesignerView.decimals') }}<span v-if="mixedOf('decimals')" class="mixed-value"> · {{ $t('screenDesignerView.mixedValue') }}</span></template>
            <el-input-number :model-value="commonOf('decimals')" :min="0" :max="6" :placeholder="mixedOf('decimals') ? $t('screenDesignerView.mixedValue') : ''" @change="applyNumber('decimals', $event)" />
          </el-form-item>
        </div>
        <el-divider>{{ $t('screenDesignerView.multiBindTitle') }}</el-divider>
        <el-form-item :label="$t('screenDesignerView.alarmStateTag')">
          <TagSelect :model-value="commonOf('alarmTagId')" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.alarmStateTag')" :placeholder="mixedOf('alarmTagId') ? $t('screenDesignerView.mixedValue') : ''" @update:model-value="(v: any) => applyAll('alarmTagId', v ?? null)" />
        </el-form-item>
        <el-form-item :label="$t('screenDesignerView.inputTag')">
          <TagSelect :model-value="commonOf('tagId')" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.inputTag')" :placeholder="mixedOf('tagId') ? $t('screenDesignerView.mixedValue') : ''" @update:model-value="(v: any) => applyAll('tagId', v ?? null)" />
        </el-form-item>
        <el-divider>{{ $t('screenDesignerView.clickAction') }}</el-divider>
        <p class="muted tiny">{{ $t('screenDesignerView.multiActionHint') }}</p>
        <el-form-item :label="$t('screenDesignerView.action')">
          <el-select :model-value="commonAction(action => action.type)" :placeholder="mixedAction(action => action.type) ? $t('screenDesignerView.mixedValue') : ''" @update:model-value="(v: ScreenAction['type']) => setBatchActionType(v)">
            <el-option :label="$t('screenDesignerView.noAction')" value="none" />
            <el-option :label="$t('screenDesignerView.navigateToScreen')" value="navigate" />
            <el-option :label="$t('screenDesignerView.popupScreen')" value="popup" />
            <el-option :label="$t('screenDesignerView.controlWrite')" value="command" />
            <el-option :label="$t('hmi.sequence')" value="sequence" />
            <el-option :label="$t('hmi.recipeAction')" value="recipe" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="batchShowsTarget" :label="$t('screenDesignerView.targetScreen')">
          <el-select :model-value="commonAction(action => action.screenId)" filterable @update:model-value="(v: number | null) => applyAction({ screenId: v ?? null })">
            <el-option v-for="screen in screens" :key="screen.id" :value="screen.id" :label="$t('screenDesignerView.screenOptionLabel', [screen.name, screen.publishedRevision ? '' : $t('screenDesignerView.unpublished')])" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="batchActionType === 'sequence'" :label="$t('hmi.sequence')">
          <el-select :model-value="commonAction(action => action.sequenceId)" filterable @update:model-value="(v: number | null) => applyAction({ type: 'sequence', sequenceId: v ?? null })">
            <el-option v-for="sequence in sequences" :key="sequence.id" :value="sequence.id" :label="sequence.name" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="batchActionType === 'recipe'" :label="$t('hmi.recipe')">
          <el-select :model-value="commonAction(action => action.recipeId)" filterable @update:model-value="(v: number | null) => applyAction({ type: 'recipe', recipeId: v ?? null })">
            <el-option v-for="recipe in recipes" :key="recipe.id" :value="recipe.id" :label="`${recipe.name} · v${recipe.version}`" />
          </el-select>
        </el-form-item>
        <template v-if="batchShowsCommand">
          <el-form-item :label="$t('screenDesignerView.outputTag')">
            <TagSelect :model-value="commonAction(action => action.tagId)" :tags="tags" :devices="devices" :values="tagValues" writable :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.outputTag')" @update:model-value="(v: any) => applyAction({ type: 'command', tagId: v ?? null })" />
          </el-form-item>
          <el-form-item :label="$t('screenDesignerView.valueToWrite')">
            <el-input-number :model-value="commonAction(action => action.value)" :controls="false" @change="(v: number | undefined) => v !== undefined && applyAction({ value: v })" />
          </el-form-item>
        </template>
        <el-divider>{{ $t('screenDesignerView.arrange') }}</el-divider>
        <div class="align-grid">
          <el-button v-for="operation in arrangeOperations" :key="operation" size="small" :disabled="operation.startsWith('space-') && selectedIds.length < 3" :data-test="`panel-arrange-${operation}`" @click="arrange(operation)">{{ $t(`screenDesignerView.arrange_${operation}`) }}</el-button>
        </div>
      </template>
      <template v-else-if="selected">
        <div v-if="selectedIssues.length" class="node-issues" role="alert" data-test="node-issues">
          <p class="node-issues-title">{{ $t('screenDesignerView.nodeIssueSummary', [selectedIssues.length]) }}</p>
          <button v-for="(issue, index) in selectedIssues" :key="`${issue.field}-${index}`" type="button" class="node-issue" @click="focusField(issue.field)">{{ issue.message }}</button>
        </div>

        <details class="prop-group" open data-test="property-group-basic">
          <summary>{{ $t('screenDesignerView.groupBasic') }}<el-button class="group-reset" link size="small" :title="$t('screenDesignerView.resetAppearanceHelp')" data-test="property-reset-basic" @click.stop.prevent="resetAppearance">{{ $t('screenDesignerView.restoreDefaults') }}</el-button></summary>
          <el-form-item v-if="selected.type !== 'image'" :label="$t('screenDesignerView.text')"><el-input v-model="selected.text" maxlength="500" :aria-label="$t('screenDesignerView.elementText')" /></el-form-item>
          <div class="field-pair"><el-form-item label="X"><el-input-number v-model="selected.x" :min="0" :max="document.width - selected.width" :controls="false" @change="commit" /></el-form-item>
            <el-form-item label="Y"><el-input-number v-model="selected.y" :min="0" :max="document.height - selected.height" :controls="false" @change="commit" /></el-form-item></div>
          <div class="field-pair"><el-form-item :label="$t('screenDesignerView.width')"><el-input-number v-model="selected.width" :min="16" :max="document.width - selected.x" :controls="false" @change="commit" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.high')"><el-input-number v-model="selected.height" :min="16" :max="document.height - selected.y" :controls="false" @change="commit" /></el-form-item></div>
          <el-form-item v-if="showEnglishText">
            <template #label><FieldLabel :label="$t('screenDesignerView.englishText')" :help="$t('screenDesignerView.helpEnglish')" /></template>
            <el-input v-model="englishText" maxlength="500" :placeholder="$t('screenDesignerView.defaultsToChineseText')" data-test="node-text-en" @change="commit" />
          </el-form-item>
          <el-form-item v-if="selected.type === 'symbol'" :label="$t('screenDesignerView.symbol')"><el-select v-model="selected.symbol" filterable data-test="node-symbol" @change="commit">
            <el-option v-for="sym in builtinSymbols" :key="sym.key" :value="sym.key" :label="symbolName(sym)" />
            <el-option v-for="sym in userSymbols" :key="sym.key" :value="`user:${sym.key}`" :label="$t('screenDesignerView.user', [sym.name])" /></el-select></el-form-item>
          <el-form-item>
            <template #label><FieldLabel :label="$t('screenDesignerView.rotation')" :help="$t('screenDesignerView.helpRotation')" /></template>
            <el-input-number v-model="selected.rotation" :min="-360" :max="360" :step="1" :disabled="selected.type === 'polyline' && !!(selected.source || selected.target)" @change="commitField('rotation')" />
          </el-form-item>
          <el-form-item v-if="showFontSize" :error="fieldError('fontSize')" data-field="fontSize">
            <template #label><FieldLabel :label="$t('screenDesignerView.fontSize')" :help="$t('screenDesignerView.helpFontSize')" /></template>
            <el-input-number :model-value="selected.fontSize ?? undefined" :min="8" :max="96" :step="1" :placeholder="String(defaultFontSizes[selected.type] ?? 16)" @update:model-value="(v: number | undefined) => { if (selected) { selected.fontSize = v ?? null; commitField('fontSize') } }" />
          </el-form-item>
          <div class="field-pair">
            <el-form-item :label="$t('screenDesignerView.defaultColor')"><ColorField v-model="selected.color" :aria-label="$t('screenDesignerView.defaultColor')" @change="commitField('color')" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.activeColor')"><ColorField v-model="selected.activeColor" :aria-label="$t('screenDesignerView.activeColor')" @change="commitField('activeColor')" /></el-form-item>
          </div>
        </details>
        <details v-if="showStyleSection" class="prop-group" open data-test="property-group-style">
          <summary>{{ $t('screenDesignerView.groupStyle') }}</summary>
          <el-form-item v-if="showTextStyle" :label="$t('screenDesignerView.textAlign')" data-test="text-align">
            <el-radio-group :model-value="selected.align ?? 'center'" size="small" @update:model-value="setAlign">
              <el-radio-button value="left">{{ $t('screenDesignerView.alignLeft') }}</el-radio-button>
              <el-radio-button value="center">{{ $t('screenDesignerView.alignCenter') }}</el-radio-button>
              <el-radio-button value="right">{{ $t('screenDesignerView.alignRight') }}</el-radio-button>
            </el-radio-group>
          </el-form-item>
          <div v-if="showTextStyle" class="style-flags">
            <el-checkbox :model-value="!!selected.wrap" data-test="text-wrap" @update:model-value="(v: any) => setStyleFlag('wrap', v)">{{ $t('screenDesignerView.textWrap') }}</el-checkbox>
            <el-checkbox :model-value="!!selected.autoFit" data-test="text-autofit" @update:model-value="(v: any) => setStyleFlag('autoFit', v)">{{ $t('screenDesignerView.textAutoFit') }}</el-checkbox>
            <el-checkbox :model-value="!!selected.bold" data-test="text-bold" @update:model-value="(v: any) => setStyleFlag('bold', v)">{{ $t('screenDesignerView.textBold') }}</el-checkbox>
          </div>
          <el-form-item v-if="showShapeStyle" :label="$t('screenDesignerView.shapeOpacity')" data-test="shape-opacity">
            <el-slider :model-value="selected.opacity ?? 1" :min="0" :max="1" :step="0.05" @update:model-value="(v: number) => setStyleNumber('opacity', v)" />
          </el-form-item>
          <div v-if="showShapeStyle" class="field-pair">
            <el-form-item :label="$t('screenDesignerView.shapeBorder')"><el-input-number :model-value="selected.borderWidth ?? 2" :min="0" :max="10" :step="1" @update:model-value="(v: number | undefined) => setStyleNumber('borderWidth', v)" /></el-form-item>
            <el-form-item v-if="selected.type !== 'ellipse'" :label="$t('screenDesignerView.shapeRadius')"><el-input-number :model-value="selected.radius ?? (selected.type === 'button' ? 8 : 2)" :min="0" :max="200" :step="1" @update:model-value="(v: number | undefined) => setStyleNumber('radius', v)" /></el-form-item>
          </div>
        </details>
        <details v-if="hasTypeSection" class="prop-group" open data-test="property-group-type">
          <summary>{{ $t('screenDesignerView.groupType') }}</summary>
        <template v-if="selected.type === 'template'">
          <el-divider data-field="bindings">{{ $t('screenDesignerView.templateBindings') }}</el-divider>
          <p class="muted tiny">{{ $t('screenDesignerView.templateBindingTitle', [selected.templateKey]) }}</p>
          <el-form-item v-for="b in selected.bindings ?? []" :key="b.key" :label="b.key">
            <TagSelect v-model="b.tagId" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="b.key" :data-test="`binding-${b.key}`" @change="commit" />
            <p v-if="tagLiveText(b.tagId)" class="tag-live-row" :class="tagLiveClass(b.tagId)" :title="tagLiveTitle(b.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(b.tagId) }}</p>
          </el-form-item>
          <el-button size="small" @click="syncBindings(selected)">{{ $t('screenDesignerView.syncPlaceholders') }}</el-button>
          <p v-if="fieldError('bindings')" class="field-error">{{ fieldError('bindings') }}</p>
        </template>
        <template v-else>
        <template v-if="selected.type === 'polyline'">
          <el-divider data-field="polyline">{{ $t('hmi.polyline') }}</el-divider>
          <div v-for="end in pipeEnds" :key="end">
            <el-form-item :label="$t(`hmi.${end}`)"><el-select :model-value="selected[end]?.nodeId" filterable clearable :aria-label="$t(`hmi.${end}`)" :data-test="`pipe-${end}`" @update:model-value="pipeEndUpdates[end]">
              <el-option v-for="node in document.nodes.filter(node => node.id !== selected?.id && !['polyline', 'template'].includes(node.type))" :key="node.id" :value="node.id" :label="node.text || node.id" /></el-select></el-form-item>
            <el-form-item v-if="selected[end]" :label="$t('hmi.port')"><el-select :model-value="selected[end]?.port" :aria-label="`${$t(`hmi.${end}`)} ${$t('hmi.port')}`" @update:model-value="pipePortUpdates[end]"><el-option v-for="port in pipePorts" :key="port" :value="port" :label="$t(`hmi.${port}`)" /></el-select></el-form-item>
          </div>
          <template v-if="!selected.source && !selected.target">
            <el-divider>{{ $t('hmi.points') }}</el-divider>
            <div v-for="(point, index) in selected.points" :key="index" class="hmi-config-row" data-test="pipe-point-row">
              <el-input-number v-model="point.x" :min="0" :max="100" :controls="false" :aria-label="`X ${index + 1}`" @change="commit" />
              <el-input-number v-model="point.y" :min="0" :max="100" :controls="false" :aria-label="`Y ${index + 1}`" @change="commit" />
              <el-tooltip :content="$t('hmi.remove')"><el-button :icon="Delete" :aria-label="$t('hmi.remove')" :disabled="(selected.points?.length ?? 0) <= 2" @click="selected.points?.splice(index, 1); commit()" /></el-tooltip>
            </div>
            <el-button :icon="Plus" :disabled="(selected.points?.length ?? 0) >= 64" data-test="pipe-add-point" @click="addPipePoint">{{ $t('hmi.addPoint') }}</el-button>
          </template>
          <p v-if="fieldError('polyline')" class="field-error">{{ fieldError('polyline') }}</p>
        </template>
        <template v-if="['symbol', 'pump', 'valve', 'motor', 'fan', 'tank', 'equipment-panel', 'selector', 'knob'].includes(selected.type)">
          <el-divider data-field="states">{{ $t('hmi.states') }}</el-divider>
          <el-button v-if="!selected.states?.length && !['selector', 'knob'].includes(selected.type)" :icon="Plus" data-test="node-common-states" @click="addCommonStates">{{ $t('hmi.enableStates') }}</el-button>
          <div v-for="(item, index) in selected.states" :key="index" class="hmi-state-row" data-test="node-state-row">
            <el-input-number v-model="item.value" :controls="false" :step="1" :aria-label="$t('hmi.stateValue')" @change="commitField('state-value')" />
            <el-input v-model="item.label" maxlength="40" :aria-label="$t('hmi.stateLabel')" @change="commitField('state-label')" />
            <ColorField compact v-model="item.color" :aria-label="$t('hmi.stateColor')" @change="commitField('state-color')" />
            <el-tooltip :content="$t('hmi.remove')"><el-button :icon="Delete" :aria-label="$t('hmi.remove')" @click="selected.states?.splice(index, 1); commit()" /></el-tooltip>
          </div>
          <el-button v-if="selected.states?.length" :icon="Plus" :disabled="selected.states.length >= stateLimit" data-test="node-add-state" @click="addState">{{ $t('hmi.addState') }}</el-button>
          <p v-if="selected.type === 'selector' || selected.type === 'knob'" class="field-hint">{{ $t('hmi.positionsHint') }}</p>
          <div v-if="selected.states?.length" class="state-preview" data-test="state-preview">
            <span v-for="state in selected.states" :key="state.value" class="state-chip"><i :style="{ backgroundColor: state.color }" />{{ state.label || state.value }}</span>
          </div>
          <p v-if="fieldError('states')" class="field-error">{{ fieldError('states') }}</p>
        </template>
        <template v-if="selected.type === 'reset' || selected.type === 'silence'">
          <el-divider :data-field="selected.type === 'reset' ? 'reset' : 'silence'">{{ selected.type === 'reset' ? $t('hmi.reset') : $t('hmi.silence') }}</el-divider>
          <template v-if="selected.type === 'reset'">
            <el-form-item :label="$t('hmi.resetMode')" :error="fieldError('reset')">
              <el-select v-model="selected.resetMode" :aria-label="$t('hmi.resetMode')" data-test="node-reset-mode" @change="commitField('reset')">
                <el-option :label="$t('hmi.resetModeAlarms')" value="alarms" />
                <el-option :label="$t('hmi.resetModeTag')" value="tag" />
              </el-select>
            </el-form-item>
            <template v-if="selected.resetMode === 'tag'">
              <el-form-item :label="$t('screenDesignerView.controlTag')">
                <TagSelect v-model="selected.action.tagId" :tags="tags" :devices="devices" :values="tagValues" writable :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.controlTag')" data-test="node-reset-tag" @change="setResetTag" />
                <p v-if="tagLiveText(selected.action.tagId)" class="tag-live-row" :class="tagLiveClass(selected.action.tagId)" :title="tagLiveTitle(selected.action.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.action.tagId) }}</p>
              </el-form-item>
              <el-form-item :label="$t('screenDesignerView.valueToWrite')">
                <el-input-number v-model="selected.action.value" :controls="false" :step="1" @change="commitField('reset')" />
              </el-form-item>
            </template>
            <p class="field-hint">{{ $t('hmi.resetHint') }}</p>
          </template>
          <template v-else>
            <el-form-item :label="$t('hmi.silenceMinutes')" :error="fieldError('silence')">
              <el-input-number :model-value="selected.silenceMinutes ?? 5" :min="1" :max="120" :step="1" data-test="node-silence-minutes" @update:model-value="(v: number | undefined) => { if (selected) { selected.silenceMinutes = v ?? 5; commitField('silence') } }" />
            </el-form-item>
            <p class="field-hint">{{ $t('hmi.silenceHint') }}</p>
          </template>
        </template>
        <template v-if="selected.type === 'recipe'">
          <el-divider data-field="recipe">{{ $t('hmi.recipe') }}</el-divider>
          <el-form-item :label="$t('hmi.recipeTemplate')" :error="fieldError('recipe')" data-field="recipe">
            <el-select v-model="selected.recipeTemplateId" filterable clearable :placeholder="$t('hmi.recipePickTemplate')" data-test="node-recipe-template" @change="commitField('recipe')">
              <el-option v-for="template in recipeTemplates" :key="template.id" :value="template.id" :label="`${template.name}${template.dualApproval ? ` · ${$t('hmi.recipeDual')}` : ''}`" />
            </el-select>
            <p class="field-hint">{{ $t('hmi.recipeControlHint') }}</p>
          </el-form-item>
        </template>
        <template v-if="selected.type === 'equipment-panel'">
          <el-divider data-field="commands">{{ $t('hmi.commands') }}</el-divider>
          <div v-for="(command, index) in selected.commands" :key="command.key" class="hmi-command-row" data-test="panel-command-row">
            <el-form-item :label="$t('hmi.commandLabel')"><el-input v-model="command.label" maxlength="40" :aria-label="$t('hmi.commandLabel')" @change="commitField('command-label')" /></el-form-item>
            <el-form-item :label="$t('hmi.commandTag')">
              <TagSelect v-model="command.tagId" :tags="tags" :devices="devices" :values="tagValues" writable :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('hmi.commandTag')" @change="commit" />
              <p v-if="tagLiveText(command.tagId)" class="tag-live-row" :class="tagLiveClass(command.tagId)" :title="tagLiveTitle(command.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(command.tagId) }}</p>
            </el-form-item>
            <div class="hmi-config-row">
              <el-input-number v-model="command.value" :controls="false" :step="1" :aria-label="$t('hmi.commandValue')" @change="commitField('command-value')" />
              <el-tooltip :content="$t('screenDesignerView.helpCommandValue')"><el-icon class="field-help" role="img" :aria-label="$t('screenDesignerView.helpCommandValue')"><QuestionFilled /></el-icon></el-tooltip>
              <el-tooltip :content="$t('hmi.remove')"><el-button :icon="Delete" :aria-label="$t('hmi.remove')" @click="selected.commands?.splice(index, 1); commit()" /></el-tooltip>
            </div>
          </div>
          <el-button :icon="Plus" :disabled="(selected.commands?.length ?? 0) >= 8" data-test="panel-add-command" @click="addPanelCommand">{{ $t('hmi.addCommand') }}</el-button>
          <p v-if="fieldError('commands')" class="field-error">{{ fieldError('commands') }}</p>
        </template>
        <el-form-item v-if="selected.type === 'image'" data-field="image">
          <template #label><FieldLabel :label="$t('screenDesignerView.imageURL')" :help="$t('screenDesignerView.helpImage')" /></template>
          <el-input v-model="selected.text" maxlength="500" data-test="node-image-url" @change="commit" />
        </el-form-item>
        <el-form-item v-if="multiTagTypes.includes(selected.type)" :error="fieldError('charts')" data-field="charts">
          <template #label><FieldLabel :label="$t('screenDesignerView.dataTags')" :help="$t('screenDesignerView.helpChartTags')" /></template>
          <TagSelect v-model="selected.tags" :tags="tags" :devices="devices" :values="tagValues" multiple :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.dataTags')" data-test="node-chart-tags" @change="commit" />
        </el-form-item>
        <el-form-item v-if="['chart-line', 'chart-area', 'history-table'].includes(selected.type)" :error="fieldError('window')" data-field="window">
          <template #label><FieldLabel :label="$t('screenDesignerView.windowSeconds')" :help="$t('screenDesignerView.helpWindow')" /></template>
          <el-input-number :model-value="selected.windowSec ?? 300" :min="10" :max="86400" :step="10" @update:model-value="(v: number | undefined) => { if (selected) { selected.windowSec = v ?? 300; commitField('window') } }" />
        </el-form-item>
        <template v-if="selected.type === 'gauge' || selected.type === 'dial'">
          <el-divider data-field="gaugeBands">{{ $t('screenDesignerView.gaugeBands') }}</el-divider>
          <div v-for="(band, index) in selected.gaugeBands ?? []" :key="index" class="gauge-band-row" data-test="gauge-band-row">
            <el-input-number :model-value="band.from" :min="selected.min" :max="selected.max" :controls="false" :aria-label="$t('screenDesignerView.gaugeBandFrom')" @update:model-value="(v: number | undefined) => updateGaugeBand(index, 'from', v)" />
            <el-input-number :model-value="band.to" :min="selected.min" :max="selected.max" :controls="false" :aria-label="$t('screenDesignerView.gaugeBandTo')" @update:model-value="(v: number | undefined) => updateGaugeBand(index, 'to', v)" />
            <ColorField compact :model-value="band.color" :aria-label="$t('screenDesignerView.gaugeBandColor')" @update:model-value="(v: string) => updateGaugeBand(index, 'color', v)" />
            <el-tooltip :content="$t('hmi.remove')"><el-button :icon="Delete" :aria-label="$t('hmi.remove')" @click="removeGaugeBand(index)" /></el-tooltip>
          </div>
          <el-button :icon="Plus" :disabled="(selected.gaugeBands?.length ?? 0) >= 6" data-test="gauge-band-add" @click="addGaugeBand">{{ $t('screenDesignerView.gaugeBandAdd') }}</el-button>
          <el-form-item>
            <template #label>{{ $t('screenDesignerView.setpoint') }}</template>
            <div class="style-flags">
              <el-checkbox :model-value="selected.setpoint != null" data-test="gauge-setpoint-toggle" @update:model-value="toggleSetpoint">{{ $t('screenDesignerView.setpointShow') }}</el-checkbox>
              <el-input-number v-if="selected.setpoint != null" v-model="selected.setpoint" :min="selected.min" :max="selected.max" :controls="false" data-test="gauge-setpoint" @change="commitField('setpoint')" />
            </div>
          </el-form-item>
          <p class="field-hint">{{ $t('screenDesignerView.gaugeBandHint') }}</p>
          <p v-if="fieldError('gaugeBands') || fieldError('setpoint')" class="field-error">{{ fieldError('gaugeBands') || fieldError('setpoint') }}</p>
        </template>
        <template v-if="selected.type === 'table' || selected.type === 'history-table'">
          <el-divider data-field="tableColumns">{{ $t('screenDesignerView.tableColumns') }}</el-divider>
          <el-checkbox-group :model-value="selected.tableColumns ?? defaultTableColumns(selected)" data-test="table-columns" @update:model-value="setTableColumns">
            <el-checkbox v-for="choice in tableColumnChoices" :key="choice.key" :value="choice.key">{{ $t(choice.labelKey) }}</el-checkbox>
          </el-checkbox-group>
          <p class="field-hint">{{ $t('screenDesignerView.tableColumnsHint') }}</p>
          <p v-if="fieldError('tableColumns')" class="field-error">{{ fieldError('tableColumns') }}</p>
        </template>
        <template v-if="chartOptionTypes.includes(selected.type)">
          <el-divider data-field="chartOptions">{{ $t('screenDesignerView.chartOptions') }}</el-divider>
          <div class="style-flags">
            <el-checkbox :model-value="selected.chartOptions?.legend ?? true" data-test="chart-legend" @update:model-value="(v: any) => setChartOption('legend', !!v)">{{ $t('screenDesignerView.chartLegend') }}</el-checkbox>
            <el-checkbox :model-value="selected.chartOptions?.grid ?? true" data-test="chart-grid" @update:model-value="(v: any) => setChartOption('grid', !!v)">{{ $t('screenDesignerView.chartGrid') }}</el-checkbox>
            <el-checkbox :model-value="selected.chartOptions?.dataLabels ?? false" data-test="chart-labels" @update:model-value="(v: any) => setChartOption('dataLabels', !!v)">{{ $t('screenDesignerView.chartDataLabels') }}</el-checkbox>
          </div>
          <el-form-item>
            <template #label>{{ $t('screenDesignerView.chartYUnit') }}</template>
            <el-input :model-value="selected.chartOptions?.yUnit ?? ''" maxlength="32" data-test="chart-y-unit" @change="(v: string) => setChartOption('yUnit', v)" />
          </el-form-item>
          <template v-if="chartTargetTypes.includes(selected.type)">
            <el-divider>{{ $t('screenDesignerView.chartTargetLines') }}</el-divider>
            <div v-for="(line, index) in selected.chartOptions?.targetLines ?? []" :key="index" class="chart-target-row" data-test="chart-target-row">
              <el-input-number :model-value="line.value" :controls="false" :aria-label="$t('screenDesignerView.chartTargetValue')" @update:model-value="(v: number | undefined) => updateChartTarget(index, 'value', v)" />
              <ColorField compact :model-value="line.color" :aria-label="$t('screenDesignerView.chartTargetColor')" @update:model-value="(v: string) => updateChartTarget(index, 'color', v)" />
              <el-input :model-value="line.label ?? ''" maxlength="40" :aria-label="$t('screenDesignerView.chartTargetLabel')" @change="(v: string) => updateChartTarget(index, 'label', v)" />
              <el-tooltip :content="$t('hmi.remove')"><el-button :icon="Delete" :aria-label="$t('hmi.remove')" @click="removeChartTarget(index)" /></el-tooltip>
            </div>
            <el-button :icon="Plus" :disabled="(selected.chartOptions?.targetLines?.length ?? 0) >= 4" data-test="chart-target-add" @click="addChartTarget">{{ $t('screenDesignerView.chartTargetAdd') }}</el-button>
            <p class="field-hint">{{ $t('screenDesignerView.chartTargetHint') }}</p>
          </template>
          <p class="field-hint">{{ $t('screenDesignerView.chartOptionHint') }}</p>
          <p v-if="fieldError('chartOptions')" class="field-error">{{ fieldError('chartOptions') }}</p>
        </template>
        <template v-if="['symbol', 'pipe', 'polyline', 'pump', 'tank', 'valve'].includes(selected.type)">
          <el-divider data-field="animation">{{ $t('screenDesignerView.animation') }}</el-divider>
          <el-form-item>
            <template #label><FieldLabel :label="$t('screenDesignerView.animationType')" :help="$t('screenDesignerView.helpAnimation')" /></template>
            <el-select :model-value="selected.animation?.type ?? 'none'" :aria-label="$t('screenDesignerView.animationType')" data-test="node-anim-type" @update:model-value="setAnimType">
              <el-option :label="$t('screenDesignerView.noAnimation')" value="none" />
              <el-option v-if="['symbol', 'pipe', 'polyline', 'pump', 'valve'].includes(selected.type)" :label="$t('screenDesignerView.rotate')" value="rotate" />
              <el-option v-if="['symbol', 'pipe', 'polyline', 'tank'].includes(selected.type)" :label="$t('screenDesignerView.level')" value="level" />
              <el-option v-if="['symbol', 'pipe', 'polyline'].includes(selected.type)" :label="$t('screenDesignerView.flow')" value="flow" />
            </el-select>
          </el-form-item>
          <el-form-item v-if="selected.animation" :label="$t('screenDesignerView.trigger')"><el-select :model-value="selected.animation.source" :aria-label="$t('screenDesignerView.trigger')" @update:model-value="(v: string) => setAnimField('source', v as ScreenAnimation['source'])">
            <el-option :label="$t('screenDesignerView.valueMapping')" value="value" />
            <el-option :label="$t('screenDesignerView.stateTrigger')" value="state" />
          </el-select></el-form-item>
          <el-form-item v-if="selected.animation?.source === 'value'" :label="$t('screenDesignerView.driverTag')">
            <TagSelect :model-value="selected.animation.tagId" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.driverTag')" data-test="node-anim-tag" @update:model-value="(v: any) => setAnimField('tagId', v ?? null)" />
            <p v-if="tagLiveText(selected.animation.tagId)" class="tag-live-row" :class="tagLiveClass(selected.animation.tagId)" :title="tagLiveTitle(selected.animation.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.animation.tagId) }}</p>
          </el-form-item>
          <div v-if="selected.animation?.source === 'value'" class="field-pair"><el-form-item :label="$t('screenDesignerView.inputMin')"><el-input-number :model-value="selected.animation.from" :controls="false" :step="1" @update:model-value="(v: number | undefined) => setAnimField('from', v ?? 0)" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.inputMax')"><el-input-number :model-value="selected.animation.to" :controls="false" :step="1" @update:model-value="(v: number | undefined) => setAnimField('to', v ?? 100)" /></el-form-item></div>
          <div v-if="selected.animation && selected.animation.type !== 'flow'" class="field-pair"><el-form-item :label="$t('screenDesignerView.outputMin')"><el-input-number :model-value="selected.animation.outFrom" :controls="false" :step="1" @update:model-value="(v: number | undefined) => setAnimField('outFrom', v ?? 0)" /></el-form-item>
            <el-form-item :label="$t('screenDesignerView.outputMax')"><el-input-number :model-value="selected.animation.outTo" :controls="false" :step="1" @update:model-value="(v: number | undefined) => setAnimField('outTo', v ?? 360)" /></el-form-item></div>
          <p v-if="selected.animation" class="animation-summary" data-test="animation-preview">{{ animationSummary(selected.animation) }}<el-button class="group-reset" link size="small" :title="$t('screenDesignerView.resetAnimationHelp')" data-test="property-reset-animation" @click.stop.prevent="resetAnimation">{{ $t('screenDesignerView.restoreDefaults') }}</el-button></p>
          <p v-if="fieldError('animation')" class="field-error">{{ fieldError('animation') }}</p>
        </template>
        <el-form-item v-if="selected.type === 'oee-kpi'" :error="fieldError('oee')" data-field="oee">
          <template #label><FieldLabel :label="$t('model.line')" :help="$t('screenDesignerView.helpOee')" /></template>
          <el-input-number :model-value="selected.lineId ?? undefined" :min="1" :step="1" @update:model-value="(v: number | undefined) => { if (selected) { selected.lineId = v ?? null; commitField('oee') } }" />
        </el-form-item>
        </template>
        </details>
        <details v-if="hasDataSection" class="prop-group" open data-test="property-group-data">
          <summary>{{ isControlSelected ? $t('screenDesignerView.controlConfig') : $t('screenDesignerView.groupData') }}<el-button class="group-reset" link size="small" :title="$t('screenDesignerView.resetDataHelp')" data-test="property-reset-data" @click.stop.prevent="resetData">{{ $t('screenDesignerView.restoreDefaults') }}</el-button></summary>
          <template v-if="isControlSelected">
          <el-form-item :label="$t('screenDesignerView.controlTag')" :error="fieldError('control')" data-field="control">
            <TagSelect v-model="selected.action.tagId" :tags="tags" :devices="devices" :values="tagValues" writable :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.controlTag')" data-test="node-control-tag" @change="setControlTag" />
            <p v-if="tagLiveText(selected.action.tagId, selected.unit)" class="tag-live-row" :class="tagLiveClass(selected.action.tagId)" :title="tagLiveTitle(selected.action.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.action.tagId, selected.unit) }}</p>
            <p class="field-hint">{{ $t('screenDesignerView.controlTagHint') }}</p>
            <div v-if="selected.tagId !== selected.action.tagId" class="field-hint">
              {{ $t('screenDesignerView.controlTagMismatch') }}
              <el-button link type="primary" size="small" data-test="sync-control-tag" @click="syncControlTag">{{ $t('screenDesignerView.syncControlTag') }}</el-button>
            </div>
          </el-form-item>
          <template v-if="selected.type === 'jog' || selected.type === 'pulse'">
            <div class="field-pair" data-field="momentary">
              <el-form-item :label="$t('hmi.releaseValue')" :error="fieldError('momentary')">
                <el-input-number :model-value="selected.releaseValue ?? 0" :controls="false" :step="1" data-test="node-release-value" @update:model-value="(v: number | undefined) => { if (selected) { selected.releaseValue = v ?? 0; commitField('momentary') } }" />
              </el-form-item>
              <el-form-item v-if="selected.type === 'jog'" :label="$t('hmi.maxHoldMs')">
                <el-input-number :model-value="selected.maxHoldMs ?? 5000" :min="500" :max="30000" :step="500" data-test="node-max-hold" @update:model-value="(v: number | undefined) => { if (selected) { selected.maxHoldMs = v ?? 5000; commitField('momentary') } }" />
              </el-form-item>
              <el-form-item v-else :label="$t('hmi.pulseMs')">
                <el-input-number :model-value="selected.pulseMs ?? 500" :min="100" :max="5000" :step="100" data-test="node-pulse-ms" @update:model-value="(v: number | undefined) => { if (selected) { selected.pulseMs = v ?? 500; commitField('momentary') } }" />
              </el-form-item>
            </div>
            <p class="field-hint">{{ $t('hmi.momentaryHint') }}</p>
          </template>
          <template v-if="controlRangeTypes.includes(selected.type)">
            <div class="field-pair" data-field="range">
              <el-form-item :label="fieldLabel($t('screenDesignerView.rangeLow'), selected.unit)" :error="fieldError('range')"><el-input-number v-model="selected.min" :controls="false" :step="1" @change="commitField('min')" /></el-form-item>
              <el-form-item :label="fieldLabel($t('screenDesignerView.rangeHigh'), selected.unit)"><el-input-number v-model="selected.max" :controls="false" :step="1" @change="commitField('max')" /></el-form-item>
            </div>
            <el-form-item :error="fieldError('decimals')" data-field="decimals">
              <template #label><FieldLabel :label="$t('screenDesignerView.decimals')" :help="$t('screenDesignerView.helpDecimals')" /></template>
              <el-input-number v-model="selected.decimals" :min="0" :max="6" :step="1" @change="commitField('decimals')" />
            </el-form-item>
            <el-form-item>
              <template #label><FieldLabel :label="$t('screenDesignerView.unit')" :help="$t('screenDesignerView.helpUnit')" /></template>
              <el-input v-model="selected.unit" maxlength="32" />
            </el-form-item>
          </template>
          <el-form-item v-if="selected.type === 'slider'">
            <template #label><FieldLabel :label="$t('screenDesignerView.activeThreshold')" :help="$t('screenDesignerView.helpThreshold')" /></template>
            <el-input-number v-model="selected.activeThreshold" :controls="false" :step="0.1" @change="commitField('threshold')" />
          </el-form-item>
          <el-form-item :label="$t('screenDesignerView.alarmStateTag')" data-field="alarm">
            <TagSelect v-model="selected.alarmTagId" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.alarmStateTag')" data-test="node-alarm-tag" @change="commit" />
            <p v-if="tagLiveText(selected.alarmTagId)" class="tag-live-row" :class="tagLiveClass(selected.alarmTagId)" :title="tagLiveTitle(selected.alarmTagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.alarmTagId) }}</p>
          </el-form-item>
        </template>
        <template v-else>
          <el-form-item :label="$t('screenDesignerView.alarmStateTag')" data-field="alarm">
            <TagSelect v-model="selected.alarmTagId" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.alarmStateTag')" data-test="node-alarm-tag" @change="commit" />
            <p v-if="tagLiveText(selected.alarmTagId)" class="tag-live-row" :class="tagLiveClass(selected.alarmTagId)" :title="tagLiveTitle(selected.alarmTagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.alarmTagId) }}</p>
          </el-form-item>
          <el-form-item :label="$t('screenDesignerView.inputTag')" :error="fieldError('tags')" data-field="tags">
            <TagSelect v-model="selected.tagId" :tags="tags" :devices="devices" :values="tagValues" :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.inputTag')" @change="bindingChanged" />
            <p v-if="tagLiveText(selected.tagId, selected.unit)" class="tag-live-row" :class="tagLiveClass(selected.tagId)" :title="tagLiveTitle(selected.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.tagId, selected.unit) }}</p>
          </el-form-item>
          <el-form-item>
            <template #label><FieldLabel :label="$t('screenDesignerView.unit')" :help="$t('screenDesignerView.helpUnit')" /></template>
            <el-input v-model="selected.unit" maxlength="32" />
          </el-form-item>
          <div class="field-pair" data-field="range">
            <el-form-item :label="fieldLabel($t('screenDesignerView.rangeLow'), selected.unit)" :error="fieldError('range')"><el-input-number v-model="selected.min" :controls="false" :step="1" @change="commitField('min')" /></el-form-item>
            <el-form-item :label="fieldLabel($t('screenDesignerView.rangeHigh'), selected.unit)"><el-input-number v-model="selected.max" :controls="false" :step="1" @change="commitField('max')" /></el-form-item>
          </div>
          <el-form-item :error="fieldError('decimals')" data-field="decimals">
            <template #label><FieldLabel :label="$t('screenDesignerView.decimals')" :help="$t('screenDesignerView.helpDecimals')" /></template>
            <el-input-number v-model="selected.decimals" :min="0" :max="6" :step="1" @change="commitField('decimals')" />
          </el-form-item>
          <el-form-item>
            <template #label><FieldLabel :label="$t('screenDesignerView.activeThreshold')" :help="$t('screenDesignerView.helpThreshold')" /></template>
            <el-input-number v-model="selected.activeThreshold" :controls="false" :step="0.1" @change="commitField('threshold')" />
          </el-form-item>
        </template>
        </details>
        <details v-if="hasActionSection" class="prop-group" open data-test="property-group-action">
          <summary>{{ $t('screenDesignerView.groupAction') }}<el-button class="group-reset" link size="small" :title="$t('screenDesignerView.resetActionHelp')" data-test="property-reset-action" @click.stop.prevent="resetAction">{{ $t('screenDesignerView.restoreDefaults') }}</el-button></summary>
          <el-form-item :label="$t('screenDesignerView.action')" :error="fieldError('action')" data-field="action"><el-select v-model="selected.action.type" :aria-label="$t('screenDesignerView.clickAction')" @change="setActionType"><el-option :label="$t('screenDesignerView.noAction')" value="none" />
            <el-option :label="$t('screenDesignerView.navigateToScreen')" value="navigate" /><el-option :label="$t('screenDesignerView.popupScreen')" value="popup" /><el-option :label="$t('screenDesignerView.controlWrite')" value="command" />
            <el-option :label="$t('hmi.sequence')" value="sequence" /><el-option :label="$t('hmi.recipeAction')" value="recipe" /></el-select></el-form-item>
          <el-form-item v-if="['navigate', 'popup'].includes(selected.action.type)" :label="$t('screenDesignerView.targetScreen')"><el-select v-model="selected.action.screenId" filterable @change="commit">
            <el-option v-for="screen in screens" :key="screen.id" :value="screen.id" :label="$t('screenDesignerView.screenOptionLabel', [screen.name, screen.publishedRevision ? '' : $t('screenDesignerView.unpublished')])" /></el-select></el-form-item>
          <el-form-item v-if="selected.action.type === 'sequence'" :label="$t('hmi.sequence')" :error="fieldError('action')">
            <el-select v-model="selected.action.sequenceId" filterable :placeholder="$t('hmi.sequencePick')" data-test="node-action-sequence" @change="commit">
              <el-option v-for="sequence in sequences" :key="sequence.id" :value="sequence.id" :label="`${sequence.name} · ${sequence.steps.length}`" />
            </el-select>
            <p class="field-hint">{{ $t('hmi.sequenceActionHint') }}</p>
          </el-form-item>
          <el-form-item v-if="selected.action.type === 'recipe'" :label="$t('hmi.recipe')" :error="fieldError('action')">
            <el-select v-model="selected.action.recipeId" filterable :placeholder="$t('hmi.recipePick')" data-test="node-action-recipe" @change="commit">
              <el-option v-for="recipe in recipes" :key="recipe.id" :value="recipe.id" :label="`${recipe.name} · v${recipe.version}（${recipe.templateName}）`" />
            </el-select>
            <p class="field-hint">{{ $t('hmi.recipeActionHint') }}</p>
          </el-form-item>
          <template v-if="selected.action.type === 'command'">
            <el-form-item :label="$t('screenDesignerView.outputTag')">
              <TagSelect v-model="selected.action.tagId" :tags="tags" :devices="devices" :values="tagValues" writable :remote="!embedded && tags.length > 400" :label-formatter="tagLabel" :aria-label="$t('screenDesignerView.outputTag')" @change="commit" />
              <p v-if="tagLiveText(selected.action.tagId)" class="tag-live-row" :class="tagLiveClass(selected.action.tagId)" :title="tagLiveTitle(selected.action.tagId)">{{ $t('screenDesignerView.currentValue') }} · {{ tagLiveText(selected.action.tagId) }}</p>
            </el-form-item>
            <el-form-item>
              <template #label><FieldLabel :label="$t('screenDesignerView.valueToWrite')" :help="$t('screenDesignerView.helpActionValue')" /></template>
              <el-input-number v-model="selected.action.value" :controls="false" :step="1" @change="commitField('action-value')" />
            </el-form-item>
            <p class="muted tiny">{{ $t('screenDesignerView.previewControlHint') }}</p>
          </template>
        </details>
      </template>
      <template v-else>
        <el-form-item :label="$t('screenDesignerView.canvasWidth')"><el-input-number v-model="document.width" :min="320" :max="7680" :step="20" @change="resizeCanvas" /></el-form-item>
        <el-form-item :label="$t('screenDesignerView.canvasHeight')"><el-input-number v-model="document.height" :min="200" :max="4320" :step="20" @change="resizeCanvas" /></el-form-item>
        <el-form-item :label="$t('screenDesignerView.backgroundColor')"><ColorField v-model="document.background" :aria-label="$t('screenDesignerView.backgroundColor')" @change="commitField('background')" /></el-form-item>
        <p class="muted">{{ $t('screenDesignerView.canvasInteractionHint') }}</p>
      </template>
    </el-form>
    <el-button class="mobile-panel-done" type="primary" size="small" data-test="close-property-panel-done" @click="closePanel">{{ $t('screenDesignerView.done') }}</el-button>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import type { ScreenAction, ScreenAnimation, ScreenChartOptions, ScreenNode } from '../screens/model'
import type { DesignerPanelApi } from '../screens/designerPanelApi'
import ColorField from './ColorField.vue'
import FieldLabel from './FieldLabel.vue'
import TagSelect from './TagSelect.vue'
import { Close, Delete, Plus, QuestionFilled } from '@element-plus/icons-vue'

const props = defineProps<{ api: DesignerPanelApi }>()
const {
  selected, selectedNodes, selectedIds, document, embedded,
  tags, devices, tagValues, tagValuesError, screens, sequences, recipes, recipeTemplates,
  typeLabel, tagLabel, selectedIssues, selectedChangedCount, selectedNodeChanged, focusField, fieldError, closePanel,
  commonOf, mixedOf, commonAction, mixedAction, applyAll, applyNumber, applyAction,
  setBatchActionType, setActionType, syncBindings, batchActionType, batchShowsTarget, batchShowsCommand, batchShowFontSize,
  arrangeOperations, arrange,
  resetAppearance, resetData, resetAction, resetAnimation,
  showEnglishText, englishText, builtinSymbols, userSymbols, symbolName,
  showFontSize, defaultFontSizes, commit, commitField,
  showStyleSection, showTextStyle, setAlign, setStyleFlag, showShapeStyle, setStyleNumber,
  hasTypeSection, hasDataSection, hasActionSection, isControlSelected,
  tagLiveText, tagLiveClass, tagLiveTitle,
  pipeEnds, pipePorts, pipeEndUpdates, pipePortUpdates, addPipePoint,
  stateLimit, addCommonStates, addState, setResetTag, addPanelCommand,
  multiTagTypes, animationSummary, setAnimType, setAnimField,
  controlRangeTypes, fieldLabel, setControlTag, syncControlTag, bindingChanged, resizeCanvas
} = props.api

const chartOptionTypes = ['chart-bar', 'chart-pie', 'chart-line', 'chart-bar-horizontal', 'chart-bar-stack', 'chart-area', 'chart-radar', 'chart-funnel', 'chart-gauge']
const chartTargetTypes = ['chart-line', 'chart-area', 'chart-bar', 'chart-bar-horizontal', 'chart-bar-stack']
function defaultTableColumns(node: ScreenNode | undefined): string[] {
  if (!node) return []
  return node.type === 'history-table' ? ['name', 'last', 'min', 'max', 'avg'] : ['name', 'value', 'unit']
}
const tableColumnChoices = computed(() => {
  const node = selected.value
  if (!node) return []
  return node.type === 'history-table'
    ? [{ key: 'name', labelKey: 'model.tag' }, { key: 'last', labelKey: 'model.latest' }, { key: 'min', labelKey: 'model.min' }, { key: 'max', labelKey: 'model.max' }, { key: 'avg', labelKey: 'model.avg' }]
    : [{ key: 'name', labelKey: 'model.tag' }, { key: 'value', labelKey: 'model.value' }, { key: 'unit', labelKey: 'model.unit' }]
})
function setTableColumns(keys: (string | number)[]): void {
  const node = selected.value
  if (!node || keys.length === 0) return
  node.tableColumns = keys.map(String)
  commitField('tableColumns')
}
function updateGaugeBand(index: number, key: 'from' | 'to' | 'color', value: number | string | undefined): void {
  const node = selected.value
  if (!node || value === undefined) return
  const band = node.gaugeBands?.[index]
  if (!band) return
  if (key === 'color') band.color = String(value)
  else band[key] = Number(value)
  commitField('gaugeBands')
}
function addGaugeBand(): void {
  const node = selected.value
  if (!node || (node.gaugeBands?.length ?? 0) >= 6) return
  node.gaugeBands = [...(node.gaugeBands ?? []), { from: node.min, to: node.max, color: '#34d399' }]
  commitField('gaugeBands')
}
function removeGaugeBand(index: number): void {
  const node = selected.value
  if (!node?.gaugeBands) return
  node.gaugeBands.splice(index, 1)
  commitField('gaugeBands')
}
function toggleSetpoint(value: string | number | boolean | undefined): void {
  const node = selected.value
  if (!node) return
  node.setpoint = value ? Number(((node.min + node.max) / 2).toFixed(node.decimals)) : null
  commitField('setpoint')
}
function setChartOption<K extends keyof ScreenChartOptions>(key: K, value: ScreenChartOptions[K]): void {
  const node = selected.value
  if (!node) return
  node.chartOptions = { ...(node.chartOptions ?? {}), [key]: value }
  commitField(`chart-${key}`)
}
function addChartTarget(): void {
  const node = selected.value
  if (!node || (node.chartOptions?.targetLines?.length ?? 0) >= 4) return
  node.chartOptions = { ...(node.chartOptions ?? {}), targetLines: [...(node.chartOptions?.targetLines ?? []), { value: node.max, color: '#e03131', label: '' }] }
  commitField('chartTarget')
}
function updateChartTarget(index: number, key: 'value' | 'color' | 'label', value: number | string | undefined): void {
  const node = selected.value
  const line = node?.chartOptions?.targetLines?.[index]
  if (!node || !line || value === undefined) return
  if (key === 'value') line.value = Number(value)
  else if (key === 'color') line.color = String(value)
  else line.label = String(value)
  commitField('chartTarget')
}
function removeChartTarget(index: number): void {
  const node = selected.value
  if (!node?.chartOptions?.targetLines) return
  node.chartOptions.targetLines.splice(index, 1)
  commitField('chartTarget')
}
</script>

<style scoped>
.section-title { position: sticky; top: 0; z-index: 2; display: flex; align-items: flex-start; justify-content: space-between; gap: 8px; padding-bottom: 8px; margin-bottom: 10px; background: var(--scada-panel); border-bottom: 1px solid var(--scada-border); font-weight: 600; color: var(--scada-text); }
.section-title small { display: block; color: var(--scada-text-muted); font-weight: 400; font-size: 11px; margin-top: 4px; }
.property-heading-text { min-width: 0; }
.group-reset { margin-left: 8px; font-weight: 400; }
.field-help { color: var(--scada-text-muted); font-size: 13px; cursor: help; }
p { font-size: 12px; line-height: 1.8; }
.hmi-config-row { display: flex; gap: 5px; margin-bottom: 8px; align-items: center; }
.hmi-config-row .el-input-number { flex: 1; width: 0; min-width: 0; }
.hmi-state-row { display: grid; grid-template-columns: 52px minmax(0, 1fr) 30px 28px; gap: 4px; margin: 8px 0; align-items: center; }
.hmi-state-row .el-input-number { width: 100%; }
.hmi-state-row .el-button { padding: 5px; }
.hmi-command-row { padding: 8px 0; border-bottom: 1px solid var(--el-border-color); margin-bottom: 8px; }
.gauge-band-row { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) 30px 28px; gap: 4px; margin: 8px 0; align-items: center; }
.gauge-band-row .el-input-number { width: 100%; }
.gauge-band-row .el-button { padding: 5px; }
.chart-target-row { display: grid; grid-template-columns: minmax(0, 1fr) 30px minmax(0, 1.2fr) 28px; gap: 4px; margin: 8px 0; align-items: center; }
.chart-target-row .el-input-number { width: 100%; }
.chart-target-row .el-button { padding: 5px; }
.style-flags { display: flex; flex-wrap: wrap; gap: 2px 12px; margin-bottom: 6px; }
.field-pair { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
.field-pair :deep(.el-input-number) { width: 100%; }
.node-issues { margin-bottom: 12px; padding: 8px 10px; border: 1px solid var(--el-color-danger-light-5); border-radius: 6px; background: var(--el-color-danger-light-9); }
.node-issues-title { margin: 0 0 4px; color: var(--el-color-danger); font-size: 12px; font-weight: 600; }
.node-issue { display: block; width: 100%; padding: 2px 0; border: 0; background: transparent; color: var(--el-color-danger); font: inherit; font-size: 12px; line-height: 1.6; text-align: left; cursor: pointer; }
.node-issue:hover { text-decoration: underline; }
.field-error { margin: 4px 0 8px; color: var(--el-color-danger); font-size: 12px; line-height: 1.6; }
.field-hint { margin: 4px 0 0; color: var(--scada-text-muted); font-size: 11px; line-height: 1.6; }
.field-focus { outline: 2px solid var(--scada-primary); outline-offset: 2px; border-radius: 4px; }
.mixed-value { color: var(--scada-text-muted); font-weight: 400; }
.tag-live-row { margin: 4px 0 0; font-size: 11px; line-height: 1.6; color: var(--scada-text-muted); }
.tag-live-row.q-Good { color: var(--el-color-success); }
.tag-live-row.q-Uncertain { color: var(--el-color-warning); }
.tag-live-row.q-Bad, .tag-live-row.q-CommFailure { color: var(--el-color-danger); }
.tag-values-error { color: var(--el-color-warning); font-weight: 400; }
.state-preview { display: flex; flex-wrap: wrap; gap: 4px; margin: 6px 0 4px; }
.state-chip { display: inline-flex; align-items: center; gap: 4px; padding: 2px 6px; border: 1px solid var(--scada-border); border-radius: 10px; font-size: 11px; line-height: 1.5; color: var(--scada-text-secondary); }
.state-chip i { width: 8px; height: 8px; border-radius: 50%; }
.animation-summary { margin: 4px 0 0; font-size: 11px; line-height: 1.6; color: var(--scada-text-muted); }
.prop-group { margin-top: 6px; border-top: 1px solid var(--scada-border); }
.prop-group > summary { padding: 8px 0 6px; font-size: 12px; font-weight: 600; color: var(--scada-text); cursor: pointer; }
.prop-group[open] > summary { margin-bottom: 4px; }
.align-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px; }
.align-grid .el-button { margin-left: 0; }
.mobile-panel-close, .mobile-panel-done { display: none; }
@media (max-width: 900px) { .mobile-panel-close { display: inline-flex; flex-shrink: 0; } .mobile-panel-done { display: flex; width: 100%; margin-top: 14px; position: sticky; bottom: 0; } }
</style>

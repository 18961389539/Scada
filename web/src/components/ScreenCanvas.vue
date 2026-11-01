<template>
  <svg ref="canvasRoot" class="screen-canvas" :class="{ editing }" :viewBox="`0 0 ${document.width} ${document.height}`"
    :style="{ background: document.background, width: `${document.width * zoom}px`, height: `${document.height * zoom}px` }"
    role="img" :aria-label="$t('screenCanvas.processScreens')" @pointerdown.self="$emit('background', $event)" @dragover.prevent @drop.prevent="$emit('drop', $event)">
    <defs>
      <pattern :id="gridId" width="20" height="20" patternUnits="userSpaceOnUse"><circle cx="1" cy="1" r="0.8" fill="#60758e" opacity="0.4" /></pattern>
    </defs>
    <rect v-if="editing" :width="document.width" :height="document.height" :fill="`url(#${gridId})`" @pointerdown="$emit('background', $event)" />
    <g v-if="guides" data-test="alignment-guides" pointer-events="none">
      <line v-for="x in guides.vertical" :key="`v${x}`" :x1="x" :y1="0" :x2="x" :y2="document.height" stroke="#f59f00" stroke-width="1" stroke-dasharray="6 4" />
      <line v-for="y in guides.horizontal" :key="`h${y}`" :x1="0" :y1="y" :x2="document.width" :y2="y" stroke="#f59f00" stroke-width="1" stroke-dasharray="6 4" />
    </g>
    <g v-for="node in nodes" :key="node.id" :data-node-id="node.id" :data-node-type="node.type"
      :data-state="state(node)"
      :data-control-unavailable="feedbackTypes.includes(node.type) && !controlReferenceGood(node) ? 'true' : undefined"
      :transform="`translate(${node.x} ${node.y}) rotate(${node.rotation} ${node.width / 2} ${node.height / 2})`"
      :class="{ actionable: !editing && isActionable(node), selected: selectedIds.includes(node.id), invalid: node.tagId && !isGood(node), alarm: alarmed(node), frame: node.type === 'template', inner: !!node.ownerId, locked: !!node.locked }" :style="node.type === 'template' && !editing ? { pointerEvents: 'none' } : undefined"
      :tabindex="rovingTabindex.get(node.id)" :role="isActionable(node) ? 'button' : undefined"
      :aria-label="label(node) || node.type" :aria-disabled="!editing && feedbackTypes.includes(node.type) ? !commandEnabled(node.action.tagId) || (node.type !== 'slider' && !controlReferenceGood(node)) : undefined" @pointerdown.stop="!editing && $emit('nodePointerDown', owner(node), $event)"
      @mousedown.stop="editing && $emit('nodePointerDown', owner(node), $event)" @touchstart.stop="editing && $emit('nodePointerDown', owner(node), $event)"
      @click.stop="!editing && activateNode(node)" @keydown="nodeKeydown(node, $event)" @keyup="nodeKeyup(node, $event)" @focus="!editing && (focusedNodeId = node.id)">
      <title>{{ label(node) }}{{ node.tagId ? $t('screenCanvas.titleWithValue', [isGood(node) ? formatted(node) : $t('screenCanvas.dataQualityInvalidOrStale')]) : '' }}</title>
      <rect :width="node.width" :height="node.height" fill="transparent" />
      <svg v-if="node.type === 'symbol'" :width="node.width" :height="node.height" :style="{ color: color(node), ...animationStyle(node) }" :class="animationClass(node)" :data-symbol="node.symbol" :data-state="state(node)" overflow="visible">
        <g v-html="symbolSvg(node)" />
        <text v-if="label(node)" :x="node.width / 2" :y="node.height + 13" text-anchor="middle" :font-size="nodeFontSize(node, 12)" fill="#dce8f5">{{ deviceLabel(node) }}</text>
      </svg>
      <rect v-else-if="node.type === 'template' && (editing || !templateKnown(node))" :width="node.width" :height="node.height" fill="none" :stroke="templateKnown(node) ? '#63e6be55' : '#f59f00'" stroke-dasharray="6 4" />
      <text v-if="node.type === 'template' && !templateKnown(node)" :x="node.width / 2" :y="node.height / 2" text-anchor="middle" fill="#f59f00" font-size="13">{{ libraryFailed ? $t('screenCanvas.libraryUnavailable') : $t('screenCanvas.templateDoesNotExist', [node.templateKey]) }}</text>
      <svg :width="node.width" :height="node.height" viewBox="0 0 140 112" overflow="visible" v-if="['pump', 'valve', 'tank', 'motor', 'fan', 'gauge', 'dial'].includes(node.type)">
        <g :stroke="color(node)" stroke-width="3" fill="none" stroke-linejoin="round">
          <g v-if="node.type === 'pump'">
            <path d="M0 55 H35 M99 55 H140 M42 87 H98 L108 99 H32 Z" />
            <circle cx="70" cy="55" r="32" :fill="active(node) ? color(node) + '25' : 'none'" />
            <g :style="equipmentAnimationStyle(node, 'pump')" :class="equipmentAnimationClass(node, 'pump')" style="transform-origin: 70px 55px" data-test="pump-rotor">
              <path d="M54 37 L89 55 L54 73 Z" :fill="color(node)" stroke="none" />
            </g>
          </g>
          <g v-else-if="node.type === 'valve'">
            <path d="M0 58 H26 M114 58 H140 M26 34 L114 82 V34 L26 82 Z" :fill="active(node) ? color(node) + '35' : 'none'" />
            <g :style="equipmentAnimationStyle(node, 'valve')" style="transform-origin: 70px 58px" data-test="valve-handle">
              <path d="M70 58 V13 M48 13 H92" />
            </g>
          </g>
          <g v-else-if="node.type === 'tank'">
            <rect x="27" y="13" width="86" height="82" rx="12" />
            <rect x="32" :y="90 - tankFillRatio(node) * 72" width="76" :height="tankFillRatio(node) * 72" rx="3" :fill="color(node)" stroke="none" opacity="0.55" data-test="tank-fill" />
            <path d="M27 29 H113 M27 80 H113 M0 72 H27 M113 72 H140" />
          </g>
          <g v-else-if="node.type === 'motor'">
            <rect x="30" y="23" width="80" height="62" rx="8" :fill="active(node) ? color(node) + '25' : 'none'" />
            <path d="M0 55 H30 M110 55 H140 M43 86 V96 H97 V86" />
            <text x="70" y="66" text-anchor="middle" font-size="30" :fill="color(node)" stroke="none">M</text>
          </g>
          <g v-else-if="node.type === 'fan'">
            <circle cx="70" cy="55" r="40" />
            <g :class="{ spinning: active(node) }" style="transform-origin: 70px 55px">
              <path d="M70 55 C25 35 46 12 65 29 Z M70 55 C110 24 119 58 94 62 Z M70 55 C72 106 39 91 53 72 Z" :fill="color(node)" />
            </g>
            <circle cx="70" cy="55" r="7" :fill="color(node)" />
          </g>
          <g v-else-if="node.type === 'gauge'">
            <path d="M25 78 A50 50 0 1 1 115 78" stroke-width="9" />
            <path v-for="(band, bandIndex) in node.gaugeBands ?? []" :key="bandIndex" :d="gaugeBandPath(node, band)" :stroke="band.color" stroke-width="9" fill="none" data-test="gauge-band" />
            <path v-if="node.setpoint != null" :d="gaugeSetpointPath(node)" stroke="#f8fafc" stroke-width="3" data-test="gauge-setpoint" />
            <path d="M70 62 L70 20" :transform="`rotate(${-125 + ratio(node) * 250} 70 62)`" stroke="#f8fafc" />
            <circle cx="70" cy="62" r="5" :fill="color(node)" />
            <text x="70" y="94" text-anchor="middle" :font-size="nodeFontSize(node, 15)" fill="#f8fafc" stroke="none">{{ formatted(node) }}</text>
          </g>
          <g v-else-if="node.type === 'dial'">
            <path d="M25 78 A50 50 0 1 1 115 78" stroke-width="9" />
            <path v-for="(band, bandIndex) in node.gaugeBands ?? []" :key="bandIndex" :d="gaugeBandPath(node, band)" :stroke="band.color" stroke-width="9" fill="none" data-test="gauge-band" />
            <line v-for="t in dialTicks" :key="t.i" :x1="t.x1" :y1="t.y1" :x2="t.x2" :y2="t.y2" stroke="#9fb3c8" stroke-width="2" />
            <path v-if="node.setpoint != null" :d="gaugeSetpointPath(node)" stroke="#f8fafc" stroke-width="3" data-test="gauge-setpoint" />
            <path d="M70 62 L70 22" stroke-width="3" stroke="#f8fafc" :transform="`rotate(${-125 + ratio(node) * 250} 70 62)`" />
            <circle cx="70" cy="62" r="5" :fill="color(node)" />
            <text x="70" y="98" text-anchor="middle" :font-size="nodeFontSize(node, 15)" fill="#f8fafc" stroke="none">{{ formatted(node) }}</text>
          </g>
        </g>
        <text v-if="!['gauge', 'dial'].includes(node.type)" x="70" y="110" text-anchor="middle" :font-size="nodeFontSize(node, 12)" fill="#dce8f5" :textLength="Math.min(132, deviceLabel(node).length * nodeFontSize(node, 12))" lengthAdjust="spacingAndGlyphs">{{ deviceLabel(node) }}</text>
      </svg>
      <path v-else-if="node.type === 'pipe'" :d="`M0 ${node.height / 2} H${node.width}`" :stroke="color(node)" stroke-width="8" fill="none"
        :stroke-dasharray="pipeFlow(node) ? '18 7' : undefined" :class="{ flowing: pipeFlow(node) }" />
      <polyline v-else-if="node.type === 'polyline'" :points="node.points?.map(point => `${point.x * node.width / 100},${point.y * node.height / 100}`).join(' ')" :stroke="color(node)" stroke-width="8" stroke-linejoin="round" stroke-linecap="round" fill="none"
        :stroke-dasharray="pipeFlow(node) ? '18 7' : undefined" :class="{ flowing: pipeFlow(node) }" />
      <foreignObject v-else-if="node.type === 'input' || node.type === 'equipment-panel'" :width="node.width" :height="node.height">
        <div class="equipment-control" :style="{ borderColor: color(node), pointerEvents: editing ? 'none' : undefined, fontSize: `${nodeFontSize(node, 13)}px` }" @pointerdown.stop @mousedown.stop @touchstart.stop @click.stop @keydown.stop>
          <strong :title="label(node)">{{ label(node) }}</strong>
          <template v-if="node.type === 'equipment-panel'">
            <div class="equipment-feedback"><span class="state-indicator" :style="{ background: color(node) }" /><span data-state-label>{{ stateLabel(node) }}</span><output>{{ formatted(node) }}</output></div>
            <div class="panel-commands">
              <button v-for="command in node.commands" :key="command.key" type="button" :title="command.label" :disabled="!commandEnabled(command.tagId)" @click="emit('command', node, command.value, command.key)"><Position aria-hidden="true" /><span>{{ command.label }}</span></button>
              <span v-if="!node.commands?.length" class="control-muted">{{ $t('hmi.noCommands') }}</span>
            </div>
          </template>
          <form v-else class="numeric-form" @submit.prevent="submitInput(node)">
            <input type="number" required :aria-label="label(node) || $t('hmi.value')" :value="inputDrafts[node.id] ?? (controlReferenceGood(node) ? ctrlValue(node) : '')" :min="node.min" :max="node.max" :step="10 ** -node.decimals" :disabled="!commandEnabled(node.action.tagId)" @input="updateInput(node, $event)" />
            <span v-if="node.unit" class="input-unit" :title="node.unit">{{ node.unit }}</span>
            <button type="submit" :title="$t('hmi.apply')" :aria-label="$t('hmi.apply')" :disabled="!commandEnabled(node.action.tagId)"><Check aria-hidden="true" /></button>
          </form>
          <span v-if="inputErrors[node.id]" role="alert" class="input-error">{{ $t('hmi.invalidInput') }}</span>
        </div>
      </foreignObject>
      <foreignObject v-else-if="node.type === 'recipe'" :width="node.width" :height="node.height">
        <div class="recipe-control" :style="{ borderColor: color(node), pointerEvents: editing ? 'none' : undefined, fontSize: `${nodeFontSize(node, 13)}px` }" @pointerdown.stop @mousedown.stop @touchstart.stop @click.stop @keydown.stop>
          <strong :title="label(node) || recipeTemplate(node)?.name">{{ label(node) || recipeTemplate(node)?.name || $t('hmi.recipe') }}</strong>
          <span v-if="editing" class="recipe-hint" data-test="recipe-editing-hint">{{ recipeTemplate(node)?.name ?? (node.recipeTemplateId ? `#${node.recipeTemplateId}` : $t('hmi.recipePickTemplate')) }}</span>
          <span v-else-if="!recipeTemplate(node)" class="recipe-hint">{{ $t('hmi.recipeUnavailable') }}</span>
          <span v-else-if="!canRecipeUse(node)" class="recipe-hint">{{ $t('hmi.recipeDenied') }}</span>
          <template v-else>
            <div class="recipe-pick">
              <select :value="recipeDrafts[node.id] ?? ''" :aria-label="label(node) || recipeTemplate(node)?.name || $t('hmi.recipe')" :disabled="!recipeOptions(node).length" data-test="recipe-select" @change="updateRecipeDraft(node, $event)">
                <option value="" disabled>{{ recipeOptions(node).length ? $t('hmi.recipePick') : $t('hmi.recipeEmpty') }}</option>
                <option v-for="recipe in recipeOptions(node)" :key="recipe.id" :value="recipe.id">{{ recipe.name }} · v{{ recipe.version }}</option>
              </select>
              <button type="button" :disabled="!recipeDrafts[node.id]" data-test="recipe-download" @click="emitRecipe(node)">{{ $t('hmi.recipeDownload') }}</button>
            </div>
            <span v-if="recipeTemplate(node)?.dualApproval" class="recipe-hint">{{ $t('hmi.recipeDual') }}</span>
          </template>
        </div>
      </foreignObject>
      <rect v-else-if="node.type === 'rectangle' || node.type === 'button'" x="2" y="2" :width="node.width - 4" :height="node.height - 4"
        :rx="node.radius ?? (node.type === 'button' ? 8 : 2)" :fill="node.type === 'button' ? color(node) + '35' : color(node) + '15'" :stroke="color(node)" :stroke-width="node.borderWidth ?? 2" :opacity="node.opacity ?? undefined" />
      <ellipse v-else-if="node.type === 'ellipse'" :cx="node.width / 2" :cy="node.height / 2" :rx="node.width / 2 - 2" :ry="node.height / 2 - 2"
        :fill="color(node) + '25'" :stroke="color(node)" :stroke-width="node.borderWidth ?? 2" :opacity="node.opacity ?? undefined" />
      <g v-else-if="node.type === 'led'">
        <circle :cx="node.width / 2" :cy="node.height / 2" :r="Math.max(4, Math.min(node.width, node.height) / 2 - 3)" :fill="color(node)"
          :opacity="active(node) ? 1 : 0.3" stroke="#0f172a" stroke-width="2" :class="{ lit: active(node) }" />
      </g>
      <g v-else-if="node.type === 'progress'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" fill="#1e293b" :stroke="node.color" stroke-width="2" />
        <rect x="3" y="3" :width="Math.max(0, (node.width - 6) * ratio(node))" :height="node.height - 6" rx="4" :fill="color(node)" />
        <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 14)" fill="#e5effa">{{ formatted(node) }}</text>
      </g>
      <g v-else-if="node.type === 'kpi'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" y="24" text-anchor="middle" font-size="14" fill="#9fb3c8">{{ label(node) }}</text>
        <text :x="node.width / 2" :y="node.height / 2 + 16" text-anchor="middle" :font-size="nodeFontSize(node, 28)" :fill="node.states?.length && !mappedState(node) ? '#e5effa' : color(node)">{{ formatted(node) }}</text>
      </g>
      <g v-else-if="node.type === 'clock'">
        <text :x="node.width / 2" :y="node.height / 2 - 4" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 26)" fill="#e5effa">{{ clockTime }}</text>
        <text :x="node.width / 2" :y="node.height / 2 + 22" text-anchor="middle" font-size="13" fill="#9fb3c8">{{ clockDate }}</text>
      </g>
      <g v-else-if="node.type === 'image'">
        <image v-if="node.text" x="0" y="0" :width="node.width" :height="node.height" :href="node.text" preserveAspectRatio="xMidYMid meet" />
        <template v-else>
          <rect :width="node.width" :height="node.height" rx="4" fill="none" :stroke="node.color" stroke-dasharray="6 4" />
          <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="13" fill="#9fb3c8">{{ $t('model.image') }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'marquee'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="4" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <clipPath :id="`mc-${node.id}`"><rect x="3" y="3" :width="node.width - 6" :height="node.height - 6" /></clipPath>
        <g :clip-path="`url(#mc-${node.id})`">
          <text x="0" :y="node.height / 2" dominant-baseline="central" :font-size="nodeFontSize(node, 16)" fill="#e5effa" class="marquee-text">{{ label(node) }}</text>
        </g>
      </g>
      <g v-else-if="node.type === 'alarm-banner'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" :fill="!alarmUnavailable && alarmTotal ? alarmLevelColor(alarmTop) + '22' : '#243449'"
          :stroke="alarmUnavailable ? '#f59f00' : alarmTotal ? alarmLevelColor(alarmTop) : '#334155'" stroke-width="2" :class="{ alarm: !alarmUnavailable && alarmTop === 'Critical' }" />
        <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 16)" :fill="alarmTotal ? '#f8fafc' : '#9fb3c8'">
          {{ alarmUnavailable ? $t('screenMonitor.alarmsUnavailable') : alarmTotal ? alarmLimited ? $t('screenMonitor.partialAlarmCount', [alarmTotal]) : $t('screenMonitor.alarmCount', [alarmTotal]) : $t('screenMonitor.systemAlarms') + ' · ' + $t('model.noAlarms') }}
        </text>
      </g>
      <g v-else-if="node.type === 'alarm-list'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text x="8" y="17" font-size="12" fill="#9fb3c8">{{ $t('screenMonitor.systemAlarms') }}</text>
        <line x1="4" :x2="node.width - 4" y1="22" y2="22" stroke="#243449" />
        <text v-if="alarmUnavailable" x="8" y="40" font-size="12" fill="#fbbf24">{{ $t('screenMonitor.alarmsUnavailable') }}</text>
        <template v-for="(a, i) in alarmUnavailable ? [] : alarmRows(node)" :key="a.id">
          <rect x="8" :y="26 + i * 20" width="8" height="8" rx="2" :fill="alarmLevelColor(a.level)" />
          <text x="22" :y="34 + i * 20" font-size="12" fill="#e5effa">{{ alarmText(node, a) }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'table'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="4" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text v-for="column in tableColumnLayout(node)" :key="column.key" :x="column.x" y="18" :text-anchor="column.anchor" font-size="12" fill="#9fb3c8" class="table-sort" @click.stop="sortTable(node, column.key)">{{ column.label + sortArrow(node, column.key) }}</text>
        <line x1="4" :x2="node.width - 4" y1="24" y2="24" stroke="#243449" />
        <g v-for="(t, i) in tableRows(node)" :key="t" class="table-row" :class="{ selected: tableSelections[node.id] === t }" @click.stop="selectTableRow(node, t)">
          <rect class="row-bg" x="2" :y="27 + i * 20" :width="node.width - 4" height="19" fill="transparent" />
          <text v-for="column in tableColumnLayout(node)" :key="column.key" :x="column.x" :y="42 + i * 20" :text-anchor="column.anchor" font-size="12" :fill="column.key === 'name' ? '#e5effa' : column.key === 'value' ? (tagGood(t) ? '#e5effa' : '#fbbf24') : '#9fb3c8'" pointer-events="none">{{ tableCellText(node, t, column.key) }}</text>
        </g>
        <foreignObject v-if="!editing" :x="node.width - 52" :y="node.height - 20" width="48" height="18">
          <div xmlns="http://www.w3.org/1999/xhtml" class="widget-tools"><button type="button" data-test="table-export" @click.stop="exportTable(node)">CSV</button></div>
        </foreignObject>
      </g>
      <g v-else-if="node.type === 'history-table'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="4" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text v-for="column in tableColumnLayout(node)" :key="column.key" :x="column.x" y="18" :text-anchor="column.anchor" font-size="12" fill="#9fb3c8" class="table-sort" @click.stop="sortTable(node, column.key)">{{ column.label + sortArrow(node, column.key) }}</text>
        <line x1="4" :x2="node.width - 4" y1="24" y2="24" stroke="#243449" />
        <g v-for="(t, i) in tableRows(node)" :key="t" class="table-row" :class="{ selected: tableSelections[node.id] === t }" @click.stop="selectTableRow(node, t)" @dblclick.stop="drillTableRow(node, t)">
          <title>{{ $t('hmi.tableDrillHint') }}</title>
          <rect class="row-bg" x="2" :y="27 + i * 20" :width="node.width - 4" height="19" fill="transparent" />
          <text v-for="column in tableColumnLayout(node)" :key="column.key" :x="column.x" :y="42 + i * 20" :text-anchor="column.anchor" font-size="12" :fill="column.key === 'name' || column.key === 'last' ? '#e5effa' : '#9fb3c8'" pointer-events="none">{{ tableCellText(node, t, column.key) }}</text>
        </g>
        <foreignObject v-if="!editing" :x="node.width - 52" :y="node.height - 20" width="48" height="18">
          <div xmlns="http://www.w3.org/1999/xhtml" class="widget-tools"><button type="button" data-test="table-export" @click.stop="exportTable(node)">CSV</button></div>
        </foreignObject>
      </g>
      <g v-else-if="node.type === 'oee-kpi'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <template v-for="m in oeeMetrics(node)" :key="m.key">
          <text :x="m.x" :y="m.y" font-size="12" fill="#9fb3c8">{{ m.label }}</text>
          <text :x="m.x" :y="m.y + 24" :font-size="nodeFontSize(node, 22)" :fill="m.color">{{ m.text }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'jog'" :class="{ actionable: !editing, held: !!heldNodes[node.id] }"
        @pointerdown.stop.prevent="!editing && jogStart(node, $event)" @pointerup.stop.prevent="jogEnd(node)" @pointercancel="jogEnd(node)"
        @pointerleave="jogEnd(node)" @lostpointercapture="jogEnd(node)" @click.stop>
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8"
          :fill="jogActive(node) ? node.activeColor + '33' : '#1e293b'" :stroke="jogActive(node) ? node.activeColor : node.color" stroke-width="2" />
        <text :x="node.width / 2" :y="node.height / 2 - (node.height >= 44 ? 6 : 0)" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 13)" fill="#e5effa">{{ label(node) }}</text>
        <text v-if="node.height >= 44" :x="node.width / 2" :y="node.height - 9" text-anchor="middle" :font-size="10" fill="#9fb3c8">{{ $t('hmi.pressHold') }}</text>
      </g>
      <g v-else-if="node.type === 'pulse'" :class="{ actionable: !editing }">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8" fill="#1e293b" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" :y="node.height / 2 - (node.height >= 44 ? 6 : 0)" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 13)" fill="#e5effa">{{ label(node) }}</text>
        <text v-if="node.height >= 44" :x="node.width / 2" :y="node.height - 9" text-anchor="middle" :font-size="10" fill="#9fb3c8">{{ $t('hmi.pulseHint') }}</text>
      </g>
      <g v-else-if="node.type === 'selector'" :class="{ actionable: !editing }">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" y="12" text-anchor="middle" :font-size="Math.min(nodeFontSize(node, 12), node.height / 4)" fill="#9fb3c8">{{ label(node) }}</text>
        <g v-for="(position, index) in node.states ?? []" :key="position.value">
          <rect :x="2 + index * (node.width - 4) / (node.states?.length || 1)" :y="Math.max(20, node.height * 0.32)" :width="(node.width - 4) / (node.states?.length || 1) - 2" :height="node.height - Math.max(20, node.height * 0.32) - 4" rx="3"
            :fill="activePosition(node) === position ? position.color + '66' : '#1e293b'" :stroke="activePosition(node) === position ? position.color : '#475569'" stroke-width="1.5"
            @click.stop="!editing && pickPosition(node, position)" />
          <text :x="2 + (index + 0.5) * (node.width - 4) / (node.states?.length || 1)" :y="Math.max(20, node.height * 0.32) + (node.height - Math.max(20, node.height * 0.32) - 4) / 2" text-anchor="middle" dominant-baseline="central"
            :font-size="selectorFontSize(node)" fill="#e5effa" @click.stop="!editing && pickPosition(node, position)">{{ position.label }}</text>
        </g>
      </g>
      <g v-else-if="node.type === 'knob'" :class="{ actionable: !editing }" @click.stop="!editing && pickKnob(node, $event)">
        <rect :width="node.width" :height="node.height" rx="8" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" y="14" text-anchor="middle" :font-size="Math.min(nodeFontSize(node, 12), node.height / 6)" fill="#9fb3c8">{{ label(node) }}</text>
        <circle v-for="(position, index) in node.states ?? []" :key="position.value" :cx="knobPoint(node, index).x" :cy="knobPoint(node, index).y" r="4"
          :fill="activePosition(node) === position ? position.color : '#475569'" />
        <line :x1="node.width / 2" :y1="knobCenter(node).y" :x2="knobPoint(node, knobIndex(node)).x" :y2="knobPoint(node, knobIndex(node)).y" stroke="#e5effa" stroke-width="4" stroke-linecap="round" />
        <circle :cx="node.width / 2" :cy="knobCenter(node).y" r="8" :fill="activePosition(node)?.color ?? node.color" />
        <text :x="node.width / 2" :y="node.height - 10" text-anchor="middle" :font-size="Math.min(nodeFontSize(node, 12), node.height / 8)" fill="#e5effa">{{ activePosition(node)?.label ?? '--' }}</text>
      </g>
      <g v-else-if="node.type === 'reset' || node.type === 'silence'" :class="{ actionable: !editing }">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8" fill="#1e293b" :stroke="node.color" stroke-width="2" />
        <text :x="node.height / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="Math.min(24, node.height * 0.5)" :fill="node.type === 'reset' ? '#f59f00' : node.color">{{ node.type === 'reset' ? '⟲' : '♪' }}</text>
        <text :x="node.height / 2 + (node.width - node.height / 2) / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 14)" fill="#e5effa">{{ label(node) }}</text>
      </g>
      <g v-else-if="node.type === 'switch'" :class="{ actionable: !editing }" @click.stop="!editing && toggleSwitch(node)">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" :rx="(node.height - 2) / 2" :fill="switchOn(node) ? '#34d39933' : '#1e293b'" :stroke="switchOn(node) ? '#34d399' : '#475569'" stroke-width="2" />
        <circle :cx="switchOn(node) ? node.width - node.height / 2 : node.height / 2" :cy="node.height / 2" :r="node.height / 2 - 6" :fill="switchOn(node) ? '#34d399' : '#64748b'" />
        <text :x="switchOn(node) ? node.height * 0.5 : node.width - node.height * 0.5" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 11)" fill="#e5effa">{{ !controlReferenceGood(node) ? '?' : switchOn(node) ? 'ON' : 'OFF' }}</text>
      </g>
      <g v-else-if="node.type === 'slider'" :class="{ actionable: !editing }" @pointerdown.stop="!editing && sliderStart(node, $event)" @pointermove="!editing && sliderMove(node, $event)" @pointerup="!editing && sliderEnd(node)" @pointercancel="sliderCancel(node)" @lostpointercapture="sliderCancel(node)">
        <rect x="1" :y="node.height / 2 - 5" :width="node.width - 2" height="10" rx="5" fill="#1e293b" :stroke="node.color" stroke-width="1" />
        <rect x="1" :y="node.height / 2 - 5" :width="Math.max(2, (node.width - 2) * sliderRatio(node))" height="10" rx="5" :fill="active(node) ? node.activeColor : node.color" />
        <circle :cx="2 + (node.width - 6) * sliderRatio(node)" :cy="node.height / 2" r="9" fill="#e5effa" />
        <text :x="node.width / 2" :y="node.height / 2 - 14" text-anchor="middle" :font-size="nodeFontSize(node, 12)" fill="#9fb3c8">{{ node.id in drafts || controlReferenceGood(node) ? sliderText(node).toFixed(node.decimals) : '--' }} {{ node.unit }}</text>
      </g>
      <g v-else-if="node.type === 'spinner'" :class="{ actionable: !editing }">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 16)" fill="#e5effa">{{ controlReferenceGood(node) ? ctrlValue(node).toFixed(node.decimals) : '--' }} {{ node.unit }}</text>
        <g @click.stop="!editing && stepValue(node, -1)"><rect x="4" y="6" width="22" :height="node.height - 12" rx="4" fill="#243449" /><text :x="15" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 16)" fill="#e5effa">−</text></g>
        <g @click.stop="!editing && stepValue(node, 1)"><rect :x="node.width - 26" y="6" width="22" :height="node.height - 12" rx="4" fill="#243449" /><text :x="node.width - 15" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="nodeFontSize(node, 16)" fill="#e5effa">＋</text></g>
      </g>
      <g v-else-if="chartTypes.includes(node.type)" data-chart :data-unavailable="chartUnavailable(node) ? 'true' : undefined" v-html="chartSvg(node)" />
      <g v-if="isTrendChart(node) && !editing" class="trend-hit" :data-test="`trend-hit-${node.id}`" @pointermove.stop="trendMove(node, $event)" @pointerleave.stop="trendLeave(node)" @click.stop="emit('trend', node)">
        <rect :width="node.width" :height="node.height" fill="transparent" />
        <g v-if="chartHover[node.id]" pointer-events="none">
          <line :x1="chartHover[node.id]!.px" :x2="chartHover[node.id]!.px" :y1="chartHover[node.id]!.top" :y2="chartHover[node.id]!.bottom" stroke="#9fb3c8" stroke-width="1" stroke-dasharray="4 3" />
          <circle v-for="(marker, index) in chartHover[node.id]!.markers" :key="index" :cx="marker.x" :cy="marker.y" r="3" :fill="marker.color" stroke="#0f172a" />
          <g :transform="`translate(${chartHover[node.id]!.box.x} ${chartHover[node.id]!.box.y})`">
            <rect :width="chartHover[node.id]!.box.width" :height="chartHover[node.id]!.box.height" rx="4" fill="#0b1622" fill-opacity="0.94" stroke="#334155" />
            <text x="8" y="15" font-size="11" fill="#9fb3c8">{{ chartHover[node.id]!.time }}</text>
            <g v-for="(line, index) in chartHover[node.id]!.lines" :key="index">
              <rect x="8" :y="22 + index * 16" width="7" height="7" rx="1" :fill="line.color" />
              <text x="20" :y="29 + index * 16" font-size="11" fill="#e5effa">{{ line.name }}</text>
              <text :x="chartHover[node.id]!.box.width - 8" :y="29 + index * 16" font-size="11" fill="#e5effa" text-anchor="end">{{ line.value }}</text>
            </g>
          </g>
        </g>
      </g>
      <foreignObject v-if="chartTypes.includes(node.type) && !editing" :x="node.width - 100" :y="node.height - 20" width="96" height="18">
        <div xmlns="http://www.w3.org/1999/xhtml" class="widget-tools"><button type="button" data-test="chart-export-csv" @click.stop="exportChartCsv(node)">CSV</button><button type="button" data-test="chart-export-svg" @click.stop="exportChartSvg(node)">SVG</button></div>
      </foreignObject>
      <text v-if="textStyleTypes.includes(node.type)" :x="textX(node)" :y="textBlockStartY(node)" :text-anchor="textAnchor(node)"
        dominant-baseline="central" :fill="textFill(node)" :font-size="textFontSize(node)" :font-weight="node.bold ? 700 : undefined" :opacity="node.opacity ?? undefined">
        <tspan v-for="(line, index) in textLines(node)" :key="index" :x="textX(node)" :y="textBlockStartY(node) + index * textLineHeight(node)">{{ line }}</tspan>
      </text>
      <text v-if="node.tagId && !isGood(node)" :x="node.width - 10" y="14" fill="#fbbf24" font-size="16">?</text>
      <rect v-if="editing && !node.ownerId && selectedIds.includes(node.id)" x="-3" y="-3" :width="node.width + 6" :height="node.height + 6" fill="none" stroke="#63e6be" stroke-width="2" stroke-dasharray="5 3" pointer-events="none" />
      <g v-if="editing && node.type === 'polyline' && !node.source && !node.target && selectedIds.includes(node.id)" data-test="pipe-waypoints">
        <circle v-for="(point, index) in node.points" :key="index" :cx="point.x * node.width / 100" :cy="point.y * node.height / 100" :r="5 / zoom" fill="#e5effa" stroke="#159f80" :stroke-width="2 / zoom" style="cursor: crosshair" :data-waypoint="index" @pointerdown.stop.prevent="emit('waypoint', node, index, $event)" @mousedown.stop @touchstart.stop />
      </g>
    </g>
    <rect v-if="editing && selectionBox" :x="selectionBox.x" :y="selectionBox.y" :width="selectionBox.width" :height="selectionBox.height"
      fill="#409eff22" stroke="#409eff" stroke-width="1" vector-effect="non-scaling-stroke" pointer-events="none" data-test="selection-box" />
  </svg>
</template>

<script setup lang="ts">
import { computed, nextTick, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useNowTicker } from '../composables/useNowTicker'
import { Check, Position } from '@element-plus/icons-vue'
import type { AlarmEvent, HistoryPoint, TagValue } from '../types'
import type { OeeKpi } from '../api/oee'
import type { Recipe, RecipeTemplate } from '../api/recipes'
import { chartTypes, expandNodes, nodeFontSize, pipeGeometry, type LibraryItem, type NodeType, type ScreenDocument, type ScreenNode, type ScreenRect, type ScreenState } from '../screens/model'
import { seriesColor } from '../screens/chartColors'
import { createChartRenderer } from '../screens/chartRenderer'
import { builtinSymbols, isaColors } from '../screens/symbols'
import { tr } from '../i18n'
import { alarmMessage } from '../utils'
const props = withDefaults(defineProps<{ document: ScreenDocument; values?: Record<number, TagValue>; sampleMaxAge?: Record<number, number>; history?: Record<number, HistoryPoint[]>; alarmEvents?: AlarmEvent[]; alarmUnavailable?: boolean; alarmLimited?: boolean; historyUnavailable?: boolean; oeeUnavailable?: boolean; tagMeta?: Record<number, { name: string; unit: string }>; oee?: Record<number, OeeKpi>; editing?: boolean; zoom?: number; selectedIds?: string[]; selectionBox?: ScreenRect | null; guides?: { vertical: number[]; horizontal: number[] } | null; library?: LibraryItem[]; libraryFailed?: boolean; canCommand?: (tagId: number | null) => boolean; controlBusy?: boolean; recipeTemplates?: RecipeTemplate[]; recipes?: Recipe[]; canRecipe?: (templateId: number) => boolean }>(),
  { values: () => ({}), sampleMaxAge: () => ({}), history: () => ({}), alarmEvents: () => [], alarmUnavailable: false, alarmLimited: false, historyUnavailable: false, oeeUnavailable: false, tagMeta: () => ({}), oee: () => ({}), editing: false, zoom: 1, selectedIds: () => [], library: () => [], libraryFailed: false, recipeTemplates: () => [], recipes: () => [] })
const { locale } = useI18n()
const nodes = computed(() => { const expanded = expandNodes(props.document.nodes, props.library); return expanded.filter(node => !node.hidden).map(node => pipeGeometry(node, expanded)) })
const byId = computed(() => new Map(props.document.nodes.map(n => [n.id, n])))
// 编辑时点中模板内部图元等同于点中实例。
function owner(node: ScreenNode): ScreenNode { return props.editing && node.ownerId ? byId.value.get(node.ownerId) ?? node : node }
function label(node: ScreenNode): string { return node.texts?.[locale.value] || node.text }
// 文字样式：默认居中，left/right 时保留内边距；wrap 按字符折行，autoFit 在框内缩小字号。
const textStyleTypes: NodeType[] = ['text', 'value', 'button', 'rectangle', 'ellipse']
function textAnchor(node: ScreenNode): string { return node.align === 'left' ? 'start' : node.align === 'right' ? 'end' : 'middle' }
function textX(node: ScreenNode): number { const pad = 8; return node.align === 'left' ? pad : node.align === 'right' ? node.width - pad : node.width / 2 }
function baseTextSize(node: ScreenNode): number { return nodeFontSize(node, node.type === 'value' ? 24 : 16) }
function wrapText(text: string, size: number, width: number): string[] {
  const max = Math.max(1, Math.floor((width - 12) / Math.max(1, size * 0.6)))
  if (text.length <= max) return [text]
  const lines: string[] = []; let current = ''
  for (const char of text) {
    if (char === '\n' || current.length >= max) { if (current.length) lines.push(current); current = char === '\n' ? '' : char }
    else current += char
  }
  if (current.length) lines.push(current)
  return lines.length ? lines : [text]
}
function textFontSize(node: ScreenNode): number {
  const base = baseTextSize(node)
  if (!node.autoFit) return base
  const lines = wrapText(node.type === 'value' ? formatted(node) : label(node), base, node.width)
  const longest = Math.max(1, ...lines.map(line => line.length))
  return Math.max(8, Math.min(base, (node.width - 12) / Math.max(1, longest * 0.6), (node.height - 8) / Math.max(1, lines.length * 1.25)))
}
function textLineHeight(node: ScreenNode): number { return textFontSize(node) * 1.25 }
function textLines(node: ScreenNode): string[] {
  const text = node.type === 'value' ? formatted(node) : label(node)
  return node.wrap ? wrapText(text, textFontSize(node), node.width) : [text]
}
function textBlockStartY(node: ScreenNode): number { return node.height / 2 - (textLines(node).length - 1) * textLineHeight(node) / 2 }
function textFill(node: ScreenNode): string { return node.type === 'text' ? node.color : node.type === 'value' && node.states?.length ? mappedState(node)?.color ?? '#e5effa' : '#e5effa' }
function templateKnown(node: ScreenNode): boolean { return props.library.some(x => x.kind === 'template' && x.key === node.templateKey) }
const symbolCache = new Map<string, { raw: string; html: string }>()
// 去掉外层 <svg>，按符号自身 viewBox 缩放到图元框内。
function symbolSvg(node: ScreenNode): string {
  const key = `${node.symbol}|${node.width}|${node.height}`
  const raw = (node.symbol ? props.document.symbolAssets?.[node.symbol] : undefined) ?? builtinSymbols.find(x => x.key === node.symbol)?.svg ?? props.library.find(x => x.kind === 'symbol' && `user:${x.key}` === node.symbol)?.svg
  if (!raw) return `<rect width="${node.width}" height="${node.height}" fill="none" stroke="#f59f00" stroke-dasharray="4 3"/>`
  const cached = symbolCache.get(key); if (cached?.raw === raw) return cached.html
  const vb = /viewBox="([\d.\s-]+)"/.exec(raw)?.[1]?.split(/\s+/).map(Number) ?? [0, 0, 100, 100]
  const stretch = /preserveAspectRatio="none"/.test(raw)
  const inner = raw.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '')
  const html = `<svg x="0" y="0" width="${node.width}" height="${node.height}" viewBox="${vb.join(' ')}" preserveAspectRatio="${stretch ? 'none' : 'xMidYMid meet'}" overflow="visible">${inner}</svg>`
  symbolCache.set(key, { raw, html }); return html
}
// 报警：按等级汇总（原来既然接了活动报警明细，条/统计/列表共用同一份数据）。
function levelRank(level: string): number { return level === 'Critical' ? 3 : level === 'Warning' ? 2 : 1 }
const alarmCounts = computed(() => { const counts: Record<string, number> = {}; for (const a of props.alarmEvents) counts[a.level] = (counts[a.level] ?? 0) + 1; return counts })
const alarmTotal = computed(() => props.alarmEvents.length)
const alarmTop = computed(() => alarmTotal.value === 0 ? '' : (['Critical', 'Warning', 'Info'].find(l => (alarmCounts.value[l] ?? 0) > 0) ?? ''))
function alarmLevelColor(level: string): string { return level === 'Critical' ? '#e03131' : level === 'Warning' ? '#f59f00' : '#4c91ff' }
function formatTs(ts: number): string { return new Date(ts).toLocaleTimeString(locale.value, { hour12: false }) }
function truncate(text: string, width: number): string { const max = Math.max(4, Math.floor((width - 40) / 7)); return text.length > max ? text.slice(0, max - 1) + '…' : text }
function alarmRows(node: ScreenNode): AlarmEvent[] {
  const rows = Math.max(1, Math.floor((node.height - 28) / 20))
  return [...props.alarmEvents].sort((a, b) => levelRank(b.level) - levelRank(a.level) || b.startTs - a.startTs).slice(0, rows)
}
function alarmText(node: ScreenNode, a: AlarmEvent): string { return `${formatTs(a.startTs)} ${truncate(alarmMessage(a), node.width)}` }
// 表格：测点名/单位来自运行态传入的 tagMeta（目录缺省时回退 #id）。
function tagName(id: number): string { return props.tagMeta[id]?.name ?? `#${id}` }
function tagUnit(id: number): string { return props.tagMeta[id]?.unit ?? '' }
function tagGood(id: number): boolean { const sample = props.values[id]; return !!sample && sample.quality === 'Good' && Number.isFinite(sample.value) && now.value - sample.ts < (props.sampleMaxAge[id] ?? 15000) }
function tagValue(node: ScreenNode, id: number): string { return tagGood(id) ? (props.values[id]!.value).toFixed(node.decimals) : '--' }
type TableSortState = { key: string; dir: 1 | -1 }
const tableSorts = reactive<Record<string, TableSortState>>({})
const tableSelections = reactive<Record<string, number | null>>({})
function tableRowCapacity(node: ScreenNode): number { return Math.max(1, Math.floor((node.height - 46) / 20)) }
// 运行态表格排序：按当前列取显示值（测点名/实时值/历史统计），升降序切换；编辑态不响应。
function tableCell(node: ScreenNode, id: number, key: string): string | number {
  if (key === 'name') return tagName(id)
  if (key === 'unit') return tagUnit(id)
  if (key === 'value') return tagGood(id) ? props.values[id]!.value : Number.NEGATIVE_INFINITY
  const stats = histStats(id)
  const value = key === 'last' ? stats.last : key === 'min' ? stats.min : key === 'max' ? stats.max : stats.avg
  return value ?? Number.NEGATIVE_INFINITY
}
function tableRows(node: ScreenNode): number[] {
  const ids = node.tags ?? []
  const sort = props.editing ? undefined : tableSorts[node.id]
  const sorted = sort ? [...ids].sort((a, b) => {
    const left = tableCell(node, a, sort.key), right = tableCell(node, b, sort.key)
    const result = typeof left === 'string' || typeof right === 'string' ? String(left).localeCompare(String(right), locale.value) : left - right
    return result * sort.dir
  }) : ids
  return sorted.slice(0, tableRowCapacity(node))
}
function sortTable(node: ScreenNode, key: string): void {
  if (props.editing) return
  const current = tableSorts[node.id]
  tableSorts[node.id] = current?.key === key ? { key, dir: current.dir === 1 ? -1 : 1 } : { key, dir: 1 }
}
function sortArrow(node: ScreenNode, key: string): string {
  const sort = props.editing ? undefined : tableSorts[node.id]
  return sort?.key === key ? (sort.dir === 1 ? ' ▲' : ' ▼') : ''
}
// 表格列布局：未配置时保持原有全部列；配置后按剩余列均分右侧区域。
type TableColumnLayout = { key: string; label: string; x: number; anchor: 'start' | 'middle' | 'end' }
function tableColumnLayout(node: ScreenNode): TableColumnLayout[] {
  const defaults = node.type === 'history-table' ? ['name', 'last', 'min', 'max', 'avg'] : ['name', 'value', 'unit']
  const keys = node.tableColumns?.length ? node.tableColumns : defaults
  const labels: Record<string, string> = {
    name: tr('model.tag'), value: tr('model.value'), unit: tr('model.unit'),
    last: tr('model.latest'), min: tr('model.min'), max: tr('model.max'), avg: tr('model.avg')
  }
  const out: TableColumnLayout[] = []
  const rest = keys.filter(key => key !== 'name')
  if (keys.includes('name')) out.push({ key: 'name', label: labels.name, x: 8, anchor: 'start' })
  const startX = keys.includes('name') ? node.width * 0.42 : 8, endX = node.width - 8
  const slot = (endX - startX) / Math.max(1, rest.length)
  rest.forEach((key, index) => out.push({ key, label: labels[key] ?? key, x: startX + slot * (index + 0.5), anchor: 'middle' }))
  return out
}
function tableCellText(node: ScreenNode, tagId: number, key: string): string {
  if (key === 'name') return tagName(tagId)
  if (key === 'unit') return tagUnit(tagId)
  if (key === 'value') return tagValue(node, tagId)
  const stats = histStats(tagId)
  const value = key === 'last' ? stats.last : key === 'min' ? stats.min : key === 'max' ? stats.max : stats.avg
  return statFmt(node, value)
}
function nodeFileName(node: ScreenNode): string {
  return (label(node) || node.type).replace(/[\\/:*?"<>|]/g, '').slice(0, 40) || node.type
}
function downloadText(fileName: string, content: string, type: string): boolean {
  try {
    const uri = URL.createObjectURL(new Blob([content], { type }))
    const link = document.createElement('a')
    link.href = uri; link.download = fileName
    link.click()
    setTimeout(() => URL.revokeObjectURL(uri), 1000)
    return true
  } catch { return false }
}
function csvCell(value: string | number | null | undefined): string {
  const text = value == null ? '' : String(value)
  return /[",\r\n]/.test(text) ? `"${text.replace(/"/g, '""')}"` : text
}
function csvLine(values: (string | number | null | undefined)[]): string { return values.map(csvCell).join(',') }
function stamp(): string {
  const now = new Date()
  const pad = (value: number) => String(value).padStart(2, '0')
  return `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`
}
function exportTable(node: ScreenNode): void {
  const columns = tableColumnLayout(node)
  const rows = [csvLine(columns.map(column => column.label))]
  for (const id of node.tags ?? []) {
    rows.push(csvLine(columns.map(column => column.key === 'value' && !tagGood(id) ? '' : tableCellText(node, id, column.key))))
  }
  downloadText(`${nodeFileName(node)}-${stamp()}.csv`, `\uFEFF${rows.join('\r\n')}`, 'text/csv;charset=utf-8')
}
function exportChartCsv(node: ScreenNode): void {
  const rows: string[] = []
  if (node.type === 'alarm-stats') {
    rows.push(csvLine([tr('model.level'), tr('alarmAnalytics.count')]))
    for (const [level, count] of Object.entries(alarmCounts.value)) rows.push(csvLine([level, count]))
  } else if (node.type === 'chart-gauge') {
    rows.push(csvLine([tr('model.tag'), tr('model.value')]))
    rows.push(csvLine([tagName(node.tagId ?? 0), node.tagId == null ? '' : tagValue(node, node.tagId)]))
  } else if (node.type === 'chart-line' || node.type === 'chart-area') {
    rows.push(csvLine([tr('model.time'), tr('model.tag'), tr('model.value')]))
    for (const id of node.tags ?? []) for (const point of props.history[id] ?? []) rows.push(csvLine([new Date(point.ts).toISOString(), tagName(id), point.value]))
  } else {
    rows.push(csvLine([tr('model.tag'), tr('model.value'), tr('model.unit')]))
    for (const id of node.tags ?? []) rows.push(csvLine([tagName(id), tagValue(node, id), tagUnit(id)]))
  }
  downloadText(`${nodeFileName(node)}-${stamp()}.csv`, `\uFEFF${rows.join('\r\n')}`, 'text/csv;charset=utf-8')
}
function exportChartSvg(node: ScreenNode): void {
  const svg = chartSvg(node)
  if (!svg || !svg.trim().startsWith('<svg')) return
  downloadText(`${nodeFileName(node)}-${stamp()}.svg`, svg, 'image/svg+xml;charset=utf-8')
}
function selectTableRow(node: ScreenNode, tagId: number): void {
  if (props.editing) return
  tableSelections[node.id] = tableSelections[node.id] === tagId ? null : tagId
}
function drillTableRow(node: ScreenNode, tagId: number): void {
  if (props.editing) return
  emit('trendTag', node, tagId)
}
function histStats(id: number): { last: number | null; min: number | null; max: number | null; avg: number | null } {
  const points = props.historyUnavailable ? [] : props.history[id] ?? []
  if (!points.length) return { last: null, min: null, max: null, avg: null }
  let min = Infinity, max = -Infinity, sum = 0
  for (const p of points) { if (p.value < min) min = p.value; if (p.value > max) max = p.value; sum += p.value }
  return { last: points[points.length - 1].value, min, max, avg: sum / points.length }
}
function statFmt(node: ScreenNode, value: number | null): string { return value == null ? '--' : value.toFixed(node.decimals) }
// OEE 看板：A/P/Q/OEE 四个指标 + 目标对比（数据由运行态按产线拉取）。
function oeeMetrics(node: ScreenNode) {
  const kpi = props.oeeUnavailable ? undefined : props.oee[node.lineId ?? 0]
  const pct = (v?: number | null) => v == null ? '--' : `${(v * 100).toFixed(1)}%`
  const col = (v?: number | null) => v == null ? '#64748b' : (kpi?.target != null && v >= kpi.target) ? '#34d399' : v >= 0.6 ? '#f59f00' : '#e03131'
  const pos = (i: number) => ({ x: i % 2 === 0 ? node.width * 0.06 : node.width * 0.56, y: i < 2 ? 36 : node.height * 0.6 + 6 })
  return [
    { key: 'a', label: 'A', text: pct(kpi?.availability), color: col(kpi?.availability), ...pos(0) },
    { key: 'p', label: 'P', text: pct(kpi?.performance), color: col(kpi?.performance), ...pos(1) },
    { key: 'q', label: 'Q', text: pct(kpi?.quality), color: col(kpi?.quality), ...pos(2) },
    { key: 'o', label: 'OEE', text: pct(kpi?.oee), color: col(kpi?.oee), ...pos(3) }
  ]
}
// 图表渲染与缓存细节见 chartRenderer；组件只保留十字线交互和提示框状态。
const charts = createChartRenderer(() => ({
  values: props.values, history: props.history, editing: props.editing,
  historyUnavailable: props.historyUnavailable, alarmUnavailable: props.alarmUnavailable,
  alarmCounts: alarmCounts.value, tagName, tagUnit, tagGood, alarmLevelColor
}))
const { chartSvg, chartUnavailable, isTrendChart } = charts
interface ChartHoverLine { name: string; color: string; value: string }
interface ChartHover {
  px: number; top: number; bottom: number; time: string
  markers: { x: number; y: number; color: string }[]
  lines: ChartHoverLine[]
  box: { x: number; y: number; width: number; height: number }
}
const chartHover = reactive<Record<string, ChartHover | undefined>>({})
function trendLeave(node: ScreenNode): void { delete chartHover[node.id] }
function trendMove(node: ScreenNode, event: PointerEvent): void {
  if (props.editing || (event.pointerType && event.pointerType !== 'mouse')) return
  const plot = charts.plot(node.id)
  const matrix = (event.currentTarget as SVGGraphicsElement).getScreenCTM?.()
  if (!plot || !matrix) return
  const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse())
  const px = Math.max(plot.x, Math.min(plot.x + plot.width, point.x))
  const py = Math.max(plot.y, Math.min(plot.y + plot.height, point.y))
  const ts = plot.xMin + (px - plot.x) / plot.width * (plot.xMax - plot.xMin)
  const rows = (node.tags ?? []).map((id, index) => ({ id, index, point: charts.nearestPoint(id, ts) }))
  const value = (id: number, raw: number): string => `${raw.toFixed(node.decimals)}${tagUnit(id) ? ` ${tagUnit(id)}` : ''}`
  const lines: ChartHoverLine[] = rows.map(row => ({ name: tagName(row.id), color: seriesColor(row.index), value: row.point ? value(row.id, row.point.value) : '--' }))
  const markers = rows.flatMap(row => {
    if (!row.point) return []
    const y = plot.y + (1 - (row.point.value - plot.yMin) / (plot.yMax - plot.yMin)) * plot.height
    return [{ x: px, y: Math.max(plot.y, Math.min(plot.y + plot.height, y)), color: seriesColor(row.index) }]
  })
  const width = Math.max(140, Math.max(...lines.map(line => line.name.length)) * 7 + 78)
  const height = 28 + lines.length * 16
  const x = px + 14 + width > node.width ? Math.max(0, px - width - 14) : px + 14
  const y = py + 12 + height > node.height ? Math.max(0, py - height - 12) : py + 12
  chartHover[node.id] = { px, top: plot.y, bottom: plot.y + plot.height, time: formatTs(ts), markers, lines, box: { x, y, width, height } }
}
// 矢量动画（A 方案）：CSS 驱动，随测点值/状态更新；编辑态禁用，避免干扰布局。
function animProgress(node: ScreenNode): number {
  const anim = node.animation; if (!anim) return 0
  const id = anim.tagId ?? node.tagId
  if (anim.source === 'state') {
    if (id != null) return tagGood(id) && props.values[id]!.value >= node.activeThreshold ? 1 : 0
    return active(node) ? 1 : 0
  }
  if (id == null) return 0
  if (!tagGood(id)) return 0
  const value = props.values[id]!
  const span = anim.to - anim.from || 1
  return Math.max(0, Math.min(1, (value.value - anim.from) / span))
}
function animationStyle(node: ScreenNode): Record<string, string> | undefined {
  const anim = node.animation; if (!anim || props.editing) return undefined
  const progress = animProgress(node)
  if (anim.type === 'rotate' && anim.source !== 'state') {
    const deg = anim.outFrom + (anim.outTo - anim.outFrom) * progress
    return { transform: `rotate(${deg}deg)`, transformOrigin: 'center', transformBox: 'fill-box', transition: 'transform 0.5s linear' }
  }
  if (anim.type === 'level') {
    const level = Math.max(0, Math.min(1, anim.outFrom + (anim.outTo - anim.outFrom) * progress))
    return { clipPath: `inset(${((1 - level) * 100).toFixed(2)}% 0 0 0)`, transition: 'clip-path 0.5s linear' }
  }
  return undefined
}
function animationClass(node: ScreenNode): Record<string, boolean> | undefined {
  const anim = node.animation; if (!anim || props.editing) return undefined
  if (anim.type === 'flow') return { flowing: animProgress(node) >= 0.5 }
  if (anim.type === 'rotate' && anim.source === 'state') return { spinning: animProgress(node) >= 0.5 }
  return undefined
}
// 内建设备动画：泵叶轮旋转、阀门开度、储罐液位；编辑态保持静态外观。
function equipmentAnimationStyle(node: ScreenNode, kind: 'pump' | 'valve'): Record<string, string> | undefined {
  const anim = node.animation; if (!anim || props.editing || anim.type !== 'rotate') return undefined
  const progress = animProgress(node)
  if (kind === 'pump' && anim.source === 'state') return undefined
  const deg = anim.source === 'state'
    ? (progress >= 0.5 ? anim.outTo : anim.outFrom)
    : anim.outFrom + (anim.outTo - anim.outFrom) * progress
  return { transform: `rotate(${deg}deg)`, transformOrigin: kind === 'pump' ? '70px 55px' : '70px 58px' }
}
function equipmentAnimationClass(node: ScreenNode, kind: 'pump' | 'valve'): Record<string, boolean> | undefined {
  const anim = node.animation; if (!anim || props.editing || kind !== 'pump' || anim.type !== 'rotate' || anim.source !== 'state') return undefined
  return { spinning: animProgress(node) >= 0.5 }
}
function tankFillRatio(node: ScreenNode): number {
  const anim = node.animation
  if (!anim || props.editing || anim.type !== 'level') return ratio(node)
  const progress = animProgress(node)
  return Math.max(0, Math.min(1, anim.outFrom + (anim.outTo - anim.outFrom) * progress))
}
// 仪表弧线：-125° 至 +125°，与指针和刻度使用同一套角度。
function gaugeAngle(node: ScreenNode, value: number): number {
  const span = node.max - node.min || 1
  return (-125 + Math.max(0, Math.min(1, (value - node.min) / span)) * 250) * Math.PI / 180
}
function gaugePoint(node: ScreenNode, value: number, radius: number): { x: number; y: number } {
  const angle = gaugeAngle(node, value)
  return { x: 70 + radius * Math.sin(angle), y: 62 - radius * Math.cos(angle) }
}
function gaugeBandPath(node: ScreenNode, band: { from: number; to: number }): string {
  const start = gaugePoint(node, band.from, 50), end = gaugePoint(node, band.to, 50)
  const large = Math.abs(gaugeAngle(node, band.to) - gaugeAngle(node, band.from)) > Math.PI ? 1 : 0
  return `M${start.x.toFixed(2)} ${start.y.toFixed(2)} A50 50 0 ${large} 1 ${end.x.toFixed(2)} ${end.y.toFixed(2)}`
}
function gaugeSetpointPath(node: ScreenNode): string {
  if (node.setpoint == null) return ''
  const outer = gaugePoint(node, node.setpoint, 56), inner = gaugePoint(node, node.setpoint, 44)
  return `M${outer.x.toFixed(2)} ${outer.y.toFixed(2)} L${inner.x.toFixed(2)} ${inner.y.toFixed(2)}`
}
function pipeFlow(node: ScreenNode): boolean { return node.animation ? animationClass(node)?.flowing === true : active(node) }
// 写交互控件：开关按状态切换，滑块按指针位置取值，步进取固定步长，选择开关/旋钮按位置取值，
// 点动按下保持、松开释放；取值通过 command/momentary 事件交给运行态走控制闭环。
const drafts = reactive<Record<string, number>>({})
const heldNodes = reactive<Record<string, boolean>>({})
const feedbackTypes = ['switch', 'slider', 'spinner', 'jog', 'pulse', 'selector', 'knob']
const inputDrafts = reactive<Record<string, string>>({}), inputErrors = reactive<Record<string, boolean>>({})
function updateInput(node: ScreenNode, event: Event): void { inputDrafts[node.id] = (event.target as HTMLInputElement).value; inputErrors[node.id] = false }
function commandEnabled(tagId: number | null): boolean { return !props.editing && !props.controlBusy && !!props.canCommand?.(tagId) }
// 配方选择/下发控件：运行态按模板选择配方，权限由运行态预判、服务端在下发时再次校验。
const recipeDrafts = reactive<Record<string, number | undefined>>({})
function recipeTemplate(node: ScreenNode): RecipeTemplate | undefined { return props.recipeTemplates.find(template => template.id === node.recipeTemplateId) }
function recipeOptions(node: ScreenNode): Recipe[] { return props.recipes.filter(recipe => recipe.templateId === node.recipeTemplateId) }
function canRecipeUse(node: ScreenNode): boolean { return !props.editing && !props.controlBusy && !!node.recipeTemplateId && !!props.canRecipe?.(node.recipeTemplateId) }
function updateRecipeDraft(node: ScreenNode, event: Event): void {
  const value = Number((event.target as HTMLSelectElement).value)
  recipeDrafts[node.id] = Number.isInteger(value) && value > 0 ? value : undefined
}
function emitRecipe(node: ScreenNode): void {
  const id = recipeDrafts[node.id]
  if (!id || !canRecipeUse(node) || !recipeOptions(node).some(recipe => recipe.id === id)) return
  emit('recipe', node, id)
}
function submitInput(node: ScreenNode): void {
  if (!commandEnabled(node.action.tagId)) return
  const raw = inputDrafts[node.id] ?? (controlReferenceGood(node) ? String(ctrlValue(node)) : ''), value = Number(raw)
  const rounded = Number(value.toFixed(node.decimals))
  inputErrors[node.id] = !raw.trim() || !Number.isFinite(value) || rounded < node.min || rounded > node.max || value < node.min || value > node.max
  if (!inputErrors[node.id]) emit('command', node, rounded)
}
function controlReferenceGood(node: ScreenNode): boolean { return props.editing || node.action.tagId != null && tagGood(node.action.tagId) }
function ctrlValue(node: ScreenNode): number { return controlReferenceGood(node) ? props.values[node.action.tagId ?? 0]?.value ?? node.min : node.min }
function switchOn(node: ScreenNode): boolean { return ctrlValue(node) >= 0.5 }
function toggleSwitch(node: ScreenNode): void {
  if (!commandEnabled(node.action.tagId) || !controlReferenceGood(node)) return
  emit('command', node, switchOn(node) ? 0 : (node.action.value || 1))
}
function isActionable(node: ScreenNode): boolean {
  return node.action.type !== 'none' || node.type === 'reset' || node.type === 'silence' || isTrendChart(node) || (node.type === 'history-table' && !!node.tags?.length)
}
function activateNode(node: ScreenNode): void {
  if (isTrendChart(node)) { emit('trend', node); return }
  if (node.type === 'history-table') { const tagId = node.tags?.[0]; if (tagId != null) emit('trendTag', node, tagId); return }
  if (node.type === 'switch') toggleSwitch(node)
  else if (node.type === 'pulse') { if (commandEnabled(node.action.tagId) && controlReferenceGood(node)) emit('activate', node) }
  else if (['jog', 'selector', 'knob'].includes(node.type)) return
  else if (!feedbackTypes.includes(node.type) && !['input', 'equipment-panel', 'recipe'].includes(node.type)) emit('activate', node)
}
// 多位置选择开关/旋钮：按当前反馈值定位最近位置，点击或键盘切换到下一位置。
function activePosition(node: ScreenNode): ScreenState | undefined {
  const states = node.states ?? []
  if (!states.length) return undefined
  if (props.editing) return states[0]
  if (!controlReferenceGood(node)) return undefined
  const current = ctrlValue(node)
  return states.reduce((best, state) => Math.abs(state.value - current) < Math.abs(best.value - current) ? state : best)
}
function positionIndex(node: ScreenNode): number {
  const states = node.states ?? []
  const active = activePosition(node)
  const index = active ? states.indexOf(active) : -1
  return index >= 0 ? index : 0
}
function pickPosition(node: ScreenNode, position: ScreenState): void {
  if (props.editing || !commandEnabled(node.action.tagId) || !controlReferenceGood(node)) return
  emit('command', node, position.value)
}
function stepPosition(node: ScreenNode): void {
  const states = node.states ?? []
  if (!states.length || !commandEnabled(node.action.tagId)) return
  pickPosition(node, states[(positionIndex(node) + 1) % states.length]!)
}
function selectorFontSize(node: ScreenNode): number {
  const count = Math.max(1, node.states?.length ?? 0)
  return Math.max(8, Math.min(nodeFontSize(node, 12), (node.width - 4) / count / 3.2))
}
function knobCenter(node: ScreenNode): { x: number; y: number } { return { x: node.width / 2, y: node.height / 2 + 4 } }
function knobPoint(node: ScreenNode, index: number): { x: number; y: number } {
  const count = node.states?.length ?? 0
  const angle = (-135 + (count > 1 ? 270 * index / (count - 1) : 0)) * Math.PI / 180
  const center = knobCenter(node)
  const radius = Math.max(8, Math.min(node.width, node.height) / 2 - 26)
  return { x: center.x + radius * Math.sin(angle), y: center.y - radius * Math.cos(angle) }
}
function knobIndex(node: ScreenNode): number { return positionIndex(node) }
function pickKnob(node: ScreenNode, event: MouseEvent): void {
  const states = node.states ?? []
  if (props.editing || !states.length || !commandEnabled(node.action.tagId) || !controlReferenceGood(node)) return
  const matrix = (event.currentTarget as SVGGraphicsElement).getScreenCTM?.()
  if (!matrix) return
  const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(matrix.inverse())
  const angle = Math.atan2(point.x - node.width / 2, -(point.y - knobCenter(node).y)) * 180 / Math.PI
  const step = states.length > 1 ? 270 / (states.length - 1) : 0
  const index = states.length > 1 ? Math.round((Math.max(-135, Math.min(135, angle)) + 135) / step) : 0
  pickPosition(node, states[Math.max(0, Math.min(states.length - 1, index))]!)
}
// 点动：按下即保持，松开/取消/失去指针捕获立即释放；键盘 Enter/空格同样成对触发。
function jogActive(node: ScreenNode): boolean { return !!heldNodes[node.id] || (controlReferenceGood(node) && Math.abs(ctrlValue(node) - node.action.value) < 1e-9) }
function jogStart(node: ScreenNode, event: PointerEvent): void {
  if (props.editing || !commandEnabled(node.action.tagId) || !controlReferenceGood(node)) return
  if (heldNodes[node.id]) return
  heldNodes[node.id] = true
  ;(event.currentTarget as Element).setPointerCapture?.(event.pointerId)
  emit('momentary', node, 'press')
}
function jogEnd(node: ScreenNode): void {
  if (!heldNodes[node.id]) return
  heldNodes[node.id] = false
  emit('momentary', node, 'release')
}
function sliderRatio(node: ScreenNode): number { const span = node.max - node.min || 1; return Math.max(0, Math.min(1, (sliderText(node) - node.min) / span)) }
function sliderText(node: ScreenNode): number { return drafts[node.id] ?? ctrlValue(node) }
function sliderValue(node: ScreenNode, ev: PointerEvent): number {
  const el = ev.currentTarget as Element
  const rect = el.getBoundingClientRect()
  const rel = rect.width ? (ev.clientX - rect.left) / rect.width : 0
  return node.min + Math.max(0, Math.min(1, rel)) * (node.max - node.min)
}
function sliderStart(node: ScreenNode, ev: PointerEvent): void { if (!commandEnabled(node.action.tagId)) return; drafts[node.id] = sliderValue(node, ev); (ev.currentTarget as Element).setPointerCapture?.(ev.pointerId) }
function sliderMove(node: ScreenNode, ev: PointerEvent): void { if (node.id in drafts) drafts[node.id] = sliderValue(node, ev) }
function sliderCancel(node: ScreenNode): void { delete drafts[node.id] }
function sliderEnd(node: ScreenNode): void { const v = drafts[node.id]; if (v == null) return; delete drafts[node.id]; if (commandEnabled(node.action.tagId)) emit('command', node, Number(v.toFixed(node.decimals))) }
function stepValue(node: ScreenNode, dir: number): void { if (!commandEnabled(node.action.tagId) || !controlReferenceGood(node)) return; const next = Math.max(node.min, Math.min(node.max, ctrlValue(node) + dir)); emit('command', node, Number(next.toFixed(node.decimals))) }
function alarmed(node: ScreenNode): boolean {
  if (props.editing || !node.alarmTagId) return false
  return tagGood(node.alarmTagId) && props.values[node.alarmTagId]!.value >= 0.5
}
// ISA-101 状态：unknown（无效/过期）> alarm > running > stopped。
function mappedState(node: ScreenNode) { return isGood(node) ? node.states?.find(item => item.value === (props.editing ? node.states?.[0]?.value : props.values[node.tagId ?? 0]?.value)) : undefined }
function state(node: ScreenNode): string { return node.tagId && !isGood(node) ? 'unknown' : alarmed(node) ? 'alarm' : node.states?.length ? mappedState(node) ? `state-${mappedState(node)!.value}` : 'unknown' : active(node) ? 'running' : 'stopped' }
function stateLabel(node: ScreenNode): string { return state(node) === 'unknown' ? tr('hmi.unknown') : alarmed(node) ? tr('hmi.fault') : mappedState(node)?.label ?? tr(active(node) ? 'hmi.running' : 'hmi.stopped') }
function deviceLabel(node: ScreenNode): string { return node.states?.length ? `${label(node)} / ${stateLabel(node)}` : label(node) }
const emit = defineEmits<{ background: [event: PointerEvent]; drop: [event: DragEvent]; nodePointerDown: [node: ScreenNode, event: MouseEvent | TouchEvent]; resizePointerDown: [node: ScreenNode, event: PointerEvent]; activate: [node: ScreenNode]; command: [node: ScreenNode, value: number, operation?: string]; momentary: [node: ScreenNode, phase: 'press' | 'release']; waypoint: [node: ScreenNode, index: number, event: PointerEvent]; trend: [node: ScreenNode]; trendTag: [node: ScreenNode, tagId: number]; recipe: [node: ScreenNode, recipeId: number] }>()
const gridId = `grid-${crypto.randomUUID()}`
const { now } = useNowTicker()
// 运行态键盘可达性：可操作图元使用 roving tabindex，Tab 只停靠一次，
// 方向键在控件间移动，避免上百个控件需要按上百次 Tab 才能走完。
const canvasRoot = ref<SVGSVGElement | null>(null)
const focusedNodeId = ref<string | null>(null)
const actionableNodes = computed(() => props.editing ? [] : nodes.value.filter(isActionable))
const rovingTabindex = computed(() => {
  const list = actionableNodes.value
  const active = list.some(node => node.id === focusedNodeId.value) ? focusedNodeId.value : list[0]?.id
  return new Map(list.map(node => [node.id, node.id === active ? 0 : -1]))
})
function nodeKeydown(node: ScreenNode, event: KeyboardEvent): void {
  if (props.editing || event.isComposing) return
  if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    if (node.type === 'jog') {
      if (!event.repeat && commandEnabled(node.action.tagId) && controlReferenceGood(node) && !heldNodes[node.id]) {
        heldNodes[node.id] = true
        emit('momentary', node, 'press')
      }
      return
    }
    if (node.type === 'selector' || node.type === 'knob') { stepPosition(node); return }
    activateNode(node)
    return
  }
  const step = event.key === 'ArrowRight' || event.key === 'ArrowDown' ? 1 : event.key === 'ArrowLeft' || event.key === 'ArrowUp' ? -1 : 0
  if (!step) return
  const list = actionableNodes.value
  if (!list.length) return
  event.preventDefault()
  const index = Math.max(0, list.findIndex(item => item.id === node.id))
  const next = list[(index + step + list.length) % list.length]
  focusedNodeId.value = next.id
  void nextTick(() => canvasRoot.value?.querySelector<SVGElement>(`[data-node-id="${CSS.escape(next.id)}"]`)?.focus({ preventScroll: true }))
}
function nodeKeyup(node: ScreenNode, event: KeyboardEvent): void {
  if (props.editing || event.isComposing || node.type !== 'jog') return
  if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); jogEnd(node) }
}
function isGood(node: ScreenNode): boolean {
  if (props.editing || !node.tagId) return true
  return tagGood(node.tagId)
}
function active(node: ScreenNode): boolean { return !!node.tagId && isGood(node) && (props.values[node.tagId]?.value ?? 0) >= node.activeThreshold }
function color(node: ScreenNode): string {
  if (node.tagId && !isGood(node)) return node.type === 'symbol' ? isaColors.unknown : '#64748b'
  if (alarmed(node)) return isaColors.alarm
  if (node.states?.length) return mappedState(node)?.color ?? isaColors.unknown
  return active(node) ? node.activeColor : node.color
}
function ratio(node: ScreenNode): number { return !isGood(node) ? 0 : Math.max(0, Math.min(1, ((props.values[node.tagId ?? 0]?.value ?? node.min) - node.min) / (node.max - node.min))) }
function formatted(node: ScreenNode): string {
  if (props.editing) return tr('screenCanvas.preview', [node.min.toFixed(node.decimals), node.unit])
  const value = props.values[node.tagId ?? 0]
  if (!isGood(node) || !value) return '--'
  if (node.states?.length && (node.type === 'value' || node.type === 'kpi'))
    return `${mappedState(node)?.label ?? tr('hmi.unknown')} (${value.value})`
  return `${value.value.toFixed(node.decimals)} ${node.unit}`
}
const clockTime = computed(() => new Date(now.value).toLocaleTimeString(locale.value, { hour12: false }))
const clockDate = computed(() => new Date(now.value).toLocaleDateString(locale.value))
// 刻度盘（dial）：与 gauge 共用 140×112 视口，从 -125° 到 +125° 均匀分布 11 条刻度线。
const dialTicks = computed(() => Array.from({ length: 11 }, (_, i) => {
  const a = (-125 + (250 * i) / 10) * Math.PI / 180
  return { i, x1: 70 + 58 * Math.sin(a), y1: 62 - 58 * Math.cos(a), x2: 70 + 46 * Math.sin(a), y2: 62 - 46 * Math.cos(a) }
}))
</script>

<style scoped>
.screen-canvas { display: block; flex-shrink: 0; user-select: none; touch-action: none; font-family: inherit; }
.editing g { cursor: move; } .editing g.locked { cursor: default; } .actionable { cursor: pointer; } .actionable:focus { outline: none; filter: drop-shadow(0 0 5px #63e6be); }
.held { filter: drop-shadow(0 0 8px #34d399); }
.invalid { opacity: .7; } .alarm { animation: blink 1s steps(2, start) infinite; }
@keyframes blink { to { opacity: .45; } } .spinning { animation: spin 2s linear infinite; } .flowing { animation: flow 1s linear infinite; }
.widget-tools { display: flex; justify-content: flex-end; gap: 2px; height: 100%; align-items: center; opacity: .55; transition: opacity .15s; }
.widget-tools:hover { opacity: 1; }
.widget-tools button { padding: 1px 5px; border: 1px solid #46576a; border-radius: 3px; background: #0b1622ee; color: #cbd5e1; font-size: 10px; line-height: 14px; cursor: pointer; }
.widget-tools button:hover { border-color: #63e6be; color: #e5effa; }
@keyframes spin { to { transform: rotate(360deg); } } @keyframes flow { to { stroke-dashoffset: -25; } }
.lit { filter: drop-shadow(0 0 6px currentColor); } .marquee-text { animation: marquee 10s linear infinite; }
.equipment-control { box-sizing: border-box; height: 100%; padding: 6px 8px; border: 1px solid; border-radius: 6px; background: #162538; color: #e5effa; display: flex; flex-direction: column; gap: 5px; overflow: hidden; font-size: 13px; }
.equipment-control strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: inherit; line-height: 1.4; min-height: 1.4em; flex-shrink: 0; }
.equipment-control button { border: 1px solid #637990; border-radius: 4px; background: #253e50; color: #f0f7fa; min-height: 30px; cursor: pointer; display: flex; align-items: center; justify-content: center; gap: 6px; font: inherit; }
.equipment-control button:disabled, .equipment-control input:disabled { opacity: .5; cursor: not-allowed; }
.equipment-control button svg { width: 18px; height: 18px; flex: 0 0 18px; }
.equipment-control button:focus-visible, .equipment-control input:focus-visible { outline: 2px solid #63e6be; outline-offset: -2px; }
.numeric-form { display: flex; gap: 4px; min-width: 0; }
.numeric-form input { width: 0; flex: 1; min-width: 32px; border: 1px solid #637990; border-radius: 3px; background: #0c1825; color: #f0f7fa; font: inherit; padding: 4px; user-select: text; }
.numeric-form button { width: 32px; flex: 0 0 32px; }
.input-unit { align-self: center; max-width: 48px; overflow: hidden; text-overflow: ellipsis; }
.input-error { color: #ffb4a9; font-size: 11px; }
.equipment-feedback { display: flex; align-items: center; flex-wrap: wrap; gap: 7px; padding: 7px 0; border-bottom: 1px solid #46576a; overflow-wrap: anywhere; }
.equipment-feedback output { margin-left: auto; }
.state-indicator { width: 9px; height: 9px; border-radius: 50%; flex: 0 0 9px; }
.panel-commands { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(110px, 100%), 1fr)); gap: 6px; overflow: auto; min-height: 0; }
.panel-commands button { padding: 5px; min-width: 0; }
.panel-commands button span { overflow-wrap: anywhere; min-width: 0; }
.control-muted { color: #9fb3c8; }
.recipe-control { box-sizing: border-box; height: 100%; padding: 8px; border: 1px solid; border-radius: 6px; background: #162538; color: #e5effa; display: flex; flex-direction: column; gap: 6px; overflow: hidden; font-size: 13px; }
.recipe-control strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: inherit; line-height: 1.3; min-height: 1.3em; }
.recipe-pick { display: flex; gap: 6px; min-width: 0; }
.recipe-pick select, .recipe-pick button { min-height: 28px; border: 1px solid #637990; border-radius: 4px; background: #0c1825; color: #f0f7fa; font: inherit; }
.recipe-pick select { flex: 1; min-width: 0; padding: 2px 4px; }
.recipe-pick button { flex: 0 0 auto; padding: 0 10px; background: #253e50; cursor: pointer; }
.recipe-pick button:disabled { opacity: .5; cursor: not-allowed; }
.recipe-pick button:focus-visible, .recipe-pick select:focus-visible { outline: 2px solid #63e6be; outline-offset: -2px; }
.recipe-hint { color: #9fb3c8; font-size: 11px; overflow-wrap: anywhere; }
.trend-hit { cursor: crosshair; }
.table-sort { cursor: pointer; } .table-row { cursor: pointer; }
.table-row .row-bg { pointer-events: all; }
.table-row:hover .row-bg { fill: #1e3a5f; }
.table-row.selected .row-bg { fill: #63e6be2e; }
.editing .table-sort, .editing .table-row { cursor: move; }
@keyframes marquee { from { transform: translateX(100%); } to { transform: translateX(-110%); } }
[data-chart] { pointer-events: none; }
@media (prefers-reduced-motion: reduce) { .spinning, .flowing, .alarm, .marquee-text { animation: none; } }
</style>
<template>
  <svg class="screen-canvas" :class="{ editing }" :viewBox="`0 0 ${document.width} ${document.height}`"
    :style="{ background: document.background, width: `${document.width * zoom}px`, height: `${document.height * zoom}px` }"
    role="img" :aria-label="$t('screenCanvas.k8a1840')" @pointerdown.self="$emit('background', $event)" @dragover.prevent @drop.prevent="$emit('drop', $event)">
    <defs>
      <pattern :id="gridId" width="20" height="20" patternUnits="userSpaceOnUse"><circle cx="1" cy="1" r="0.8" fill="#60758e" opacity="0.4" /></pattern>
    </defs>
    <rect v-if="editing" :width="document.width" :height="document.height" :fill="`url(#${gridId})`" @pointerdown="$emit('background', $event)" />
    <g v-for="node in nodes" :key="node.id" :data-node-id="node.id" :data-node-type="node.type"
      :data-state="state(node)"
      :transform="`translate(${node.x} ${node.y}) rotate(${node.rotation} ${node.width / 2} ${node.height / 2})`"
      :class="{ actionable: !editing && node.action.type !== 'none', selected: selectedIds.includes(node.id), invalid: node.tagId && !isGood(node), alarm: alarmed(node), frame: node.type === 'template', inner: !!node.ownerId }" :style="node.type === 'template' && !editing ? { pointerEvents: 'none' } : undefined"
      :tabindex="!editing && node.action.type !== 'none' ? 0 : undefined" :role="node.action.type !== 'none' ? 'button' : undefined"
      :aria-label="label(node) || node.type" @pointerdown.stop="!editing && $emit('nodePointerDown', owner(node), $event)"
      @mousedown.stop="editing && $emit('nodePointerDown', owner(node), $event)" @touchstart.stop="editing && $emit('nodePointerDown', owner(node), $event)"
      @click.stop="!editing && $emit('activate', node)" @keydown.enter.prevent="!editing && $emit('activate', node)"
      @keydown.space.prevent="!editing && $emit('activate', node)">
      <title>{{ label(node) }}{{ node.tagId ? $t('screenCanvas.kaa9823', [isGood(node) ? formatted(node) : $t('screenCanvas.k88dd4c')]) : '' }}</title>
      <rect :width="node.width" :height="node.height" fill="transparent" />
      <svg v-if="node.type === 'symbol'" :width="node.width" :height="node.height" :style="{ color: color(node), ...animationStyle(node) }" :class="animationClass(node)" :data-symbol="node.symbol" :data-state="state(node)" overflow="visible">
        <g v-html="symbolSvg(node)" />
        <text v-if="label(node)" :x="node.width / 2" :y="node.height + 13" text-anchor="middle" font-size="12" fill="#dce8f5">{{ deviceLabel(node) }}</text>
      </svg>
      <rect v-else-if="node.type === 'template' && (editing || !templateKnown(node))" :width="node.width" :height="node.height" fill="none" :stroke="templateKnown(node) ? '#63e6be55' : '#f59f00'" stroke-dasharray="6 4" />
      <text v-if="node.type === 'template' && !templateKnown(node)" :x="node.width / 2" :y="node.height / 2" text-anchor="middle" fill="#f59f00" font-size="13">{{ libraryFailed ? $t('screenCanvas.libraryUnavailable') : $t('screenCanvas.k0c6383', [node.templateKey]) }}</text>
      <svg :width="node.width" :height="node.height" viewBox="0 0 140 112" overflow="visible" v-if="['pump', 'valve', 'tank', 'motor', 'fan', 'gauge', 'dial'].includes(node.type)">
        <g :stroke="color(node)" stroke-width="3" fill="none" stroke-linejoin="round">
          <g v-if="node.type === 'pump'">
            <path d="M0 55 H35 M99 55 H140 M42 87 H98 L108 99 H32 Z" />
            <circle cx="70" cy="55" r="32" :fill="active(node) ? color(node) + '25' : 'none'" />
            <path d="M54 37 L89 55 L54 73 Z" :fill="color(node)" stroke="none" />
          </g>
          <g v-else-if="node.type === 'valve'">
            <path d="M0 58 H26 M114 58 H140 M26 34 L114 82 V34 L26 82 Z M70 58 V13 M48 13 H92" :fill="active(node) ? color(node) + '35' : 'none'" />
          </g>
          <g v-else-if="node.type === 'tank'">
            <rect x="27" y="13" width="86" height="82" rx="12" />
            <rect x="32" :y="90 - ratio(node) * 72" width="76" :height="ratio(node) * 72" rx="3" :fill="color(node)" stroke="none" opacity="0.55" />
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
            <path d="M70 62 L70 20" :transform="`rotate(${-125 + ratio(node) * 250} 70 62)`" stroke="#f8fafc" />
            <circle cx="70" cy="62" r="5" :fill="color(node)" />
            <text x="70" y="94" text-anchor="middle" font-size="15" fill="#f8fafc" stroke="none">{{ formatted(node) }}</text>
          </g>
          <g v-else-if="node.type === 'dial'">
            <path d="M25 78 A50 50 0 1 1 115 78" stroke-width="9" />
            <line v-for="t in dialTicks" :key="t.i" :x1="t.x1" :y1="t.y1" :x2="t.x2" :y2="t.y2" stroke="#9fb3c8" stroke-width="2" />
            <path d="M70 62 L70 22" stroke-width="3" stroke="#f8fafc" :transform="`rotate(${-125 + ratio(node) * 250} 70 62)`" />
            <circle cx="70" cy="62" r="5" :fill="color(node)" />
            <text x="70" y="98" text-anchor="middle" font-size="15" fill="#f8fafc" stroke="none">{{ formatted(node) }}</text>
          </g>
        </g>
        <text v-if="!['gauge', 'dial'].includes(node.type)" x="70" y="110" text-anchor="middle" font-size="12" fill="#dce8f5" :textLength="Math.min(132, deviceLabel(node).length * 12)" lengthAdjust="spacingAndGlyphs">{{ deviceLabel(node) }}</text>
      </svg>
      <path v-else-if="node.type === 'pipe'" :d="`M0 ${node.height / 2} H${node.width}`" :stroke="color(node)" stroke-width="8" fill="none"
        :stroke-dasharray="pipeFlow(node) ? '18 7' : undefined" :class="{ flowing: pipeFlow(node) }" />
      <polyline v-else-if="node.type === 'polyline'" :points="node.points?.map(point => `${point.x * node.width / 100},${point.y * node.height / 100}`).join(' ')" :stroke="color(node)" stroke-width="8" stroke-linejoin="round" stroke-linecap="round" fill="none"
        :stroke-dasharray="pipeFlow(node) ? '18 7' : undefined" :class="{ flowing: pipeFlow(node) }" />
      <foreignObject v-else-if="node.type === 'input' || node.type === 'equipment-panel'" :width="node.width" :height="node.height">
        <div class="equipment-control" :style="{ borderColor: color(node), pointerEvents: editing ? 'none' : undefined }" @pointerdown.stop @mousedown.stop @touchstart.stop @click.stop @keydown.stop>
          <strong :title="label(node)">{{ label(node) }}</strong>
          <template v-if="node.type === 'equipment-panel'">
            <div class="equipment-feedback"><span class="state-indicator" :style="{ background: color(node) }" /><span data-state-label>{{ stateLabel(node) }}</span><output>{{ formatted(node) }}</output></div>
            <div class="panel-commands">
              <button v-for="command in node.commands" :key="command.key" type="button" :title="command.label" :disabled="!commandEnabled(command.tagId)" @click="emit('command', node, command.value, command.key)"><Position aria-hidden="true" /><span>{{ command.label }}</span></button>
              <span v-if="!node.commands?.length" class="control-muted">{{ $t('hmi.noCommands') }}</span>
            </div>
          </template>
          <form v-else class="numeric-form" @submit.prevent="submitInput(node)">
            <input type="number" required :aria-label="label(node) || $t('hmi.value')" :value="inputDrafts[node.id] ?? ctrlValue(node)" :min="node.min" :max="node.max" :step="10 ** -node.decimals" :disabled="!commandEnabled(node.action.tagId)" @input="updateInput(node, $event)" />
            <span v-if="node.unit" class="input-unit" :title="node.unit">{{ node.unit }}</span>
            <button type="submit" :title="$t('hmi.apply')" :aria-label="$t('hmi.apply')" :disabled="!commandEnabled(node.action.tagId)"><Check aria-hidden="true" /></button>
          </form>
          <span v-if="inputErrors[node.id]" role="alert" class="input-error">{{ $t('hmi.invalidInput') }}</span>
        </div>
      </foreignObject>
      <rect v-else-if="node.type === 'rectangle' || node.type === 'button'" x="2" y="2" :width="node.width - 4" :height="node.height - 4"
        :rx="node.type === 'button' ? 8 : 2" :fill="node.type === 'button' ? color(node) + '35' : color(node) + '15'" :stroke="color(node)" stroke-width="2" />
      <ellipse v-else-if="node.type === 'ellipse'" :cx="node.width / 2" :cy="node.height / 2" :rx="node.width / 2 - 2" :ry="node.height / 2 - 2"
        :fill="color(node) + '25'" :stroke="color(node)" stroke-width="2" />
      <g v-else-if="node.type === 'led'">
        <circle :cx="node.width / 2" :cy="node.height / 2" :r="Math.max(4, Math.min(node.width, node.height) / 2 - 3)" :fill="color(node)"
          :opacity="active(node) ? 1 : 0.3" stroke="#0f172a" stroke-width="2" :class="{ lit: active(node) }" />
      </g>
      <g v-else-if="node.type === 'progress'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" fill="#1e293b" :stroke="node.color" stroke-width="2" />
        <rect x="3" y="3" :width="Math.max(0, (node.width - 6) * ratio(node))" :height="node.height - 6" rx="4" :fill="color(node)" />
        <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="14" fill="#e5effa">{{ formatted(node) }}</text>
      </g>
      <g v-else-if="node.type === 'kpi'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" y="24" text-anchor="middle" font-size="14" fill="#9fb3c8">{{ label(node) }}</text>
        <text :x="node.width / 2" :y="node.height / 2 + 16" text-anchor="middle" font-size="28" :fill="color(node)">{{ formatted(node) }}</text>
      </g>
      <g v-else-if="node.type === 'clock'">
        <text :x="node.width / 2" :y="node.height / 2 - 4" text-anchor="middle" dominant-baseline="central" font-size="26" fill="#e5effa">{{ clockTime }}</text>
        <text :x="node.width / 2" :y="node.height / 2 + 22" text-anchor="middle" font-size="13" fill="#9fb3c8">{{ clockDate }}</text>
      </g>
      <g v-else-if="node.type === 'image'">
        <image v-if="node.text" x="0" y="0" :width="node.width" :height="node.height" :href="node.text" preserveAspectRatio="xMidYMid meet" />
        <template v-else>
          <rect :width="node.width" :height="node.height" rx="4" fill="none" :stroke="node.color" stroke-dasharray="6 4" />
          <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="13" fill="#9fb3c8">{{ $t('model.kbe8da6') }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'marquee'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="4" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <clipPath :id="`mc-${node.id}`"><rect x="3" y="3" :width="node.width - 6" :height="node.height - 6" /></clipPath>
        <g :clip-path="`url(#mc-${node.id})`">
          <text x="0" :y="node.height / 2" dominant-baseline="central" font-size="16" fill="#e5effa" class="marquee-text">{{ label(node) }}</text>
        </g>
      </g>
      <g v-else-if="node.type === 'alarm-banner'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" :fill="!alarmUnavailable && alarmTotal ? alarmLevelColor(alarmTop) + '22' : '#243449'"
          :stroke="alarmUnavailable ? '#f59f00' : alarmTotal ? alarmLevelColor(alarmTop) : '#334155'" stroke-width="2" :class="{ alarm: !alarmUnavailable && alarmTop === 'Critical' }" />
        <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="16" :fill="alarmTotal ? '#f8fafc' : '#9fb3c8'">
          {{ alarmUnavailable ? $t('screenMonitor.alarmsUnavailable') : alarmTotal ? alarmLimited ? $t('screenMonitor.partialAlarmCount', [alarmTotal]) : $t('screenMonitor.alarmCount', [alarmTotal]) : $t('screenMonitor.systemAlarms') + ' · ' + $t('model.ka3e187') }}
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
        <text x="8" y="18" font-size="12" fill="#9fb3c8">{{ $t('model.k8cff1c') }}</text>
        <text :x="node.width * 0.58" y="18" font-size="12" fill="#9fb3c8">{{ $t('model.k321b95') }}</text>
        <text :x="node.width - 8" y="18" font-size="12" fill="#9fb3c8" text-anchor="end">{{ $t('model.k8ec8c8') }}</text>
        <line x1="4" :x2="node.width - 4" y1="24" y2="24" stroke="#243449" />
        <template v-for="(t, i) in tableTags(node)" :key="t">
          <text x="8" :y="42 + i * 20" font-size="12" fill="#e5effa">{{ tagName(t) }}</text>
          <text :x="node.width * 0.58" :y="42 + i * 20" font-size="12" :fill="tagGood(t) ? '#e5effa' : '#fbbf24'">{{ tagValue(node, t) }}</text>
          <text :x="node.width - 8" :y="42 + i * 20" font-size="12" fill="#9fb3c8" text-anchor="end">{{ tagUnit(t) }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'history-table'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="4" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text x="8" y="18" font-size="12" fill="#9fb3c8">{{ $t('model.k8cff1c') }}</text>
        <text :x="node.width * 0.42" y="18" font-size="12" fill="#9fb3c8">{{ $t('model.k7e805a') }}</text>
        <text :x="node.width * 0.58" y="18" font-size="12" fill="#9fb3c8">{{ $t('model.k31793b') }}</text>
        <text :x="node.width * 0.74" y="18" font-size="12" fill="#9fb3c8">{{ $t('model.ked690e') }}</text>
        <text :x="node.width - 6" y="18" font-size="12" fill="#9fb3c8" text-anchor="end">{{ $t('model.kd1454e') }}</text>
        <line x1="4" :x2="node.width - 4" y1="24" y2="24" stroke="#243449" />
        <template v-for="(t, i) in tableTags(node)" :key="t">
          <text x="8" :y="42 + i * 20" font-size="12" fill="#e5effa">{{ tagName(t) }}</text>
          <text :x="node.width * 0.42" :y="42 + i * 20" font-size="12" fill="#e5effa">{{ statFmt(node, histStats(t).last) }}</text>
          <text :x="node.width * 0.58" :y="42 + i * 20" font-size="12" fill="#9fb3c8">{{ statFmt(node, histStats(t).min) }}</text>
          <text :x="node.width * 0.74" :y="42 + i * 20" font-size="12" fill="#9fb3c8">{{ statFmt(node, histStats(t).max) }}</text>
          <text :x="node.width - 6" :y="42 + i * 20" font-size="12" fill="#9fb3c8" text-anchor="end">{{ statFmt(node, histStats(t).avg) }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'oee-kpi'">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="8" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <template v-for="m in oeeMetrics(node)" :key="m.key">
          <text :x="m.x" :y="m.y" font-size="12" fill="#9fb3c8">{{ m.label }}</text>
          <text :x="m.x" :y="m.y + 24" font-size="22" :fill="m.color">{{ m.text }}</text>
        </template>
      </g>
      <g v-else-if="node.type === 'switch'" :class="{ actionable: !editing }" @click.stop="!editing && toggleSwitch(node)">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" :rx="(node.height - 2) / 2" :fill="switchOn(node) ? '#34d39933' : '#1e293b'" :stroke="switchOn(node) ? '#34d399' : '#475569'" stroke-width="2" />
        <circle :cx="switchOn(node) ? node.width - node.height / 2 : node.height / 2" :cy="node.height / 2" :r="node.height / 2 - 6" :fill="switchOn(node) ? '#34d399' : '#64748b'" />
        <text :x="switchOn(node) ? node.height * 0.5 : node.width - node.height * 0.5" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="11" fill="#e5effa">{{ switchOn(node) ? 'ON' : 'OFF' }}</text>
      </g>
      <g v-else-if="node.type === 'slider'" :class="{ actionable: !editing }" @pointerdown.stop="!editing && sliderStart(node, $event)" @pointermove="!editing && sliderMove(node, $event)" @pointerup="!editing && sliderEnd(node)" @pointercancel="!editing && sliderEnd(node)">
        <rect x="1" :y="node.height / 2 - 5" :width="node.width - 2" height="10" rx="5" fill="#1e293b" :stroke="node.color" stroke-width="1" />
        <rect x="1" :y="node.height / 2 - 5" :width="Math.max(2, (node.width - 2) * sliderRatio(node))" height="10" rx="5" :fill="active(node) ? node.activeColor : node.color" />
        <circle :cx="2 + (node.width - 6) * sliderRatio(node)" :cy="node.height / 2" r="9" fill="#e5effa" />
        <text :x="node.width / 2" :y="node.height / 2 - 14" text-anchor="middle" font-size="12" fill="#9fb3c8">{{ sliderText(node).toFixed(node.decimals) }} {{ node.unit }}</text>
      </g>
      <g v-else-if="node.type === 'spinner'" :class="{ actionable: !editing }">
        <rect x="1" y="1" :width="node.width - 2" :height="node.height - 2" rx="6" fill="#0f172a" :stroke="node.color" stroke-width="2" />
        <text :x="node.width / 2" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="16" fill="#e5effa">{{ ctrlValue(node).toFixed(node.decimals) }} {{ node.unit }}</text>
        <g @click.stop="!editing && stepValue(node, -1)"><rect x="4" y="6" width="22" :height="node.height - 12" rx="4" fill="#243449" /><text :x="15" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="16" fill="#e5effa">−</text></g>
        <g @click.stop="!editing && stepValue(node, 1)"><rect :x="node.width - 26" y="6" width="22" :height="node.height - 12" rx="4" fill="#243449" /><text :x="node.width - 15" :y="node.height / 2" text-anchor="middle" dominant-baseline="central" font-size="16" fill="#e5effa">＋</text></g>
      </g>
      <g v-else-if="chartTypes.includes(node.type)" data-chart :data-unavailable="chartUnavailable(node) ? 'true' : undefined" v-html="chartSvg(node)" />
      <text v-if="['text', 'value', 'button', 'rectangle', 'ellipse'].includes(node.type)" :x="node.width / 2" :y="node.height / 2"
        text-anchor="middle" dominant-baseline="central" :fill="node.type === 'text' ? node.color : '#e5effa'" :font-size="node.type === 'value' ? 24 : 16">
        {{ node.type === 'value' ? formatted(node) : label(node) }}
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
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Position } from '@element-plus/icons-vue'
import * as echarts from '../charts'
import type { AlarmEvent, HistoryPoint, TagValue } from '../types'
import type { OeeKpi } from '../api/oee'
import { chartTypes, expandNodes, pipeGeometry, type LibraryItem, type ScreenDocument, type ScreenNode, type ScreenRect } from '../screens/model'
import { builtinSymbols, isaColors } from '../screens/symbols'
import { tr } from '../i18n'
const props = withDefaults(defineProps<{ document: ScreenDocument; values?: Record<number, TagValue>; sampleMaxAge?: Record<number, number>; history?: Record<number, HistoryPoint[]>; alarmEvents?: AlarmEvent[]; alarmUnavailable?: boolean; alarmLimited?: boolean; historyUnavailable?: boolean; oeeUnavailable?: boolean; tagMeta?: Record<number, { name: string; unit: string }>; oee?: Record<number, OeeKpi>; editing?: boolean; zoom?: number; selectedIds?: string[]; selectionBox?: ScreenRect | null; library?: LibraryItem[]; libraryFailed?: boolean; canCommand?: (tagId: number | null) => boolean; controlBusy?: boolean }>(),
  { values: () => ({}), sampleMaxAge: () => ({}), history: () => ({}), alarmEvents: () => [], alarmUnavailable: false, alarmLimited: false, historyUnavailable: false, oeeUnavailable: false, tagMeta: () => ({}), oee: () => ({}), editing: false, zoom: 1, selectedIds: () => [], library: () => [], libraryFailed: false })
const { locale } = useI18n()
const nodes = computed(() => { const expanded = expandNodes(props.document.nodes, props.library); return expanded.map(node => pipeGeometry(node, expanded)) })
const byId = computed(() => new Map(props.document.nodes.map(n => [n.id, n])))
// 编辑时点中模板内部图元等同于点中实例。
function owner(node: ScreenNode): ScreenNode { return props.editing && node.ownerId ? byId.value.get(node.ownerId) ?? node : node }
function label(node: ScreenNode): string { return node.texts?.[locale.value] || node.text }
function templateKnown(node: ScreenNode): boolean { return props.library.some(x => x.kind === 'template' && x.key === node.templateKey) }
const symbolCache = new Map<string, string>()
// 去掉外层 <svg>，按符号自身 viewBox 缩放到图元框内。
function symbolSvg(node: ScreenNode): string {
  const key = `${node.symbol}|${node.width}|${node.height}`
  const raw = builtinSymbols.find(x => x.key === node.symbol)?.svg ?? props.library.find(x => x.kind === 'symbol' && `user:${x.key}` === node.symbol)?.svg
  if (!raw) return `<rect width="${node.width}" height="${node.height}" fill="none" stroke="#f59f00" stroke-dasharray="4 3"/>`
  const cached = symbolCache.get(key + raw.length); if (cached) return cached
  const vb = /viewBox="([\d.\s-]+)"/.exec(raw)?.[1]?.split(/\s+/).map(Number) ?? [0, 0, 100, 100]
  const stretch = /preserveAspectRatio="none"/.test(raw)
  const inner = raw.replace(/^[\s\S]*?<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '')
  const html = `<svg x="0" y="0" width="${node.width}" height="${node.height}" viewBox="${vb.join(' ')}" preserveAspectRatio="${stretch ? 'none' : 'xMidYMid meet'}" overflow="visible">${inner}</svg>`
  symbolCache.set(key + raw.length, html); return html
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
function alarmText(node: ScreenNode, a: AlarmEvent): string { return `${formatTs(a.startTs)} ${truncate(a.message ?? '', node.width)}` }
// 表格：测点名/单位来自运行态传入的 tagMeta（目录缺省时回退 #id）。
function tagName(id: number): string { return props.tagMeta[id]?.name ?? `#${id}` }
function tagUnit(id: number): string { return props.tagMeta[id]?.unit ?? '' }
function tagGood(id: number): boolean { const sample = props.values[id]; return !!sample && sample.quality === 'Good' && Number.isFinite(sample.value) && now.value - sample.ts < (props.sampleMaxAge[id] ?? 15000) }
function tagValue(node: ScreenNode, id: number): string { return tagGood(id) ? (props.values[id]!.value).toFixed(node.decimals) : '--' }
function tableTags(node: ScreenNode): number[] { const rows = Math.max(1, Math.floor((node.height - 46) / 20)); return (node.tags ?? []).slice(0, rows) }
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
// 图表：用 ECharts 服务端渲染（SSR）输出静态 SVG 字符串，避免在画面内维护 canvas 实例与生命周期。
const chartPalette = ['#5b8ff9', '#61ddaa', '#f6bd16', '#7262fd', '#78d3f8', '#9661bc', '#f6903d', '#657798', '#ff9d4d', '#f759ab', '#36cfc9', '#9254de']
const chartCache = new Map<string, string>()
function chartSigOf(id: number): string {
  const points = props.history[id]
  const sample = props.values[id], lastPoint = points?.[points.length - 1]
  return `${points?.length}:${lastPoint?.ts}:${lastPoint?.value}|${sample?.value}:${sample?.quality}:${sample?.ts}:${tagGood(id)}`
}
function chartSig(node: ScreenNode): string {
  if (node.type === 'alarm-stats') return Object.entries(alarmCounts.value).filter(([, v]) => v > 0).map(([k, v]) => `${k}:${v}`).join(',')
  if (node.type === 'chart-gauge') return node.tagId == null ? '' : chartSigOf(node.tagId)
  return (node.tags ?? []).map(id => chartSigOf(id)).join(',')
}
function chartUnavailable(node: ScreenNode): boolean {
  if (props.editing) return false
  if (node.type === 'alarm-stats') return props.alarmUnavailable
  if (node.type === 'chart-line' || node.type === 'chart-area') return props.historyUnavailable
  const ids = node.type === 'chart-gauge' ? node.tagId == null ? [] : [node.tagId] : node.tags ?? []
  return !ids.length || ids.some(id => !tagGood(id))
}
function chartSvg(node: ScreenNode): string {
  if (chartUnavailable(node)) return `<text x="${node.width / 2}" y="${node.height / 2}" text-anchor="middle" fill="#fbbf24" font-size="14">${tr('screenMonitor.unavailable')}</text>`
  const sig = `${node.type}|${Math.round(node.width)}x${Math.round(node.height)}|${chartSig(node)}|${props.editing ? 1 : 0}`
  const cached = chartCache.get(sig); if (cached) return cached
  const width = Math.max(40, Math.round(node.width)), height = Math.max(40, Math.round(node.height))
  const chart = echarts.init(null, null, { renderer: 'svg', ssr: true, width, height })
  chart.setOption(chartOption(node, node.tags ?? []))
  const svg = chart.renderToSVGString()
  chart.dispose()
  if (chartCache.size > 300) chartCache.clear()
  chartCache.set(sig, svg)
  return svg
}
function value(id: number): number { return Number(props.values[id]?.value ?? 0) }
function chartOption(node: ScreenNode, ids: number[]): echarts.EChartsOption {
  const names = ids.map(id => `#${id}`)
  const colors = ids.map((_, i) => chartPalette[i % chartPalette.length])
  if (node.type === 'alarm-stats') {
    const levels = ['Info', 'Warning', 'Critical']
    return { backgroundColor: 'transparent', animation: false,
      grid: { left: 38, right: 10, top: 14, bottom: 26 },
      xAxis: { type: 'category', data: levels, axisLabel: { color: '#9fb3c8', fontSize: 10 } },
      yAxis: { type: 'value', minInterval: 1, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
      series: [{ type: 'bar', data: levels.map(l => ({ value: alarmCounts.value[l] ?? 0, itemStyle: { color: alarmLevelColor(l) } })), barMaxWidth: 40 }] }
  }
  if (node.type === 'chart-line') {
    return { backgroundColor: 'transparent', animation: false,
      grid: { left: 42, right: 12, top: 14, bottom: 26 },
      xAxis: { type: 'time', axisLabel: { color: '#9fb3c8', fontSize: 10 }, axisLine: { lineStyle: { color: '#243449' } }, splitLine: { show: false } },
      yAxis: { type: 'value', scale: true, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
      series: ids.map((id, i) => ({ type: 'line', name: names[i], showSymbol: false, sampling: 'lttb', data: (props.history[id] ?? []).map(p => [p.ts, p.value]),
        lineStyle: { color: colors[i] }, itemStyle: { color: colors[i] } })) }
  }
  if (node.type === 'chart-pie') {
    return { backgroundColor: 'transparent', animation: false,
      series: [{ type: 'pie', radius: ['34%', '70%'], center: ['50%', '50%'], data: ids.map((id, i) => ({ name: names[i], value: value(id), itemStyle: { color: colors[i] } })), label: { color: '#e5effa', fontSize: 11 } }] }
  }
  if (node.type === 'chart-bar-horizontal') {
    return { backgroundColor: 'transparent', animation: false,
      grid: { left: 56, right: 14, top: 12, bottom: 24 },
      xAxis: { type: 'value', axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
      yAxis: { type: 'category', data: names, axisLabel: { color: '#9fb3c8', fontSize: 10 } },
      series: [{ type: 'bar', data: ids.map((id, i) => ({ value: value(id), itemStyle: { color: colors[i] } })), barMaxWidth: 18 }] }
  }
  if (node.type === 'chart-bar-stack') {
    return { backgroundColor: 'transparent', animation: false,
      legend: { show: true, top: 0, textStyle: { color: '#9fb3c8', fontSize: 9 } },
      grid: { left: 36, right: 10, top: 26, bottom: 22 },
      xAxis: { type: 'category', data: ['Total'], axisLabel: { color: '#9fb3c8', fontSize: 10 } },
      yAxis: { type: 'value', axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
      series: ids.map((id, i) => ({ type: 'bar', stack: 'total', name: names[i], data: [value(id)], itemStyle: { color: colors[i] }, barMaxWidth: 70 })) }
  }
  if (node.type === 'chart-area') {
    return { backgroundColor: 'transparent', animation: false,
      legend: { show: true, top: 0, textStyle: { color: '#9fb3c8', fontSize: 9 } },
      grid: { left: 42, right: 12, top: 26, bottom: 24 },
      xAxis: { type: 'time', axisLabel: { color: '#9fb3c8', fontSize: 10 }, axisLine: { lineStyle: { color: '#243449' } }, splitLine: { show: false } },
      yAxis: { type: 'value', scale: true, axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
      series: ids.map((id, i) => ({ type: 'line', name: names[i], showSymbol: false, sampling: 'lttb', stack: 'area',
        data: (props.history[id] ?? []).map(p => [p.ts, p.value]), lineStyle: { color: colors[i] }, itemStyle: { color: colors[i] }, areaStyle: { color: colors[i], opacity: 0.35 } })) }
  }
  if (node.type === 'chart-radar') {
    return { backgroundColor: 'transparent', animation: false,
      radar: { indicator: names.map(name => ({ name, max: 100 })), radius: '62%', center: ['50%', '54%'],
        axisName: { color: '#9fb3c8', fontSize: 9 }, splitLine: { lineStyle: { color: '#243449' } }, axisLine: { lineStyle: { color: '#243449' } } },
      series: [{ type: 'radar', data: [{ value: ids.map(id => Math.max(0, Math.min(100, value(id)))), itemStyle: { color: colors[0] }, areaStyle: { color: colors[0], opacity: 0.35 } }] }] }
  }
  if (node.type === 'chart-funnel') {
    const data = ids.map((id, i) => ({ name: names[i], value: value(id), itemStyle: { color: colors[i] } })).sort((a, b) => b.value - a.value)
    return { backgroundColor: 'transparent', animation: false,
      series: [{ type: 'funnel', left: '12%', right: '12%', top: 8, bottom: 8, minSize: '20%', maxSize: '90%',
        label: { color: '#e5effa', fontSize: 10, position: 'inside' }, data }] }
  }
  if (node.type === 'chart-gauge') {
    const current = node.tagId == null ? node.min : value(node.tagId)
    return { backgroundColor: 'transparent', animation: false,
      series: [{ type: 'gauge', min: node.min, max: node.max, startAngle: 210, endAngle: -30, radius: '82%', center: ['50%', '58%'],
        progress: { show: true, width: 12, itemStyle: { color: colors[0] } },
        axisLine: { lineStyle: { width: 12, color: [[1, '#243449']] } },
        axisTick: { show: false }, splitLine: { length: 8, lineStyle: { color: '#3d4650' } },
        axisLabel: { color: '#9fb3c8', fontSize: 9, distance: 12 },
        pointer: { width: 4, itemStyle: { color: '#e5effa' } }, anchor: { show: false },
        detail: { fontSize: 16, color: '#e5effa', offsetCenter: [0, '40%'], formatter: (v: number) => `${Number(v).toFixed(node.decimals)}${node.unit ? ` ${node.unit}` : ''}` },
        title: { offsetCenter: [0, '72%'], color: '#9fb3c8', fontSize: 10 },
        data: [{ value: current, name: node.text }] }] }
  }
  return { backgroundColor: 'transparent', animation: false,
    grid: { left: 38, right: 10, top: 14, bottom: 26 },
    xAxis: { type: 'category', data: names, axisLabel: { color: '#9fb3c8', fontSize: 10, interval: 0, hideOverlap: true } },
    yAxis: { type: 'value', axisLabel: { color: '#9fb3c8', fontSize: 10 }, splitLine: { lineStyle: { color: '#243449' } } },
    series: [{ type: 'bar', data: ids.map((id, i) => ({ value: value(id), itemStyle: { color: colors[i] } })), barMaxWidth: 34 }] }
}
// 矢量动画（A 方案）：CSS 驱动，随测点值/状态更新；编辑态禁用，避免干扰布局。
function animProgress(node: ScreenNode): number {
  const anim = node.animation; if (!anim) return 0
  const id = anim.tagId ?? node.tagId
  if (anim.source === 'state') return active(node) ? 1 : 0
  if (id == null) return 0
  const value = props.values[id]; if (!value || value.quality !== 'Good') return 0
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
function pipeFlow(node: ScreenNode): boolean { return node.animation ? animationClass(node)?.flowing === true : active(node) }
// 写交互控件：开关按状态切换，滑块按指针位置取值，步进取固定步长；取值通过 command 事件交给运行态走控制闭环。
const drafts = reactive<Record<string, number>>({})
const inputDrafts = reactive<Record<string, string>>({}), inputErrors = reactive<Record<string, boolean>>({})
function updateInput(node: ScreenNode, event: Event): void { inputDrafts[node.id] = (event.target as HTMLInputElement).value; inputErrors[node.id] = false }
function commandEnabled(tagId: number | null): boolean { return !props.editing && !props.controlBusy && !!props.canCommand?.(tagId) }
function submitInput(node: ScreenNode): void {
  if (!commandEnabled(node.action.tagId)) return
  const raw = inputDrafts[node.id] ?? String(ctrlValue(node)), value = Number(raw)
  const rounded = Number(value.toFixed(node.decimals))
  inputErrors[node.id] = !raw.trim() || !Number.isFinite(value) || rounded < node.min || rounded > node.max || value < node.min || value > node.max
  if (!inputErrors[node.id]) emit('command', node, rounded)
}
function ctrlValue(node: ScreenNode): number { const id = node.action.tagId; const v = id == null ? undefined : props.values[id]; return v && v.quality === 'Good' ? v.value : node.min }
function switchOn(node: ScreenNode): boolean { return ctrlValue(node) >= 0.5 }
function toggleSwitch(node: ScreenNode): void { emit('command', node, switchOn(node) ? 0 : (node.action.value || 1)) }
function sliderRatio(node: ScreenNode): number { const span = node.max - node.min || 1; return Math.max(0, Math.min(1, (sliderText(node) - node.min) / span)) }
function sliderText(node: ScreenNode): number { return drafts[node.id] ?? ctrlValue(node) }
function sliderValue(node: ScreenNode, ev: PointerEvent): number {
  const el = ev.currentTarget as Element
  const rect = el.getBoundingClientRect()
  const rel = rect.width ? (ev.clientX - rect.left) / rect.width : 0
  return node.min + Math.max(0, Math.min(1, rel)) * (node.max - node.min)
}
function sliderStart(node: ScreenNode, ev: PointerEvent): void { drafts[node.id] = sliderValue(node, ev); (ev.currentTarget as Element).setPointerCapture?.(ev.pointerId) }
function sliderMove(node: ScreenNode, ev: PointerEvent): void { if (node.id in drafts) drafts[node.id] = sliderValue(node, ev) }
function sliderEnd(node: ScreenNode): void { const v = drafts[node.id]; if (v == null) return; delete drafts[node.id]; emit('command', node, Number(v.toFixed(node.decimals))) }
function stepValue(node: ScreenNode, dir: number): void { const next = Math.max(node.min, Math.min(node.max, ctrlValue(node) + dir)); emit('command', node, Number(next.toFixed(node.decimals))) }
function alarmed(node: ScreenNode): boolean {
  if (props.editing || !node.alarmTagId) return false
  const v = props.values[node.alarmTagId]; return !!v && v.quality === 'Good' && v.value >= 0.5
}
// ISA-101 状态：unknown（无效/过期）> alarm > running > stopped。
function mappedState(node: ScreenNode) { return isGood(node) ? node.states?.find(item => item.value === (props.editing ? node.states?.[0]?.value : props.values[node.tagId ?? 0]?.value)) : undefined }
function state(node: ScreenNode): string { return node.tagId && !isGood(node) ? 'unknown' : alarmed(node) ? 'alarm' : node.states?.length ? mappedState(node) ? `state-${mappedState(node)!.value}` : 'unknown' : active(node) ? 'running' : 'stopped' }
function stateLabel(node: ScreenNode): string { return state(node) === 'unknown' ? tr('hmi.unknown') : alarmed(node) ? tr('hmi.fault') : mappedState(node)?.label ?? tr(active(node) ? 'hmi.running' : 'hmi.stopped') }
function deviceLabel(node: ScreenNode): string { return node.states?.length ? `${label(node)} / ${stateLabel(node)}` : label(node) }
const emit = defineEmits<{ background: [event: PointerEvent]; drop: [event: DragEvent]; nodePointerDown: [node: ScreenNode, event: MouseEvent | TouchEvent]; resizePointerDown: [node: ScreenNode, event: PointerEvent]; activate: [node: ScreenNode]; command: [node: ScreenNode, value: number, operation?: string]; waypoint: [node: ScreenNode, index: number, event: PointerEvent] }>()
const gridId = `grid-${crypto.randomUUID()}`
const now = ref(Date.now()); let timer: ReturnType<typeof setInterval> | undefined
onMounted(() => { timer = setInterval(() => { now.value = Date.now() }, 1000) })
onBeforeUnmount(() => clearInterval(timer))
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
  if (props.editing) return tr('screenCanvas.kc7d26e', [node.min.toFixed(node.decimals), node.unit])
  const value = props.values[node.tagId ?? 0]
  return isGood(node) && value ? `${value.value.toFixed(node.decimals)} ${node.unit}` : '--'
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
.editing g { cursor: move; } .actionable { cursor: pointer; } .actionable:focus { outline: none; filter: drop-shadow(0 0 5px #63e6be); }
.invalid { opacity: .7; } .alarm { animation: blink 1s steps(2, start) infinite; }
@keyframes blink { to { opacity: .45; } } .spinning { animation: spin 2s linear infinite; } .flowing { animation: flow 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } } @keyframes flow { to { stroke-dashoffset: -25; } }
.lit { filter: drop-shadow(0 0 6px currentColor); } .marquee-text { animation: marquee 10s linear infinite; }
.equipment-control { box-sizing: border-box; height: 100%; padding: 6px 8px; border: 1px solid; border-radius: 6px; background: #162538; color: #e5effa; display: flex; flex-direction: column; gap: 5px; overflow: hidden; font-size: 13px; }
.equipment-control strong { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; line-height: 20px; min-height: 20px; flex-shrink: 0; }
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
@keyframes marquee { from { transform: translateX(100%); } to { transform: translateX(-110%); } }
[data-chart] { pointer-events: none; }
@media (prefers-reduced-motion: reduce) { .spinning, .flowing, .alarm, .marquee-text { animation: none; } }
</style>
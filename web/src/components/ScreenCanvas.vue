<template>
  <svg class="screen-canvas" :class="{ editing }" :viewBox="`0 0 ${document.width} ${document.height}`"
    :style="{ background: document.background, width: `${document.width * zoom}px`, height: `${document.height * zoom}px` }"
    role="img" :aria-label="$t('screenCanvas.k8a1840')" @pointerdown.self="$emit('background', $event)" @dragover.prevent @drop.prevent="$emit('drop', $event)">
    <defs>
      <pattern :id="gridId" width="20" height="20" patternUnits="userSpaceOnUse"><circle cx="1" cy="1" r="0.8" fill="#60758e" opacity="0.4" /></pattern>
    </defs>
    <rect v-if="editing" :width="document.width" :height="document.height" :fill="`url(#${gridId})`" @pointerdown="$emit('background', $event)" />
    <g v-for="node in nodes" :key="node.id" :data-node-id="node.id" :data-node-type="node.type"
      :transform="`translate(${node.x} ${node.y}) rotate(${node.rotation} ${node.width / 2} ${node.height / 2})`"
      :class="{ actionable: !editing && node.action.type !== 'none', selected: selectedIds.includes(node.id), invalid: node.tagId && !isGood(node), alarm: alarmed(node), frame: node.type === 'template', inner: !!node.ownerId }" :style="node.type === 'template' && !editing ? { pointerEvents: 'none' } : undefined"
      :tabindex="!editing && node.action.type !== 'none' ? 0 : undefined" :role="node.action.type !== 'none' ? 'button' : undefined"
      :aria-label="label(node) || node.type" @pointerdown.stop="$emit('nodePointerDown', owner(node), $event)"
      @click.stop="!editing && $emit('activate', node)" @keydown.enter.prevent="!editing && $emit('activate', node)"
      @keydown.space.prevent="!editing && $emit('activate', node)">
      <title>{{ label(node) }}{{ node.tagId ? $t('screenCanvas.kaa9823', [isGood(node) ? formatted(node) : $t('screenCanvas.k88dd4c')]) : '' }}</title>
      <rect :width="node.width" :height="node.height" fill="transparent" />
      <svg v-if="node.type === 'symbol'" :width="node.width" :height="node.height" :style="{ color: color(node) }" :data-symbol="node.symbol" :data-state="state(node)" overflow="visible">
        <g v-html="symbolSvg(node)" />
        <text v-if="label(node)" :x="node.width / 2" :y="node.height + 13" text-anchor="middle" font-size="12" fill="#dce8f5">{{ label(node) }}</text>
      </svg>
      <rect v-else-if="node.type === 'template' && (editing || !templateKnown(node))" :width="node.width" :height="node.height" fill="none" :stroke="templateKnown(node) ? '#63e6be55' : '#f59f00'" stroke-dasharray="6 4" />
      <text v-if="node.type === 'template' && !templateKnown(node)" :x="node.width / 2" :y="node.height / 2" text-anchor="middle" fill="#f59f00" font-size="13">{{ $t('screenCanvas.k0c6383', [node.templateKey]) }}</text>
      <svg :width="node.width" :height="node.height" viewBox="0 0 140 112" overflow="visible" v-if="['pump', 'valve', 'tank', 'motor', 'fan', 'gauge'].includes(node.type)">
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
        </g>
        <text v-if="node.type !== 'gauge'" x="70" y="110" text-anchor="middle" font-size="12" fill="#dce8f5">{{ label(node) }}</text>
      </svg>
      <path v-else-if="node.type === 'pipe'" :d="`M0 ${node.height / 2} H${node.width}`" :stroke="color(node)" stroke-width="8" fill="none"
        :stroke-dasharray="active(node) ? '18 7' : undefined" :class="{ flowing: active(node) }" />
      <rect v-else-if="node.type === 'rectangle' || node.type === 'button'" x="2" y="2" :width="node.width - 4" :height="node.height - 4"
        :rx="node.type === 'button' ? 8 : 2" :fill="node.type === 'button' ? color(node) + '35' : color(node) + '15'" :stroke="color(node)" stroke-width="2" />
      <ellipse v-else-if="node.type === 'ellipse'" :cx="node.width / 2" :cy="node.height / 2" :rx="node.width / 2 - 2" :ry="node.height / 2 - 2"
        :fill="color(node) + '25'" :stroke="color(node)" stroke-width="2" />
      <text v-if="['text', 'value', 'button', 'rectangle', 'ellipse'].includes(node.type)" :x="node.width / 2" :y="node.height / 2"
        text-anchor="middle" dominant-baseline="central" :fill="node.type === 'text' ? node.color : '#e5effa'" :font-size="node.type === 'value' ? 24 : 16">
        {{ node.type === 'value' ? formatted(node) : label(node) }}
      </text>
      <text v-if="node.tagId && !isGood(node)" :x="node.width - 10" y="14" fill="#fbbf24" font-size="16">?</text>
      <rect v-if="editing && !node.ownerId && selectedIds.includes(node.id)" x="-3" y="-3" :width="node.width + 6" :height="node.height + 6" fill="none" stroke="#63e6be" stroke-width="2" stroke-dasharray="5 3" pointer-events="none" />
      <rect v-if="editing && !node.ownerId && selectedIds.includes(node.id)" data-resize-handle :x="node.width - 5" :y="node.height - 5" width="10" height="10" fill="#63e6be" style="cursor: nwse-resize"
        @pointerdown.stop="$emit('resizePointerDown', node, $event)" />
    </g>
  </svg>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import type { TagValue } from '../types'
import { expandNodes, type LibraryItem, type ScreenDocument, type ScreenNode } from '../screens/model'
import { builtinSymbols, isaColors } from '../screens/symbols'
import { tr } from '../i18n'
const props = withDefaults(defineProps<{ document: ScreenDocument; values?: Record<number, TagValue>; editing?: boolean; zoom?: number; selectedIds?: string[]; library?: LibraryItem[] }>(),
  { values: () => ({}), editing: false, zoom: 1, selectedIds: () => [], library: () => [] })
const { locale } = useI18n()
const nodes = computed(() => expandNodes(props.document.nodes, props.library))
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
function alarmed(node: ScreenNode): boolean {
  if (props.editing || !node.alarmTagId) return false
  const v = props.values[node.alarmTagId]; return !!v && v.quality === 'Good' && v.value >= 0.5
}
// ISA-101 状态：unknown（无效/过期）> alarm > running > stopped。
function state(node: ScreenNode): string { return node.tagId && !isGood(node) ? 'unknown' : alarmed(node) ? 'alarm' : active(node) ? 'running' : 'stopped' }
defineEmits<{ background: [event: PointerEvent]; drop: [event: DragEvent]; nodePointerDown: [node: ScreenNode, event: PointerEvent]; resizePointerDown: [node: ScreenNode, event: PointerEvent]; activate: [node: ScreenNode] }>()
const gridId = `grid-${crypto.randomUUID()}`
const now = ref(Date.now()); let timer: ReturnType<typeof setInterval> | undefined
onMounted(() => { timer = setInterval(() => { now.value = Date.now() }, 1000) })
onBeforeUnmount(() => clearInterval(timer))
function isGood(node: ScreenNode): boolean {
  if (props.editing || !node.tagId) return true
  const value = props.values[node.tagId]
  return !!value && value.quality === 'Good' && now.value - value.ts < 15000
}
function active(node: ScreenNode): boolean { return !!node.tagId && isGood(node) && (props.values[node.tagId]?.value ?? 0) >= node.activeThreshold }
function color(node: ScreenNode): string {
  if (node.tagId && !isGood(node)) return node.type === 'symbol' ? isaColors.unknown : '#64748b'
  if (alarmed(node)) return isaColors.alarm
  return active(node) ? node.activeColor : node.color
}
function ratio(node: ScreenNode): number { return !isGood(node) ? 0 : Math.max(0, Math.min(1, ((props.values[node.tagId ?? 0]?.value ?? node.min) - node.min) / (node.max - node.min))) }
function formatted(node: ScreenNode): string {
  if (props.editing) return tr('screenCanvas.kc7d26e', [node.min.toFixed(node.decimals), node.unit])
  const value = props.values[node.tagId ?? 0]
  return isGood(node) && value ? `${value.value.toFixed(node.decimals)} ${node.unit}` : '--'
}
</script>

<style scoped>
.screen-canvas { display: block; flex-shrink: 0; user-select: none; touch-action: none; font-family: inherit; }
.editing g { cursor: move; } .actionable { cursor: pointer; } .actionable:focus { outline: none; filter: drop-shadow(0 0 5px #63e6be); }
.invalid { opacity: .7; } .alarm { animation: blink 1s steps(2, start) infinite; }
@keyframes blink { to { opacity: .45; } } .spinning { animation: spin 2s linear infinite; } .flowing { animation: flow 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } } @keyframes flow { to { stroke-dashoffset: -25; } }
@media (prefers-reduced-motion: reduce) { .spinning, .flowing, .alarm { animation: none; } }
</style>

<script setup lang="ts">
import { computed } from 'vue'
import type { ScreenThumbnail, ScreenThumbnailNode } from '../api/screens'
import { tr } from '../i18n'

const props = defineProps<{ document: ScreenThumbnail }>()
const viewBox = computed(() => `0 0 ${props.document.width} ${props.document.height}`)
const labeledTypes = new Set(['text', 'value', 'button', 'kpi', 'marquee', 'led', 'progress', 'switch', 'slider', 'spinner', 'input', 'selector', 'knob', 'jog', 'pulse', 'reset', 'silence', 'recipe', 'gauge', 'dial', 'equipment-panel'])
function transform(node: ScreenThumbnailNode): string | undefined {
  return node.rotation ? `rotate(${node.rotation} ${node.x + node.width / 2} ${node.y + node.height / 2})` : undefined
}
function points(node: ScreenThumbnailNode): string {
  const source = node.points?.length ? node.points : [{ x: 0, y: 0 }, { x: 100, y: 100 }]
  return source.map(point => `${node.x + point.x * node.width / 100},${node.y + point.y * node.height / 100}`).join(' ')
}
function fill(node: ScreenThumbnailNode): string {
  if (node.type === 'image') return '#23364c'
  if (node.type.startsWith('chart-')) return '#1d3550'
  if (node.type.startsWith('alarm')) return '#5a3a2a'
  return node.color
}
function label(node: ScreenThumbnailNode): string | null {
  return labeledTypes.has(node.type) && node.text ? node.text.slice(0, 18) : null
}
function fontSize(node: ScreenThumbnailNode): number { return Math.max(8, Math.min(14, node.height * 0.18)) }
</script>

<template>
  <div class="screen-thumbnail" :style="{ aspectRatio: `${document.width} / ${document.height}` }">
    <svg :viewBox="viewBox" preserveAspectRatio="xMidYMid meet" role="img" :aria-label="tr('screensView.preview')">
      <rect :width="document.width" :height="document.height" :fill="document.background" />
      <g v-for="node in document.nodes" :key="node.id" :transform="transform(node)">
        <polyline v-if="node.type === 'polyline'" :points="points(node)" fill="none" :stroke="node.color" stroke-width="2" stroke-linejoin="round" />
        <ellipse v-else-if="node.type === 'ellipse'" :cx="node.x + node.width / 2" :cy="node.y + node.height / 2" :rx="node.width / 2" :ry="node.height / 2" :fill="fill(node)" fill-opacity="0.42" :stroke="node.color" />
        <template v-else>
          <rect :x="node.x" :y="node.y" :width="node.width" :height="node.height" rx="3" :fill="fill(node)" fill-opacity="0.42" :stroke="node.color"
            :stroke-dasharray="node.type === 'template' || node.type === 'symbol' ? '4 3' : undefined" />
          <text v-if="label(node)" :x="node.x + 4" :y="node.y + Math.max(10, Math.min(18, node.height - 4))" :font-size="fontSize(node)" fill="#e5effa">{{ label(node) }}</text>
          <text v-if="node.type === 'image'" :x="node.x + node.width / 2" :y="node.y + node.height / 2" text-anchor="middle" dominant-baseline="central" :font-size="fontSize(node)" fill="#9fb3c8">IMG</text>
        </template>
      </g>
    </svg>
  </div>
</template>

<style scoped>
.screen-thumbnail { width: 100%; overflow: hidden; background: #0a111b; border: 1px solid var(--scada-border); border-radius: 4px; }
.screen-thumbnail svg { display: block; width: 100%; height: 100%; }
</style>

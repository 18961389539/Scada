<template>
  <el-select class="tag-select" :model-value="selectValue" :multiple="multiple" :clearable="clearable" :disabled="disabled"
    filterable :remote="remoteEnabled" :remote-method="remoteSearch" :loading="searching" :reserve-keyword="false"
    :placeholder="placeholder || $t('tagPicker.placeholder')" :aria-label="ariaLabel || placeholder || $t('tagPicker.placeholder')"
    :no-data-text="$t('tagPicker.noResults')" popper-class="tag-select-popper" @update:model-value="update" @visible-change="onVisible">
    <el-option-group v-if="recentTags.length" :label="$t('tagPicker.recent')">
      <el-option v-for="tag in recentTags" :key="`recent-${tag.id}`" :value="tag.id" :label="optionLabel(tag)" :aria-label="optionLabel(tag)">
        <div class="tag-option">
          <span>{{ tag.name }}</span>
          <small>{{ optionMeta(tag) }}</small>
          <small v-if="liveText(tag)" class="tag-live" :class="`q-${liveQuality(tag)}`">{{ liveText(tag) }}</small>
        </div>
      </el-option>
    </el-option-group>
    <el-option-group v-for="group in groups" :key="group.key" :label="group.label">
      <el-option v-for="tag in group.tags" :key="tag.id" :value="tag.id" :label="optionLabel(tag)" :aria-label="optionLabel(tag)">
        <div class="tag-option">
          <span>{{ tag.name }}</span>
          <small>{{ optionMeta(tag) }}</small>
          <small v-if="liveText(tag)" class="tag-live" :class="`q-${liveQuality(tag)}`">{{ liveText(tag) }}</small>
        </div>
      </el-option>
    </el-option-group>
  </el-select>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import { tagApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { formatValue, qualityLabels } from '../utils'
import { tr } from '../i18n'
import type { Device, Tag, TagValue } from '../types'

const props = withDefaults(defineProps<{
  modelValue?: any
  tags: Tag[]
  devices?: Device[]
  values?: Record<number, TagValue>
  writable?: boolean
  multiple?: boolean
  clearable?: boolean
  disabled?: boolean
  remote?: boolean
  placeholder?: string
  ariaLabel?: string
  labelFormatter?: (tag: Tag) => string
}>(), {
  devices: () => [], values: () => ({}), writable: false, multiple: false, clearable: true,
  disabled: false, remote: false, placeholder: '', ariaLabel: ''
})
const emit = defineEmits<{
  'update:modelValue': [value: any]
  change: [value: any]
  'visible-change': [visible: boolean]
}>()

// 最近使用的测点按账号保存在本机，只用于缩短选择路径，不影响配置数据。
const auth = useAuthStore()
const recentKey = `scada:recent-tags:${auth.user?.id ?? 'anon'}`
function readRecent(): number[] {
  try {
    const raw = JSON.parse(localStorage.getItem(recentKey) ?? '[]')
    return Array.isArray(raw) ? raw.filter((id): id is number => typeof id === 'number') : []
  } catch { return [] }
}
const recentIds = ref<number[]>(readRecent())
function remember(value: unknown): void {
  const ids = (Array.isArray(value) ? value : [value]).filter((id): id is number => typeof id === 'number')
  if (!ids.length) return
  recentIds.value = [...new Set([...ids, ...recentIds.value])].slice(0, 8)
  try { localStorage.setItem(recentKey, JSON.stringify(recentIds.value)) } catch { /* 本地存储不可用时忽略 */ }
}

const filteredTags = computed(() => props.writable ? props.tags.filter(tag => tag.writable) : props.tags)
const selectValue = computed(() => props.multiple ? (Array.isArray(props.modelValue) ? props.modelValue : []) : props.modelValue ?? null)
const selectedIds = computed<number[]>(() => props.multiple
  ? (Array.isArray(props.modelValue) ? props.modelValue : [])
  : props.modelValue == null ? [] : [props.modelValue as number])
const selectedTags = computed(() => {
  const ids = new Set(selectedIds.value)
  return filteredTags.value.filter(tag => ids.has(tag.id))
})
const recentTags = computed(() => {
  const map = new Map(filteredTags.value.map(tag => [tag.id, tag]))
  return recentIds.value.map(id => map.get(id)).filter((tag): tag is Tag => !!tag)
})

// 大目录只渲染首批选项，输入关键字后走服务端搜索，避免一次生成上万条选项。
const searchLimit = 200
const searched = ref<Tag[] | null>(null), searching = ref(false)
let searchSeq = 0, searchTimer: number | undefined
const remoteEnabled = computed(() => props.remote)
const optionTags = computed(() => {
  const base = remoteEnabled.value ? searched.value ?? filteredTags.value.slice(0, searchLimit) : filteredTags.value
  const merged = new Map(base.map(tag => [tag.id, tag]))
  for (const tag of selectedTags.value) merged.set(tag.id, tag)
  return [...merged.values()]
})
const regularTags = computed(() => optionTags.value.filter(tag => !recentIds.value.includes(tag.id)))
const groups = computed(() => {
  const map = new Map<string, { key: string; label: string; tags: Tag[] }>()
  for (const tag of regularTags.value) {
    const key = tag.deviceId == null ? (tag.kind === 'Calculated' ? 'calc' : 'none') : `d${tag.deviceId}`
    const group = map.get(key)
    if (group) group.tags.push(tag)
    else map.set(key, { key, label: deviceLabel(tag), tags: [tag] })
  }
  return [...map.values()]
})

function deviceLabel(tag: Tag): string {
  if (tag.deviceId == null) return tag.kind === 'Calculated' ? tr('tagPicker.calculated') : tr('tagPicker.noDevice')
  return props.devices.find(device => device.id === tag.deviceId)?.name ?? `#${tag.deviceId}`
}
function optionLabel(tag: Tag): string {
  return props.labelFormatter?.(tag) ?? `${deviceLabel(tag)} / ${tag.name} (#${tag.id})`
}
function optionMeta(tag: Tag): string {
  const address = tag.kind === 'Calculated' ? tag.expression : String(tag.address)
  return `${deviceLabel(tag)} · #${tag.id} · ${address}`
}
function liveText(tag: Tag): string {
  const value = props.values[tag.id]
  if (!value) return ''
  return `${formatValue(value.value)}${tag.unit ? ` ${tag.unit}` : ''} · ${qualityLabels[value.quality]}`
}
function liveQuality(tag: Tag): string { return props.values[tag.id]?.quality ?? '' }

function update(value: any): void {
  emit('update:modelValue', value)
  remember(value)
  emit('change', value)
}
function remoteSearch(query: string): void {
  window.clearTimeout(searchTimer)
  const seq = ++searchSeq
  if (!query.trim()) { searched.value = null; searching.value = false; return }
  searching.value = true
  searchTimer = window.setTimeout(async () => {
    try {
      const result = await tagApi.list({ search: query.trim() })
      if (seq !== searchSeq) return
      searched.value = props.writable ? result.filter(tag => tag.writable) : result
    } catch {
      if (seq === searchSeq) searched.value = []
    } finally {
      if (seq === searchSeq) searching.value = false
    }
  }, 250)
}
function onVisible(visible: boolean): void {
  if (!visible) {
    window.clearTimeout(searchTimer)
    searchSeq++
    searched.value = null
    searching.value = false
  }
  emit('visible-change', visible)
}
onBeforeUnmount(() => window.clearTimeout(searchTimer))
</script>

<style scoped>
.tag-select { width: 100%; }
</style>

<style>
.tag-select-popper .el-select-dropdown__item { height: auto; line-height: 1.35; padding: 6px 12px; }
.tag-select-popper .tag-option { display: flex; flex-direction: column; gap: 1px; max-width: 380px; }
.tag-select-popper .tag-option span { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 13px; }
.tag-select-popper .tag-option small { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-size: 11px; color: var(--scada-text-muted); }
.tag-select-popper .tag-live.q-Good { color: var(--el-color-success); }
.tag-select-popper .tag-live.q-Uncertain { color: var(--el-color-warning); }
.tag-select-popper .tag-live.q-Bad, .tag-select-popper .tag-live.q-CommFailure { color: var(--el-color-danger); }
</style>

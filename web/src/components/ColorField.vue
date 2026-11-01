<template>
  <div class="color-field" :class="{ compact }">
    <el-color-picker :model-value="modelValue" color-format="hex" :disabled="disabled" :predefine="recentColors"
      :aria-label="ariaLabel" :title="modelValue" @update:model-value="update" @change="emit('change')" />
    <el-input v-if="!compact" v-model="text" maxlength="7" :disabled="disabled" :aria-label="ariaLabel" data-test="color-hex" @change="applyText" />
  </div>
</template>

<script setup lang="ts">
import { ref, watch } from 'vue'
import { useAuthStore } from '../stores/auth'

const props = withDefaults(defineProps<{
  modelValue?: string
  compact?: boolean
  disabled?: boolean
  ariaLabel?: string
}>(), { modelValue: '#7394b8', compact: false, disabled: false, ariaLabel: '' })
const emit = defineEmits<{ 'update:modelValue': [value: string]; change: [] }>()

// 最近颜色按账号保存在本机；服务端只接受 #RRGGBB，透明度不由组态模型支持。
const auth = useAuthStore()
const recentKey = `scada:recent-colors:${auth.user?.id ?? 'anon'}`
function readRecent(): string[] {
  try {
    const raw = JSON.parse(localStorage.getItem(recentKey) ?? '[]')
    return Array.isArray(raw) ? raw.filter((value): value is string => typeof value === 'string' && /^#[0-9a-fA-F]{6}$/.test(value)).slice(0, 10) : []
  } catch { return [] }
}
const recentColors = ref<string[]>(readRecent())
const text = ref(props.modelValue)
watch(() => props.modelValue, value => { text.value = value })

function normalize(value: unknown): string {
  if (typeof value !== 'string') return ''
  const color = value.trim()
  if (/^#[0-9a-fA-F]{6}$/.test(color)) return color.toLowerCase()
  if (/^#[0-9a-fA-F]{3}$/.test(color)) return `#${color.slice(1).split('').map(char => char + char).join('')}`.toLowerCase()
  return ''
}
function remember(color: string): void {
  recentColors.value = [color, ...recentColors.value.filter(item => item !== color)].slice(0, 10)
  try { localStorage.setItem(recentKey, JSON.stringify(recentColors.value)) } catch { /* 本地存储不可用时忽略 */ }
}
function update(value: unknown): void {
  const color = normalize(value)
  if (!color) return
  text.value = color
  emit('update:modelValue', color)
  remember(color)
}
function applyText(value: string): void {
  const color = normalize(value)
  if (!color) { text.value = props.modelValue; return }
  update(color)
  emit('change')
}
</script>

<style scoped>
.color-field { display: flex; align-items: center; gap: 6px; min-width: 0; width: 100%; }
.color-field.compact { gap: 0; }
.color-field :deep(.el-input) { flex: 1; min-width: 0; }
.color-field :deep(.el-color-picker__trigger) { width: 30px; height: 30px; padding: 2px; }
</style>

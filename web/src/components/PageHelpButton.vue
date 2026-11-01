<template>
  <span v-if="key" class="page-help">
    <el-button
      class="page-help-btn"
      :class="{ 'is-compact': compact }"
      :circle="compact"
      :aria-label="$t('pageHelp.button')"
      :title="compact ? $t('pageHelp.button') : undefined"
      data-test="page-help"
      @click="open = true"
    >
      <el-icon><QuestionFilled /></el-icon>
      <span v-if="!compact" class="page-help-label">{{ $t('pageHelp.button') }}</span>
    </el-button>
    <el-drawer
      v-model="open"
      :size="isMobile ? '92%' : '560px'"
      class="page-help-drawer"
      append-to-body
      data-test="page-help-drawer"
    >
      <template #header>
        <div class="page-help-head">
          <span class="page-help-kicker">{{ $t('pageHelp.title') }}</span>
          <h2>{{ messages?.title }}</h2>
          <p>{{ messages?.summary }}</p>
        </div>
      </template>
      <el-skeleton v-if="!messages" :rows="6" animated />
      <div v-else class="page-help-body">
        <div
          v-for="(line, index) in bodyLines"
          :key="index"
          class="page-help-line"
          :class="{ 'is-heading': line.startsWith('■'), 'is-item': /^\d+\./.test(line), 'is-gap': !line.trim() }"
        >{{ line.startsWith('■') ? line.slice(1).trim() : line }}</div>
      </div>
      <template #footer>
        <el-button type="primary" data-test="page-help-close" @click="open = false">{{ $t('pageHelp.gotIt') }}</el-button>
      </template>
    </el-drawer>
  </span>
</template>

<script setup lang="ts">
import { computed, ref, shallowRef, watch } from 'vue'
import { useRoute } from 'vue-router'
import { QuestionFilled } from '@element-plus/icons-vue'
import { isMobile } from '../mobile'
import { currentLocale } from '../i18n'
import { helpKeyForPath } from '../help/pages'

interface HelpText { title: string; summary: string; body: string }

const props = withDefaults(defineProps<{ helpKey?: string; compact?: boolean }>(), { helpKey: '', compact: false })
const route = useRoute()
const open = ref(false)
const key = computed(() => props.helpKey || helpKeyForPath(route.path))
const content = shallowRef<Record<string, HelpText> | null>(null)
const loading = ref(false)

async function ensureContent(): Promise<void> {
  if (content.value || loading.value) return
  loading.value = true
  try {
    const module = currentLocale() === 'en-US' ? await import('../help/en.json') : await import('../help/zh.json')
    content.value = (module.default ?? module) as unknown as Record<string, HelpText>
  } finally {
    loading.value = false
  }
}

const messages = computed(() => {
  const name = key.value
  return name && content.value ? content.value[name] ?? null : null
})
const bodyLines = computed(() => messages.value?.body.split('\n') ?? [])

watch(open, (value) => { if (value) void ensureContent() })
watch(() => route.path, () => { open.value = false })
</script>

<style scoped>
.page-help { display: inline-flex; align-items: center; }
.page-help-btn { display: inline-flex; align-items: center; gap: 6px; }
.page-help-label { font-size: 13px; }
.page-help-head { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.page-help-kicker { font-size: 12px; letter-spacing: 0.08em; color: var(--scada-text-muted); }
.page-help-head h2 { margin: 0; font-size: 20px; line-height: 1.3; color: var(--scada-text); }
.page-help-head p { margin: 0; font-size: 13px; line-height: 1.6; color: var(--scada-text-secondary); }
.page-help-body { display: flex; flex-direction: column; gap: 4px; padding-right: 4px; color: var(--scada-text); font-size: 14px; line-height: 1.75; }
.page-help-line { white-space: pre-wrap; overflow-wrap: anywhere; }
.page-help-line.is-heading { margin-top: 18px; padding-left: 10px; border-left: 3px solid var(--scada-accent); font-size: 15px; font-weight: 600; color: var(--scada-text); }
.page-help-line.is-heading:first-child { margin-top: 0; }
.page-help-line.is-item { padding-left: 6px; }
.page-help-line.is-gap { height: 4px; }

@media (max-width: 1280px) {
  .page-help-label { display: none; }
  .page-help-btn:not(.is-compact) { padding: 8px; }
}
</style>

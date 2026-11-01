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
          <div class="page-help-head-top">
            <span class="page-help-kicker">{{ $t('pageHelp.title') }}</span>
            <el-button
              text
              size="small"
              class="page-help-search-btn"
              :aria-label="$t(searchOpen ? 'pageHelp.searchClose' : 'pageHelp.search')"
              :aria-expanded="searchOpen"
              data-test="page-help-search"
              @click="toggleSearch"
            >
              <el-icon><Close v-if="searchOpen" /><Search v-else /></el-icon>
              <span v-if="!isMobile">{{ $t(searchOpen ? 'pageHelp.searchClose' : 'pageHelp.search') }}</span>
            </el-button>
          </div>
          <h2>{{ messages?.title }}</h2>
          <p>{{ messages?.summary }}</p>
          <div v-if="searchOpen" class="page-help-search">
            <el-input
              ref="searchInput"
              v-model="query"
              clearable
              :prefix-icon="Search"
              :placeholder="$t('pageHelp.searchPlaceholder')"
              data-test="page-help-search-input"
            />
            <span v-if="activeQuery" class="page-help-search-count" role="status" data-test="page-help-search-count">{{ $t('pageHelp.searchMatches', [matchCount]) }}</span>
          </div>
        </div>
      </template>
      <el-skeleton v-if="!messages" :rows="6" animated />
      <div v-else-if="activeQuery && !matchCount" class="page-help-empty" data-test="page-help-search-empty">
        <p>{{ $t('pageHelp.searchNoMatch') }}</p>
        <el-button link type="primary" @click="query = ''">{{ $t('pageHelp.searchClear') }}</el-button>
      </div>
      <div v-else class="page-help-body">
        <div
          v-for="(line, index) in visibleLines"
          :key="index"
          class="page-help-line"
          :class="{ 'is-heading': line.heading, 'is-item': line.item, 'is-gap': line.gap }"
        >
          <template v-for="(part, partIndex) in line.parts" :key="partIndex">
            <mark v-if="part.hit">{{ part.text }}</mark>
            <template v-else>{{ part.text }}</template>
          </template>
        </div>
      </div>
      <template #footer>
        <el-button type="primary" data-test="page-help-close" @click="open = false">{{ $t('pageHelp.gotIt') }}</el-button>
      </template>
    </el-drawer>
  </span>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, watch } from 'vue'
import type { InputInstance } from 'element-plus'
import { useRoute } from 'vue-router'
import { Close, QuestionFilled, Search } from '@element-plus/icons-vue'
import { isMobile } from '../mobile'
import { currentLocale } from '../i18n'
import { helpKeyForPath } from '../help/pages'

interface HelpText { title: string; summary: string; body: string }
interface HelpPart { text: string; hit: boolean }
interface HelpLine { text: string; heading: boolean; item: boolean; gap: boolean; parts: HelpPart[] }

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

const searchOpen = ref(false)
const query = ref('')
const searchInput = ref<InputInstance>()
const activeQuery = computed(() => query.value.trim())

function splitParts(text: string, needle: string): HelpPart[] {
  if (!needle) return [{ text, hit: false }]
  const haystack = text.toLowerCase()
  const target = needle.toLowerCase()
  const parts: HelpPart[] = []
  let from = 0
  while (from < text.length) {
    const at = haystack.indexOf(target, from)
    if (at < 0) {
      parts.push({ text: text.slice(from), hit: false })
      break
    }
    if (at > from) parts.push({ text: text.slice(from, at), hit: false })
    parts.push({ text: text.slice(at, at + target.length), hit: true })
    from = at + target.length
  }
  return parts.length ? parts : [{ text, hit: false }]
}

const searchResult = computed<{ lines: HelpLine[]; count: number }>(() => {
  const needle = activeQuery.value
  const lines: HelpLine[] = []
  let count = 0
  if (!needle) {
    for (const raw of bodyLines.value) {
      const heading = raw.startsWith('■')
      const text = heading ? raw.slice(1).trim() : raw
      lines.push({ text, heading, item: /^\d+\./.test(raw), gap: !raw.trim(), parts: [{ text, hit: false }] })
    }
    return { lines, count }
  }
  const target = needle.toLowerCase()
  let section = ''
  let shownSection = ''
  for (const raw of bodyLines.value) {
    if (!raw.trim()) continue
    const heading = raw.startsWith('■')
    const text = heading ? raw.slice(1).trim() : raw
    if (heading) {
      section = text
      if (text.toLowerCase().includes(target)) {
        count++
        shownSection = section
        lines.push({ text, heading: true, item: false, gap: false, parts: splitParts(text, needle) })
      }
      continue
    }
    if (!text.toLowerCase().includes(target)) continue
    count++
    if (section && shownSection !== section) {
      shownSection = section
      lines.push({ text: section, heading: true, item: false, gap: false, parts: splitParts(section, needle) })
    }
    lines.push({ text, heading: false, item: /^\d+\./.test(raw), gap: false, parts: splitParts(text, needle) })
  }
  return { lines, count }
})
const visibleLines = computed(() => searchResult.value.lines)
const matchCount = computed(() => searchResult.value.count)

function toggleSearch(): void {
  searchOpen.value = !searchOpen.value
  if (!searchOpen.value) {
    query.value = ''
    return
  }
  void nextTick(() => searchInput.value?.focus())
}

function resetSearch(): void {
  searchOpen.value = false
  query.value = ''
}

watch(open, (value) => {
  if (value) void ensureContent()
  else resetSearch()
})
watch(() => route.path, () => {
  open.value = false
  resetSearch()
})
</script>

<style scoped>
.page-help { display: inline-flex; align-items: center; }
.page-help-btn { display: inline-flex; align-items: center; gap: 6px; }
.page-help-label { font-size: 13px; }
.page-help-head { display: flex; flex-direction: column; gap: 6px; min-width: 0; }
.page-help-head-top { display: flex; align-items: center; justify-content: space-between; gap: 8px; min-width: 0; }
.page-help-search-btn { flex: none; margin-right: 12px; padding: 2px 6px; font-size: 13px; }
.page-help-search-btn :deep(.el-icon) { margin-right: 4px; }
.page-help-search { margin-top: 4px; }
.page-help-search-count { display: block; margin-top: 4px; font-size: 12px; color: var(--scada-text-muted); }
.page-help-kicker { font-size: 12px; letter-spacing: 0.08em; color: var(--scada-text-muted); }
.page-help-head h2 { margin: 0; font-size: 20px; line-height: 1.3; color: var(--scada-text); }
.page-help-head p { margin: 0; font-size: 13px; line-height: 1.6; color: var(--scada-text-secondary); }
.page-help-body { display: flex; flex-direction: column; gap: 4px; padding-right: 4px; color: var(--scada-text); font-size: 14px; line-height: 1.75; }
.page-help-line { white-space: pre-wrap; overflow-wrap: anywhere; }
.page-help-line mark { padding: 0 1px; border-radius: 2px; background: var(--el-color-warning-light-5); color: inherit; }
.page-help-empty { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; padding: 24px 4px; color: var(--scada-text-muted); font-size: 14px; }
.page-help-empty p { margin: 0; }
.page-help-line.is-heading { margin-top: 18px; padding-left: 10px; border-left: 3px solid var(--scada-accent); font-size: 15px; font-weight: 600; color: var(--scada-text); }
.page-help-line.is-heading:first-child { margin-top: 0; }
.page-help-line.is-item { padding-left: 6px; }
.page-help-line.is-gap { height: 4px; }

@media (max-width: 1280px) {
  .page-help-label { display: none; }
  .page-help-btn:not(.is-compact) { padding: 8px; }
}
</style>

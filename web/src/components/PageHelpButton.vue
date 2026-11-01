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
      <!-- 帮助本身也要能被搜到：用户常知道「要改什么」，不知道在哪个页面 -->
      <section v-if="activeQuery && otherHits.length" class="page-help-others" data-test="page-help-others">
        <h3 class="page-help-others-title">{{ $t('nav.otherPages') }} · {{ otherHits.length }}</h3>
        <button
          v-for="hit in otherHits"
          :key="hit.key"
          type="button"
          class="page-help-other"
          data-test="page-help-other"
          @click="openHit(hit)"
        >
          <span class="page-help-other-title">{{ hit.title }}</span>
          <span class="page-help-other-line">{{ hit.lines[0] }}</span>
        </button>
      </section>
      <template #footer>
        <el-button type="primary" data-test="page-help-close" @click="open = false">{{ $t('pageHelp.gotIt') }}</el-button>
      </template>
    </el-drawer>
  </span>
</template>

<script setup lang="ts">
import { computed, nextTick, ref, shallowRef, watch } from 'vue'
import type { InputInstance } from 'element-plus'
import { useRoute, useRouter } from 'vue-router'
import { Close, QuestionFilled, Search } from '@element-plus/icons-vue'
import { isMobile } from '../mobile'
import { currentLocale } from '../i18n'
import { helpKeyForPath, helpPathForKey } from '../help/pages'
import { visibleNavItems } from '../nav/catalog'
import { useAuthStore } from '../stores/auth'

interface HelpText { title: string; summary: string; body: string }
interface HelpPart { text: string; hit: boolean }
interface HelpLine { text: string; heading: boolean; item: boolean; gap: boolean; parts: HelpPart[] }

const props = withDefaults(defineProps<{ helpKey?: string; compact?: boolean }>(), { helpKey: '', compact: false })
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
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

interface HelpHit { key: string; path: string; title: string; lines: string[] }

let keepOpenOnNextNavigation = false

// 跨页命中：内容在打开抽屉时已整份加载（content 就是全量 help/xx.json），这里只做筛选
const otherHits = computed<HelpHit[]>(() => {
  const needle = activeQuery.value.toLowerCase()
  const book = content.value
  if (!needle || !book) return []
  // 当前账号打不开的页面不列出来，避免点了被弹回首页
  const allowed = new Set(visibleNavItems(auth.user).map((item) => item.path))
  const hits: HelpHit[] = []
  for (const [name, entry] of Object.entries(book)) {
    if (name === key.value || !entry) continue
    const path = helpPathForKey(name)
    if (!path || !allowed.has(path)) continue
    const body = entry.body ?? ''
    const lines = body.split('\n').filter((line) => line.toLowerCase().includes(needle)).slice(0, 2)
    const inHead = (entry.title ?? '').toLowerCase().includes(needle) || (entry.summary ?? '').toLowerCase().includes(needle)
    if (!lines.length && !inHead) continue
    hits.push({ key: name, path, title: entry.title, lines: lines.length ? lines : [entry.summary ?? ''] })
    if (hits.length >= 6) break
  }
  return hits
})

// 跳到其它页面后抽屉保持打开，直接看到那一页的完整说明
function openHit(hit: HelpHit): void {
  query.value = ''
  if (route.path === hit.path) return
  keepOpenOnNextNavigation = true
  void router.push(hit.path)
}

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
  // 从跨页搜索结果跳转时不要关抽屉，否则用户点完还得再开一次
  if (keepOpenOnNextNavigation) {
    keepOpenOnNextNavigation = false
    return
  }
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
.page-help-others {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 20px;
  padding-top: 16px;
  border-top: 1px solid var(--scada-border);
}
.page-help-others-title { margin: 0 0 2px; font-size: 13px; font-weight: 600; color: var(--scada-text-muted); }
.page-help-other {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 2px;
  padding: 8px 10px;
  border-radius: 6px;
  border: 1px solid var(--scada-border);
  background: var(--scada-panel-muted);
  color: var(--scada-text);
  font: inherit;
  text-align: left;
  cursor: pointer;
}
.page-help-other:hover { border-color: var(--scada-accent); }
.page-help-other:focus-visible { outline: 2px solid var(--scada-accent); outline-offset: 2px; }
.page-help-other-title { font-size: 14px; font-weight: 500; }
.page-help-other-line { font-size: 12px; line-height: 1.5; color: var(--scada-text-secondary); overflow-wrap: anywhere; }
.page-help-line.is-heading { margin-top: 18px; padding-left: 10px; border-left: 3px solid var(--scada-accent); font-size: 15px; font-weight: 600; color: var(--scada-text); }
.page-help-line.is-heading:first-child { margin-top: 0; }
.page-help-line.is-item { padding-left: 6px; }
.page-help-line.is-gap { height: 4px; }

@media (max-width: 1280px) {
  .page-help-label { display: none; }
  .page-help-btn:not(.is-compact) { padding: 8px; }
}
</style>

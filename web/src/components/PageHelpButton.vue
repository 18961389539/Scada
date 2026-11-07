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
      :size="isMobile ? '92%' : '680px'"
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
        <section v-if="!activeQuery && quickSteps.length" class="page-help-start" data-test="page-help-start">
          <div class="page-help-start-head">
            <strong class="page-help-start-title">{{ $t('pageHelp.startHere') }}</strong>
            <p class="page-help-start-hint">{{ $t('pageHelp.startHint') }}</p>
          </div>
          <button
            v-for="step in quickSteps"
            :key="step.blockId"
            type="button"
            class="page-help-start-item"
            :class="{ 'is-current': focusedId === step.blockId }"
            data-test="page-help-start-item"
            @click="focusStep(step.sectionId, step.blockId)"
          >
            <span class="page-help-num">{{ step.number }}</span>
            <span class="page-help-start-copy">
              <span class="page-help-start-label">{{ step.label }}</span>
              <span v-if="step.detail" class="page-help-start-detail">{{ step.detail }}</span>
            </span>
          </button>
        </section>
        <div v-if="!activeQuery && outline.length > 1" class="page-help-toc" data-test="page-help-toc">
          <span class="page-help-toc-label">{{ $t('pageHelp.contents') }}</span>
          <button
            v-for="section in outline"
            :key="section.id"
            type="button"
            class="page-help-jump"
            data-test="page-help-jump"
            @click="jump(section.id)"
          >{{ section.title }}</button>
          <button type="button" class="page-help-fold" data-test="page-help-fold" @click="toggleAll">
            {{ $t(allExpanded ? 'pageHelp.collapseAll' : 'pageHelp.expandAll') }}
          </button>
        </div>
        <section
          v-for="section in visibleSections"
          :id="section.id"
          :key="section.id"
          class="page-help-section"
          data-test="page-help-section"
        >
          <button
            type="button"
            class="page-help-section-head"
            :aria-expanded="isOpen(section.id)"
            @click="toggle(section.id)"
          >
            <span class="page-help-section-title">
              <template v-for="(part, partIndex) in section.titleParts" :key="partIndex">
                <mark v-if="part.hit">{{ part.text }}</mark>
                <template v-else>{{ part.text }}</template>
              </template>
            </span>
            <span class="page-help-section-count">{{ $t('pageHelp.items', [section.blocks.length]) }}</span>
          </button>
          <div v-show="isOpen(section.id)" class="page-help-section-body" data-test="page-help-section-body">
            <template v-for="(block, blockIndex) in section.blocks" :key="blockIndex">
              <p v-if="block.kind === 'prose'" class="page-help-prose">
                <template v-for="(part, partIndex) in block.parts" :key="partIndex">
                  <mark v-if="part.hit">{{ part.text }}</mark>
                  <template v-else>{{ part.text }}</template>
                </template>
              </p>
              <article v-else :id="block.id" class="page-help-step" :class="{ 'is-focused': focusedId === block.id }">
                <span class="page-help-num">{{ block.number }}</span>
                <div class="page-help-step-copy">
                  <strong v-if="block.lead" class="page-help-step-title" data-test="page-help-step-title">
                    <template v-for="(part, partIndex) in block.leadParts" :key="partIndex">
                      <mark v-if="part.hit">{{ part.text }}</mark>
                      <template v-else>{{ part.text }}</template>
                    </template>
                  </strong>
                  <p v-if="block.text">
                    <template v-for="(part, partIndex) in block.parts" :key="partIndex">
                      <mark v-if="part.hit">{{ part.text }}</mark>
                      <template v-else>{{ part.text }}</template>
                    </template>
                  </p>
                </div>
              </article>
            </template>
          </div>
        </section>
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
interface HelpBlock { id: string; kind: 'prose' | 'step'; number: string; lead: string; text: string; parts: HelpPart[]; leadParts: HelpPart[] }
interface QuickStep { sectionId: string; blockId: string; number: string; label: string; detail: string }
interface HelpSection { id: string; title: string; titleParts: HelpPart[]; blocks: HelpBlock[] }

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
const collapsed = ref(new Set<string>())
const collapseTouched = ref(false)
const focusedId = ref('')

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

function clipLead(body: string, at: number, max: number): { lead: string; text: string } | null {
  if (at < 2 || at > max) return null
  const lead = body.slice(0, at).trim()
  if (!lead || /[“"「]/.test(lead)) return null
  return { lead, text: body.slice(at + 1).trim() }
}

function leadOf(body: string): { lead: string; text: string } {
  return clipLead(body, body.search(/[：:]/), 22)
    ?? clipLead(body, body.search(/[。！？.!?]/), 32)
    ?? { lead: '', text: body }
}

function clip(text: string, max = 32): string {
  const value = text.trim()
  return value.length > max ? `${value.slice(0, max)}…` : value
}

function parseBody(lines: string[], needle: string): { sections: HelpSection[]; count: number } {
  const sections: HelpSection[] = []
  let count = 0
  let seq = 0
  const target = needle.toLowerCase()
  let section: HelpSection | null = null
  let included = false
  for (const raw of lines) {
    if (!raw.trim()) continue
    if (raw.startsWith('■')) {
      const title = raw.slice(1).trim()
      const hit = !!target && title.toLowerCase().includes(target)
      section = { id: `page-help-s${seq++}`, title, titleParts: splitParts(title, needle), blocks: [] }
      included = !target || hit
      if (included) sections.push(section)
      if (hit) count++
      continue
    }
    const hit = !target || raw.toLowerCase().includes(target)
    if (!hit || !section) continue
    if (!included) {
      included = true
      sections.push(section)
    }
    const numbered = raw.match(/^(\d+)\.\s+([\s\S]+)$/)
    const id = `${section.id}-b${section.blocks.length}`
    if (!numbered) {
      section.blocks.push({ id, kind: 'prose', number: '', lead: '', text: raw, parts: splitParts(raw, needle), leadParts: [] })
    } else {
      const split = leadOf(numbered[2])
      section.blocks.push({
        id,
        kind: 'step',
        number: numbered[1],
        lead: split.lead,
        text: split.text,
        parts: splitParts(split.text, needle),
        leadParts: splitParts(split.lead, needle)
      })
    }
    if (target) count++
  }
  return { sections, count }
}

const outline = computed(() => parseBody(bodyLines.value, '').sections)
const searchResult = computed(() => parseBody(bodyLines.value, activeQuery.value))
const visibleSections = computed(() => searchResult.value.sections)
const matchCount = computed(() => searchResult.value.count)
const allExpanded = computed(() => outline.value.every(section => !collapsed.value.has(section.id)))
const quickSteps = computed<QuickStep[]>(() => {
  const how = outline.value[2]
  if (!how) return []
  return how.blocks.filter(block => block.kind === 'step').map(block => ({
    sectionId: how.id,
    blockId: block.id,
    number: block.number,
    label: block.lead || clip(block.text),
    detail: block.lead ? clip(block.text, 120) : ''
  }))
})

function restingCollapsed(sections: HelpSection[]): Set<string> {
  return new Set(sections.map(section => section.id))
}

function resetReading(): void {
  collapseTouched.value = false
  focusedId.value = ''
  collapsed.value = restingCollapsed(outline.value)
}

function isOpen(id: string): boolean {
  return !!activeQuery.value || !collapsed.value.has(id)
}

function toggle(id: string): void {
  if (activeQuery.value) return
  collapseTouched.value = true
  const next = new Set(collapsed.value)
  if (next.has(id)) next.delete(id)
  else next.add(id)
  collapsed.value = next
}

function toggleAll(): void {
  collapseTouched.value = true
  collapsed.value = allExpanded.value ? new Set(outline.value.map(section => section.id)) : new Set()
}

function jump(id: string): void {
  collapseTouched.value = true
  const next = new Set(collapsed.value)
  next.delete(id)
  collapsed.value = next
  focusedId.value = ''
  void nextTick(() => document.getElementById(id)?.scrollIntoView({ block: 'start' }))
}

function focusStep(sectionId: string, blockId: string): void {
  collapseTouched.value = true
  const next = new Set(collapsed.value)
  next.delete(sectionId)
  collapsed.value = next
  focusedId.value = blockId
  void nextTick(() => document.getElementById(blockId)?.scrollIntoView({ block: 'center' }))
}

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

watch(outline, () => {
  if (!open.value || collapseTouched.value) return
  collapsed.value = restingCollapsed(outline.value)
})
watch(open, (value) => {
  if (value) {
    resetReading()
    void ensureContent()
  } else {
    resetSearch()
    resetReading()
  }
})
watch(() => route.path, () => {
  // 从跨页搜索结果跳转时不要关抽屉，否则用户点完还得再开一次
  if (keepOpenOnNextNavigation) {
    keepOpenOnNextNavigation = false
    resetReading()
    return
  }
  open.value = false
  resetSearch()
  resetReading()
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
.page-help-body { display: flex; flex-direction: column; gap: 8px; padding-right: 4px; color: var(--scada-text); font-size: 14px; line-height: 1.7; }
.page-help-start {
  display: flex;
  flex-direction: column;
  gap: 2px;
  padding: 10px 12px;
  border: 1px solid var(--scada-border);
  border-radius: 8px;
  background: var(--scada-panel-muted);
}
.page-help-start-head { display: flex; flex-direction: column; gap: 2px; margin-bottom: 4px; }
.page-help-start-title { font-size: 13px; font-weight: 600; }
.page-help-start-hint { margin: 0; font-size: 12px; line-height: 1.5; color: var(--scada-text-secondary); }
.page-help-start-item {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  width: 100%;
  padding: 6px 2px;
  border: 0;
  background: transparent;
  color: var(--scada-text);
  font: inherit;
  font-size: 13px;
  line-height: 1.45;
  text-align: left;
  cursor: pointer;
}
.page-help-start-copy { display: flex; flex-direction: column; gap: 1px; min-width: 0; }
.page-help-start-label { font-weight: 600; }
.page-help-start-detail { color: var(--scada-text-secondary); font-size: 12px; line-height: 1.45; }
.page-help-start-item.is-current .page-help-start-label { color: var(--scada-accent-ink); }
.page-help-start-item:hover .page-help-start-label { color: var(--scada-accent-ink); }
.page-help-start-item:focus-visible { outline: 2px solid var(--scada-accent-ink); outline-offset: 2px; }
.page-help-start-item .page-help-num { width: 22px; height: 22px; font-size: 11px; flex: none; }
.page-help-toc {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
  position: sticky;
  top: 0;
  z-index: 2;
  margin: -4px 0 4px;
  padding: 8px 0;
  background: var(--el-bg-color);
  border-bottom: 1px solid var(--scada-border);
}
.page-help-toc-label { font-size: 12px; font-weight: 600; color: var(--scada-text-muted); }
.page-help-jump, .page-help-fold {
  border: 1px solid var(--scada-border-strong);
  background: transparent;
  border-radius: 999px;
  padding: 3px 10px;
  font: inherit;
  font-size: 12px;
  line-height: 1.4;
  color: var(--scada-text-secondary);
  cursor: pointer;
}
.page-help-fold { margin-left: auto; color: var(--scada-text); }
.page-help-jump:hover, .page-help-fold:hover { border-color: var(--scada-accent-ink); color: var(--scada-text); }
.page-help-jump:focus-visible, .page-help-fold:focus-visible, .page-help-section-head:focus-visible { outline: 2px solid var(--scada-accent-ink); outline-offset: 2px; }
.page-help-section { scroll-margin-top: 52px; }
.page-help-section-head {
  display: flex;
  align-items: center;
  width: 100%;
  margin: 0;
  padding: 8px 16px 8px 10px;
  border: 0;
  border-left: 3px solid var(--scada-accent-ink);
  background: transparent;
  color: var(--scada-text);
  font: inherit;
  font-size: 15px;
  font-weight: 600;
  line-height: 1.4;
  text-align: left;
  cursor: pointer;
}
.page-help-section-title { min-width: 0; }
.page-help-section-count { margin-left: auto; font-size: 12px; font-weight: 500; color: var(--scada-text-muted); }
.page-help-section-head::after {
  content: '';
  width: 7px;
  height: 7px;
  margin-left: 8px;
  border-right: 1.5px solid currentColor;
  border-bottom: 1.5px solid currentColor;
  transform: rotate(45deg) translateY(-2px);
}
.page-help-section-head[aria-expanded="false"]::after { transform: rotate(-45deg) translateY(1px); }
.page-help-section-body { display: flex; flex-direction: column; gap: 8px; padding: 4px 0 8px; }
.page-help-prose { margin: 0; }
.page-help-step {
  display: grid;
  grid-template-columns: 28px minmax(0, 1fr);
  gap: 10px;
  align-items: start;
  padding: 8px 10px;
  border: 1px solid var(--scada-border);
  border-radius: 8px;
  background: var(--scada-panel-muted);
  scroll-margin-top: 64px;
}
.page-help-step.is-focused { border-color: var(--scada-accent-ink); }
.page-help-num {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 1px solid var(--scada-border-strong);
  border-radius: 50%;
  color: var(--scada-text-secondary);
  font-size: 12px;
  font-weight: 600;
}
.page-help-step-copy { min-width: 0; }
.page-help-step-title { display: block; font-size: 14px; font-weight: 600; }
.page-help-step-copy p { margin: 2px 0 0; color: var(--scada-text-secondary); }
.page-help-prose mark, .page-help-step mark, .page-help-section-head mark { padding: 0 1px; border-radius: 2px; background: var(--el-color-warning-light-5); color: inherit; }
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
.page-help-other:hover { border-color: var(--scada-accent-ink); }
.page-help-other:focus-visible { outline: 2px solid var(--scada-accent-ink); outline-offset: 2px; }
.page-help-other-title { font-size: 14px; font-weight: 500; }
.page-help-other-line { font-size: 12px; line-height: 1.5; color: var(--scada-text-secondary); overflow-wrap: anywhere; }
.page-help-prose, .page-help-step-copy p, .page-help-step-title { overflow-wrap: anywhere; }

@media (max-width: 1280px) {
  .page-help-label { display: none; }
  .page-help-btn:not(.is-compact) { padding: 8px; }
}
</style>

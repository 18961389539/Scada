<template>
  <section
    v-if="issues.length"
    class="page-status"
    :class="[`is-${worst}`, { 'is-summary': collapsed }]"
    role="status"
    data-test="page-status"
  >
    <button
      v-if="collapsed"
      type="button"
      class="page-status-summary"
      :aria-expanded="open"
      data-test="page-status-toggle"
      @click="open = !open"
    >
      <span class="page-status-dot" aria-hidden="true" />
      <strong>{{ $t('uxShell.issueCount', [issues.length]) }}</strong>
      <span class="page-status-first">{{ issues[0]?.message }}</span>
      <span class="page-status-spacer" />
      <span class="page-status-toggle">{{ $t(open ? 'uxShell.issueCollapse' : 'uxShell.issueExpand') }}</span>
    </button>
    <div v-show="!collapsed || open" class="page-status-list">
      <div
        v-for="issue in issues"
        :key="issue.id"
        class="page-status-item"
        :class="`is-${issue.level}`"
        :data-test="issue.test"
      >
        <span class="page-status-dot" aria-hidden="true" />
        <span class="page-status-message">{{ issue.message }}</span>
        <span v-if="issue.detail" class="page-status-detail">{{ issue.detail }}</span>
        <span class="page-status-spacer" />
        <el-button
          v-if="issue.action"
          link
          type="primary"
          size="small"
          :data-test="issue.actionTest"
          @click="emit('action', issue.id)"
        >{{ issue.action }}</el-button>
      </div>
    </div>
  </section>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'

export type PageIssueLevel = 'error' | 'warning' | 'info'

export interface PageIssue {
  id: string
  /** 保留原有 data-test，便于现有用例与自动化定位。 */
  test: string
  level: PageIssueLevel
  message: string
  detail?: string
  action?: string
  actionTest?: string
}

// 三条以内直接铺开；再多才折叠成一行，避免状态条本身又变成一堵墙。
const INLINE_LIMIT = 3

const props = defineProps<{ issues: PageIssue[] }>()
const emit = defineEmits<{ action: [id: string] }>()

const open = ref(false)
const collapsed = computed(() => props.issues.length > INLINE_LIMIT)
const worst = computed<PageIssueLevel>(() => {
  if (props.issues.some(issue => issue.level === 'error')) return 'error'
  if (props.issues.some(issue => issue.level === 'warning')) return 'warning'
  return 'info'
})
</script>

<style scoped>
.page-status {
  display: flex;
  flex-direction: column;
  margin-bottom: var(--scada-space-3);
  border: 1px solid var(--scada-border);
  border-left: 3px solid var(--scada-info);
  border-radius: var(--scada-radius-sm);
  background: var(--scada-panel);
  overflow: hidden;
}
.page-status.is-warning { border-left-color: var(--scada-warning); }
.page-status.is-error { border-left-color: var(--scada-danger); }

.page-status-summary {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-height: 36px;
  padding: 6px 12px;
  border: 0;
  background: transparent;
  color: var(--scada-text);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
}
.page-status-summary:hover { background: var(--scada-panel-hover); }
.page-status-summary:focus-visible { outline: 2px solid var(--scada-primary); outline-offset: -2px; }
.page-status-first {
  min-width: 0;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
  color: var(--scada-text-secondary);
}
.page-status-toggle { color: var(--scada-primary); font-size: 12px; flex-shrink: 0; }

.page-status-item {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 4px 8px;
  min-height: 32px;
  padding: 5px 12px;
  border-top: 1px solid var(--scada-border);
  font-size: 13px;
  line-height: 1.45;
}
.page-status-list > .page-status-item:first-child { border-top: 0; }
.page-status.is-summary .page-status-list > .page-status-item:first-child { border-top: 1px solid var(--scada-border); }

.page-status-dot {
  flex-shrink: 0;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--scada-info);
}
.page-status-item.is-warning .page-status-dot { background: var(--scada-warning); }
.page-status-item.is-error .page-status-dot { background: var(--scada-danger); }
.page-status.is-warning > .page-status-summary .page-status-dot { background: var(--scada-warning); }
.page-status.is-error > .page-status-summary .page-status-dot { background: var(--scada-danger); }

.page-status-message { min-width: 0; color: var(--scada-text); overflow-wrap: anywhere; }
.page-status-detail { color: var(--scada-text-secondary); font-size: 12px; overflow-wrap: anywhere; }
.page-status-spacer { flex: 1; min-width: 8px; }
</style>

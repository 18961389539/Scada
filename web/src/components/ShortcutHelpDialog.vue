<template>
  <el-dialog
    v-model="shortcutHelpOpen"
    :title="$t('shortcuts.title')"
    width="440px"
    append-to-body
    class="shortcut-dialog"
    data-test="shortcut-help"
  >
    <section v-for="group in shortcutGroups" :key="group.id" class="shortcut-group">
      <h3 class="shortcut-group-title">{{ $t(group.titleKey) }}</h3>
      <div v-for="item in group.items" :key="item.descKey" class="shortcut-row">
        <span class="shortcut-desc">{{ $t(item.descKey) }}</span>
        <span class="shortcut-keys">
          <kbd v-for="(key, index) in item.keys" :key="key + index" class="shortcut-key">{{ key }}</kbd>
        </span>
      </div>
    </section>
    <p class="shortcut-hint tiny muted">{{ $t('shortcuts.hint') }}</p>
  </el-dialog>
</template>

<script setup lang="ts">
import { shortcutGroups, shortcutHelpOpen } from '../shortcuts/registry'
</script>

<style scoped>
.shortcut-group { margin-bottom: var(--scada-space-4, 16px); }
.shortcut-group:last-of-type { margin-bottom: 0; }
.shortcut-group-title {
  margin: 0 0 var(--scada-space-2, 8px);
  font-size: var(--scada-fs-small, 13px);
  font-weight: 600;
  color: var(--scada-text-muted, #64748b);
}
.shortcut-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--scada-space-3, 12px);
  padding: 5px 0;
  border-bottom: 1px dashed var(--scada-border);
}
.shortcut-row:last-child { border-bottom: 0; }
.shortcut-desc { font-size: var(--scada-fs-body, 14px); }
.shortcut-keys { display: inline-flex; gap: 4px; flex: none; }
.shortcut-key {
  min-width: 22px;
  padding: 1px 6px;
  border: 1px solid var(--scada-border-strong);
  border-bottom-width: 2px;
  border-radius: 4px;
  background: var(--scada-bg-subtle);
  color: var(--scada-text);
  font-family: var(--scada-font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
  font-size: 12px;
  line-height: 18px;
  text-align: center;
}
.shortcut-hint { margin: var(--scada-space-3, 12px) 0 0; text-align: center; }
</style>

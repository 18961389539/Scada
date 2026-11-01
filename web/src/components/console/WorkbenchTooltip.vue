<template>
  <el-tooltip
    :content="content"
    :trigger="['hover', 'focus']"
    :trigger-keys="[]"
    :show-after="200"
    :hide-after="0"
    :enterable="false"
    placement="top"
    popper-class="workbench-tooltip"
  >
    <span v-if="disabledTrigger || wrapTrigger" class="workbench-tooltip-trigger" :class="{ 'disabled-tooltip-trigger': disabledTrigger }" :tabindex="disabledTrigger ? 0 : undefined" :aria-disabled="disabledTrigger || undefined"><slot /></span>
    <slot v-else />
  </el-tooltip>
</template>

<script setup lang="ts">
withDefaults(defineProps<{ content: string; disabledTrigger?: boolean; wrapTrigger?: boolean }>(), { disabledTrigger: false, wrapTrigger: false })
</script>

<style scoped>
.workbench-tooltip-trigger { display: inline-flex; max-width: 100%; }
:global(.workbench-tooltip) { max-width: min(320px, calc(100vw - 24px)); box-sizing: border-box; font-size: 12px; line-height: 1.6; overflow-wrap: anywhere; }
</style>

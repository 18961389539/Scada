<template>
  <span class="field-label">
    <span>{{ label }}</span>
    <el-tooltip v-if="help" :content="help" placement="top" :show-after="300">
      <el-icon class="field-help" role="img" :aria-label="help"><QuestionFilled /></el-icon>
    </el-tooltip>
  </span>
</template>

<script setup lang="ts">
import { QuestionFilled } from '@element-plus/icons-vue'
defineProps<{ label: string; help?: string }>()
</script>

<style scoped>
.field-label { display: inline-flex; align-items: center; gap: 4px; }
.field-help { color: var(--scada-text-muted); font-size: 13px; cursor: help; }
</style>

<template>
  <div
    class="page-state"
    :class="{ 'is-compact': compact }"
    :data-test="`page-state-${state}`"
    role="status"
    :aria-live="state === 'error' ? 'assertive' : 'polite'"
  >
    <el-skeleton v-if="state === 'loading'" :rows="compact ? 2 : rows" animated />
    <el-empty v-else-if="state === 'empty'" :description="text" :image-size="compact ? 52 : 96">
      <slot />
    </el-empty>
    <el-alert v-else-if="state === 'error'" type="error" :title="text" :closable="false" show-icon>
      <template #default>
        <el-button v-if="retryText" link type="primary" data-test="page-state-retry" @click="emit('retry')">{{ retryText }}</el-button>
      </template>
    </el-alert>
    <el-alert v-else-if="state === 'forbidden'" type="warning" :title="text" :closable="false" show-icon />
    <slot v-else />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { tr } from '../i18n'

/**
 * 页面/表格的统一状态展示：加载中、无数据、加载失败、无权限。
 *
 * 约定：错误态优先交给页面级 `PageStatusBar`（它带重试动作与多问题汇总）；
 * 这里的 error 只用于"表格自身拿不到数据"的兜底，保证空表不会伪装成"没有数据"。
 */
const props = withDefaults(defineProps<{
  state: 'loading' | 'empty' | 'error' | 'forbidden' | 'ready'
  /** 覆盖默认文案；空数据可给更具体的说明 */
  description?: string
  /** 显示重试按钮的文案；留空则不渲染按钮 */
  retryText?: string
  /** 加载骨架行数（compact 时固定 2 行） */
  rows?: number
  /** 表格/面板内使用：更小的图标与留白 */
  compact?: boolean
}>(), { description: '', retryText: '', rows: 3, compact: false })

const emit = defineEmits<{ retry: [] }>()

const text = computed(() => {
  if (props.description) return props.description
  if (props.state === 'error') return tr('pageState.error')
  if (props.state === 'forbidden') return tr('pageState.forbidden')
  return tr('pageState.empty')
})
</script>

<style scoped>
.page-state {
  padding: 8px 0;
}
.page-state.is-compact {
  padding: 4px 0;
}
.page-state :deep(.el-empty) {
  padding: 8px 0;
}
.page-state :deep(.el-empty__description) {
  margin-top: 4px;
}
</style>
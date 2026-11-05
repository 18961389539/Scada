<template>
  <div class="layout-actions" :class="{ mobile }">
    <WorkbenchTooltip :content="layout.publishedRevision == null ? $t('workbenchTips.unpublishedLayout') : $t('workbenchTips.openLayout')" :disabled-trigger="(layout.publishedRevision == null)"><el-button size="small" type="primary" :disabled="layout.publishedRevision == null" :data-test="`layout-open-${layout.id}`" @click="$emit('open')">{{ $t('layoutsView.k65fc81') }}</el-button></WorkbenchTooltip>
    <WorkbenchTooltip :content="$t('workbenchTips.editDraft')"><el-button size="small" @click="$emit('edit')">{{ $t('layoutsView.ka7f814') }}</el-button></WorkbenchTooltip>
    <WorkbenchTooltip v-if="layout.publishedRevision == null || layout.hasDraftChanges !== false" :content="$t('workbenchTips.publishReview')"><el-button size="small" type="warning" plain :data-test="`layout-publish-${layout.id}`" @click="$emit('publish')">{{ $t('layoutLifecycle.publish') }}</el-button></WorkbenchTooltip>
    <WorkbenchTooltip :content="$t('workbenchTips.more')" wrap-trigger>
    <el-dropdown ref="menu" trigger="click" @keydown.esc.stop="handleClose">
      <el-button size="small" :data-test="`layout-more-${layout.id}`" :aria-label="$t('layoutCatalog.moreFor', [layout.name])">{{ $t('layoutCatalog.more') }}</el-button>
      <template #dropdown><el-dropdown-menu>
        <WorkbenchTooltip :content="$t('workbenchTips.copyLayout')"><el-dropdown-item :data-test="`layout-copy-${layout.id}`" @click="$emit('copy')">{{ $t('layoutCatalog.copyLayout') }}</el-dropdown-item></WorkbenchTooltip>
        <WorkbenchTooltip :content="$t('workbenchTips.history')"><el-dropdown-item :data-test="`layout-history-${layout.id}`" @click="$emit('history')">{{ $t('layoutLifecycle.history') }}</el-dropdown-item></WorkbenchTooltip>
        <WorkbenchTooltip v-if="layout.publishedRevision != null && layout.hasDraftChanges === false" :content="$t('workbenchTips.publishReview')"><el-dropdown-item :data-test="`layout-publish-${layout.id}`" @click="$emit('publish')">{{ $t('layoutLifecycle.publish') }}</el-dropdown-item></WorkbenchTooltip>
        <WorkbenchTooltip :content="$t('workbenchTips.deleteLayout')"><el-dropdown-item divided :data-test="`layout-delete-${layout.id}`" @click="$emit('remove')">{{ $t('layoutsView.k3755f5') }}</el-dropdown-item></WorkbenchTooltip>
      </el-dropdown-menu></template>
    </el-dropdown>
    </WorkbenchTooltip>
  </div>
</template>

<script setup lang="ts">
import WorkbenchTooltip from './WorkbenchTooltip.vue'
import { ref } from 'vue'
import type { HmiLayout } from '../../api/hmiLayouts'

defineProps<{ layout: HmiLayout; mobile?: boolean }>()
defineEmits<{ open: []; edit: []; publish: []; copy: []; history: []; remove: [] }>()
const menu = ref<{ handleClose: () => void } | null>(null)
function handleClose(): void { menu.value?.handleClose() }
defineExpose({ handleClose })
</script>

<style scoped>
.layout-actions { display: flex; flex-wrap: wrap; gap: 5px; }
.layout-actions :deep(.el-button) { margin: 0; }
.mobile { gap: 8px; }
.mobile :deep(.el-button) { min-height: 44px; padding-inline: 15px; font-size: 13px; }
</style>

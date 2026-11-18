<template>
  <div v-if="visible" class="service-status-chips" role="status" :data-test="testId">
    <el-tooltip v-if="healthAbnormal" :content="healthError || healthHint" placement="bottom" :trigger="compact ? 'click' : 'hover'">
      <el-tag :type="healthError ? 'info' : 'warning'" effect="light" size="small" class="status-chip" data-test="health-tag" :aria-label="$t(healthError ? 'overviewStatus.serviceUnknown' : 'overviewStatus.serviceIssue')">
        <el-icon v-if="compact"><Warning /></el-icon>
        <span>{{ $t(healthError ? 'overviewStatus.serviceUnknown' : 'overviewStatus.serviceIssue') }}</span>
      </el-tag>
    </el-tooltip>
    <el-tooltip v-if="hubIssue" :content="$t('appLayout.hubHint')" placement="bottom">
      <el-tag type="danger" effect="dark" size="small" data-test="hub-state">{{ hubIssue }}</el-tag>
    </el-tooltip>
    <el-tag v-if="isStandby" type="danger" effect="dark" size="small" data-test="standby-banner">
      {{ $t('appLayout.k41b031') }}<template v-if="primaryUrl">{{ $t('appLayout.k3d889a') }} <a v-if="allowPrimaryLink" :href="primaryUrl">{{ primaryUrl }}</a><template v-else>{{ primaryUrl }}</template></template>
    </el-tag>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { Warning } from '@element-plus/icons-vue'

const props = withDefaults(defineProps<{
  healthAbnormal: boolean
  healthError: string
  healthHint: string
  hubIssue: string
  isStandby: boolean
  primaryUrl?: string
  allowPrimaryLink?: boolean
  compact?: boolean
  testId?: string
}>(), { primaryUrl: '', allowPrimaryLink: false, compact: false, testId: 'service-status' })

const visible = computed(() => props.healthAbnormal || !!props.hubIssue || props.isStandby)
</script>

<style scoped>
.service-status-chips {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 8px;
  min-width: 0;
  max-width: 100%;
}
.service-status-chips :deep(.el-tag) { max-width: 100%; height: auto; min-height: 20px; white-space: normal; }
.service-status-chips :deep(.el-tag__content) { min-width: 0; overflow-wrap: anywhere; white-space: normal; }
.service-status-chips .status-chip { display: inline-flex; align-items: center; gap: 4px; cursor: pointer; }
.service-status-chips .status-chip :deep(.el-icon) { margin-right: 0; }
.service-status-chips a { color: inherit; }
</style>

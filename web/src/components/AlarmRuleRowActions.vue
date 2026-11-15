<template>
  <el-button v-if="canConfigure" link type="primary" @click="emit('edit', rule)">{{ $t('alarmRulesView.ka7f814') }}</el-button>
  <el-button v-if="canConfigure && !rule.outOfService" link @click="emit('out-of-service', rule, true)">{{ $t('alarmRulesView.kd989e5') }}</el-button>
  <el-button v-if="canConfigure && rule.outOfService" link @click="emit('out-of-service', rule, false)">{{ $t('alarmRulesView.k79748c') }}</el-button>
  <el-popconfirm v-if="canConfigure" :title="$t('alarmRulesView.k2343c0')" @confirm="emit('remove', rule)">
    <template #reference><el-button link type="danger">{{ $t('alarmRulesView.k3755f5') }}</el-button></template>
  </el-popconfirm>
  <el-dropdown v-if="canConfigure" trigger="click">
    <el-button link data-test="rule-more">{{ $t('ux.tables.more') }}</el-button>
    <template #dropdown><el-dropdown-menu>
      <el-dropdown-item data-test="rule-simulate" @click="emit('simulate', rule)">{{ $t('alarmRulesView.simulate') }}</el-dropdown-item>
      <el-dropdown-item data-test="rule-copy" @click="emit('copy', rule)">{{ $t('alarmRulesView.k4edd1d') }}</el-dropdown-item>
    </el-dropdown-menu></template>
  </el-dropdown>
</template>

<script setup lang="ts">
import type { AlarmRule } from '../types'

defineProps<{ rule: AlarmRule; canConfigure: boolean }>()
const emit = defineEmits<{
  (event: 'edit', rule: AlarmRule): void
  (event: 'out-of-service', rule: AlarmRule, value: boolean): void
  (event: 'remove', rule: AlarmRule): void
  (event: 'simulate', rule: AlarmRule): void
  (event: 'copy', rule: AlarmRule): void
}>()
</script>

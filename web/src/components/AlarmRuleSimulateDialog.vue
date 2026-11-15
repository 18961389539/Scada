<template>
  <el-dialog v-model="simulate.visible" :title="$t('alarmRulesView.simulateTitle')" width="560px" data-test="rule-simulate-dialog" :close-on-click-modal="!simulate.loading" :close-on-press-escape="!simulate.loading" :show-close="!simulate.loading">
    <el-alert type="info" :closable="false" show-icon :title="$t('alarmRulesView.simulateNote')" style="margin-bottom: 12px" />
    <div v-if="simulate.rule" class="tiny muted" style="margin-bottom: 12px">
      #{{ simulate.rule.id }} · {{ tagName(simulate.rule.tagId) }} · {{ alarmTypeLabels[simulate.rule.type] }}
    </div>
    <el-alert v-if="simulate.error" :title="simulate.error" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="simulate-error" />
    <el-form label-width="120px" :disabled="simulate.loading" @submit.prevent>
      <el-form-item :label="$t('alarmRulesView.simulateValue')">
        <el-input-number v-model="simulate.value" :controls="false" style="width: 160px" data-test="simulate-value" />
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.simulateQuality')">
        <el-select v-model="simulate.quality" style="width: 160px" data-test="simulate-quality">
          <el-option v-for="(label, value) in qualityLabels" :key="value" :label="label" :value="value" />
        </el-select>
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.simulatePrevious')">
        <el-input-number v-model="simulate.previousValue" :controls="false" style="width: 160px" data-test="simulate-previous" />
      </el-form-item>
      <template v-if="simulateRefIds.length">
        <el-form-item v-for="id in simulateRefIds" :key="id" :label="tagName(id)">
          <el-input-number v-model="simulate.references[id]" :controls="false" style="width: 140px" :data-test="`simulate-ref-${id}`" />
          <el-select v-model="simulate.referenceQualities[id]" style="width: 110px; margin-left: 8px" :data-test="`simulate-ref-quality-${id}`">
            <el-option v-for="(label, value) in qualityLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
      </template>
    </el-form>
    <el-alert v-if="simulate.result" :type="simulateResultType" :closable="false" show-icon :title="simulateResultTitle" data-test="simulate-result">
      <div class="tiny" style="white-space: pre-wrap">{{ simulate.result.indeterminate || localizedText(simulate.result.message, simulate.result.messageEn) }}</div>
      <div v-if="simulate.result.messageEn" class="tiny muted" style="white-space: pre-wrap">{{ simulate.result.messageEn }}</div>
      <div v-if="simulate.result.references.length" class="tiny muted">
        {{ simulate.result.references.map(r => `${r.tagName}=${r.value} (${r.quality})`).join(' · ') }}
      </div>
    </el-alert>
    <template #footer>
      <el-button :disabled="simulate.loading" @click="simulate.visible = false">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
      <el-button type="primary" :loading="simulate.loading" data-test="simulate-run" @click="runSimulate">{{ $t('alarmRulesView.simulateRun') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive } from 'vue'
import { ruleApi, type AlarmRuleSimulateResult } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmRule } from '../types'
import { alarmTypeLabels, expressionTagIds, localizedText, qualityLabels } from '../utils'
import { tr } from '../i18n'

defineProps<{ tagName: (id?: number | null) => string }>()

// 模拟使用已保存的规则：只预览条件与文案，不改变实际报警状态。
const simulate = reactive({
  visible: false,
  rule: null as AlarmRule | null,
  value: 0,
  previousValue: null as number | null,
  quality: 'Good',
  references: {} as Record<number, number>,
  referenceQualities: {} as Record<number, string>,
  loading: false,
  result: null as AlarmRuleSimulateResult | null,
  error: ''
})
const simulateRefIds = computed(() => expressionTagIds(simulate.rule?.expression))
const simulateResultType = computed(() => simulate.result?.indeterminate ? 'warning' : simulate.result?.triggered ? 'error' : 'success')
const simulateResultTitle = computed(() => simulate.result?.indeterminate
  ? tr('alarmRulesView.simulateIndeterminate')
  : simulate.result?.triggered ? tr('alarmRulesView.simulateTriggered') : tr('alarmRulesView.simulateNotTriggered'))

let disposed = false
onBeforeUnmount(() => { disposed = true })

function open(rule: AlarmRule): void {
  const references: Record<number, number> = {}
  const qualities: Record<number, string> = {}
  for (const id of expressionTagIds(rule.expression)) { references[id] = 0; qualities[id] = 'Good' }
  Object.assign(simulate, {
    visible: true, rule, value: 0, previousValue: null, quality: 'Good', references, referenceQualities: qualities,
    loading: false, result: null, error: ''
  })
  if (rule.type === 'High' || rule.type === 'HighHigh') simulate.value = Number((rule.threshold + 1).toFixed(3))
  else if (rule.type === 'Low' || rule.type === 'LowLow') simulate.value = Number((rule.threshold - 1).toFixed(3))
  else if (rule.type === 'Discrete') simulate.value = rule.activeWhen === 'Off' ? 0 : rule.bitIndex != null ? 2 ** rule.bitIndex : 1
}

async function runSimulate(): Promise<void> {
  if (disposed || !simulate.rule || simulate.loading) return
  simulate.loading = true
  simulate.error = ''
  try {
    simulate.result = await ruleApi.simulate(simulate.rule.id, {
      value: simulate.value,
      quality: simulate.quality,
      previousValue: simulate.previousValue ?? undefined,
      references: simulate.references,
      referenceQualities: simulate.referenceQualities
    })
  } catch (error) {
    simulate.result = null
    simulate.error = errorMessage(error, tr('alarmRulesView.simulateFailed'))
  } finally {
    simulate.loading = false
  }
}

defineExpose({ open })
</script>

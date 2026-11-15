<template>
  <el-dialog v-model="quadVisible" :title="$t('alarmRulesView.k55a182')" width="640px" data-test="rule-quad-dialog" :close-on-click-modal="!quadSaving" :close-on-press-escape="!quadSaving" :show-close="!quadSaving" :before-close="quadGuard.beforeClose" @keydown="quadGuard.onKey">
    <el-alert v-if="quadError" :title="quadError" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-error" />
    <el-alert v-if="quadLoading" :title="$t('alarmRulesView.kbb9cb2')" type="info" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-loading" />
    <el-alert v-else-if="quadForm.tagId && quadRows.length === 0" :title="$t('alarmRulesView.ka49545')" type="warning" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-none" />
    <el-alert v-else-if="quadSkipNotice" :title="quadSkipNotice" type="info" :closable="false" show-icon style="margin-bottom: 12px" data-test="rule-quad-skip" />
    <el-form :disabled="quadSaving" label-width="110px" @submit.prevent="saveQuad">
      <el-form-item :label="$t('alarmRulesView.k8cff1c')" required>
        <el-select :model-value="quadForm.tagId > 0 ? quadForm.tagId : undefined" filterable remote :remote-method="searchQuadTags" :loading="quadTagLoading" style="width: 100%" data-test="rule-quad-tag" @update:model-value="onQuadTagPicked" @visible-change="onQuadTagsOpen">
          <el-option v-for="tag in quadTagOptions" :key="tag.id" :label="alarmRuleTagLabel(tag)" :value="tag.id" />
        </el-select>
      </el-form-item>
      <el-row :gutter="12">
        <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.kfe8edb')"><el-input-number v-model="quadForm.highHigh" :controls="false" style="width: 100%" data-test="rule-quad-hh" /></el-form-item></el-col>
        <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.k88deca')"><el-input-number v-model="quadForm.high" :controls="false" style="width: 100%" data-test="rule-quad-h" /></el-form-item></el-col>
        <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.kb44804')"><el-input-number v-model="quadForm.low" :controls="false" style="width: 100%" data-test="rule-quad-l" /></el-form-item></el-col>
        <el-col :xs="12" :sm="6"><el-form-item :label="$t('alarmRulesView.k09df8e')"><el-input-number v-model="quadForm.lowLow" :controls="false" style="width: 100%" data-test="rule-quad-ll" /></el-form-item></el-col>
      </el-row>
      <el-form-item :label="$t('alarmRulesView.ke682b0')">
        <el-radio-group v-model="quadForm.criticalLevel" data-test="rule-quad-critical-level">
          <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
          <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
          <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item :label="$t('alarmRulesView.k318342')">
        <el-radio-group v-model="quadForm.highLevel" data-test="rule-quad-high-level">
          <el-radio-button value="Info">{{ $t('alarmRulesView.kab3656') }}</el-radio-button>
          <el-radio-button value="Warning">{{ $t('alarmRulesView.k5521e3') }}</el-radio-button>
          <el-radio-button value="Critical">{{ $t('alarmRulesView.k81ffc6') }}</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-row :gutter="12">
        <el-col :xs="24" :sm="12"><el-form-item :label="$t('alarmRulesView.k85d836')"><el-input-number v-model="quadForm.delaySec" :min="0" :max="3600" style="width: 100%" data-test="rule-quad-delay" /></el-form-item></el-col>
        <el-col :xs="24" :sm="12"><el-form-item :label="$t('alarmRulesView.k3b86db')"><el-input-number v-model="quadForm.recoverDeadband" :min="0" :step="0.1" style="width: 100%" data-test="rule-quad-deadband" /></el-form-item></el-col>
      </el-row>
      <el-form-item :label="$t('alarmRulesView.kd4e9ca')">
        <el-switch v-model="quadForm.enabled" data-test="rule-quad-enabled" />
      </el-form-item>
    </el-form>
    <el-alert v-if="quadOrderError" :title="quadOrderError" type="warning" :closable="false" show-icon style="margin-bottom: 8px" data-test="rule-quad-order" />
    <p class="tiny muted">{{ $t('alarmRulesView.k2377c1') }}</p>
    <el-table :data="quadRows" size="small" data-test="rule-quad-preview">
      <el-table-column :label="$t('alarmRulesView.k9f23cf')" min-width="120" show-overflow-tooltip><template #default="{ row }">{{ row.label }}</template></el-table-column>
      <el-table-column :label="$t('alarmRulesView.ka05320')" width="120" show-overflow-tooltip><template #default="{ row }"><span class="mono">{{ row.threshold ?? '-' }}</span></template></el-table-column>
      <el-table-column :label="$t('alarmRulesView.k5c42c0')" width="100" show-overflow-tooltip><template #default="{ row }">{{ levelLabel(row.level) }}</template></el-table-column>
    </el-table>
    <template #footer>
      <el-button :disabled="quadSaving" @click="quadGuard.close">{{ $t('alarmRulesView.k4d0b46') }}</el-button>
      <el-button type="primary" :loading="quadSaving" :disabled="!canSaveQuad" data-test="rule-quad-save" @click="saveQuad">{{ $t('alarmRulesView.k4dcb41', [quadRows.length]) }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { ruleApi } from '../api'
import { errorMessage } from '../api/http'
import type { AlarmRule, Tag } from '../types'
import { alarmTypeLabels, levelLabel } from '../utils'
import { tr } from '../i18n'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { rememberNumber } from '../composables/lastChoice'
import { useEditDialog } from '../composables/useEditDialog'
import { useAlarmRuleTagPicker } from '../composables/useAlarmRuleTagPicker'
import { alarmRuleTagLabel, emptyAlarmRuleInput } from '../views/alarmRuleModel'

const props = defineProps<{
  resolveTag: (id?: number | null) => Promise<Tag | undefined>
  ensureDevices: () => Promise<void>
  canConfigureTag: (tag: Tag) => boolean
}>()
const emit = defineEmits<{ (event: 'saved'): void }>()

type QuadRow = { type: AlarmRule['type']; level: AlarmRule['level']; threshold: number | null; label: string }
const quadTypeDefs: { type: AlarmRule['type']; thresholdKey: 'highHigh' | 'high' | 'low' | 'lowLow'; levelKey: 'criticalLevel' | 'highLevel' }[] = [
  { type: 'HighHigh', thresholdKey: 'highHigh', levelKey: 'criticalLevel' },
  { type: 'High', thresholdKey: 'high', levelKey: 'highLevel' },
  { type: 'Low', thresholdKey: 'low', levelKey: 'highLevel' },
  { type: 'LowLow', thresholdKey: 'lowLow', levelKey: 'criticalLevel' }
]
const quadVisible = ref(false), quadSaving = ref(false), quadLoading = ref(false), quadLoaded = ref(false), quadError = ref('')
const quadExisting = ref<AlarmRule[]>([])
const quadForm = reactive({
  tagId: 0,
  highHigh: 100,
  high: 90,
  low: 10,
  lowLow: 0,
  highLevel: 'Warning' as AlarmRule['level'],
  criticalLevel: 'Critical' as AlarmRule['level'],
  delaySec: 3,
  recoverDeadband: 0,
  enabled: true
})

const tagPicker = useAlarmRuleTagPicker({
  resolveTag: props.resolveTag,
  ensureDevices: props.ensureDevices,
  accept: () => props.canConfigureTag,
  pinnedId: () => quadForm.tagId || undefined,
  forcePin: () => false
})
const { items: quadTagOptions, loading: quadTagLoading, search: searchQuadTags, open: openQuadTags, reset: resetQuadTags } = tagPicker
function onQuadTagsOpen(open: boolean): void { openQuadTags(open) }

const quadSkippedDefs = computed(() => {
  const types = new Set(quadExisting.value.map(rule => rule.type))
  return quadTypeDefs.filter(def => types.has(def.type))
})
const quadRows = computed<QuadRow[]>(() => {
  const types = new Set(quadExisting.value.map(rule => rule.type))
  return quadTypeDefs.filter(def => !types.has(def.type)).map(def => ({
    type: def.type,
    label: alarmTypeLabels[def.type],
    threshold: quadForm[def.thresholdKey],
    level: quadForm[def.levelKey]
  }))
})
const quadSkipNotice = computed(() => {
  const labels = quadSkippedDefs.value.map(def => alarmTypeLabels[def.type])
  return labels.length ? tr('alarmRulesView.kffb30c', [labels.length, labels.join('、')]) : ''
})
const quadOrderError = computed(() => {
  const { highHigh, high, low, lowLow } = quadForm
  if ([highHigh, high, low, lowLow].some(value => value == null || !Number.isFinite(value))) return tr('alarmRulesView.kd28e9b')
  if (highHigh <= high) return tr('alarmRulesView.k8cc9ce')
  if (high <= low) return tr('alarmRulesView.k4f69f6')
  if (low <= lowLow) return tr('alarmRulesView.kcb2b7f')
  return ''
})
const canSaveQuad = computed(() => quadForm.tagId > 0 && quadLoaded.value && !quadLoading.value && quadRows.value.length > 0 && !quadOrderError.value)

function roundThreshold(value: number): number { return Number(value.toFixed(3)) }

async function selectQuadTag(): Promise<void> {
  const tag = quadTagOptions.value.find(item => item.id === quadForm.tagId) ?? await props.resolveTag(quadForm.tagId)
  if (tag?.minValue != null && tag.maxValue != null && tag.maxValue > tag.minValue) {
    const span = tag.maxValue - tag.minValue
    quadForm.lowLow = roundThreshold(tag.minValue)
    quadForm.low = roundThreshold(tag.minValue + span * 0.1)
    quadForm.high = roundThreshold(tag.minValue + span * 0.9)
    quadForm.highHigh = roundThreshold(tag.maxValue)
  }
  quadError.value = ''
  void loadQuadExisting()
}

function onQuadTagPicked(value: number | undefined): void {
  quadForm.tagId = value ?? 0
  void selectQuadTag()
}

async function loadQuadExisting(): Promise<void> {
  quadExisting.value = []
  quadLoaded.value = false
  if (!quadForm.tagId) return
  quadLoading.value = true
  try {
    quadExisting.value = await ruleApi.list(quadForm.tagId)
    quadLoaded.value = true
    quadError.value = ''
  } catch (error) {
    quadError.value = errorMessage(error, tr('alarmRulesView.kc2def9'))
  } finally {
    quadLoading.value = false
  }
}

async function open(preferredTagId?: number): Promise<void> {
  quadError.value = ''
  quadExisting.value = []
  quadLoaded.value = false
  await props.ensureDevices()
  const tag = preferredTagId ? await props.resolveTag(preferredTagId) : undefined
  quadForm.tagId = tag && props.canConfigureTag(tag) ? tag.id : 0
  resetQuadTags(quadForm.tagId && tag ? [tag] : [])
  quadForm.highHigh = 100
  quadForm.high = 90
  quadForm.low = 10
  quadForm.lowLow = 0
  quadForm.highLevel = 'Warning'
  quadForm.criticalLevel = 'Critical'
  quadForm.delaySec = 3
  quadForm.recoverDeadband = 0
  quadForm.enabled = true
  // 先按量程回填阈值再打开弹窗：守卫的基线在 visible 变 true 时快照，
  // 顺序反过来会把「自动回填」误判成用户改动，一开窗就提示丢弃。
  if (quadForm.tagId) await selectQuadTag()
  quadVisible.value = true
}

async function saveQuad(): Promise<void> {
  if (!canSaveQuad.value || quadSaving.value) return
  const rows = quadRows.value.slice()
  quadSaving.value = true
  quadError.value = ''
  try {
    const report = await runBatch(rows, row => `${row.label} ${row.threshold}`, async row => {
      await ruleApi.create({
        ...emptyAlarmRuleInput(),
        // 四限生成由操作员逐条指定等级；默认的自动优先级会让服务端把 Level 重算成同一个值。
        autoPriority: false,
        tagId: quadForm.tagId,
        type: row.type,
        threshold: row.threshold!,
        level: row.level,
        delaySec: quadForm.delaySec,
        recoverDeadband: quadForm.recoverDeadband,
        enabled: quadForm.enabled
      })
    })
    if (report.failed.length === 0) {
      rememberNumber('rule-tag', quadForm.tagId)
      quadVisible.value = false
      ElMessage.success(tr('alarmRulesView.kf8f201', [report.ok]))
      emit('saved')
    } else {
      quadError.value = summarizeBatch(report, tr('alarmRulesView.k84b6d1'))
      emit('saved')
      await loadQuadExisting()
    }
  } finally {
    quadSaving.value = false
  }
}

// 四元组一次改四条阈值，改到一半按 Esc 会全丢；与单条规则编辑弹窗用同一套守卫
const quadGuard = useEditDialog({
  visible: quadVisible,
  saving: quadSaving,
  snapshot: () => JSON.stringify(quadForm),
  save: saveQuad
})

defineExpose({ open })
</script>

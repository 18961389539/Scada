<template>
  <el-dialog v-model="visible" :title="`${$t('simulation.title')} · ${device?.name ?? ''}`" width="980px" data-test="simulation-dialog" @opened="start" @closed="stop">
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon style="margin-bottom: 12px" />
    <el-alert :title="$t('simulation.warning')" type="warning" :closable="false" show-icon style="margin-bottom: 12px" />

    <div class="sim-meta">
      <span>{{ $t('simulation.waveform') }}：{{ waveformLabel }}</span>
      <span>{{ $t('simulation.period') }}：{{ snapshot?.periodSec ?? '-' }}</span>
      <span>{{ $t('simulation.range') }}：{{ rangeLabel }}</span>
      <span>{{ $t('simulation.fault') }}：{{ faultLabel }}</span>
    </div>

    <div class="sim-toolbar">
      <el-select v-model="faultKind" style="width: 150px" data-test="simulation-fault-kind">
        <el-option v-for="kind in faultKinds" :key="kind.value" :label="kind.label" :value="kind.value" />
      </el-select>
      <el-input-number v-model="faultDuration" :min="1" :max="3600" :step="10" style="width: 130px" />
      <el-button type="warning" :loading="busy" data-test="simulation-fault-inject" @click="injectFault">{{ $t('simulation.inject') }}</el-button>
      <el-button :loading="busy" @click="clearFault">{{ $t('simulation.clearFault') }}</el-button>
      <el-button :loading="loading" @click="load">{{ $t('simulation.refresh') }}</el-button>
      <el-button :loading="busy" @click="releaseAll">{{ $t('simulation.releaseAll') }}</el-button>
    </div>

    <el-table :data="items" size="small" height="360" data-test="simulation-table" :empty-text="$t('simulation.noTags')">
      <el-table-column prop="name" :label="$t('simulation.tag')" min-width="160" show-overflow-tooltip />
      <el-table-column prop="dataType" :label="$t('simulation.type')" width="100" show-overflow-tooltip />
      <el-table-column :label="$t('simulation.current')" width="120">
        <template #default="{ row }">{{ formatValue(row.value) }}</template>
      </el-table-column>
      <el-table-column :label="$t('simulation.quality')" width="100">
        <template #default="{ row }">
          <el-tag :type="row.quality === 'Good' ? undefined : 'danger'" size="small" :class="{ 'tag-quiet': row.quality === 'Good' }">{{ row.quality }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="$t('simulation.force')" min-width="290">
        <template #default="{ row }">
          <div class="sim-force">
            <el-input-number v-model="row.forceValue" :controls="false" size="small" style="width: 120px" data-test="simulation-force-value" />
            <el-button size="small" :loading="busy" @click="force(row)">{{ $t('simulation.force') }}</el-button>
            <el-button v-if="row.forced" size="small" text @click="release(row)">{{ $t('simulation.release') }}</el-button>
          </div>
        </template>
      </el-table-column>
      <el-table-column v-if="hasError" :label="$t('simulation.error')" min-width="140" show-overflow-tooltip>
        <template #default="{ row }"><span class="tiny muted">{{ row.error }}</span></template>
      </el-table-column>
    </el-table>

    <p class="tiny muted">{{ $t('simulation.forceHint') }}</p>
    <p class="tiny muted">{{ $t('simulation.faultHint') }}</p>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import type { Device, SimulationTagView, SimulationView } from '../types'
import { simulationApi } from '../api'
import { tr } from '../i18n'
import { formatValue } from '../utils'

const props = defineProps<{ modelValue: boolean; device: Device | null }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

const visible = computed({
  get: () => props.modelValue,
  set: (value: boolean) => emit('update:modelValue', value)
})
const deviceId = computed(() => props.device?.id ?? 0)
const snapshot = ref<SimulationView | null>(null)
const items = ref<(SimulationTagView & { forceValue: number })[]>([])
const loading = ref(false)
const busy = ref(false)
const error = ref('')
const faultKind = ref('None')
const faultDuration = ref(60)
let timer: number | undefined

const faultKinds = computed(() => [
  { value: 'None', label: tr('simulation.none') },
  { value: 'CommFailure', label: tr('simulation.commFailure') },
  { value: 'BadQuality', label: tr('simulation.badQuality') },
  { value: 'Frozen', label: tr('simulation.frozen') }
])
const waveformLabel = computed(() => {
  const key = snapshot.value?.waveform
  const map: Record<string, string> = { Sine: tr('simulation.sine'), Square: tr('simulation.square'), Triangle: tr('simulation.triangle'), Counter: tr('simulation.counter'), Random: tr('simulation.random'), Constant: tr('simulation.constant') }
  return key ? (map[key] ?? key) : '-'
})
const faultLabel = computed(() => {
  const key = snapshot.value?.fault ?? 'None'
  return faultKinds.value.find(item => item.value === key)?.label ?? key
})
const rangeLabel = computed(() => {
  if (!snapshot.value) return '-'
  return snapshot.value.min == null || snapshot.value.max == null ? '0 ~ 100' : `${formatValue(snapshot.value.min)} ~ ${formatValue(snapshot.value.max)}`
})
const hasError = computed(() => items.value.some(item => item.error))

watch(() => props.modelValue, value => { if (!value) stop() })

function start(): void {
  load()
  stop()
  timer = window.setInterval(load, 2000)
}

function stop(): void {
  if (timer !== undefined) window.clearInterval(timer)
  timer = undefined
}

async function load(): Promise<void> {
  if (!deviceId.value || loading.value) return
  loading.value = true
  error.value = ''
  try {
    const data = await simulationApi.get(deviceId.value)
    const previous = new Map(items.value.map(item => [item.tagId, item.forceValue]))
    snapshot.value = data
    items.value = data.items.map(item => ({ ...item, forceValue: previous.get(item.tagId) ?? item.value }))
  } catch (ex) {
    error.value = (ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? tr('simulation.loadFailed')
  } finally {
    loading.value = false
  }
}

async function force(row: SimulationTagView & { forceValue: number }): Promise<void> {
  busy.value = true
  try {
    await simulationApi.force(deviceId.value, row.tagId, row.forceValue)
    await load()
  } catch (ex) {
    ElMessage.error((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? tr('simulation.loadFailed'))
  } finally { busy.value = false }
}

async function release(row: SimulationTagView): Promise<void> {
  busy.value = true
  try {
    await simulationApi.release(deviceId.value, row.tagId)
    await load()
  } catch (ex) {
    ElMessage.error((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? tr('simulation.loadFailed'))
  } finally { busy.value = false }
}

async function releaseAll(): Promise<void> {
  busy.value = true
  try {
    const result = await simulationApi.release(deviceId.value) as { released: number }
    ElMessage.success(tr('simulation.released', [String(result.released)]))
    await load()
  } catch (ex) {
    ElMessage.error((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? tr('simulation.loadFailed'))
  } finally { busy.value = false }
}

async function injectFault(): Promise<void> {
  busy.value = true
  try {
    await simulationApi.fault(deviceId.value, faultKind.value, faultDuration.value)
    ElMessage.success(tr('simulation.injected'))
    await load()
  } catch (ex) {
    ElMessage.error((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? tr('simulation.loadFailed'))
  } finally { busy.value = false }
}

async function clearFault(): Promise<void> {
  busy.value = true
  try {
    await simulationApi.clearFault(deviceId.value)
    ElMessage.success(tr('simulation.cleared'))
    await load()
  } catch (ex) {
    ElMessage.error((ex as { response?: { data?: { message?: string } } })?.response?.data?.message ?? tr('simulation.loadFailed'))
  } finally { busy.value = false }
}
</script>

<style scoped>
.sim-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 16px;
  margin-bottom: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.sim-toolbar {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 10px;
  align-items: center;
}
.sim-force {
  display: flex;
  gap: 6px;
  align-items: center;
}
</style>

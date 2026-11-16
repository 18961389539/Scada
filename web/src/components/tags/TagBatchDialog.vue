<template>
  <el-dialog v-model="visible" :title="$t('tagsView.k5342be')" width="760px" data-test="tag-batch-dialog" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" :before-close="guard.beforeClose" @keydown="guard.onKey">
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="tag-batch-error" />
    <el-form :disabled="saving" label-width="90px" @submit.prevent="save">
      <el-row :gutter="12">
        <el-col :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.k01f2c1')" required>
            <el-select v-model="form.deviceId" data-test="tag-batch-device" style="width: 100%" @change="selectDevice">
              <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.ke736e9')">
            <el-select v-model="form.area" data-test="tag-batch-area" style="width: 100%" @change="syncWritableByArea">
              <el-option v-for="(label, value) in areas" :key="value" :label="label" :value="value" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="8">
          <el-form-item :label="$t('tagsView.kae79aa')">
            <el-input-number v-model="form.startAddress" :min="0" :max="65535" :controls="false" style="width: 100%" data-test="tag-batch-start" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="8">
          <el-form-item :label="$t('tagsView.kb9ae89')">
            <el-input-number v-model="form.count" :min="1" :max="500" style="width: 100%" data-test="tag-batch-count" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="8">
          <el-form-item :label="$t('tagsView.kac69f5')">
            <el-input-number v-model="form.step" :min="1" :max="1000" style="width: 100%" data-test="tag-batch-step" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="8">
          <el-form-item :label="$t('tagsView.kdd4f17')">
            <el-input v-model="form.prefix" maxlength="40" data-test="tag-batch-prefix" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="8">
          <el-form-item :label="$t('tagsView.kf1eef0')">
            <el-input-number v-model="form.startIndex" :min="1" :max="999999" style="width: 100%" data-test="tag-batch-index" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="8">
          <el-form-item :label="$t('tagsView.k1e078c')">
            <el-input-number v-model="form.pad" :min="0" :max="6" style="width: 100%" data-test="tag-batch-pad" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.kb031dc')">
            <el-select v-model="form.dataType" data-test="tag-batch-data-type" style="width: 100%">
              <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col v-if="form.dataType !== 'Bool'" :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.k8ec8c8')">
            <el-input v-model="form.unit" maxlength="32" placeholder="℃ / MPa" />
          </el-form-item>
        </el-col>
        <el-col v-if="isS7 && form.area === 'DataBlock'" :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.kaf82d3')">
            <el-input-number v-model="form.dbNumber" :min="1" :max="65535" style="width: 100%" data-test="tag-batch-db" />
          </el-form-item>
        </el-col>
        <el-col :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.ka4edb9')">
            <el-select v-model="form.archiveMode" data-test="tag-batch-archive" style="width: 100%">
              <el-option v-for="(label, value) in archiveModeLabels" :key="value" :label="label" :value="value" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col v-if="!readOnlyArea" :xs="24" :sm="12">
          <el-form-item :label="$t('tagsView.k26ae2c')">
            <el-switch v-model="form.writable" data-test="tag-batch-writable" />
          </el-form-item>
        </el-col>
      </el-row>
    </el-form>
    <el-alert v-if="addressOverflow" :title="$t('tagsView.ke6c5b3')" type="warning" :closable="false" show-icon style="margin-bottom: 8px" />
    <el-alert v-if="nameError" :title="nameError" type="warning" :closable="false" show-icon style="margin-bottom: 8px" />
    <p class="tiny muted">{{ $t('tagsView.k7ae149', [rows.length]) }}</p>
    <el-table :data="rows" size="small" max-height="260" data-test="tag-batch-preview">
      <el-table-column type="index" label="#" width="50" />
      <el-table-column :label="$t('tagsView.k1be7ae')" min-width="220">
        <template #default="{ row }"><el-input v-model="row.name" size="small" maxlength="100" :data-test="`tag-batch-name-${row.address}`" /></template>
      </el-table-column>
      <el-table-column :label="$t('tagsView.k67d2d7')" width="100" show-overflow-tooltip><template #default="{ row }"><span class="mono">{{ row.address }}</span></template></el-table-column>
      <el-table-column :label="$t('tagsView.k17fc93')" width="120" show-overflow-tooltip><template #default>{{ areaLabels[form.area] }}</template></el-table-column>
      <el-table-column :label="$t('tagsView.kb031dc')" width="100" show-overflow-tooltip><template #default>{{ dataTypeLabels[form.dataType] }}</template></el-table-column>
    </el-table>
    <template #footer>
      <el-button :disabled="saving" @click="guard.close">{{ $t('tagsView.k4d0b46') }}</el-button>
      <el-button type="primary" :loading="saving" :disabled="!canSave" data-test="tag-batch-save" @click="save">{{ $t('tagsView.k8c0eab', [rows.length]) }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { tagApi } from '../../api'
import { serverFormErrors } from '../../composables/formErrors'
import type { Device, TagInput } from '../../types'
import { areaLabels, archiveModeLabels, dataTypeLabels, protocolAreas } from '../../utils'
import { tr } from '../../i18n'
import { readLastNumber, readLastText, rememberNumber, rememberText } from '../../composables/lastChoice'
import { useEditDialog } from '../../composables/useEditDialog'
import { emptyTagInput } from './tagInput'

const props = defineProps<{ devices: Device[]; initialDeviceId?: number | null }>()
const emit = defineEmits<{ saved: [deviceId: number, created: number] }>()

const visible = ref(false)
const saving = ref(false)
const error = ref('')
const form = reactive({
  deviceId: 0,
  area: 'HoldingRegister' as TagInput['area'],
  startAddress: 0,
  count: 10,
  step: 1,
  dataType: 'Float' as TagInput['dataType'],
  prefix: '',
  startIndex: 1,
  pad: 0,
  unit: '',
  archiveMode: 'OnChange' as TagInput['archiveMode'],
  writable: false,
  dbNumber: 1
})
const rows = ref<{ name: string; address: number }[]>([])

// 批量建点是一屏多字段 + 可逐行改名的表单，误按 Esc 丢掉整批配置代价很高
const guard = useEditDialog({
  visible,
  saving,
  snapshot: () => JSON.stringify({ form, names: rows.value.map(row => row.name) }),
  save
})

const device = computed(() => props.devices.find(item => item.id === form.deviceId))
const isS7 = computed(() => device.value?.protocol === 'SiemensS7')
const areas = computed(() => protocolAreas(device.value?.protocol))
const addressOverflow = computed(() => rows.value.some(row => row.address > 65535))
const nameError = computed(() => {
  const names = rows.value.map(row => row.name.trim())
  if (names.some(name => !name)) return tr('tagsView.k8a830b')
  if (names.some(name => name.length > 100)) return tr('tagsView.kd010b6')
  if (new Set(names).size !== names.length) return tr('tagsView.kb92888')
  return ''
})
const canSave = computed(() => rows.value.length > 0 && !addressOverflow.value && !nameError.value && form.deviceId > 0)
// 只读区域（离散输入、输入寄存器、S7 输入）不允许设置可写，隐藏开关并在保存时强制为否。
const readOnlyAreas = ['DiscreteInput', 'InputRegister', 'Input']
const readOnlyArea = computed(() => readOnlyAreas.includes(form.area))

function generateRows(): void {
  const count = Math.max(1, Math.min(500, Math.floor(Number(form.count) || 0)))
  const generated: { name: string; address: number }[] = []
  for (let i = 0; i < count; i++) {
    const sequence = form.startIndex + i
    const suffix = form.pad > 0 ? String(sequence).padStart(form.pad, '0') : String(sequence)
    generated.push({ name: `${form.prefix}${suffix}`, address: form.startAddress + i * form.step })
  }
  rows.value = generated
}

function selectDevice(): void {
  form.area = (Object.keys(protocolAreas(device.value?.protocol))[0] ?? 'HoldingRegister') as TagInput['area']
  syncWritableByArea()
  form.startAddress = 0
  error.value = ''
  generateRows()
}

function syncWritableByArea(): void { if (readOnlyArea.value) form.writable = false }

function open(): void {
  error.value = ''
  const rememberedDevice = readLastNumber('tag-batch-device')
  const initial = props.initialDeviceId
  form.deviceId = (initial && props.devices.some(item => item.id === initial)
    ? initial
    : rememberedDevice != null && props.devices.some(item => item.id === rememberedDevice) ? rememberedDevice : props.devices[0]?.id) ?? 0
  form.count = 10
  form.step = 1
  form.dataType = 'Float'
  form.prefix = readLastText('tag-batch-prefix')
  form.startIndex = 1
  form.pad = 0
  form.unit = ''
  form.archiveMode = 'OnChange'
  form.writable = false
  form.dbNumber = 1
  selectDevice()
  visible.value = true
}

async function save(): Promise<void> {
  if (!canSave.value || saving.value) return
  saving.value = true
  error.value = ''
  try {
    const base = emptyTagInput()
    const payload = rows.value.map(row => ({
      ...base,
      deviceId: form.deviceId,
      name: row.name.trim(),
      address: row.address,
      area: form.area,
      dbNumber: isS7.value && form.area === 'DataBlock' ? form.dbNumber : 1,
      dataType: form.dataType,
      textLength: form.dataType === 'String' ? 32 : 0,
      unit: form.unit,
      writable: readOnlyArea.value ? false : form.writable,
      archiveMode: form.archiveMode
    }))
    const result = await tagApi.batchCreate(form.deviceId, payload)
    rememberNumber('tag-batch-device', form.deviceId)
    rememberText('tag-batch-prefix', form.prefix)
    visible.value = false
    emit('saved', form.deviceId, result.created)
  } catch (reason) {
    const mapped = serverFormErrors(reason, tr('tagsView.kb16b27'))
    const rowIssues = mapped.issues.filter(issue => issue.row != null && issue.message)
    error.value = rowIssues.length > 0
      ? rowIssues.slice(0, 5).map(issue => tr('tagsUx.batchRowError', [issue.row!, issue.message!])).join('；')
      : mapped.message
  } finally {
    saving.value = false
  }
}

watch(() => [form.startAddress, form.count, form.step, form.prefix, form.startIndex, form.pad], generateRows)

defineExpose({ open })
</script>

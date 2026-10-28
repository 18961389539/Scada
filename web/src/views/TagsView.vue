<template>
  <div class="page page--fill">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterDevice" clearable filterable :placeholder="$t('tagsView.kfb9b2c')" :aria-label="$t('tagsView.kfb9b2c')" style="width: 180px" data-test="tag-filter-device">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable :placeholder="$t('tagsView.k06b76d')" :aria-label="$t('tagsView.k06b76d')" style="width: 220px" @keydown.enter="onSearchKey" @clear="load" />
        <el-button :icon="Search" :loading="loading" @click="load">{{ $t('tagsView.k711363') }}</el-button>
        <el-button v-if="filterDevice !== undefined || search" link type="primary" data-test="tag-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <el-button link type="primary" @click="router.push('/config/calculations')">{{ $t('tagsView.k4e0387') }}</el-button>
        <span class="spacer" />
        <template v-if="selected.length">
          <el-text data-test="tag-batch-count">{{ $t('tagsView.k805f4f', [selected.length]) }}</el-text>
          <el-dropdown trigger="click" @command="batchArchive">
            <el-button data-test="tag-batch-archive">{{ $t('tagsView.k66f217') }}</el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item v-for="(label, value) in archiveModeLabels" :key="value" :command="value">{{ label }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
          <el-button type="danger" plain data-test="tag-batch-delete" :loading="batching" @click="batchDelete">{{ $t('tagsView.k362aed') }}</el-button>
        </template>
        <el-button v-if="controllableDevices.length" :icon="View" data-test="tag-preview" @click="openPreview">{{ $t('tagsView.k7f76da') }}</el-button>
        <el-button :icon="Download" @click="exportTags">{{ $t('tagsView.kc7f85b') }}</el-button>
        <el-upload v-if="canImport" :show-file-list="false" :before-upload="importTags" accept=".xlsx">
          <el-button :icon="Upload" data-test="tag-import">{{ $t('tagsView.k7d6dc8') }}</el-button>
        </el-upload>
        <el-button type="primary" :icon="Plus" data-test="tag-create" @click="openCreate">{{ $t('tagsView.k573b8c') }}</el-button>
      </div>
      <div class="engineering-summary">
        <p v-if="!loading || tags.length" class="catalog-result muted" role="status" data-test="tag-result-count">{{ $t('listFilters.loadedCount', [tags.length]) }}</p>
        <el-switch v-model="showEngineering" :active-text="$t('ux.tables.engineering')" data-test="tag-engineering-columns" />
      </div>
      <el-alert v-if="batchProgress" :title="batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" data-test="tag-batch-progress" />
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="tags-load-error" />
      <el-alert v-if="devicesError" :title="devicesError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="tags-devices-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="tags-action-error" />

      <el-table
        v-loading="loading"
        :data="pagedTags"
        size="small"
        height="100%"
        :empty-text="loadError && !tags.length ? loadError : $t('tagsView.k1b4bde')"
        row-key="id"
        class="engineering-table"
        data-test="tags-table"
        @selection-change="onSelect"
      >
        <template #empty>
          <span v-if="loadError">{{ loadError }}</span>
          <template v-else-if="filterDevice !== undefined || search.trim()">
            <span>{{ $t('listFilters.noMatches') }}</span>
            <el-button link type="primary" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
          </template>
          <span v-else>{{ $t('tagsView.k1b4bde') }}</span>
        </template>
        <el-table-column type="selection" width="48" :selectable="canSelect" />
        <el-table-column v-if="showEngineering" prop="id" label="ID" width="52" />
        <el-table-column :label="$t('tagsView.k01f2c1')" width="130">
          <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
        </el-table-column>
        <el-table-column prop="name" :label="$t('tagsView.kd70b6b')" min-width="180">
          <template #default="{ row }">
            <div>{{ row.name }}</div>
            <router-link v-if="canConfigureTag(row)" :to="{ path: '/config/rules', query: { tagId: String(row.id) } }" class="tiny" data-test="tag-rules">{{ $t('tagsView.viewRules') }}</router-link>
          </template>
        </el-table-column>
        <el-table-column v-if="showEngineering" prop="description" :label="$t('tagsView.k412f54')" min-width="140" show-overflow-tooltip />
        <el-table-column v-if="showEngineering" :label="$t('tagsView.k17fc93')" width="90">
          <template #default="{ row }">{{ row.kind === 'Calculated' ? $t('tagsView.k513c1c') : row.kind === 'Derived' ? $t('tagsView.k8efdcf') : areaLabels[row.area] }}</template>
        </el-table-column>
        <el-table-column :label="$t('tagsView.k67d2d7')" min-width="150" show-overflow-tooltip><template #default="{ row }">{{ addressLabel(row) }}<div class="tiny muted">{{ row.kind === 'Calculated' ? $t('tagsView.k513c1c') : row.kind === 'Derived' ? $t('tagsView.k8efdcf') : areaLabels[row.area] }}</div></template></el-table-column>
        <el-table-column :label="$t('tagsView.ke4e46c')" width="80">
          <template #default="{ row }">{{ dataTypeLabels[row.dataType] }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" :label="$t('tagsView.k93c2cc')" width="100">
          <template #default="{ row }">{{ row.scale }} / {{ row.offset }}</template>
        </el-table-column>
        <el-table-column prop="unit" :label="$t('tagsView.k8ec8c8')" width="70" />
        <el-table-column :label="$t('tagsView.k26ae2c')" width="56">
          <template #default="{ row }">{{ row.writable ? $t('tagsView.k30160a') : $t('tagsView.k8bf5c1') }}</template>
        </el-table-column>
        <el-table-column :label="$t('tagsView.k091ca5')" width="90">
          <template #default="{ row }">{{ archiveModeLabels[row.archiveMode] }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" prop="deadband" :label="$t('tagsView.k687c0b')" width="60" />
        <el-table-column :label="$t('tagsView.kf3ea6d')" width="160" fixed="right">
          <template #default="{ row }"><div class="engineering-actions">
            <el-button link type="primary" v-if="canConfigureTag(row)" @click="openEdit(row)">{{ row.kind === 'Calculated' ? $t('tagsView.kc4f4b2') : row.kind === 'Derived' ? $t('tagsView.k3725a1') : $t('tagsView.ka7f814') }}</el-button>
            <el-dropdown v-if="row.kind === 'Physical' && canConfigureTag(row)" trigger="click">
              <el-button link data-test="tag-more">{{ $t('ux.tables.more') }}</el-button>
              <template #dropdown><el-dropdown-menu>
                <el-dropdown-item data-test="tag-copy" @click="openCopy(row)">{{ $t('tagsView.k4edd1d') }}</el-dropdown-item>
                <el-dropdown-item divided data-test="tag-delete" @click="remove(row)">{{ $t('tagsView.k3755f5') }}</el-dropdown-item>
              </el-dropdown-menu></template>
            </el-dropdown>
          </div></template>
        </el-table-column>
      </el-table>
      <el-pagination :current-page="page" :page-size="100" :total="tags.length" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="tag-pagination" @update:current-page="page = $event" @current-change="selected = []" />
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? $t('tagsView.k4521a6') : (copying ? $t('tagsView.k40056b') : $t('tagsView.k573b8c'))" width="720px" data-test="tag-dialog" :before-close="editDialog.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" @keydown="editDialog.onKey" @opened="onDialogOpened">
      <el-alert
        v-if="!editingId"
        type="info"
        :closable="false"
        show-icon
        data-test="tag-defaults-tip"
        :title="$t('tagsView.k90e866')"
        style="margin-bottom: 12px"
      />
      <el-alert v-if="fieldErrors._form" :title="fieldErrors._form" type="error" :closable="false" show-icon tabindex="-1" style="margin-bottom: 12px" data-test="tag-form-error" />
      <el-form ref="formRef" :model="form" :rules="rules" :disabled="saving" label-width="110px" :label-position="isMobile ? 'top' : 'right'" class="tag-form" status-icon @submit.prevent="save">
        <el-row :gutter="12">
          <el-col :xs="24" :sm="12">
            <el-form-item :label="$t('tagsView.k6bb18f')" prop="deviceId" :error="fieldErrors.deviceId">
              <el-select v-model="form.deviceId" style="width: 100%" data-test="tag-device" @change="selectFormDevice">
                <el-option v-for="device in configurableDevices" :key="device.id" :label="device.name" :value="device.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12">
            <el-form-item :label="$t('tagsView.kd70b6b')" prop="name" :error="fieldErrors.name">
              <el-input v-model="form.name" data-test="tag-name" maxlength="100" show-word-limit />
            </el-form-item>
          </el-col>
          <el-col :span="24">
            <el-form-item :label="$t('tagsView.k412f54')" prop="description">
              <el-input v-model="form.description" maxlength="500" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k17fc93')" prop="area">
              <el-select v-model="form.area" style="width: 100%" data-test="tag-area">
                <el-option v-for="(label, value) in formAreas" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col v-if="formIsS7 && form.area === 'DataBlock'" :xs="24" :sm="12" :md="8"><el-form-item :label="$t('tagsView.kaf82d3')"><el-input-number v-model="form.dbNumber" :min="1" :max="65535" style="width: 100%" /></el-form-item></el-col>
          <el-col v-if="formIsS7 && form.dataType === 'Bool'" :xs="24" :sm="12" :md="8"><el-form-item :label="$t('tagsView.k08ca6d')"><el-input-number v-model="form.bitOffset" :min="0" :max="7" style="width: 100%" /></el-form-item></el-col>
          <el-col v-if="formIsS7" :span="24"><el-text type="info">{{ $t('tagsView.kb5c05c') }}</el-text></el-col>
          <el-col v-if="!formIsOpc && !formText" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k67d2d7')" prop="address" :error="fieldErrors.address">
              <el-input-number v-model="form.address" :min="0" :max="65535" style="width: 100%" data-test="tag-address" />
            </el-form-item>
          </el-col>
          <el-col v-if="formIsOpc" :span="24"><el-form-item label="NodeId" prop="nodeId" :error="fieldErrors.nodeId" required><el-input v-model="form.nodeId" data-test="tag-node-id" placeholder="nsu=urn:plant;s=Boiler.Temperature" /></el-form-item></el-col>
          <el-col v-if="formText" :span="24"><el-form-item :label="$t('tagsView.k67d2d7')" prop="nodeId" :error="fieldErrors.nodeId" required><el-input v-model="form.nodeId" data-test="tag-text-address" :placeholder="formText.addressPlaceholder" /><el-text type="info" size="small">{{ formText.addressHelp }}</el-text></el-form-item></el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.kb031dc')" prop="dataType">
              <el-select v-model="form.dataType" style="width: 100%" data-test="tag-data-type">
                <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k985c8c')">
              <el-input-number v-model="form.scale" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.ka87924')">
              <el-input-number v-model="form.offset" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k8ec8c8')" prop="unit" :error="fieldErrors.unit">
              <el-input v-model="form.unit" placeholder="℃ / MPa" maxlength="32" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k30a088')" prop="archiveMode">
              <el-select v-model="form.archiveMode" style="width: 100%" data-test="tag-archive-mode">
                <el-option v-for="(label, value) in archiveModeLabels" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k687c0b')">
              <el-input-number v-model="form.deadband" :min="0" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.ke4c7e2')" prop="archivePeriodSec" :error="fieldErrors.archivePeriodSec">
              <el-input-number v-model="form.archivePeriodSec" :min="1" style="width: 100%" data-test="tag-archive-period" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.kc3d641')" :error="fieldErrors.minValue">
              <el-input-number v-model="form.minValue" :controls="false" style="width: 100%" :placeholder="$t('tagsView.k09c4fc')" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k018219')">
              <el-input-number v-model="form.maxValue" :controls="false" style="width: 100%" :placeholder="$t('tagsView.k09c4fc')" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.kc3dd2c')">
              <el-switch v-model="form.writable" data-test="tag-writable" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <span class="edit-dialog-shortcut">{{ $t('editDialog.shortcut', [editDialog.modKey]) }}</span>
        <el-button :disabled="saving" @click="editDialog.close">{{ $t('tagsView.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="tag-save" :loading="saving" :disabled="!canSave" @click="save">{{ $t('tagsView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="previewVisible" :title="$t('tagsView.kdea846')" width="480px">
      <el-alert type="info" :closable="false" show-icon :title="$t('tagsView.kd7595e')" style="margin-bottom: 12px" />
      <el-alert v-if="previewError" :title="previewError" type="error" :closable="false" show-icon style="margin-bottom: 12px" data-test="tag-preview-error" />
      <el-form label-width="100px">
        <el-form-item :label="$t('tagsView.k01f2c1')">
          <el-select v-model="previewDevice" data-test="tag-preview-device" style="width: 100%" @change="selectPreviewDevice">
            <el-option v-for="device in controllableDevices" :key="device.id" :label="device.name" :value="device.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('tagsView.k17fc93')">
          <el-select v-model="previewForm.area" style="width: 100%">
            <el-option v-for="(label, value) in previewAreas" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="!previewIsOpc && !previewText" :label="$t('tagsView.k67d2d7')">
          <el-input-number v-model="previewForm.address" :min="0" :max="65535" style="width: 100%" />
        </el-form-item>
        <el-form-item v-if="previewIsOpc" label="NodeId"><el-input v-model="previewForm.nodeId" /></el-form-item>
        <el-form-item v-if="previewText" :label="$t('tagsView.k67d2d7')"><el-input v-model="previewForm.nodeId" :placeholder="previewText.addressPlaceholder" /></el-form-item>
        <el-form-item :label="$t('tagsView.kb031dc')">
          <el-select v-model="previewForm.dataType" style="width: 100%">
            <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="previewIsS7 && previewForm.area === 'DataBlock'" :label="$t('tagsView.kaf82d3')"><el-input-number v-model="previewForm.dbNumber" :min="1" :max="65535" /></el-form-item>
        <el-form-item v-if="previewIsS7 && previewForm.dataType === 'Bool'" :label="$t('tagsView.k08ca6d')"><el-input-number v-model="previewForm.bitOffset" :min="0" :max="7" /></el-form-item>
      </el-form>
      <el-alert
        v-if="previewResult"
        :type="previewResult.connected ? 'success' : 'error'"
        :closable="false"
        show-icon
        :title="previewResult.connected ? $t('tagsView.k2d7a00', [formatValue(previewResult.value)]) : previewResult.message"
      />
      <template #footer>
        <el-button @click="previewVisible = false">{{ $t('tagsView.k6c14bd') }}</el-button>
        <el-button type="primary" :loading="previewing" @click="runPreview">{{ $t('tagsView.k591670') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { useRoute, useRouter } from 'vue-router'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import { Download, Plus, Search, Upload, View } from '@element-plus/icons-vue'
import type { UploadRawFile } from 'element-plus'
import { deviceApi, tagApi } from '../api'
import { errorMessage, errorText } from '../api/http'
import type { Device, Tag, TagInput } from '../types'
import { protocolDef } from '../protocols'
import { areaLabels, archiveModeLabels, dataTypeLabels, downloadBlob, formatValue, protocolAreas } from '../utils'
import { can, canGlobal } from '../access/policies'
import { tr } from '../i18n'
import { isMobile } from '../mobile'
import { useAuthStore } from '../stores/auth'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'
import { positiveQueryId } from '../nav/query'
import { focusFormError, useEditDialog } from '../composables/useEditDialog'

const auth = useAuthStore()
const route = useRoute()
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const deviceIndex = computed(() => new Map(devices.value.map(device => [device.id, device])))
const page = ref(1)
const pagedTags = computed(() => tags.value.slice((page.value - 1) * 100, page.value * 100))
// Excel 导入可能覆盖任意设备的测点，服务端要求全局 Configure；按钮必须按同一口径显示
const canImport = computed(() => canGlobal(auth.user, 'Configure'))
const configurableDevices = computed(() => devices.value.filter(d => can(auth.user, 'Configure', d.areaId)))
const controllableDevices = computed(() => devices.value.filter(d => can(auth.user, 'Control', d.areaId)))
function canConfigureTag(tag: Tag): boolean {
  if (tag.kind === 'Calculated') {
    if (tag.areaId != null) return can(auth.user, 'Configure', tag.areaId)
    return canGlobal(auth.user, 'Configure') || can(auth.user, 'Configure', 1)
  }
  if (tag.kind !== 'Physical') return can(auth.user, 'Configure')
  return can(auth.user, 'Configure', deviceIndex.value.get(tag.deviceId ?? 0)?.areaId)
}
const filterDevice = computed({
  get: () => positiveQueryId(route.query.deviceId),
  set: (value: number | undefined) => {
    const query = { ...route.query }
    if (value) query.deviceId = String(value)
    else delete query.deviceId
    void router.replace({ query })
  }
})
const search = ref('')
const showEngineering = ref(false)
const loading = ref(false)
const loadError = ref('')
const devicesError = ref('')
const actionError = ref('')
const previewError = ref('')
const saving = ref(false)
const batching = ref(false)
const batchProgress = ref('')
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const copying = ref(false)
const selected = ref<Tag[]>([])
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})

const emptyForm = (): TagInput => ({
  deviceId: 0,
  name: '',
  description: '',
  area: 'HoldingRegister',
  address: 0,
  dbNumber: 1, bitOffset: 0, nodeId: '',
  dataType: 'Float',
  scale: 1,
  offset: 0,
  unit: '',
  writable: false,
  archiveMode: 'OnChange',
  deadband: 0,
  archivePeriodSec: 600,
  minValue: null,
  maxValue: null
})
const form = reactive<TagInput>(emptyForm())
const router = useRouter()
const editDialog = useEditDialog({ visible: dialogVisible, saving, form: formRef, snapshot: () => JSON.stringify(form), initialField: 'name', save })

const rules = computed<FormRules>(() => ({
  deviceId: [{ required: true, type: 'number', min: 1, message: tr('tagsView.k61f337'), trigger: 'change' }],
  name: [
    { required: true, message: tr('tagsView.k7af69f'), trigger: 'blur' },
    { min: 1, max: 100, message: tr('tagsView.kb535b0'), trigger: 'blur' }
  ],
  nodeId: [{
    validator: (_r, _v, cb) => {
      if ((formIsOpc.value || formText.value) && !String(form.nodeId || '').trim()) cb(new Error(formIsOpc.value ? tr('tagsView.k2fb788') : tr('tagsView.kf7dd03')))
      else cb()
    },
    trigger: 'blur'
  }],
  archivePeriodSec: [{
    validator: (_r, v, cb) => {
      if (form.archiveMode === 'Periodic' && (!v || v < 1)) cb(new Error(tr('tagsView.k903912')))
      else cb()
    },
    trigger: 'change'
  }]
}))

const canSave = computed(() => {
  if (!form.deviceId || !String(form.name || '').trim()) return false
  if ((formIsOpc.value || formText.value) && !String(form.nodeId || '').trim()) return false
  if (form.archiveMode === 'Periodic' && form.archivePeriodSec < 1) return false
  return true
})

const previewVisible = ref(false)
const previewing = ref(false)
const previewDevice = ref<number | undefined>()
const previewForm = reactive({ area: 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
const formIsS7 = computed(() => devices.value.find(x => x.id === form.deviceId)?.protocol === 'SiemensS7')
const previewIsS7 = computed(() => devices.value.find(x => x.id === previewDevice.value)?.protocol === 'SiemensS7')
const formIsOpc = computed(() => devices.value.find(x => x.id === form.deviceId)?.protocol === 'OpcUa')
const previewIsOpc = computed(() => devices.value.find(x => x.id === previewDevice.value)?.protocol === 'OpcUa')
const formAreas = computed(() => protocolAreas(devices.value.find(x => x.id === form.deviceId)?.protocol))
const previewAreas = computed(() => protocolAreas(devices.value.find(x => x.id === previewDevice.value)?.protocol))
const formText = computed(() => protocolDef(devices.value.find(x => x.id === form.deviceId)?.protocol))
const previewText = computed(() => protocolDef(devices.value.find(x => x.id === previewDevice.value)?.protocol))
function selectFormDevice(): void { form.area = formText.value ? 'Address' : formIsOpc.value ? 'OpcNode' : formIsS7.value ? 'DataBlock' : 'HoldingRegister'; form.bitOffset = 0; clearFieldErrors(fieldErrors) }
function selectPreviewDevice(): void { previewForm.area = previewText.value ? 'Address' : previewIsOpc.value ? 'OpcNode' : previewIsS7.value ? 'DataBlock' : 'HoldingRegister'; previewForm.bitOffset = 0 }
const previewResult = ref<{ connected: boolean; value?: number; message?: string } | null>(null)

function canSelect(row: Tag): boolean { return row.kind === 'Physical' && canConfigureTag(row) }
function onSelect(rows: Tag[]): void { selected.value = rows.filter(canSelect) }

function deviceName(id: number | null): string {
  if (id === null) return tr('tagsView.k8dc940')
  return deviceIndex.value.get(id)?.name ?? `#${id}`
}
function addressLabel(tag: Tag): string {
  if (tag.kind === 'Calculated') return tag.expression
  if (tag.kind === 'Derived') return tr('tagsView.k8a818f', [tag.jobOutput ?? ''])
  if (tag.area === 'OpcNode' || tag.area === 'Address') return tag.nodeId
  if (['DataBlock', 'Memory', 'Input', 'Output'].includes(tag.area)) {
    const prefix = tag.area === 'DataBlock' ? `DB${tag.dbNumber}` : ({ Memory: 'M', Input: 'I', Output: 'Q' } as Record<string, string>)[tag.area]
    return `${prefix} · ${tag.address}${tag.dataType === 'Bool' ? `.${tag.bitOffset}` : ''}`
  }
  return String(tag.address)
}

function toInput(tag: Tag, patch: Partial<TagInput> = {}): TagInput {
  return {
    deviceId: tag.deviceId!,
    name: tag.name,
    description: tag.description ?? '',
    area: tag.area,
    address: tag.address,
    dbNumber: tag.dbNumber,
    bitOffset: tag.bitOffset,
    nodeId: tag.nodeId,
    dataType: tag.dataType,
    scale: tag.scale,
    offset: tag.offset,
    unit: tag.unit ?? '',
    writable: tag.writable,
    archiveMode: tag.archiveMode,
    deadband: tag.deadband,
    archivePeriodSec: tag.archivePeriodSec,
    minValue: tag.minValue ?? null,
    maxValue: tag.maxValue ?? null,
    ...patch
  }
}

let loadVersion = 0, disposed = false
async function load(): Promise<void> {
  if (disposed) return
  const version = ++loadVersion
  loading.value = true
  selected.value = []
  try {
    const result = await tagApi.list({
      deviceId: filterDevice.value,
      search: search.value.trim() || undefined
    })
    if (version !== loadVersion) return
    page.value = 1
    tags.value = result
    loadError.value = ''
  } catch (error) {
    if (version !== loadVersion) return
    loadError.value = errorMessage(error, tr('tagsView.kdf92e4'))
  } finally {
    if (version === loadVersion) loading.value = false
  }
}
watch(filterDevice, () => { void load() })
onBeforeUnmount(() => { disposed = true; loadVersion++ })
function onSearchKey(event: KeyboardEvent): void { if (!event.isComposing && event.keyCode !== 229) { event.preventDefault(); void load() } }
function resetFilters(): void {
  search.value = ''
  if (filterDevice.value !== undefined) filterDevice.value = undefined
  else void load()
}

function fillForm(tag: Tag, nameOverride?: string): void {
  Object.assign(form, toInput(tag, nameOverride ? { name: nameOverride } : {}))
}

function openCreate(): void {
  editingId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  Object.assign(form, emptyForm())
  form.deviceId = (filterDevice.value && configurableDevices.value.some(d => d.id === filterDevice.value) ? filterDevice.value : configurableDevices.value[0]?.id) ?? 0
  selectFormDevice()
  dialogVisible.value = true
}

function openEdit(tag: Tag): void {
  if (tag.kind === 'Calculated') { void router.push({ path: '/config/calculations', query: { tagId: String(tag.id) } }); return }
  if (tag.kind === 'Derived') { void router.push('/calc-jobs'); return }
  editingId.value = tag.id
  copying.value = false
  clearFieldErrors(fieldErrors)
  fillForm(tag)
  dialogVisible.value = true
}

function openCopy(tag: Tag): void {
  if (tag.kind !== 'Physical') return
  editingId.value = null
  copying.value = true
  clearFieldErrors(fieldErrors)
  fillForm(tag, tr('tagsView.kc0769b', [tag.name]))
  dialogVisible.value = true
}

async function onDialogOpened(): Promise<void> {
  await nextTick()
  formRef.value?.clearValidate()
  await editDialog.focusInitial()
}

async function save(): Promise<void> {
  if (!dialogVisible.value || saving.value) return
  saving.value = true
  let focusError = false
  clearFieldErrors(fieldErrors)
  try {
    const valid = await formRef.value?.validate().catch(() => false)
    if (!valid) {
      fieldErrors._form = tr('tagsView.k02668e'); focusError = true; return
    }
    if (!canSave.value) {
      fieldErrors._form = tr('tagsView.k8512c2'); focusError = true; return
    }
    const input = { ...form, bitOffset: formIsS7.value && form.dataType === 'Bool' ? form.bitOffset : 0 }
    if (editingId.value) await tagApi.update(editingId.value, input)
    else await tagApi.create(input)
    ElMessage.success(tr('tagsView.k7e68eb'))
    dialogVisible.value = false
    await load()
  } catch (error) {
    const mapped = serverFormErrors(error, tr('tagsView.k40525a'), ['name', 'deviceId', 'nodeId', 'address'])
    applyFieldErrors(fieldErrors, mapped.fields)
    fieldErrors._form = mapped.message
    focusError = true
  } finally {
    saving.value = false
    if (focusError) await focusFormError(formRef.value)
  }
}

async function remove(tag: Tag): Promise<void> {
  actionError.value = ''
  try {
    await ElMessageBox.confirm(tr('tagsView.kb70f0d'), tr('ux.tables.confirmDelete', [tag.name]), { type: 'warning', confirmButtonText: tr('tagsView.k3755f5'), cancelButtonText: tr('tagsView.k4d0b46') })
    await tagApi.remove(tag.id)
    ElMessage.success(tr('tagsView.kfb5fe1'))
    await load()
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    actionError.value = errorMessage(error, tr('tagsView.k72250c'))
  }
}

async function reportBatch(report: Awaited<ReturnType<typeof runBatch>>, okText: string): Promise<void> {
  batchProgress.value = ''
  if (report.failed.length === 0) {
    ElMessage.success(okText)
  } else {
    await ElMessageBox.alert(summarizeBatch(report, tr('tagsView.k64728a')), tr('tagsView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('tagsView.kcb63c6') })
  }
  await load()
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(tr('tagsView.kcddd80', [rows.length]), tr('tagsView.k362aed'), { type: 'warning', confirmButtonText: tr('tagsView.k3755f5'), cancelButtonText: tr('tagsView.k4d0b46') })
  } catch { return }
  batching.value = true
  const report = await runBatch(rows, (t) => `#${t.id} ${t.name}`, async (t) => { await tagApi.remove(t.id) }, (p) => {
    batchProgress.value = tr('tagsView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  })
  batching.value = false
  await reportBatch(report, tr('tagsView.k8870a2', [report.ok]))
}

async function batchArchive(mode: string): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  batching.value = true
  const report = await runBatch(rows, (t) => `#${t.id} ${t.name}`, async (t) => { await tagApi.update(t.id, toInput(t, { archiveMode: mode as TagInput['archiveMode'] })) }, (p) => {
    batchProgress.value = tr('tagsView.kcd395b', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  })
  batching.value = false
  await reportBatch(report, tr('tagsView.k10213b', [report.ok]))
}

async function exportTags(): Promise<void> {
  actionError.value = ''
  try {
    const blob = await tagApi.export()
    downloadBlob(blob, `tags_${new Date().toISOString().slice(0, 10)}.xlsx`)
  } catch (error) {
    actionError.value = await errorText(error, tr('tagsView.k675338'))
  }
}

async function importTags(file: UploadRawFile): Promise<boolean> {
  actionError.value = ''
  try {
    const result = await tagApi.import(file)
    await ElMessageBox.alert(
      tr('tagsView.kfc1164', [result.created, result.updated, result.skipped, result.errors.length ? tr('tagsView.k5a171b', [result.errors.join('；')]) : '']),
      tr('tagsView.k747404'),
      { type: result.errors.length ? 'warning' : 'success' }
    )
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('tagsView.ka01a8d'))
  }
  return false
}

function openPreview(): void {
  previewVisible.value = true
  previewError.value = ''
  previewResult.value = null
  previewDevice.value = controllableDevices.value.find(d => d.id === filterDevice.value)?.id ?? controllableDevices.value[0]?.id
  selectPreviewDevice()
}

async function runPreview(): Promise<void> {
  if (!previewDevice.value) {
    previewError.value = tr('tagsView.k61f337')
    return
  }
  previewError.value = ''

  previewing.value = true
  try {
    previewResult.value = await deviceApi.test(
      previewDevice.value,
      previewForm.area,
      previewForm.address,
      previewForm.dataType, previewForm.dbNumber, previewForm.dataType === 'Bool' ? previewForm.bitOffset : 0, previewForm.nodeId
    )
  } catch (error) {
    previewResult.value = { connected: false, message: errorMessage(error, tr('tagsView.kd9f607')) }
  } finally {
    previewing.value = false
  }
}

onMounted(async () => {
  try {
    devices.value = await deviceApi.list()
    devicesError.value = ''
  } catch (error) {
    devicesError.value = errorMessage(error, tr('tagsView.ka70156'))
  }
  await load()
})
</script>

<style scoped>
.tag-form :deep(.el-col),
.tag-form :deep(.el-form-item__content) {
  min-width: 0;
}

@media (max-width: 768px) {
  .tag-form :deep(.el-form-item__label) {
    height: auto;
    line-height: 1.5;
  }
}
</style>

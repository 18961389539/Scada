<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar page-toolbar">
        <div class="title-block">
          <h3>{{ $t('serialProfiles.title') }}</h3>
          <p class="tiny muted">{{ $t('serialProfiles.subtitle') }}</p>
        </div>
        <span class="spacer" />
        <el-input v-model="search" clearable :placeholder="$t('serialProfiles.searchPlaceholder')" style="width: 220px" data-test="serial-profile-search" />
        <el-button :icon="Refresh" :loading="loading" data-test="serial-profile-refresh" @click="refresh">{{ $t('serialProfiles.refresh') }}</el-button>
        <el-button v-if="canConfigure" type="primary" :icon="Plus" :disabled="loading" data-test="serial-profile-add" @click="openCreate">{{ $t('serialProfiles.add') }}</el-button>
      </div>

      <div class="status-row">
        <el-tag v-if="list?.serialReady" type="success" effect="plain" size="small">{{ $t('serialProfiles.workerReady') }}</el-tag>
        <el-tag v-else-if="hasActiveProfiles" type="warning" effect="plain" size="small">{{ $t('serialProfiles.workerNeedsOne', [list?.effectiveWorkerCount ?? '?']) }}</el-tag>
        <el-tag type="info" effect="plain" size="small">{{ $t('serialProfiles.localPorts', [ports.length]) }}</el-tag>
        <el-tag v-if="conflictCount" type="danger" effect="plain" size="small">{{ $t('serialProfiles.conflictCount', [conflictCount]) }}</el-tag>
      </div>

      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="serial-profile-load-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="serial-profile-action-error" />
      <el-alert v-if="list && !list.serialReady && hasActiveProfiles" :title="$t('serialProfiles.workerWarning', [list.effectiveWorkerCount])" type="warning" :closable="false" show-icon data-test="serial-profile-worker-warning" />

      <el-table :data="filtered" v-loading="loading" row-key="name" size="small" data-test="serial-profile-table">
        <el-table-column :label="$t('serialProfiles.name')" min-width="190" show-overflow-tooltip>
          <template #default="{ row }">
            <div class="name-cell">
              <strong>{{ row.name }}</strong>
              <el-tag size="small" :type="row.source === 'database' ? 'success' : 'info'" effect="plain">
                {{ row.source === 'database' ? $t('serialProfiles.sourceDatabase') : $t('serialProfiles.sourceConfig') }}
              </el-tag>
            </div>
            <div v-if="row.description" class="tiny muted">{{ row.description }}</div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('serialProfiles.port')" min-width="235" show-overflow-tooltip>
          <template #default="{ row }">
            <code class="digest">{{ row.port }}</code>
            <div class="tag-line">
              <el-tag v-if="row.conflict" size="small" type="danger" :title="row.conflict">{{ $t('serialProfiles.conflictLabel') }}</el-tag>
              <el-tag v-else-if="row.enabled && !portExists(row.port)" size="small" type="warning">{{ $t('serialProfiles.notFoundOnHost') }}</el-tag>
              <span class="tiny muted">{{ row.baudRate }}/{{ row.dataBits }}/{{ parityLabel(row.parity) }}/{{ stopLabel(row.stopBits) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column :label="$t('serialProfiles.timeouts')" min-width="135" show-overflow-tooltip>
          <template #default="{ row }">{{ row.readTimeoutMs }} ms · {{ row.maxReplyChars }}</template>
        </el-table-column>
        <el-table-column :label="$t('serialProfiles.lastUsed')" min-width="165" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="!row.lastUsedAt" class="tiny muted">{{ $t('serialProfiles.neverUsed') }}</span>
            <template v-else>
              <div class="last-call">
                <el-tag size="small" :type="row.lastOk ? 'success' : 'danger'">{{ row.lastOk ? $t('serialProfiles.usedOk') : $t('serialProfiles.usedFail') }}</el-tag>
                <span class="tiny muted">{{ formatTime(row.lastUsedAt) }} · {{ Math.round(row.lastElapsedMs ?? 0) }} ms</span>
              </div>
              <div v-if="row.lastError" class="tiny error-text" :title="row.lastError">{{ row.lastError }}</div>
            </template>
          </template>
        </el-table-column>
        <el-table-column :label="$t('serialProfiles.enabled')" width="82">
          <template #default="{ row }">
            <el-switch :model-value="row.enabled" :disabled="busy || !row.editable" size="small" data-test="serial-profile-toggle" @change="toggleEnabled(row, $event === true)" />
          </template>
        </el-table-column>
        <el-table-column v-if="canConfigure" :label="$t('serialProfiles.actions')" width="175" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :disabled="busy || !row.editable" data-test="serial-profile-edit" @click="openEdit(row)">{{ $t('serialProfiles.edit') }}</el-button>
            <el-button link :disabled="busy" data-test="serial-profile-copy" @click="openCopy(row)">{{ $t('serialProfiles.copy') }}</el-button>
            <el-button v-if="row.editable" link type="danger" :disabled="busy" data-test="serial-profile-delete" @click="remove(row)">{{ $t('serialProfiles.delete') }}</el-button>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty :description="search ? $t('serialProfiles.noMatch') : $t('serialProfiles.empty')">
            <el-button v-if="canConfigure && !search" type="primary" :icon="Plus" data-test="serial-profile-empty-add" @click="openCreate">{{ $t('serialProfiles.add') }}</el-button>
          </el-empty>
        </template>
      </el-table>
      <p v-if="list && list.items.some(item => !item.editable)" class="tiny muted readonly-hint">{{ $t('serialProfiles.readonlyHint') }}</p>
    </div>

    <el-dialog v-model="dialogVisible" class="serial-profile-dialog" top="4vh" :title="editingId ? $t('serialProfiles.editTitle') : $t('serialProfiles.addTitle')" width="760px" :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy">
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" show-icon data-test="serial-profile-save-error" />
      <el-form label-position="top" :disabled="busy" @submit.prevent="save">
        <div class="form-section">
          <div class="section-title">{{ $t('serialProfiles.sectionBasic') }}</div>
          <el-row :gutter="16">
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.name')" required>
                <el-input v-model="form.name" maxlength="64" show-word-limit data-test="serial-profile-name" :placeholder="$t('serialProfiles.namePlaceholder')" />
              </el-form-item>
            </el-col>
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.enabled')">
                <el-switch v-model="form.enabled" data-test="serial-profile-enabled" />
              </el-form-item>
            </el-col>
            <el-col :span="24">
              <el-form-item :label="$t('serialProfiles.description')">
                <el-input v-model="form.description" maxlength="500" show-word-limit data-test="serial-profile-description" />
              </el-form-item>
            </el-col>
          </el-row>
        </div>

        <div class="form-section">
          <div class="section-title">{{ $t('serialProfiles.sectionConnection') }}</div>
          <el-row :gutter="16">
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.port')" required>
                <el-select v-model="form.port" filterable allow-create default-first-option class="w-full" data-test="serial-profile-port" :placeholder="$t('serialProfiles.portPlaceholder')">
                  <el-option v-for="port in ports" :key="port" :label="port" :value="port" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.baudRate')">
                <el-input-number v-model="form.baudRate" :min="300" :max="921600" :step="100" class="w-full" data-test="serial-profile-baud" />
              </el-form-item>
            </el-col>
            <el-col :xs="12" :sm="8">
              <el-form-item :label="$t('serialProfiles.dataBits')">
                <el-select v-model="form.dataBits" class="w-full" data-test="serial-profile-databits">
                  <el-option v-for="bits in [5, 6, 7, 8]" :key="bits" :label="String(bits)" :value="bits" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :xs="12" :sm="8">
              <el-form-item :label="$t('serialProfiles.parity')">
                <el-select v-model="form.parity" class="w-full" data-test="serial-profile-parity">
                  <el-option :label="$t('serialProfiles.parityNone')" value="None" />
                  <el-option :label="$t('serialProfiles.parityOdd')" value="Odd" />
                  <el-option :label="$t('serialProfiles.parityEven')" value="Even" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :xs="12" :sm="8">
              <el-form-item :label="$t('serialProfiles.stopBits')">
                <el-select v-model="form.stopBits" class="w-full" data-test="serial-profile-stopbits">
                  <el-option label="1" value="One" />
                  <el-option label="2" value="Two" />
                </el-select>
              </el-form-item>
            </el-col>
          </el-row>
          <p v-if="form.port && !portExists(form.port)" class="tiny port-hint">{{ $t('serialProfiles.portMissing') }}</p>
        </div>

        <div class="form-section">
          <div class="section-title">{{ $t('serialProfiles.sectionReading') }}</div>
          <el-row :gutter="16">
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.encoding')">
                <el-select v-model="form.encoding" class="w-full" data-test="serial-profile-encoding">
                  <el-option label="UTF-8" value="utf-8" />
                  <el-option label="ASCII" value="ascii" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.newline')">
                <el-select v-model="form.newLine" class="w-full" data-test="serial-profile-newline">
                  <el-option label="CRLF" :value="'\r\n'" />
                  <el-option label="LF" :value="'\n'" />
                  <el-option label="CR" :value="'\r'" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.readTimeoutMs')">
                <el-input-number v-model="form.readTimeoutMs" :min="50" :max="60000" :step="250" class="w-full" data-test="serial-profile-timeout" />
              </el-form-item>
            </el-col>
            <el-col :xs="24" :sm="12">
              <el-form-item :label="$t('serialProfiles.maxReplyChars')">
                <el-input-number v-model="form.maxReplyChars" :min="1" :max="4096" class="w-full" data-test="serial-profile-maxreply" />
              </el-form-item>
            </el-col>
          </el-row>
          <el-collapse v-model="advancedOpen" class="advanced">
            <el-collapse-item :title="$t('serialProfiles.advancedSignals')" name="signals">
              <el-row :gutter="16">
                <el-col :xs="24" :sm="12">
                  <el-form-item :label="$t('serialProfiles.dtr')">
                    <el-switch v-model="form.dtrEnable" data-test="serial-profile-dtr" />
                  </el-form-item>
                </el-col>
                <el-col :xs="24" :sm="12">
                  <el-form-item :label="$t('serialProfiles.rts')">
                    <el-switch v-model="form.rtsEnable" data-test="serial-profile-rts" />
                  </el-form-item>
                </el-col>
              </el-row>
            </el-collapse-item>
          </el-collapse>
        </div>

        <div class="usage-box">
          <div class="tiny muted">{{ $t('serialProfiles.exampleTitle') }}</div>
          <div class="usage-line">
            <code>{{ scriptExample }}</code>
            <el-button link :icon="CopyDocument" data-test="serial-profile-copy-example" @click="copyExample">{{ $t('serialProfiles.copy') }}</el-button>
          </div>
          <div class="tiny muted">{{ $t('serialProfiles.exampleHint') }}</div>
        </div>
      </el-form>
      <template #footer>
        <el-button :disabled="busy" @click="dialogVisible = false">{{ $t('serialProfiles.cancel') }}</el-button>
        <el-button type="primary" :loading="busy" data-test="serial-profile-save" @click="save">{{ $t('serialProfiles.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { CopyDocument, Plus, Refresh } from '@element-plus/icons-vue'
import { serialProfileApi, type SerialProfileInfo, type SerialProfileInput, type SerialProfileList } from '../api/serialProfiles'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { canGlobal } from '../access/policies'
import { tr } from '../i18n'

const auth = useAuthStore()
const canConfigure = computed(() => canGlobal(auth.user, 'Configure'))

const list = ref<SerialProfileList | null>(null)
const ports = ref<string[]>([])
const portSet = computed(() => new Set(ports.value.map(port => port.trim().toLowerCase())))
const search = ref('')
const loading = ref(false)
const busy = ref(false)
const loadError = ref('')
const actionError = ref('')
const formError = ref('')
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const advancedOpen = ref<string[]>([])

const emptyForm = (): SerialProfileInput => ({
  name: '', description: '', enabled: true, port: '', baudRate: 9600, dataBits: 8,
  parity: 'None', stopBits: 'One', encoding: 'utf-8', newLine: '\r\n',
  readTimeoutMs: 2000, maxReplyChars: 4096, dtrEnable: false, rtsEnable: false
})
const form = reactive<SerialProfileInput>(emptyForm())

const items = computed(() => list.value?.items ?? [])
const filtered = computed(() => {
  const query = search.value.trim().toLowerCase()
  if (!query) return items.value
  return items.value.filter(item => `${item.name} ${item.port} ${item.description}`.toLowerCase().includes(query))
})
const hasActiveProfiles = computed(() => items.value.some(item => item.enabled))
const conflictCount = computed(() => items.value.filter(item => item.enabled && item.conflict).length)
const scriptExample = computed(() => `context.Serial("${form.name.trim() || 'scanner'}", "start\\r\\n", ${form.readTimeoutMs})`)

function portExists(port: string): boolean {
  return portSet.value.has(port.trim().toLowerCase())
}

function parityLabel(value: string): string {
  return value === 'Odd' ? tr('serialProfiles.parityOdd') : value === 'Even' ? tr('serialProfiles.parityEven') : tr('serialProfiles.parityNone')
}

function stopLabel(value: string): string {
  return value === 'Two' ? '2' : '1'
}

function formatTime(timestamp: number): string {
  return new Date(timestamp).toLocaleString()
}

async function refresh(): Promise<void> {
  await Promise.all([load(), loadPorts()])
}

async function load(): Promise<void> {
  loading.value = true
  try {
    list.value = await serialProfileApi.list()
    loadError.value = ''
  } catch (error) {
    loadError.value = errorMessage(error, tr('serialProfiles.loadFail'))
  } finally {
    loading.value = false
  }
}

async function loadPorts(): Promise<void> {
  try { ports.value = await serialProfileApi.ports() } catch { ports.value = [] }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, emptyForm())
  formError.value = ''
  dialogVisible.value = true
  void loadPorts()
}

function openEdit(row: SerialProfileInfo): void {
  if (row.id == null || !row.editable) return
  editingId.value = row.id
  Object.assign(form, rowInput(row))
  formError.value = ''
  dialogVisible.value = true
  void loadPorts()
}

function openCopy(row: SerialProfileInfo): void {
  editingId.value = null
  Object.assign(form, rowInput(row, false), { name: `${row.name}-copy`, revision: undefined })
  formError.value = ''
  dialogVisible.value = true
  void loadPorts()
}

function rowInput(row: SerialProfileInfo, enabled = row.enabled): SerialProfileInput {
  return {
    name: row.name, description: row.description, enabled, port: row.port, baudRate: row.baudRate,
    dataBits: row.dataBits, parity: row.parity, stopBits: row.stopBits, encoding: row.encoding,
    newLine: row.newLine, readTimeoutMs: row.readTimeoutMs, maxReplyChars: row.maxReplyChars,
    dtrEnable: row.dtrEnable, rtsEnable: row.rtsEnable, revision: row.revision
  }
}

async function save(): Promise<void> {
  if (busy.value) return
  if (!form.name.trim()) { formError.value = tr('serialProfiles.nameRequired'); return }
  if (!form.port.trim()) { formError.value = tr('serialProfiles.portRequired'); return }
  busy.value = true
  formError.value = ''
  try {
    if (editingId.value) await serialProfileApi.update(editingId.value, { ...form })
    else await serialProfileApi.create({ ...form })
    dialogVisible.value = false
    ElMessage.success(tr('serialProfiles.saved'))
    await refresh()
  } catch (error) {
    formError.value = errorMessage(error, tr('serialProfiles.saveFail'))
  } finally {
    busy.value = false
  }
}

async function toggleEnabled(row: SerialProfileInfo, enabled: boolean): Promise<void> {
  if (busy.value || row.id == null || !row.editable) return
  busy.value = true
  actionError.value = ''
  try {
    await serialProfileApi.update(row.id, rowInput(row, enabled))
    ElMessage.success(tr('serialProfiles.saved'))
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('serialProfiles.toggleFailed'))
  } finally {
    busy.value = false
  }
}

async function remove(row: SerialProfileInfo): Promise<void> {
  if (busy.value || row.id == null || !row.editable) return
  try {
    await ElMessageBox.confirm(tr('serialProfiles.deleteConfirm', [row.name]), tr('serialProfiles.delete'), { type: 'warning' })
  } catch { return }
  busy.value = true
  actionError.value = ''
  try {
    await serialProfileApi.remove(row.id)
    ElMessage.success(tr('serialProfiles.deleted'))
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('serialProfiles.deleteFail'))
  } finally {
    busy.value = false
  }
}

async function copyExample(): Promise<void> {
  try {
    await navigator.clipboard.writeText(scriptExample.value)
    ElMessage.success(tr('serialProfiles.copied'))
  } catch {
    ElMessage.warning(tr('serialProfiles.copyFailed'))
  }
}

onMounted(refresh)
</script>

<style scoped>
.page-toolbar { align-items: flex-start; }
.title-block h3 { margin: 0; }
.status-row { display: flex; flex-wrap: wrap; gap: 8px; margin: 10px 0 8px; }
.name-cell { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.tag-line { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; margin-top: 3px; }
.last-call { display: flex; align-items: center; gap: 6px; }
.error-text { color: var(--el-color-danger); max-width: 210px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.form-section { margin-bottom: 6px; }
.section-title { font-weight: 600; font-size: 13px; margin: 4px 0 8px; }
.w-full { width: 100%; }
.port-hint { color: var(--el-color-warning); margin: -4px 0 8px; }
.advanced { margin-top: 2px; }
.usage-box { background: var(--el-fill-color-light); border-radius: 6px; padding: 10px 12px; margin-top: 6px; }
.usage-line { display: flex; align-items: center; justify-content: space-between; gap: 10px; margin: 4px 0; }
.usage-line code { word-break: break-all; }
.readonly-hint { margin-top: 8px; }
:global(.serial-profile-dialog .el-dialog__body) { max-height: 68vh; overflow-y: auto; padding-top: 6px; }
</style>

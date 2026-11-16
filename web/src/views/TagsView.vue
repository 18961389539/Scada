<template>
  <div class="page page--fill">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterDevice" clearable filterable :placeholder="$t('tagsView.kfb9b2c')" :aria-label="$t('tagsView.kfb9b2c')" style="width: 180px" data-test="tag-filter-device">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable :placeholder="$t('tagsView.k06b76d')" :aria-label="$t('tagsView.k06b76d')" style="width: 220px" @keydown.enter="onSearchKey" />
        <el-button type="primary" :icon="Search" :loading="loading" data-test="tag-query" @click="applyQuery">{{ $t('configurationUx.query') }}</el-button>
        <el-button v-if="filterDevice !== undefined || search || appliedDeviceId || catalog.applied.value.search" link type="primary" data-test="tag-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <el-button :icon="Refresh" :loading="loading" data-test="tag-refresh" @click="load">{{ $t('listFilters.refresh') }}</el-button>
        <el-button v-if="controllableDevices.length" :icon="View" data-test="tag-preview" @click="openPreview">{{ $t('tagsView.k7f76da') }}</el-button>
        <!-- 低频操作收进「更多」：核心的查询、预览、新建始终留在工具栏首层 -->
        <el-dropdown trigger="click" @command="onMoreCommand">
          <el-button data-test="tag-more-actions">{{ $t('uxImprove.more') }}<el-icon class="more-caret"><ArrowDown /></el-icon></el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="calculations" data-test="tag-calculations">{{ $t('tagsView.k4e0387') }}</el-dropdown-item>
              <el-dropdown-item command="export" data-test="tag-export">{{ $t('tagsView.kc7f85b') }}</el-dropdown-item>
              <el-dropdown-item v-if="canImport" command="import" :disabled="importing" data-test="tag-import">{{ $t('tagsView.k7d6dc8') }}</el-dropdown-item>
              <el-dropdown-item v-if="batchableDevices.length" command="batch-create" data-test="tag-batch-create">{{ $t('tagsView.k5342be') }}</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <input ref="importInput" type="file" accept=".xlsx" class="hidden-file" @change="onImportFile" />
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
          <el-button v-if="batching" type="warning" plain :disabled="batchCancel" data-test="tag-batch-stop" @click="batchCancel = true">{{ $t('batchUx.stopBatch') }}</el-button>
        </template>
        <el-button type="primary" :icon="Plus" data-test="tag-create" @click="openCreate">{{ $t('tagsView.k573b8c') }}</el-button>
      </div>
      <div class="engineering-summary">
        <p v-if="!loading || tags.length" class="catalog-result muted" role="status" data-test="tag-result-count">{{ $t('listFilters.loadedCount', [tags.length]) }}</p>
        <el-switch v-model="showEngineering" :active-text="$t('ux.tables.engineering')" data-test="tag-engineering-columns" />
      </div>
      <el-alert v-if="pendingQuery" :title="$t('configurationUx.pendingQuery')" type="info" :closable="false" show-icon data-test="tag-query-pending" />
      <p v-if="displayedQuery" class="tiny muted" role="status" data-test="tag-query-context">{{ $t('configurationUx.resultScope', [queryScope, page, tags.length]) }}</p>
      <el-alert v-if="batchProgress" :title="batchCancel ? $t('batchUx.stopping') : batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" data-test="tag-batch-progress" />
      <PageStatusBar :issues="pageIssues" @action="onStatusAction" />

      <el-table
        ref="tableRef"
        v-loading="loading"
        :data="pagedTags"
        :default-sort="{ prop: sortKey, order: sortOrder }"
        size="small"
        height="100%"
        :empty-text="loadError && !tags.length ? loadError : $t('tagsView.k1b4bde')"
        row-key="id"
        class="engineering-table"
        data-test="tags-table"
        @selection-change="onSelect"
        @sort-change="onSortChange"
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
        <el-table-column v-if="showEngineering" prop="id" label="ID" width="70" sortable="custom" show-overflow-tooltip />
        <el-table-column :label="$t('tagsView.k01f2c1')" width="130" show-overflow-tooltip>
          <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
        </el-table-column>
        <el-table-column prop="name" :label="$t('tagsView.kd70b6b')" min-width="180" sortable="custom" show-overflow-tooltip>
          <template #default="{ row }">
            <div>{{ row.name }}</div>
            <router-link v-if="canConfigureTag(row)" :to="{ path: '/config/rules', query: { tagId: String(row.id) } }" class="tiny" data-test="tag-rules">{{ $t('tagsView.viewRules') }}</router-link>
          </template>
        </el-table-column>
        <el-table-column v-if="showEngineering" prop="description" :label="$t('tagsView.k412f54')" min-width="140" show-overflow-tooltip />
        <el-table-column v-if="showEngineering" :label="$t('tagsView.k17fc93')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ row.kind === 'Calculated' ? $t('tagsView.k513c1c') : row.kind === 'Derived' ? $t('tagsView.k8efdcf') : areaLabels[row.area] }}</template>
        </el-table-column>
        <el-table-column :label="$t('tagsView.k67d2d7')" min-width="150" show-overflow-tooltip><template #default="{ row }">{{ addressLabel(row) }}<div class="tiny muted">{{ row.kind === 'Calculated' ? $t('tagsView.k513c1c') : row.kind === 'Derived' ? $t('tagsView.k8efdcf') : areaLabels[row.area] }}</div></template></el-table-column>
        <el-table-column :label="$t('tagsView.ke4e46c')" width="80" show-overflow-tooltip>
          <template #default="{ row }">{{ dataTypeLabels[row.dataType] }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" :label="$t('tagsView.k93c2cc')" width="100" show-overflow-tooltip>
          <template #default="{ row }">{{ row.scale }} / {{ row.offset }}</template>
        </el-table-column>
        <el-table-column prop="unit" :label="$t('tagsView.k8ec8c8')" width="70" show-overflow-tooltip />
        <el-table-column :label="$t('tagsView.k26ae2c')" width="56" show-overflow-tooltip>
          <template #default="{ row }">{{ row.writable ? $t('tagsView.k30160a') : $t('tagsView.k8bf5c1') }}</template>
        </el-table-column>
        <el-table-column :label="$t('tagsView.k091ca5')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ archiveModeLabels[row.archiveMode] }}</template>
        </el-table-column>
        <el-table-column v-if="showEngineering" prop="deadband" :label="$t('tagsView.k687c0b')" width="60" show-overflow-tooltip />
        <el-table-column v-if="showEngineering" :label="$t('tagsView.k9b415a')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ row.kind === 'Physical' && row.scanRateMs ? `${row.scanRateMs} ms` : '—' }}</template>
        </el-table-column>
        <el-table-column :label="$t('tagsView.kf3ea6d')" width="160" fixed="right">
          <template #default="{ row }"><div class="engineering-actions">
            <el-button link type="primary" v-if="canConfigureTag(row)" @click="openEdit(row)">{{ row.kind === 'Calculated' ? $t('tagsView.kc4f4b2') : row.kind === 'Derived' ? $t('tagsView.k3725a1') : $t('tagsView.ka7f814') }}</el-button>
            <template v-if="row.kind === 'Physical' && canConfigureTag(row)">
              <el-dropdown v-if="rowMenuId === row.id" ref="rowMenuRef" trigger="click" @command="onRowMenuCommand" @visible-change="onRowMenuVisible">
                <el-button link data-test="tag-more">{{ $t('ux.tables.more') }}</el-button>
                <template #dropdown><el-dropdown-menu>
                  <el-dropdown-item command="rules" data-test="tag-add-rule">{{ $t('tagsView.k6ebd9c') }}</el-dropdown-item>
                  <el-dropdown-item command="copy" data-test="tag-copy">{{ $t('tagsView.k4edd1d') }}</el-dropdown-item>
                  <el-dropdown-item command="usage" data-test="tag-usage">{{ $t('tagUsage.title') }}</el-dropdown-item>
                  <el-dropdown-item command="delete" divided data-test="tag-delete">{{ $t('tagsView.k3755f5') }}</el-dropdown-item>
                </el-dropdown-menu></template>
              </el-dropdown>
              <el-button v-else link data-test="tag-more" @click="openRowMenu(row)">{{ $t('ux.tables.more') }}</el-button>
            </template>
          </div></template>
        </el-table-column>
      </el-table>
      <el-pagination :current-page="page" :page-size="100" :total="tags.length" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="tag-pagination" @update:current-page="page = $event" @current-change="clearSelection" />
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
              <el-select v-model="form.area" style="width: 100%" data-test="tag-area" @change="syncWritableByArea">
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
          <el-col v-if="formIsNumeric" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k985c8c')">
              <el-input-number v-model="form.scale" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col v-if="formIsNumeric" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.ka87924')">
              <el-input-number v-model="form.offset" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col v-if="formIsNumeric" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k8ec8c8')" prop="unit" :error="fieldErrors.unit">
              <el-input v-model="form.unit" placeholder="℃ / MPa" maxlength="32" />
            </el-form-item>
          </el-col>
          <el-col v-if="form.dataType === 'String'" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.ktextlen')">
              <el-input-number v-model="form.textLength" :min="1" :max="254" style="width: 100%" data-test="tag-text-length" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k30a088')" prop="archiveMode">
              <el-select v-model="form.archiveMode" style="width: 100%" data-test="tag-archive-mode">
                <el-option v-for="(label, value) in archiveModeLabels" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col v-if="formIsNumeric && form.archiveMode !== 'None'" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k687c0b')">
              <el-input-number v-model="form.deadband" :min="0" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col v-if="formIsNumeric && form.archiveMode === 'Periodic'" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.ke4c7e2')" prop="archivePeriodSec" :error="fieldErrors.archivePeriodSec">
              <el-input-number v-model="form.archivePeriodSec" :min="1" style="width: 100%" data-test="tag-archive-period" />
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k9b415a')">
              <el-input-number v-model="form.scanRateMs" :min="10" :max="3600000" :step="100" :controls="false" style="width: 100%" :placeholder="$t('tagsView.k71ea75')" data-test="tag-scan-rate" />
              <p class="tiny" data-test="tag-scan-rate-hint">{{ $t('tagsView.kd15d82') }}</p>
            </el-form-item>
          </el-col>
          <el-col v-if="formIsNumeric" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.kc3d641')" :error="fieldErrors.minValue">
              <el-input-number v-model="form.minValue" :controls="false" style="width: 100%" :placeholder="$t('tagsView.k09c4fc')" />
            </el-form-item>
          </el-col>
          <el-col v-if="formIsNumeric" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.k018219')">
              <el-input-number v-model="form.maxValue" :controls="false" style="width: 100%" :placeholder="$t('tagsView.k09c4fc')" />
            </el-form-item>
          </el-col>
          <el-col v-if="!formReadOnlyArea" :xs="24" :sm="12" :md="8">
            <el-form-item :label="$t('tagsView.kc3dd2c')">
              <el-switch v-model="form.writable" :disabled="wordBitAddress" data-test="tag-writable" />
              <p v-if="wordBitAddress" class="tiny" data-test="tag-word-bit">{{ $t('tagsView.wordBitReadonly') }}</p>
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

    <TagBatchDialog ref="batchDialog" :devices="batchableDevices" :initial-device-id="appliedDeviceId" @saved="onBatchSaved" />

    <TagPreviewDialog ref="previewDialog" :devices="controllableDevices" :initial-device-id="appliedDeviceId" />
    <TagUsageDialog ref="usageDialog" :can-configure-tag="canConfigureTag" :fallback-candidates="tags" @changed="load" />
  </div>
</template>

<script setup lang="ts">
import { useRoute, useRouter } from 'vue-router'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox, type DropdownInstance, type FormInstance, type FormRules, type TableInstance } from 'element-plus'
import { ArrowDown, Plus, Refresh, Search, View } from '@element-plus/icons-vue'
import { deviceApi, tagApi } from '../api'
import { errorMessage, errorText } from '../api/http'
import type { Device, Tag, TagInput } from '../types'
import { protocolDef } from '../protocols'
import { areaLabels, archiveModeLabels, dataTypeLabels, downloadBlob, protocolAreas } from '../utils'
import { can, canGlobal } from '../access/policies'
import { tr } from '../i18n'
import { isMobile } from '../mobile'
import { useAuthStore } from '../stores/auth'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { readLastNumber, readLastText, rememberNumber, rememberText } from '../composables/lastChoice'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'
import { positiveQueryId } from '../nav/query'
import { focusFormError, useEditDialog } from '../composables/useEditDialog'
import { useCatalogQuery } from '../composables/useCatalogQuery'
import PageStatusBar, { type PageIssue } from '../components/PageStatusBar.vue'
import TagBatchDialog from '../components/tags/TagBatchDialog.vue'
import TagPreviewDialog from '../components/tags/TagPreviewDialog.vue'
import TagUsageDialog from '../components/tags/TagUsageDialog.vue'
import { emptyTagInput, tagToInput } from '../components/tags/tagInput'

const auth = useAuthStore()
const route = useRoute()
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const deviceIndex = computed(() => new Map(devices.value.map(device => [device.id, device])))
const catalog = useCatalogQuery({ deviceId: '', search: '' })
const page = computed({
  get: () => Math.min(catalog.page.value, Math.max(1, Math.ceil(tags.value.length / 100))),
  set: value => { catalog.page.value = value }
})
const sortKey = computed(() => route.query.sort === 'name' || route.query.sort === 'id' ? route.query.sort : '')
const sortOrder = computed(() => route.query.order === 'ascending' || route.query.order === 'descending' ? route.query.order : null)
const sortedTags = computed(() => {
  const key = sortKey.value, direction = sortOrder.value === 'descending' ? -1 : 1
  if (!key || !sortOrder.value) return tags.value
  return [...tags.value].sort((a, b) => direction * (key === 'id' ? a.id - b.id : a.name.localeCompare(b.name, undefined, { numeric: true })))
})
const pagedTags = computed(() => sortedTags.value.slice((page.value - 1) * 100, page.value * 100))
const tableRef = ref<TableInstance>()
// 行内“更多”菜单只挂载当前打开的那一个下拉：100 行各挂一个 popper 会把首次渲染拖慢约 0.4 秒。
const rowMenuId = ref<number | null>(null)
const rowMenuRef = ref<DropdownInstance>()
const rowMenuTag = computed(() => pagedTags.value.find(tag => tag.id === rowMenuId.value) ?? null)
async function openRowMenu(tag: Tag): Promise<void> {
  rowMenuId.value = tag.id
  await nextTick()
  rowMenuRef.value?.handleOpen()
}
function onRowMenuVisible(visible: boolean): void {
  if (!visible) rowMenuId.value = null
}
function onRowMenuCommand(command: string): void {
  const tag = rowMenuTag.value
  if (!tag) return
  if (command === 'rules') openRules(tag)
  else if (command === 'copy') openCopy(tag)
  else if (command === 'usage') openUsage(tag)
  else if (command === 'delete') void remove(tag)
}
function clearSelection(): void { selected.value = []; tableRef.value?.clearSelection() }
function onSortChange({ prop, order }: { prop: string; order: 'ascending' | 'descending' | null }): void {
  void router.replace({ query: { ...route.query, sort: order ? prop : undefined, order: order ?? undefined, page: undefined } })
  clearSelection()
}
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
  get: () => positiveQueryId(catalog.draft.deviceId),
  set: (value: number | undefined) => {
    catalog.draft.deviceId = value ? String(value) : ''
  }
})
const appliedDeviceId = computed(() => positiveQueryId(catalog.applied.value.deviceId))
const search = computed({ get: () => catalog.draft.search, set: value => { catalog.draft.search = value } })
const displayedQuery = ref<{ deviceId?: number; search?: string } | null>(null)
const requestedQuery = computed(() => ({ deviceId: appliedDeviceId.value, search: catalog.applied.value.search.trim() || undefined }))
const pendingQuery = computed(() => catalog.pending.value || !!displayedQuery.value && JSON.stringify(displayedQuery.value) !== JSON.stringify(requestedQuery.value))
const queryScope = computed(() => [displayedQuery.value?.deviceId ? deviceName(displayedQuery.value.deviceId) : '', displayedQuery.value?.search].filter(Boolean).join(' · ') || tr('configurationUx.all'))
const showEngineering = ref(false)
const loading = ref(false)
const loadError = ref('')
const devicesError = ref('')
const actionError = ref('')
// 加载、设备目录和操作错误统一交给状态条：单条原位展开，多条折叠，避免首屏被多条 alert 占满。
const pageIssues = computed<PageIssue[]>(() => {
  const issues: PageIssue[] = []
  if (loadError.value) issues.push({ id: 'load', test: 'tags-load-error', level: 'error', message: loadError.value, detail: tr('configurationUx.loadHelp'), action: tr('common.retry'), actionTest: 'tags-load-retry' })
  if (devicesError.value) issues.push({ id: 'devices', test: 'tags-devices-error', level: 'error', message: devicesError.value, detail: tr('configurationUx.loadHelp'), action: tr('common.retry'), actionTest: 'tags-devices-retry' })
  if (actionError.value) issues.push({ id: 'action', test: 'tags-action-error', level: 'error', message: actionError.value })
  return issues
})
function onStatusAction(id: string): void {
  if (id === 'load') void load()
  else if (id === 'devices') void loadDevices()
}
const saving = ref(false)
const batching = ref(false)
const batchCancel = ref(false)
const batchProgress = ref('')
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const copying = ref(false)
const selected = ref<Tag[]>([])
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})

const emptyForm = emptyTagInput
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

// 批量建点只对支持连续地址的协议开放；弹窗内部维护表单与校验。
const batchableDevices = computed(() => configurableDevices.value.filter(d => d.protocol !== 'OpcUa' && !protocolDef(d.protocol)))
const batchDialog = ref<InstanceType<typeof TagBatchDialog>>()
const previewDialog = ref<InstanceType<typeof TagPreviewDialog>>()
const usageDialog = ref<InstanceType<typeof TagUsageDialog>>()

async function onBatchSaved(deviceId: number, created: number): Promise<void> {
  const deviceLabel = deviceName(deviceId)
  catalog.draft.deviceId = String(deviceId)
  catalog.draft.search = ''
  const filtered = await catalog.apply()
  ElMessage.success(filtered ? tr('tagsUx.batchCreatedFiltered', [created, deviceLabel]) : tr('tagsView.k5e1fa9', [created]))
  if (!filtered) await load()
}

const formIsS7 = computed(() => devices.value.find(x => x.id === form.deviceId)?.protocol === 'SiemensS7')
const formIsNumeric = computed(() => form.dataType !== 'Bool' && form.dataType !== 'String')
const formIsOpc = computed(() => devices.value.find(x => x.id === form.deviceId)?.protocol === 'OpcUa')
const formAreas = computed(() => protocolAreas(devices.value.find(x => x.id === form.deviceId)?.protocol))
const formText = computed(() => protocolDef(devices.value.find(x => x.id === form.deviceId)?.protocol))
// 只读区域（离散输入、输入寄存器、S7 输入）不允许设置可写，隐藏开关并在保存时强制为否。
const readOnlyAreas = ['DiscreteInput', 'InputRegister', 'Input']
const formReadOnlyArea = computed(() => readOnlyAreas.includes(form.area))
const wordBitAddress = computed(() => {
  const protocol = devices.value.find(device => device.id === form.deviceId)?.protocol
  const node = form.nodeId.trim()
  if (protocol === 'MitsubishiMc') return /^[A-Za-z]+[0-9A-Fa-f]+\.[0-9A-Fa-f]+$/.test(node)
  if (protocol === 'Inovance') return /^(?!QX)[A-Za-z]+\d+\.\d+$/i.test(node)
  if (protocol === 'Xinje') return /^[A-Za-z]+\d+\.\d+$/.test(node)
  if (protocol === 'Delta') return /^(X\d+(\.\d+)?|(D|SR|E)\d+\.\d+)$/i.test(node)
  return false
})
watch(wordBitAddress, locked => { if (locked) form.writable = false })
function syncWritableByArea(): void { if (formReadOnlyArea.value) form.writable = false }
function selectFormDevice(): void { form.area = formText.value ? 'Address' : formIsOpc.value ? 'OpcNode' : formIsS7.value ? 'DataBlock' : 'HoldingRegister'; form.bitOffset = 0; syncWritableByArea(); clearFieldErrors(fieldErrors) }

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

const toInput = tagToInput

let loadVersion = 0, disposed = false
async function load(): Promise<void> {
  if (disposed) return
  const version = ++loadVersion
  loading.value = true
  clearSelection()
  const query = { ...requestedQuery.value }
  try {
    const result = await tagApi.list(query)
    if (version !== loadVersion) return
    tags.value = result
    displayedQuery.value = query
    loadError.value = ''
  } catch (error) {
    if (version !== loadVersion) return
    loadError.value = errorMessage(error, tr('tagsView.kdf92e4'))
  } finally {
    if (version === loadVersion) loading.value = false
  }
}
watch(() => JSON.stringify(requestedQuery.value), () => { void load() })
onBeforeUnmount(() => { disposed = true; loadVersion++ })
function onSearchKey(event: KeyboardEvent): void { event.preventDefault(); if (!event.isComposing && event.keyCode !== 229) void applyQuery() }
async function applyQuery(): Promise<void> { if (!await catalog.apply()) await load() }
async function resetFilters(): Promise<void> { if (!await catalog.apply(true)) await load() }

function fillForm(tag: Tag, nameOverride?: string): void {
  Object.assign(form, toInput(tag, nameOverride ? { name: nameOverride } : {}))
}

function openCreate(): void {
  editingId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  Object.assign(form, emptyForm())
  const rememberedDevice = readLastNumber('tag-device')
  form.deviceId = (appliedDeviceId.value && configurableDevices.value.some(d => d.id === appliedDeviceId.value)
    ? appliedDeviceId.value
    : rememberedDevice != null && configurableDevices.value.some(d => d.id === rememberedDevice) ? rememberedDevice : configurableDevices.value[0]?.id) ?? 0
  const rememberedArchive = readLastText('tag-archive-mode')
  if (['None', 'OnChange', 'Periodic'].includes(rememberedArchive)) form.archiveMode = rememberedArchive as TagInput['archiveMode']
  const rememberedPeriod = readLastNumber('tag-archive-period')
  if (rememberedPeriod != null && rememberedPeriod >= 1) form.archivePeriodSec = rememberedPeriod
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

function openRules(tag: Tag): void {
  void router.push({ path: '/config/rules', query: { tagId: String(tag.id), create: '1' } })
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
    const isString = form.dataType === 'String'
    const input = {
      ...form,
      writable: formReadOnlyArea.value || wordBitAddress.value ? false : form.writable,
      bitOffset: formIsS7.value && form.dataType === 'Bool' ? form.bitOffset : 0,
      textLength: isString ? form.textLength : 0,
      scale: isString ? 1 : form.scale,
      offset: isString ? 0 : form.offset,
      deadband: isString ? 0 : form.deadband,
      minValue: isString ? null : form.minValue,
      maxValue: isString ? null : form.maxValue,
      unit: isString ? null : form.unit
    }
    if (editingId.value) await tagApi.update(editingId.value, input)
    else {
      await tagApi.create(input)
      rememberNumber('tag-device', form.deviceId)
      rememberText('tag-archive-mode', form.archiveMode)
      rememberNumber('tag-archive-period', form.archivePeriodSec)
    }
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

function openUsage(tag: Tag): void {
  usageDialog.value?.open(tag)
}
function openBatch(): void {
  batchDialog.value?.open()
}
async function remove(tag: Tag): Promise<void> {
  actionError.value = ''
  try {
    const references = await tagApi.usage(tag.id)
    if (references.references.length || references.unreadable) { openUsage(tag); return }
  } catch { /* 引用检查失败时交给服务端删除保护 */ }
  try {
    await ElMessageBox.confirm(`${tr('tagsView.kb70f0d')}\n\n${tr('actionUx.deleteTagCascade')}`, tr('ux.tables.confirmDelete', [tag.name]), { type: 'warning', confirmButtonText: tr('tagsView.k3755f5'), cancelButtonText: tr('tagsView.k4d0b46') })
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
  batchCancel.value = false
  const pending = report.total - report.ok - report.failed.length
  if (report.failed.length === 0) {
    if (report.canceled) ElMessage.info(tr('batchUx.stoppedOnly', [report.ok, report.total, pending]))
    else ElMessage.success(okText)
  } else {
    const summary = summarizeBatch(report, tr('tagsView.k64728a'))
    const body = report.canceled ? `${summary}\n\n${tr('batchUx.stoppedSuffix', [pending])}` : summary
    await ElMessageBox.alert(body, tr('tagsView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('tagsView.kcb63c6') })
  }
  await load()
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(`${tr('tagsView.kcddd80', [rows.length])}\n\n${tr('actionUx.deleteTagCascade')}`, tr('tagsView.k362aed'), { type: 'warning', confirmButtonText: tr('tagsView.k3755f5'), cancelButtonText: tr('tagsView.k4d0b46') })
  } catch { return }
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (t) => `#${t.id} ${t.name}`, async (t) => { await tagApi.remove(t.id) }, (p) => {
    batchProgress.value = tr('tagsView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
  batching.value = false
  await reportBatch(report, tr('tagsView.k8870a2', [report.ok]))
}

async function batchArchive(mode: string): Promise<void> {
  const rows = selected.value.slice()
  if (!rows.length) return
  batching.value = true
  batchCancel.value = false
  const report = await runBatch(rows, (t) => `#${t.id} ${t.name}`, async (t) => { await tagApi.update(t.id, toInput(t, { archiveMode: mode as TagInput['archiveMode'] })) }, (p) => {
    batchProgress.value = tr('tagsView.kcd395b', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  }, () => batchCancel.value)
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

const importing = ref(false)
const importInput = ref<HTMLInputElement>()
function chooseImport(): void {
  if (!importing.value) importInput.value?.click()
}
function onImportFile(event: Event): void {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  // 清空 value，连续选择同一个文件也能触发 change
  input.value = ''
  if (file) void importTags(file)
}
function onMoreCommand(command: string): void {
  if (command === 'calculations') void router.push('/config/calculations')
  else if (command === 'export') void exportTags()
  else if (command === 'import') chooseImport()
  else if (command === 'batch-create') openBatch()
}
async function importTags(file: File): Promise<void> {
  if (importing.value) return
  importing.value = true
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
  } finally { importing.value = false }
}

function openPreview(): void {
  previewDialog.value?.open()
}

async function loadDevices(): Promise<void> {
  try {
    devices.value = await deviceApi.list()
    devicesError.value = ''
  } catch (error) {
    devicesError.value = errorMessage(error, tr('tagsView.ka70156'))
  }
}
onMounted(async () => { await Promise.all([loadDevices(), load()]) })
</script>

<style scoped>
.tag-form :deep(.el-col),
.tag-form :deep(.el-form-item__content) {
  min-width: 0;
}
.hidden-file { display: none; }
.more-caret { margin-left: 4px; }

@media (max-width: 768px) {
  .tag-form :deep(.el-form-item__label) {
    height: auto;
    line-height: 1.5;
  }
}
</style>

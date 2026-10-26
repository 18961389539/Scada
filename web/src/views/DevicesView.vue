<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <h3>{{ $t('devicesView.kc70d1b') }}</h3>
        <el-button v-if="gatewayFilter !== null" link @click="clearFilter">{{ $t('devicesView.k48528b') }}</el-button>
        <span class="spacer" />
        <template v-if="selected.length">
          <el-text data-test="device-batch-count">{{ $t('devicesView.k805f4f', [selected.length]) }}</el-text>
          <el-button data-test="device-batch-enable" :loading="batching" @click="batchSetEnabled(true)">{{ $t('devicesView.kf5e360') }}</el-button>
          <el-button data-test="device-batch-disable" :loading="batching" @click="batchSetEnabled(false)">{{ $t('devicesView.k45f528') }}</el-button>
          <el-button type="danger" plain data-test="device-batch-delete" :loading="batching" @click="batchDelete">{{ $t('devicesView.k362aed') }}</el-button>
        </template>
        <el-button v-if="canOpenCertificates" data-test="device-certificates" @click="certificatesVisible = true">{{ $t('devicesView.k1c11ad') }}</el-button>
        <el-button v-if="configurableAreas.length" type="primary" :icon="Plus" data-test="device-create" @click="openCreate">{{ $t('devicesView.k56ac09') }}</el-button>
      </div>
      <div class="catalog-filters">
        <el-input v-model="search" clearable :prefix-icon="Search" :placeholder="$t('devicesView.searchPlaceholder')" :aria-label="$t('devicesView.searchPlaceholder')" class="catalog-search" />
        <el-select v-model="filterProtocol" :aria-label="$t('devicesView.protocolFilter')" class="catalog-select" data-test="device-filter-protocol">
          <el-option :label="$t('devicesView.allProtocols')" value="all" />
          <el-option v-for="protocol in protocols" :key="protocol" :label="protocolLabel(protocol)" :value="protocol" />
        </el-select>
        <el-select v-model="filterEnabled" :aria-label="$t('listFilters.status')" class="catalog-select" data-test="device-filter-enabled">
          <el-option :label="$t('listFilters.allStates')" value="all" />
          <el-option :label="$t('listFilters.enabled')" value="enabled" />
          <el-option :label="$t('listFilters.disabled')" value="disabled" />
        </el-select>
        <el-button v-if="hasFilters" link type="primary" data-test="device-reset-filters" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
        <span v-if="!loading || devices.length" class="catalog-count muted" role="status" data-test="device-result-count">{{ $t('listFilters.resultCount', [filteredDevices.length, gatewayDevices.length]) }}</span>
        <el-button :icon="Refresh" :loading="loading" data-test="device-refresh" @click="load">{{ $t('listFilters.refresh') }}</el-button>
      </div>
      <el-alert v-if="batchProgress" :title="batchProgress" type="info" :closable="false" show-icon style="margin-bottom: 8px" />
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="devices-load-error" />
      <el-alert v-if="areasError" :title="areasError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="devices-areas-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon style="margin-bottom: 8px" data-test="devices-action-error" />
      <el-alert v-if="gatewayFilter !== null" :title="$t('devicesView.kf60ef3', [gatewayFilter])" type="info" :closable="false" />
      <el-table ref="tableRef" v-loading="loading" :data="filteredDevices" size="small" row-key="id" @selection-change="onSelect">
        <template #empty>
          <span v-if="loadError">{{ loadError }}</span>
          <template v-else-if="hasFilters">
            <span>{{ $t('listFilters.noMatches') }}</span>
            <el-button link type="primary" @click="resetFilters">{{ $t('listFilters.clear') }}</el-button>
          </template>
          <span v-else>{{ $t('devicesView.k2b9379') }}</span>
        </template>
        <el-table-column type="selection" width="42" :selectable="canConfigureDevice" />
        <el-table-column prop="id" label="ID" width="60" />
        <el-table-column prop="name" :label="$t('devicesView.k1be7ae')" min-width="150">
          <template #default="{ row }">
            <div>{{ row.name }}</div>
            <router-link v-if="canConfigureDevice(row)" :to="{ path: '/config/tags', query: { deviceId: String(row.id) } }" class="tiny" data-test="device-tags">{{ $t('devicesView.viewTags') }}</router-link>
          </template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.k17fc93')" width="110"><template #default="{ row }">{{ areaName(row.areaId) }}</template></el-table-column>
        <el-table-column :label="$t('devicesView.kb0c431')" min-width="140" show-overflow-tooltip><template #default="{ row }">{{ protocolLabel(row.protocol) }}</template></el-table-column>
        <el-table-column :label="$t('devicesView.k5dfe3e')" width="135"><template #default="{ row }">
          <router-link v-if="row.gatewayId !== null && globalConfigure" :to="{ path: '/config/gateways', query: { gatewayId: String(row.gatewayId) } }">{{ $t('devicesView.k38b4cc', [row.gatewayId]) }}</router-link><span v-else-if="row.gatewayId !== null">{{ $t('devicesView.k38b4cc', [row.gatewayId]) }}</span><span v-else>{{ $t('devicesView.k3963d7') }}</span>
          <div v-if="globalConfigure"><el-button link type="primary" data-test="device-assign" @click="openAssignment(row.id)">{{ $t('devicesView.k5ad09b') }}</el-button></div>
        </template></el-table-column>
        <el-table-column :label="$t('devicesView.k67d2d7')" min-width="150">
          <template #default="{ row }">{{ row.protocol === 'OpcUa' ? row.opcEndpointUrl : row.protocol === 'ModbusRtu' ? `${row.serialPort} · ${row.baudRate}` : `${row.host}:${row.port}` }}</template>
        </el-table-column>
        <el-table-column prop="stationNumber" :label="$t('devicesView.k60aa5c')" width="70" />
        <el-table-column prop="byteOrder" :label="$t('devicesView.ka15ac8')" width="90" />
        <el-table-column prop="pollIntervalMs" :label="$t('devicesView.k9b415a')" width="95">
          <template #default="{ row }">{{ row.pollIntervalMs }} ms</template>
        </el-table-column>
        <el-table-column prop="timeoutMs" :label="$t('devicesView.kff06c2')" width="80">
          <template #default="{ row }">{{ row.timeoutMs }} ms</template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.kd4e9ca')" width="80">
          <template #default="{ row }">
            <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? $t('devicesView.kd4e9ca') : $t('devicesView.kd989e5') }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('devicesView.kf3ea6d')" width="420" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :disabled="!hasInArea(auth.user, 'Control', row.areaId)" @click="openTest(row)">{{ $t('devicesView.k10b7d8') }}</el-button>
            <el-button link type="primary" @click="diagnosticDevice = row; diagnosticsVisible = true">{{ $t('devicesView.ke7a75c') }}</el-button>
            <el-button v-if="row.protocol === 'OpcUa'" link type="primary" :disabled="!hasInArea(auth.user, 'Configure', row.areaId)" @click="openBrowse(row)">{{ $t('devicesView.kfcad10') }}</el-button>
            <el-button link type="primary" :disabled="!hasInArea(auth.user, 'Configure', row.areaId)" @click="openEdit(row)">{{ $t('devicesView.ka7f814') }}</el-button>
            <el-button link data-test="device-copy" :disabled="!hasInArea(auth.user, 'Configure', row.areaId)" @click="openCopy(row)">{{ $t('devicesView.k4edd1d') }}</el-button>
            <el-popconfirm :title="$t('devicesView.kd54ce8')" @confirm="remove(row)">
              <template #reference><el-button link type="danger" :disabled="!hasInArea(auth.user, 'Configure', row.areaId)">{{ $t('devicesView.k3755f5') }}</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? $t('devicesView.ke1db1a') : (copying ? $t('devicesView.k9d4ba0') : $t('devicesView.k56ac09'))" width="600px" top="5vh" data-test="device-dialog" :before-close="editDialog.beforeClose" :close-on-click-modal="!saving" :close-on-press-escape="!saving" :show-close="!saving" @keydown="editDialog.onKey" @opened="onDialogOpened" @closed="form.opcPassword = ''; form.clearOpcPassword = false">
      <el-alert v-if="!editingId && creatingForGateway" :title="$t('devicesView.kace170', [creatingForGateway])" type="info" :closable="false" />
      <el-alert
        v-if="!editingId"
        type="info"
        :closable="false"
        show-icon
        data-test="device-defaults-tip"
        :title="$t('devicesView.k35f2b4')"
        style="margin-bottom: 12px"
      />
      <el-alert v-if="fieldErrors._form" :title="fieldErrors._form" type="error" :closable="false" show-icon tabindex="-1" style="margin-bottom: 12px" data-test="device-form-error" />
      <el-form ref="formRef" :model="form" :rules="validationRules" :disabled="saving" label-width="110px" status-icon style="max-height: 68vh; overflow-y: auto; padding-right: 12px" @submit.prevent="save">
        <el-form-item :label="$t('devicesView.k4433eb')" prop="name" :error="fieldErrors.name">
          <el-input v-model="form.name" data-test="device-name" :placeholder="$t('devicesView.kc55ec1')" maxlength="100" show-word-limit />
        </el-form-item>
        <el-form-item :label="$t('devicesView.kac1459')">
          <el-select v-model="form.areaId" data-test="device-area" style="width: 100%">
            <el-option v-for="a in configurableAreas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('devicesView.kb0c431')">
          <el-select v-model="form.protocol" style="width: 100%" @change="protocolChanged">
            <el-option label="Modbus TCP" value="ModbusTcp" />
            <el-option :label="$t('devicesView.k6d140f')" value="ModbusRtu" />
            <el-option label="Siemens S7" value="SiemensS7" />
            <el-option label="OPC UA" value="OpcUa" />
            <el-option v-for="p in textProtocols" :key="p.value" :label="p.label" :value="p.value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="showHostPort" :label="$t('devicesView.k2e8a0c')" prop="host" :error="fieldErrors.host">
          <el-input v-model="form.host" data-test="device-host" placeholder="127.0.0.1" />
        </el-form-item>
        <el-form-item v-if="showHostPort" :label="$t('devicesView.k6cbb73')" prop="port" :error="fieldErrors.port">
          <el-input-number v-model="form.port" data-test="device-port" :min="1" :max="65535" style="width: 100%" />
        </el-form-item>
        <template v-if="form.protocol === 'ModbusRtu'">
          <el-form-item :label="$t('devicesView.kedd09e')" required><el-input v-model="form.serialPort" :placeholder="$t('devicesView.kdd7399')" /></el-form-item>
          <el-form-item :label="$t('devicesView.k917c91')"><el-input-number v-model="form.baudRate" :min="300" :max="921600" /></el-form-item>
          <el-form-item :label="$t('devicesView.k39a52f')"><el-select v-model="form.dataBits"><el-option :value="8" /></el-select></el-form-item>
          <el-form-item :label="$t('devicesView.k20feec')"><el-select v-model="form.parity"><el-option :label="$t('devicesView.k720777')" value="None" /><el-option :label="$t('devicesView.kc23797')" value="Odd" /><el-option :label="$t('devicesView.k4421c7')" value="Even" /></el-select></el-form-item>
          <el-form-item :label="$t('devicesView.ka04650')"><el-select v-model="form.stopBits"><el-option label="1" value="One" /><el-option label="2" value="Two" /></el-select></el-form-item>
        </template>
        <template v-if="form.protocol === 'SiemensS7'">
          <el-form-item label="CPU"><el-select v-model="form.cpu"><el-option v-for="cpu in ['S7200', 'S7300', 'S7400', 'S71200', 'S71500']" :key="cpu" :value="cpu" /></el-select></el-form-item>
          <el-form-item :label="$t('devicesView.kc40935')"><el-input-number v-model="form.rack" :min="0" :max="7" /></el-form-item>
          <el-form-item :label="$t('devicesView.k911ac3')"><el-input-number v-model="form.slot" :min="0" :max="31" /></el-form-item>
          <el-alert :title="$t('devicesView.k5b3f14')" type="info" :closable="false" />
        </template>
        <template v-if="form.protocol === 'OpcUa'">
          <el-form-item :label="$t('devicesView.k285482')" prop="opcEndpointUrl"><el-input v-model="form.opcEndpointUrl" data-test="device-opc-url" placeholder="opc.tcp://host:4840/path" /></el-form-item>
          <el-form-item :label="$t('devicesView.k38ba2b')"><el-select v-model="form.opcSecurityMode" @change="opcModeChanged"><el-option :label="$t('devicesView.kc870dd')" value="SignAndEncrypt" /><el-option :label="$t('devicesView.k8ba46c')" value="Sign" /><el-option :label="$t('devicesView.k6e017f')" value="None" /></el-select></el-form-item>
          <el-form-item :label="$t('devicesView.k7fb276')"><el-select v-model="form.opcSecurityPolicy"><el-option v-if="form.opcSecurityMode === 'None'" value="None" /><template v-else><el-option v-for="policy in ['Basic256Sha256', 'Aes128Sha256RsaOaep', 'Aes256Sha256RsaPss']" :key="policy" :value="policy" /></template></el-select></el-form-item>
          <el-form-item :label="$t('devicesView.ka1aaf3')"><el-input v-model="form.opcUserName" autocomplete="off" :placeholder="$t('devicesView.k4d54b9')" /></el-form-item>
          <el-form-item :label="$t('devicesView.kc839a8')"><el-input v-model="form.opcPassword" type="password" autocomplete="new-password" show-password :placeholder="storedPassword ? $t('devicesView.k050138') : $t('devicesView.ke22306')" /></el-form-item>
          <el-form-item v-if="storedPassword" :label="$t('devicesView.kbcf73a')"><el-checkbox v-model="form.clearOpcPassword">{{ $t('devicesView.k1ee6d7') }}</el-checkbox></el-form-item>
          <el-alert :title="form.opcSecurityMode === 'None' ? $t('devicesView.k334ebb') : $t('devicesView.kd9f837')" :type="form.opcSecurityMode === 'None' ? 'warning' : 'info'" :closable="false" />
        </template>
        <template v-if="currentDef">
          <el-alert v-if="currentDef.notice" :title="currentDef.notice" type="warning" :closable="false" />
          <el-form-item v-for="f in currentDef.fields" :key="f.key" :label="f.label">
            <el-select v-if="f.type === 'select'" v-model="options[f.key]" :data-test="`opt-${f.key}`" style="width: 100%"><el-option v-for="o in f.options" :key="o" :value="o" /></el-select>
            <el-switch v-else-if="f.type === 'bool'" v-model="options[f.key]" :data-test="`opt-${f.key}`" />
            <el-input-number v-else-if="f.type === 'number'" v-model="options[f.key]" :data-test="`opt-${f.key}`" :min="f.min" :max="f.max" style="width: 100%" />
            <el-input v-else v-model="options[f.key]" :data-test="`opt-${f.key}`" :type="f.type === 'textarea' ? 'textarea' : 'text'" :placeholder="f.placeholder" />
            <el-text v-if="f.help" type="info" size="small">{{ f.help }}</el-text>
          </el-form-item>
          <template v-if="currentDef.credentials">
            <el-form-item :label="$t('devicesView.ka1aaf3')"><el-input v-model="form.opcUserName" autocomplete="off" :placeholder="$t('devicesView.kb297e0')" /></el-form-item>
            <el-form-item :label="$t('devicesView.kc839a8')"><el-input v-model="form.opcPassword" type="password" autocomplete="new-password" show-password :placeholder="storedPassword ? $t('devicesView.k050138') : $t('devicesView.kc839a8')" /></el-form-item>
            <el-form-item v-if="storedPassword" :label="$t('devicesView.kbcf73a')"><el-checkbox v-model="form.clearOpcPassword">{{ $t('devicesView.k1ee6d7') }}</el-checkbox></el-form-item>
          </template>
        </template>
        <el-form-item v-if="['ModbusTcp', 'ModbusRtu'].includes(form.protocol)" :label="$t('devicesView.k60aa5c')">
          <el-input-number v-model="form.stationNumber" :min="form.protocol === 'ModbusRtu' ? 1 : 0" :max="form.protocol === 'ModbusRtu' ? 247 : 255" style="width: 100%" />
        </el-form-item>
        <el-form-item v-if="['ModbusTcp', 'ModbusRtu'].includes(form.protocol)" :label="$t('devicesView.ka15ac8')">
          <el-radio-group v-model="form.byteOrder">
            <el-radio-button value="ABCD">ABCD</el-radio-button>
            <el-radio-button value="BADC">BADC</el-radio-button>
            <el-radio-button value="CDAB">CDAB</el-radio-button>
            <el-radio-button value="DCBA">DCBA</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="$t('devicesView.k750bbf')">
          <el-input-number v-model="form.pollIntervalMs" data-test="device-poll" :min="100" :max="600000" :step="100" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('devicesView.ke4bc6d')">
          <el-input-number v-model="form.timeoutMs" data-test="device-timeout" :min="100" :max="60000" :step="100" style="width: 100%" />
        </el-form-item>
        <el-form-item :label="$t('devicesView.kd4e9ca')">
          <el-switch v-model="form.enabled" data-test="device-enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <span class="edit-dialog-shortcut">{{ $t('editDialog.shortcut', [editDialog.modKey]) }}</span>
        <el-button :disabled="saving" @click="editDialog.close">{{ $t('devicesView.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="device-save" :loading="saving" :disabled="!canSave" @click="save">{{ $t('devicesView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="testVisible" :title="$t('devicesView.k0e247b')" width="480px">
      <el-form label-width="100px">
        <el-form-item :label="$t('devicesView.k17fc93')">
          <el-select v-model="testForm.area" style="width: 100%">
            <el-option v-for="(label, value) in testAreas" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="testText" :label="$t('devicesView.k67d2d7')"><el-input v-model="testForm.nodeId" :placeholder="testText.addressPlaceholder" /></el-form-item>
        <el-form-item v-if="!testIsOpc && !testText" :label="$t('devicesView.k67d2d7')">
          <el-input-number v-model="testForm.address" :min="0" :max="65535" style="width: 100%" />
        </el-form-item>
        <el-form-item v-if="testIsOpc" label="NodeId"><el-input v-model="testForm.nodeId" placeholder="nsu=urn:plant;s=Boiler.Temperature" /></el-form-item>
        <el-form-item :label="$t('devicesView.kb031dc')">
          <el-select v-model="testForm.dataType" style="width: 100%">
            <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <template v-if="testIsS7">
          <el-form-item v-if="testForm.area === 'DataBlock'" :label="$t('devicesView.kaf82d3')"><el-input-number v-model="testForm.dbNumber" :min="1" :max="65535" /></el-form-item>
          <el-form-item v-if="testForm.dataType === 'Bool'" :label="$t('devicesView.k08ca6d')"><el-input-number v-model="testForm.bitOffset" :min="0" :max="7" /></el-form-item>
          <el-text type="info">{{ $t('devicesView.k91ff21') }}</el-text>
        </template>
      </el-form>
      <el-alert
        v-if="testResult"
        :type="testGood ? 'success' : 'error'"
        :closable="false"
        show-icon
        :title="testGood ? $t('devicesView.kdeaab5', [formatValue(testResult.value)]) : testResult.message"
      />
      <template #footer>
        <el-button @click="testVisible = false">{{ $t('devicesView.k6c14bd') }}</el-button>
        <el-button type="primary" :loading="testing" @click="runTest">{{ $t('devicesView.ke373f7') }}</el-button>
      </template>
    </el-dialog>
    <OpcCertificatesDialog v-model="certificatesVisible" :devices="certificateDevices" :include-center="globalConfigure" />
    <GatewayAssignmentDialog v-if="assignmentDeviceId" v-model="assignmentVisible" :device-id="assignmentDeviceId" :suggested-gateway-id="assignmentSuggested" @changed="load" />
    <CollectorDiagnosticsDialog v-if="diagnosticDevice" v-model="diagnosticsVisible" :device="diagnosticDevice" />
    <OpcBrowserDialog v-if="browseDevice" v-model="browseVisible" :device="browseDevice" @imported="load" />
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules, type TableInstance } from 'element-plus'
import { Plus, Refresh, Search } from '@element-plus/icons-vue'
import { areaApi, deviceApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import type { Area, Device, DeviceInput } from '../types'
import { dataTypeLabels, formatValue, hasGlobal, hasInArea, protocolAreas } from '../utils'
import { defaultOptions, parseOptions, protocolDef, protocolLabel, textProtocols } from '../protocols'
import OpcCertificatesDialog from '../components/OpcCertificatesDialog.vue'
import OpcBrowserDialog from '../components/OpcBrowserDialog.vue'
import CollectorDiagnosticsDialog from '../components/CollectorDiagnosticsDialog.vue'
import GatewayAssignmentDialog from '../components/GatewayAssignmentDialog.vue'
import { tr } from '../i18n'
import { runBatch, summarizeBatch } from '../composables/batchRunner'
import { applyFieldErrors, clearFieldErrors, serverFormErrors, type FieldErrorMap } from '../composables/formErrors'
import { focusFormError, useEditDialog } from '../composables/useEditDialog'

const devices = ref<Device[]>([])
const route = useRoute(), router = useRouter()
const auth = useAuthStore()
const globalConfigure = computed(() => hasGlobal(auth.user, 'Configure'))
const areas = ref<Area[]>([])
const areasError = ref('')
const configurableAreas = computed(() => areas.value.filter(a => hasInArea(auth.user, 'Configure', a.id)))
function canConfigureDevice(row: Device): boolean { return hasInArea(auth.user, 'Configure', row.areaId) }
const certificateDevices = computed(() => devices.value.filter(d => hasInArea(auth.user, 'Configure', d.areaId)))
const canOpenCertificates = computed(() => globalConfigure.value || certificateDevices.value.some(d => d.protocol === 'OpcUa' && d.gatewayId != null))
function areaName(id?: number): string { return areas.value.find(a => a.id === id)?.name ?? (id ? `#${id}` : '-') }
const gatewayFilter = computed(() => typeof route.query.gatewayId === 'string' && /^[1-9]\d*$/.test(route.query.gatewayId) ? Number(route.query.gatewayId) : null)
const search = ref(''), filterProtocol = ref('all'), filterEnabled = ref('all')
const gatewayDevices = computed(() => gatewayFilter.value === null ? devices.value : devices.value.filter(x => x.gatewayId === gatewayFilter.value))
const protocols = computed(() => [...new Set(devices.value.map(device => device.protocol))].sort((a, b) => protocolLabel(a).localeCompare(protocolLabel(b))))
const hasFilters = computed(() => !!search.value || filterProtocol.value !== 'all' || filterEnabled.value !== 'all')
const filteredDevices = computed(() => {
  const terms = search.value.trim().toLocaleLowerCase().split(/\s+/).filter(Boolean)
  return gatewayDevices.value.filter(device => {
    if (filterProtocol.value !== 'all' && device.protocol !== filterProtocol.value) return false
    if (filterEnabled.value !== 'all' && device.enabled !== (filterEnabled.value === 'enabled')) return false
    const text = `${device.name} ${device.host} ${device.port} ${device.serialPort} ${device.opcEndpointUrl} ${protocolLabel(device.protocol)} ${areaName(device.areaId)}`.toLocaleLowerCase()
    return terms.every(term => text.includes(term))
  })
})
const tableRef = ref<TableInstance>()
function resetFilters(): void { search.value = ''; filterProtocol.value = 'all'; filterEnabled.value = 'all' }
watch([search, filterProtocol, filterEnabled, gatewayFilter], () => { selected.value = []; tableRef.value?.clearSelection() })
const assignmentDeviceId = ref<number | null>(null), assignmentVisible = ref(false), assignmentSuggested = ref<number | undefined>(), creatingForGateway = ref<number | null>(null)
function clearFilter(): void { const query = { ...route.query }; delete query.gatewayId; void router.replace({ query }) }
function openAssignment(id: number, suggested?: number): void { assignmentDeviceId.value = id; assignmentSuggested.value = suggested; assignmentVisible.value = true }
const loading = ref(false)
const loadError = ref('')
const actionError = ref('')
const saving = ref(false)
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const copying = ref(false)
const selected = ref<Device[]>([])
const batching = ref(false)
const batchProgress = ref('')
const formRef = ref<FormInstance>()
const fieldErrors = reactive<FieldErrorMap>({})
function onSelect(rows: Device[]): void { selected.value = rows }

const validationRules = computed<FormRules>(() => ({
  name: [
    { required: true, message: tr('devicesView.kac75fb'), trigger: 'blur' },
    { min: 1, max: 100, message: tr('devicesView.k616c06'), trigger: 'blur' }
  ],
  host: [{
    validator: (_r, v, cb) => {
      if (showHostPort.value && !String(v || '').trim()) cb(new Error(tr('devicesView.kb695a3')))
      else cb()
    }, trigger: 'blur'
  }],
  port: [{
    validator: (_r, v, cb) => {
      if (showHostPort.value && !(v >= 1 && v <= 65535)) cb(new Error(tr('devicesView.ke6b89c')))
      else cb()
    }, trigger: 'change'
  }],
  serialPort: [{
    validator: (_r, v, cb) => {
      if (form.protocol === 'ModbusRtu' && !String(v || '').trim()) cb(new Error(tr('devicesView.keaabc9')))
      else cb()
    }, trigger: 'blur'
  }],
  opcEndpointUrl: [{
    validator: (_r, v, cb) => {
      if (form.protocol === 'OpcUa' && !String(v || '').trim()) cb(new Error(tr('devicesView.k41d3d1')))
      else cb()
    }, trigger: 'blur'
  }]
}))

const canSave = computed(() => {
  if (!String(form.name || '').trim()) return false
  if (form.protocol === 'OpcUa') return !!String(form.opcEndpointUrl || '').trim()
  if (form.protocol === 'ModbusRtu') return !!String(form.serialPort || '').trim()
  if (showHostPort.value) return !!String(form.host || '').trim() && form.port >= 1
  return true
})
const storedPassword = ref(false), certificatesVisible = ref(false), browseVisible = ref(false), browseDevice = ref<Device | null>(null)
const diagnosticsVisible = ref(false), diagnosticDevice = ref<Device | null>(null)
function openBrowse(device: Device): void { browseDevice.value = device; browseVisible.value = true }

const emptyForm = (): DeviceInput => ({
  name: '',
  protocol: 'ModbusTcp',
  host: '127.0.0.1',
  port: 502,
  stationNumber: 1,
  byteOrder: 'ABCD',
  pollIntervalMs: 1000,
  timeoutMs: 3000,
  enabled: true,
  areaId: 1,
  serialPort: 'COM1', baudRate: 9600, dataBits: 8, parity: 'None', stopBits: 'One', cpu: 'S71200', rack: 0, slot: 1,
  opcEndpointUrl: 'opc.tcp://localhost:4840', opcSecurityMode: 'SignAndEncrypt', opcSecurityPolicy: 'Basic256Sha256', opcUserName: '', opcPassword: '', clearOpcPassword: false
})
const form = reactive<DeviceInput>(emptyForm())
const options = reactive<Record<string, string | number | boolean>>({})
const editDialog = useEditDialog({ visible: dialogVisible, saving, form: formRef, snapshot: () => JSON.stringify({ form, options }), initialField: 'name', save })
const currentDef = computed(() => protocolDef(form.protocol))
const showHostPort = computed(() => currentDef.value ? currentDef.value.hostPort : !['ModbusRtu', 'OpcUa'].includes(form.protocol))
function setOptions(values: Record<string, string | number | boolean>): void { for (const k of Object.keys(options)) delete options[k]; Object.assign(options, values) }

const testVisible = ref(false)
const testing = ref(false)
const testDeviceId = ref<number | null>(null)
const testForm = reactive({ area: 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
const testIsS7 = computed(() => devices.value.find(x => x.id === testDeviceId.value)?.protocol === 'SiemensS7')
const testText = computed(() => protocolDef(devices.value.find(x => x.id === testDeviceId.value)?.protocol))
const testIsOpc = computed(() => devices.value.find(x => x.id === testDeviceId.value)?.protocol === 'OpcUa')
const testAreas = computed(() => protocolAreas(devices.value.find(x => x.id === testDeviceId.value)?.protocol))
function protocolChanged(): void {
  const def = protocolDef(form.protocol)
  form.port = def ? def.defaultPort : form.protocol === 'SiemensS7' ? 102 : 502
  setOptions(def ? defaultOptions(def) : {})
}
function opcModeChanged(): void { form.opcSecurityPolicy = form.opcSecurityMode === 'None' ? 'None' : 'Basic256Sha256' }
const testResult = ref<{ connected: boolean; value?: number; message?: string; quality?: string } | null>(null)
const testGood = computed(() => testResult.value?.connected && (!testResult.value.quality || testResult.value.quality === 'Good'))

async function load(): Promise<void> {
  loading.value = true
  try {
    const [deviceList, areaList] = await Promise.all([
      deviceApi.list(),
      areaApi.list().then(list => { areasError.value = ''; return list }).catch((error) => { areasError.value = errorMessage(error, tr('devicesView.areasLoadFailed')); return areas.value })
    ])
    devices.value = deviceList
    areas.value = areaList
    selected.value = []
    loadError.value = ''
  } catch (error) {
    loadError.value = errorMessage(error, tr('devicesView.ka70156'))
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  const areaId = configurableAreas.value[0]?.id
  if (!areaId) return
  creatingForGateway.value = gatewayFilter.value; if (creatingForGateway.value !== null) clearFilter()
  editingId.value = null
  copying.value = false
  clearFieldErrors(fieldErrors)
  storedPassword.value = false
  Object.assign(form, emptyForm())
  setOptions({})
  form.areaId = areaId
  if (creatingForGateway.value !== null) form.enabled = false
  dialogVisible.value = true
}

function openEdit(device: Device): void {
  creatingForGateway.value = null
  editingId.value = device.id
  copying.value = false
  clearFieldErrors(fieldErrors)
  storedPassword.value = device.hasOpcPassword
  Object.assign(form, {
    name: device.name,
    protocol: device.protocol,
    host: device.host,
    port: device.port,
    stationNumber: device.stationNumber,
    byteOrder: device.byteOrder,
    pollIntervalMs: device.pollIntervalMs,
    timeoutMs: device.timeoutMs,
    enabled: device.enabled,
    areaId: device.areaId ?? 1,
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: '', clearOpcPassword: false
  })
  setOptions(parseOptions(protocolDef(device.protocol), device.protocolOptions))
  dialogVisible.value = true
}

function fillFromDevice(device: Device, nameOverride?: string): void {
  Object.assign(form, {
    name: nameOverride ?? device.name,
    protocol: device.protocol,
    host: device.host,
    port: device.port,
    stationNumber: device.stationNumber,
    byteOrder: device.byteOrder,
    pollIntervalMs: device.pollIntervalMs,
    timeoutMs: device.timeoutMs,
    enabled: device.enabled,
    areaId: device.areaId ?? 1,
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: '', clearOpcPassword: false
  })
  setOptions(parseOptions(protocolDef(device.protocol), device.protocolOptions))
}

function openCopy(device: Device): void {
  creatingForGateway.value = null
  editingId.value = null
  copying.value = true
  clearFieldErrors(fieldErrors)
  storedPassword.value = false
  fillFromDevice(device, tr('devicesView.kc0769b', [device.name]))
  form.enabled = false
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
      fieldErrors._form = tr('devicesView.k02668e'); focusError = true; return
    }
    if (!canSave.value) {
      fieldErrors._form = tr('devicesView.k9124df'); focusError = true; return
    }
    if (!editingId.value && !configurableAreas.value.some(area => area.id === form.areaId)) {
      fieldErrors._form = tr('devicesView.areaRequired'); focusError = true; return
    }
    const input = { ...form, opcPassword: form.opcPassword?.length ? form.opcPassword : null, protocolOptions: currentDef.value ? JSON.stringify(options) : '' }
    if (editingId.value) await deviceApi.update(editingId.value, input)
    else {
      const created = await deviceApi.create(input)
      if (creatingForGateway.value !== null && globalConfigure.value) openAssignment(created.id, creatingForGateway.value)
    }
    ElMessage.success(tr('devicesView.k7e68eb'))
    dialogVisible.value = false
    await load()
  } catch (error) {
    const mapped = serverFormErrors(error, tr('devicesView.k40525a'), ['name', 'host', 'port', 'opcEndpointUrl', 'serialPort'])
    applyFieldErrors(fieldErrors, mapped.fields)
    fieldErrors._form = mapped.message
    focusError = true
  } finally {
    saving.value = false
    if (focusError) await focusFormError(formRef.value)
  }
}

function toDeviceInput(device: Device, patch: Partial<DeviceInput> = {}): DeviceInput {
  return {
    name: device.name,
    protocol: device.protocol,
    host: device.host,
    port: device.port,
    stationNumber: device.stationNumber,
    byteOrder: device.byteOrder,
    pollIntervalMs: device.pollIntervalMs,
    timeoutMs: device.timeoutMs,
    enabled: device.enabled,
    areaId: device.areaId ?? 1,
    serialPort: device.serialPort, baudRate: device.baudRate, dataBits: device.dataBits, parity: device.parity,
    stopBits: device.stopBits, cpu: device.cpu, rack: device.rack, slot: device.slot,
    opcEndpointUrl: device.opcEndpointUrl, opcSecurityMode: device.opcSecurityMode, opcSecurityPolicy: device.opcSecurityPolicy,
    opcUserName: device.opcUserName, opcPassword: null, clearOpcPassword: false,
    protocolOptions: device.protocolOptions,
    ...patch
  }
}

async function reportBatch(report: Awaited<ReturnType<typeof runBatch>>, okText: string): Promise<void> {
  batchProgress.value = ''
  if (report.failed.length === 0) ElMessage.success(okText)
  else await ElMessageBox.alert(summarizeBatch(report, tr('devicesView.kdda4f8')), tr('devicesView.kcfc1db'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('devicesView.kcb63c6') })
  await load()
}

async function batchSetEnabled(enabled: boolean): Promise<void> {
  const rows = selected.value.filter(d => canConfigureDevice(d))
  if (!rows.length) return
  batching.value = true
  const report = await runBatch(rows, (d) => `#${d.id} ${d.name}`, async (d) => { await deviceApi.update(d.id, toDeviceInput(d, { enabled })) }, (p) => {
    batchProgress.value = tr('devicesView.k4c5062', [enabled ? tr('devicesView.kd4e9ca') : tr('devicesView.kd989e5'), p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  })
  batching.value = false
  await reportBatch(report, tr('devicesView.k312a99', [enabled ? tr('devicesView.kd4e9ca') : tr('devicesView.kd989e5'), report.ok]))
}

async function batchDelete(): Promise<void> {
  const rows = selected.value.filter(d => canConfigureDevice(d))
  if (!rows.length) return
  try {
    await ElMessageBox.confirm(tr('devicesView.k072151', [rows.length]), tr('devicesView.k362aed'), { type: 'warning', confirmButtonText: tr('devicesView.k3755f5'), cancelButtonText: tr('devicesView.k4d0b46') })
  } catch { return }
  batching.value = true
  const report = await runBatch(rows, (d) => `#${d.id} ${d.name}`, async (d) => { await deviceApi.remove(d.id) }, (p) => {
    batchProgress.value = tr('devicesView.kee9792', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : ''])
  })
  batching.value = false
  await reportBatch(report, tr('devicesView.k9d4fef', [report.ok]))
}

async function remove(device: Device): Promise<void> {
  actionError.value = ''
  try {
    await deviceApi.remove(device.id)
    ElMessage.success(tr('devicesView.kfb5fe1'))
    await load()
  } catch (error) {
    actionError.value = errorMessage(error, tr('devicesView.k72250c'))
  }
}

function openTest(device: Device): void {
  testDeviceId.value = device.id
  Object.assign(testForm, { area: protocolDef(device.protocol) ? 'Address' : device.protocol === 'OpcUa' ? 'OpcNode' : device.protocol === 'SiemensS7' ? 'DataBlock' : 'HoldingRegister', address: 0, dataType: 'Float', dbNumber: 1, bitOffset: 0, nodeId: '' })
  testResult.value = null
  testVisible.value = true
}

async function runTest(): Promise<void> {
  if (!testDeviceId.value) return
  testing.value = true
  try {
    testResult.value = await deviceApi.test(testDeviceId.value, testForm.area, testForm.address, testForm.dataType, testForm.dbNumber, testForm.dataType === 'Bool' ? testForm.bitOffset : 0, testForm.nodeId)
  } catch (error) {
    testResult.value = { connected: false, message: errorMessage(error, tr('devicesView.k4184cd')) }
  } finally {
    testing.value = false
  }
}

onMounted(load)
</script>

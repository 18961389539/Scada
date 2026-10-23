<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterDevice" clearable placeholder="全部设备" style="width: 180px" @change="load">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable placeholder="搜索测点名称/描述" style="width: 220px" @keyup.enter="load" />
        <el-button :icon="Search" @click="load">查询</el-button>
        <span class="spacer" />
        <el-button :icon="View" @click="openPreview">字节序预览</el-button>
        <el-button :icon="Download" @click="exportTags">导出 Excel</el-button>
        <el-upload :show-file-list="false" :before-upload="importTags" accept=".xlsx">
          <el-button :icon="Upload">导入 Excel</el-button>
        </el-upload>
        <el-button type="primary" :icon="Plus" @click="openCreate">新增测点</el-button>
      </div>

      <el-table v-loading="loading" :data="tags" size="small" height="calc(100vh - 250px)" empty-text="暂无测点">
        <el-table-column prop="id" label="ID" width="60" />
        <el-table-column label="设备" width="120">
          <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
        </el-table-column>
        <el-table-column prop="name" label="测点名称" min-width="120" />
        <el-table-column prop="description" label="描述" min-width="160" show-overflow-tooltip />
        <el-table-column label="区域" width="100">
          <template #default="{ row }">{{ areaLabels[row.area] }}</template>
        </el-table-column>
        <el-table-column prop="address" label="地址" width="70" />
        <el-table-column label="类型" width="80">
          <template #default="{ row }">{{ dataTypeLabels[row.dataType] }}</template>
        </el-table-column>
        <el-table-column label="缩放/偏移" width="110">
          <template #default="{ row }">{{ row.scale }} / {{ row.offset }}</template>
        </el-table-column>
        <el-table-column prop="unit" label="单位" width="70" />
        <el-table-column label="可写" width="60">
          <template #default="{ row }">{{ row.writable ? '是' : '否' }}</template>
        </el-table-column>
        <el-table-column label="存储" width="100">
          <template #default="{ row }">{{ archiveModeLabels[row.archiveMode] }}</template>
        </el-table-column>
        <el-table-column prop="deadband" label="死区" width="70" />
        <el-table-column label="操作" width="140" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
            <el-popconfirm title="确认删除该测点？" @confirm="remove(row)">
              <template #reference><el-button link type="danger">删除</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="dialogVisible" :title="editingId ? '编辑测点' : '新增测点'" width="720px">
      <el-form :model="form" label-width="110px" class="tag-form">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="所属设备" required>
              <el-select v-model="form.deviceId" style="width: 100%">
                <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="测点名称" required>
              <el-input v-model="form.name" />
            </el-form-item>
          </el-col>
          <el-col :span="24">
            <el-form-item label="描述">
              <el-input v-model="form.description" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="区域">
              <el-select v-model="form.area" style="width: 100%">
                <el-option v-for="(label, value) in areaLabels" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="地址">
              <el-input-number v-model="form.address" :min="0" :max="65535" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="数据类型">
              <el-select v-model="form.dataType" style="width: 100%">
                <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="缩放系数">
              <el-input-number v-model="form.scale" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="偏移量">
              <el-input-number v-model="form.offset" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="单位">
              <el-input v-model="form.unit" placeholder="℃ / MPa" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="存储方式">
              <el-select v-model="form.archiveMode" style="width: 100%">
                <el-option v-for="(label, value) in archiveModeLabels" :key="value" :label="label" :value="value" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="死区">
              <el-input-number v-model="form.deadband" :min="0" :step="0.1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="周期 (秒)">
              <el-input-number v-model="form.archivePeriodSec" :min="1" style="width: 100%" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="最小值">
              <el-input-number v-model="form.minValue" :controls="false" style="width: 100%" placeholder="不限" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="最大值">
              <el-input-number v-model="form.maxValue" :controls="false" style="width: 100%" placeholder="不限" />
            </el-form-item>
          </el-col>
          <el-col :span="8">
            <el-form-item label="允许写入">
              <el-switch v-model="form.writable" />
            </el-form-item>
          </el-col>
        </el-row>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="save">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="previewVisible" title="字节序 / 地址预览" width="480px">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="地址按 Modbus 报文地址从 0 开始。切换设备字节序后重新测试即可确认现场解析方式。"
        style="margin-bottom: 12px"
      />
      <el-form label-width="100px">
        <el-form-item label="设备">
          <el-select v-model="previewDevice" style="width: 100%">
            <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="区域">
          <el-select v-model="previewForm.area" style="width: 100%">
            <el-option v-for="(label, value) in areaLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <el-form-item label="地址">
          <el-input-number v-model="previewForm.address" :min="0" :max="65535" style="width: 100%" />
        </el-form-item>
        <el-form-item label="数据类型">
          <el-select v-model="previewForm.dataType" style="width: 100%">
            <el-option v-for="(label, value) in dataTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
      </el-form>
      <el-alert
        v-if="previewResult"
        :type="previewResult.connected ? 'success' : 'error'"
        :closable="false"
        show-icon
        :title="previewResult.connected ? `原始值：${formatValue(previewResult.value)}` : previewResult.message"
      />
      <template #footer>
        <el-button @click="previewVisible = false">关闭</el-button>
        <el-button type="primary" :loading="previewing" @click="runPreview">读取预览</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Download, Plus, Search, Upload, View } from '@element-plus/icons-vue'
import type { UploadRawFile } from 'element-plus'
import { deviceApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import type { Device, Tag, TagInput } from '../types'
import { areaLabels, archiveModeLabels, dataTypeLabels, downloadBlob, formatValue } from '../utils'

const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const filterDevice = ref<number | undefined>()
const search = ref('')
const loading = ref(false)
const saving = ref(false)
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)

const emptyForm = (): TagInput => ({
  deviceId: 0,
  name: '',
  description: '',
  area: 'HoldingRegister',
  address: 0,
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

const previewVisible = ref(false)
const previewing = ref(false)
const previewDevice = ref<number | undefined>()
const previewForm = reactive({ area: 'HoldingRegister', address: 0, dataType: 'Float' })
const previewResult = ref<{ connected: boolean; value?: number; message?: string } | null>(null)

function deviceName(id: number): string {
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

async function load(): Promise<void> {
  loading.value = true
  try {
    tags.value = await tagApi.list({
      deviceId: filterDevice.value,
      search: search.value.trim() || undefined
    })
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载测点失败'))
  } finally {
    loading.value = false
  }
}

function openCreate(): void {
  editingId.value = null
  Object.assign(form, emptyForm())
  form.deviceId = filterDevice.value ?? devices.value[0]?.id ?? 0
  dialogVisible.value = true
}

function openEdit(tag: Tag): void {
  editingId.value = tag.id
  Object.assign(form, {
    deviceId: tag.deviceId,
    name: tag.name,
    description: tag.description ?? '',
    area: tag.area,
    address: tag.address,
    dataType: tag.dataType,
    scale: tag.scale,
    offset: tag.offset,
    unit: tag.unit ?? '',
    writable: tag.writable,
    archiveMode: tag.archiveMode,
    deadband: tag.deadband,
    archivePeriodSec: tag.archivePeriodSec,
    minValue: tag.minValue ?? null,
    maxValue: tag.maxValue ?? null
  })
  dialogVisible.value = true
}

async function save(): Promise<void> {
  if (!form.deviceId || !form.name.trim()) {
    ElMessage.warning('请选择设备并填写测点名称')
    return
  }

  saving.value = true
  try {
    if (editingId.value) await tagApi.update(editingId.value, { ...form })
    else await tagApi.create({ ...form })
    ElMessage.success('保存成功')
    dialogVisible.value = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

async function remove(tag: Tag): Promise<void> {
  try {
    await tagApi.remove(tag.id)
    ElMessage.success('已删除')
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
  }
}

async function exportTags(): Promise<void> {
  try {
    const blob = await tagApi.export()
    downloadBlob(blob, `tags_${new Date().toISOString().slice(0, 10)}.xlsx`)
  } catch (error) {
    ElMessage.error(errorMessage(error, '导出失败'))
  }
}

async function importTags(file: UploadRawFile): Promise<boolean> {
  try {
    const result = await tagApi.import(file)
    await ElMessageBox.alert(
      `新增 ${result.created} 条，更新 ${result.updated} 条，跳过 ${result.skipped} 条${
        result.errors.length ? `\n错误：${result.errors.join('；')}` : ''
      }`,
      '导入结果',
      { type: result.errors.length ? 'warning' : 'success' }
    )
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '导入失败'))
  }
  return false
}

function openPreview(): void {
  previewVisible.value = true
  previewResult.value = null
  previewDevice.value = filterDevice.value ?? devices.value[0]?.id
}

async function runPreview(): Promise<void> {
  if (!previewDevice.value) {
    ElMessage.warning('请选择设备')
    return
  }

  previewing.value = true
  try {
    previewResult.value = await deviceApi.test(
      previewDevice.value,
      previewForm.area,
      previewForm.address,
      previewForm.dataType
    )
  } catch (error) {
    previewResult.value = { connected: false, message: errorMessage(error, '读取失败') }
  } finally {
    previewing.value = false
  }
}

onMounted(async () => {
  try {
    devices.value = await deviceApi.list()
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载设备失败'))
  }
  await load()
})
</script>

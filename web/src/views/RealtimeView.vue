<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterDevice" clearable placeholder="全部设备" style="width: 180px">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable placeholder="搜索测点" style="width: 200px" />
        <span class="spacer" />
        <el-tag :type="hubState === 'connected' ? 'success' : 'warning'" size="small">
          {{ hubState === 'connected' ? '实时推送已连接' : '推送重连中' }}
        </el-tag>
        <el-button size="small" :icon="Refresh" @click="loadSnapshot">刷新快照</el-button>
      </div>

      <svg class="process-svg" viewBox="0 0 1120 380" preserveAspectRatio="xMidYMid meet">
        <defs>
          <linearGradient id="flame" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="#ff9f43" />
            <stop offset="100%" stop-color="#d4380d" />
          </linearGradient>
        </defs>

        <rect x="40" y="28" width="18" height="330" rx="8" fill="#1d3a5f" />
        <text x="42" y="20" font-size="12">给水</text>
        <path class="pipe" d="M58 190 H150" />
        <path class="pipe" d="M150 190 V120" />

        <rect class="vessel" x="150" y="80" width="260" height="230" rx="16" />
        <text x="256" y="110" font-size="14" text-anchor="middle">锅 炉</text>
        <circle class="vessel" cx="190" cy="180" r="30" />
        <text x="190" y="176" font-size="11" text-anchor="middle">汽包</text>
        <text x="190" y="192" font-size="10" text-anchor="middle">水位</text>
        <text class="measure" x="228" y="186" font-size="16">{{ display('汽包水位') }}<tspan font-size="10"> mm</tspan></text>

        <rect x="230" y="240" width="150" height="52" rx="6" fill="url(#flame)" opacity="0.75" />
        <text x="305" y="272" font-size="12" text-anchor="middle" fill="#2b1600">炉膛燃烧</text>

        <path class="pipe" d="M410 120 H560" />
        <text class="measure" x="450" y="108" font-size="16">{{ display('出口温度') }}<tspan font-size="10"> ℃</tspan></text>
        <text class="measure" x="450" y="140" font-size="16">{{ display('出口压力') }}<tspan font-size="10"> MPa</tspan></text>

        <rect class="vessel" x="560" y="60" width="120" height="190" rx="60" />
        <text x="620" y="90" font-size="13" text-anchor="middle">过热器</text>

        <path class="pipe" d="M680 155 H900" />
        <text class="measure" x="730" y="143" font-size="16">{{ display('主蒸汽流量') }}<tspan font-size="10"> t/h</tspan></text>

        <rect x="900" y="110" width="150" height="90" rx="8" fill="#16283f" stroke="#3d6090" stroke-width="3" />
        <text x="975" y="140" font-size="13" text-anchor="middle">汽轮机</text>
        <text class="measure" x="975" y="170" font-size="14" text-anchor="middle">{{ display('出口压力') }} MPa</text>

        <path class="pipe" d="M300 310 V350 H520" />
        <text class="measure" x="360" y="342" font-size="14">{{ display('烟气含氧量') }}<tspan font-size="10"> %</tspan></text>
        <path class="pipe" d="M520 350 V300" />
        <rect class="vessel" x="480" y="250" width="90" height="50" rx="6" />
        <text x="525" y="280" font-size="11" text-anchor="middle">空预器</text>
        <text class="measure" x="585" y="292" font-size="14">{{ display('排烟温度') }}<tspan font-size="10"> ℃</tspan></text>

        <path class="pipe" d="M150 260 H110 V330" />
        <text class="measure" x="60" y="350" font-size="14">给水泵 {{ display('给水泵频率') }} Hz</text>
        <circle cx="150" cy="260" r="14" :fill="bool('给水泵运行') ? '#63e6be' : '#44546a'" />
        <text x="185" y="300" font-size="11">运行状态：{{ bool('给水泵运行') ? '运行' : '停止' }}</text>

        <text class="measure" x="60" y="300" font-size="14">炉膛负压 {{ display('炉膛负压') }} Pa</text>
        <text class="measure" x="900" y="230" font-size="14">鼓风机 {{ display('鼓风机电流') }} A</text>
        <text class="measure" x="900" y="255" font-size="14">引风机 {{ display('引风机电流') }} A</text>
      </svg>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>实时测点</strong>
        <span class="muted tiny">SignalR 每 500 ms 合并推送，按变化值刷新</span>
        <span class="spacer" />
        <el-tag type="info" size="small">共 {{ visibleTags.length }} 点</el-tag>
      </div>
      <el-table :data="visibleTags" size="small" height="calc(100vh - 560px)" empty-text="暂无测点">
        <el-table-column label="设备" width="120">
          <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
        </el-table-column>
        <el-table-column prop="name" label="测点" min-width="130" />
        <el-table-column label="数值" width="150">
          <template #default="{ row }">
            <span class="value-strong" :class="valueClass(row.id)">
              {{ displayId(row.id) }} <span class="muted tiny">{{ row.unit }}</span>
            </span>
          </template>
        </el-table-column>
        <el-table-column label="质量" width="110">
          <template #default="{ row }">
            <el-tag :type="qualityType(qualityOf(row.id))" size="small">
              {{ qualityLabels[qualityOf(row.id)] }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="时间" width="170">
          <template #default="{ row }">{{ formatTime(valueOf(row.id)?.ts) }}</template>
        </el-table-column>
        <el-table-column label="描述" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.description }}</template>
        </el-table-column>
        <el-table-column label="操作" width="110" fixed="right">
          <template #default="{ row }">
            <el-button v-if="row.writable && hasAny(auth.user, 'Control')" link type="primary" @click="openWrite(row)">
              写值
            </el-button>
            <span v-else class="muted tiny">{{ row.writable ? '查看员只读' : '只读' }}</span>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <CommandReceipt v-if="receiptId && !writeVisible" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <el-dialog v-model="writeVisible" :title="`写入测点：${writeTag?.name ?? ''}`" width="420px">
      <el-form label-width="90px">
        <el-form-item label="当前值">
          <span class="value-strong">{{ display(writeTag?.name ?? '') }} {{ writeTag?.unit }}</span>
        </el-form-item>
        <el-form-item label="目标值">
          <el-input-number v-model="writeValue" :disabled="!!writeRequestId" :min="writeTag?.minValue ?? undefined" :max="writeTag?.maxValue ?? undefined" style="width: 100%" />
        </el-form-item>
        <el-alert type="warning" :closable="false" show-icon :title="writeRequestId ? '本次操作保留原命令编号；请查询回执。新的操作请重新打开对话框。' : '下发前会检查限幅、互锁、挂牌与控制权并请你确认；命令串行下发并回读确认。'" />
        <CommandReceipt v-if="receiptId && writeVisible" :id="receiptId" />
      </el-form>
      <template #footer>
        <el-button @click="writeVisible = false">取消</el-button>
        <el-button type="primary" :loading="writing" @click="submitWrite">下发</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import CommandReceipt from '../components/CommandReceipt.vue'
import { createCommandId } from '../api/commandTypes'
import { Refresh } from '@element-plus/icons-vue'
import * as signalR from '@microsoft/signalr'
import { commandApi, deviceApi, realtimeApi, tagApi } from '../api'
import ControlConfirmDialog from '../components/ControlConfirmDialog.vue'
import { TOKEN_KEY, errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { Device, Tag, TagQuality, TagValue } from '../types'
import {
  hasAny,
  formatTime,
  formatValue,
  qualityLabels,
  qualityType
} from '../utils'

const auth = useAuthStore()
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const filterDevice = ref<number | undefined>()
const search = ref('')
const values = reactive<Record<number, TagValue>>({})
const hubState = ref<'connected' | 'reconnecting'>('reconnecting')

let connection: signalR.HubConnection | null = null

const visibleTags = computed(() =>
  tags.value.filter(
    (tag) =>
      (!filterDevice.value || tag.deviceId === filterDevice.value) &&
      (!search.value.trim() || tag.name.includes(search.value.trim()) || (tag.description ?? '').includes(search.value.trim()))
  )
)

function tagByName(name: string): Tag | undefined {
  return tags.value.find((x) => x.name === name)
}

function valueOf(tagId: number): TagValue | undefined {
  return values[tagId]
}

function display(name: string): string {
  const tag = tagByName(name)
  if (!tag) return '--'
  const value = values[tag.id]
  if (!value) return '--'
  if (tag.dataType === 'Bool') return value.value !== 0 ? '1' : '0'
  return formatValue(value.value, 2)
}
function displayId(id: number): string { const value = values[id]; return value ? formatValue(value.value, 2) : '--' }

function bool(name: string): boolean {
  const tag = tagByName(name)
  return tag ? (values[tag.id]?.value ?? 0) !== 0 : false
}

function qualityOf(tagId: number): TagQuality {
  return values[tagId]?.quality ?? 'Bad'
}

function valueClass(tagId: number): string {
  const quality = qualityOf(tagId)
  if (quality === 'Good') return 'tag-good'
  if (quality === 'CommFailure') return 'tag-fail'
  return 'tag-bad'
}

function deviceName(id: number | null): string {
  if (id === null) return '计算测点'
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

async function loadSnapshot(): Promise<void> {
  try {
    const snapshot = await realtimeApi.get(tags.value.map((x) => x.id))
    for (const item of snapshot) values[item.tagId] = item
  } catch (error) {
    ElMessage.error(errorMessage(error, '读取实时值失败'))
  }
}

function applyValues(incoming: TagValue[]): void {
  for (const item of incoming) values[item.tagId] = item
}

async function startHub(): Promise<void> {
  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/realtime', { accessTokenFactory: () => localStorage.getItem(TOKEN_KEY) ?? '' })
    .withAutomaticReconnect()
    .build()

  connection.on('values', (incoming: TagValue[]) => applyValues(incoming))
  connection.onreconnecting(() => (hubState.value = 'reconnecting'))
  connection.onreconnected(async () => {
    hubState.value = 'connected'
    await connection?.invoke('Subscribe', tags.value.map((x) => x.id))
    await loadSnapshot()
  })

  try {
    await connection.start()
    hubState.value = 'connected'
    await connection.invoke('Subscribe', tags.value.map((x) => x.id))
  } catch {
    hubState.value = 'reconnecting'
  }
}

const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
const writeVisible = ref(false)
const writing = ref(false)
const writeTag = ref<Tag | null>(null)
const writeValue = ref(0)
const writeRequestId = ref<string | null>(null), receiptId = ref<string | null>(null)

function openWrite(tag: Tag): void {
  writeRequestId.value = null; receiptId.value = null
  writeTag.value = tag
  writeValue.value = values[tag.id]?.value ?? tag.minValue ?? 0
  writeVisible.value = true
}

async function submitWrite(): Promise<void> {
  if (!writeTag.value) return
  writing.value = true
  try {
    if (!writeRequestId.value && !await confirmDialog.value?.open(writeTag.value.id, writeValue.value, `写入 ${writeTag.value.name}`)) return
    writeRequestId.value ??= createCommandId()
    const result = await commandApi.write(writeTag.value.id, writeValue.value, writeRequestId.value, true)
    receiptId.value = result.commandId ?? null
    if (result.pending) {
      ElMessage.warning('已提交双人复核，等待另一位用户批准'); writeVisible.value = false
    } else if (result.success) {
      ElMessage.success(`写入成功，回读 ${formatValue(result.readBack)}`)
      writeVisible.value = false
      await loadSnapshot()
    } else {
      ElMessage.error(result.message)
    }
  } catch (error) {
    if (devices.value.find(x => x.id === writeTag.value?.deviceId)?.gatewayId != null) receiptId.value = writeRequestId.value
    ElMessage.error(errorMessage(error, '写入失败'))
  } finally {
    writing.value = false
  }
}

onMounted(async () => {
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载配置失败'))
    return
  }
  await loadSnapshot()
  await startHub()
})

onBeforeUnmount(() => {
  connection?.stop()
})
</script>

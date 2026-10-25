<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterDevice" clearable :placeholder="$t('realtimeView.kfb9b2c')" style="width: 180px" data-test="device-filter">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable :placeholder="$t('realtimeView.kc23497')" style="width: 200px" />
        <el-select v-model="qualityFilter" size="small" style="width: 130px" data-test="quality-filter">
          <el-option :label="$t('realtimeView.qualityAll')" value="all" />
          <el-option :label="$t('realtimeView.qualityAbnormal')" value="abnormal" />
          <el-option :label="$t('realtimeView.qualityGood')" value="good" />
        </el-select>
        <span class="spacer" />
        <el-tag :type="hubState === 'connected' ? 'success' : 'warning'" size="small">
          {{ hubState === 'connected' ? $t('realtimeView.kbd279e') : $t('realtimeView.k1439ec') }}
        </el-tag>
        <el-button size="small" :type="frozen ? 'warning' : 'default'" data-test="freeze-toggle" @click="toggleFreeze">
          {{ frozen ? $t('realtimeView.resume') : $t('realtimeView.pause') }}
        </el-button>
        <el-button size="small" :icon="Refresh" @click="loadSnapshot">{{ $t('realtimeView.ka4fa6a') }}</el-button>
      </div>

      <div class="process-wrap" data-test="process-wrap">
        <div class="process-hint">{{ $t('realtimeView.processPanHint') }}</div>
        <svg class="process-svg" viewBox="0 0 1120 380" preserveAspectRatio="xMidYMid meet">
        <defs>
          <linearGradient id="flame" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="#ff9f43" />
            <stop offset="100%" stop-color="#d4380d" />
          </linearGradient>
        </defs>

        <rect x="40" y="28" width="18" height="330" rx="8" fill="#1d3a5f" />
        <text x="42" y="20" font-size="12">{{ $t('realtimeView.ka30773') }}</text>
        <path class="pipe" d="M58 190 H150" />
        <path class="pipe" d="M150 190 V120" />

        <rect class="vessel" x="150" y="80" width="260" height="230" rx="16" />
        <text x="256" y="110" font-size="14" text-anchor="middle">{{ $t('realtimeView.k548e4c') }}</text>
        <circle class="vessel" cx="190" cy="180" r="30" />
        <text x="190" y="176" font-size="11" text-anchor="middle">{{ $t('realtimeView.kfb4fe2') }}</text>
        <text x="190" y="192" font-size="10" text-anchor="middle">{{ $t('realtimeView.kefce30') }}</text>
        <text class="measure" x="228" y="186" font-size="16">{{ display($t('realtimeView.k78584e')) }}<tspan font-size="10"> mm</tspan></text>

        <rect x="230" y="240" width="150" height="52" rx="6" fill="url(#flame)" opacity="0.75" />
        <text x="305" y="272" font-size="12" text-anchor="middle" fill="#2b1600">{{ $t('realtimeView.ka666e8') }}</text>

        <path class="pipe" d="M410 120 H560" />
        <text class="measure" x="450" y="108" font-size="16">{{ display($t('realtimeView.k20b60e')) }}<tspan font-size="10"> ℃</tspan></text>
        <text class="measure" x="450" y="140" font-size="16">{{ display($t('realtimeView.kb38bfb')) }}<tspan font-size="10"> MPa</tspan></text>

        <rect class="vessel" x="560" y="60" width="120" height="190" rx="60" />
        <text x="620" y="90" font-size="13" text-anchor="middle">{{ $t('realtimeView.k854f05') }}</text>

        <path class="pipe" d="M680 155 H900" />
        <text class="measure" x="730" y="143" font-size="16">{{ display($t('realtimeView.k0a870d')) }}<tspan font-size="10"> t/h</tspan></text>

        <rect x="900" y="110" width="150" height="90" rx="8" fill="#16283f" stroke="#3d6090" stroke-width="3" />
        <text x="975" y="140" font-size="13" text-anchor="middle">{{ $t('realtimeView.ke7d99b') }}</text>
        <text class="measure" x="975" y="170" font-size="14" text-anchor="middle">{{ display($t('realtimeView.kb38bfb')) }} MPa</text>

        <path class="pipe" d="M300 310 V350 H520" />
        <text class="measure" x="360" y="342" font-size="14">{{ display($t('realtimeView.k19f64b')) }}<tspan font-size="10"> %</tspan></text>
        <path class="pipe" d="M520 350 V300" />
        <rect class="vessel" x="480" y="250" width="90" height="50" rx="6" />
        <text x="525" y="280" font-size="11" text-anchor="middle">{{ $t('realtimeView.k6ec495') }}</text>
        <text class="measure" x="585" y="292" font-size="14">{{ display($t('realtimeView.kf7fc28')) }}<tspan font-size="10"> ℃</tspan></text>

        <path class="pipe" d="M150 260 H110 V330" />
        <text class="measure" x="60" y="350" font-size="14">{{ $t('realtimeView.k02e7f5', [display($t('realtimeView.k8314ad'))]) }}</text>
        <circle cx="150" cy="260" r="14" :fill="bool($t('realtimeView.k60f7ca')) ? '#63e6be' : '#44546a'" />
        <text x="185" y="300" font-size="11">{{ $t('realtimeView.kdda149', [bool($t('realtimeView.k60f7ca')) ? $t('realtimeView.k0c3acd') : $t('realtimeView.ka17f70')]) }}</text>

        <text class="measure" x="60" y="300" font-size="14">{{ $t('realtimeView.kd38693', [display($t('realtimeView.kadb735'))]) }}</text>
        <text class="measure" x="900" y="230" font-size="14">{{ $t('realtimeView.kaeae42', [display($t('realtimeView.kc0f32d'))]) }}</text>
        <text class="measure" x="900" y="255" font-size="14">{{ $t('realtimeView.k1ee1c1', [display($t('realtimeView.k2161c1'))]) }}</text>
        </svg>
      </div>
    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>{{ $t('realtimeView.k60a3fe') }}</strong>
        <span class="muted tiny">{{ $t('realtimeView.kc253d0') }}</span>
        <span class="spacer" />
        <el-tag type="info" size="small">{{ $t('realtimeView.keb7bc0', [visibleTags.length]) }}</el-tag>
        <el-tag v-if="abnormalCount" type="danger" size="small" data-test="abnormal-count">{{ $t('realtimeView.abnormalCount', [abnormalCount]) }}</el-tag>
      </div>
      <el-table :data="visibleTags" size="small" max-height="calc(100vh - 560px)" :empty-text="$t('realtimeView.k1b4bde')" :row-class-name="rowClass">
        <el-table-column :label="$t('realtimeView.k01f2c1')" width="120">
          <template #default="{ row }">{{ deviceName(row.deviceId) }}</template>
        </el-table-column>
        <el-table-column prop="name" :label="$t('realtimeView.k8cff1c')" min-width="130" />
        <el-table-column :label="$t('realtimeView.k521523')" width="150">
          <template #default="{ row }">
            <span class="value-strong" :class="valueClass(row.id)">
              {{ displayId(row.id) }} <span class="muted tiny">{{ row.unit }}</span>
            </span>
          </template>
        </el-table-column>
        <el-table-column :label="$t('realtimeView.k82f516')" width="110">
          <template #default="{ row }">
            <el-tag :type="qualityType(qualityOf(row.id))" size="small">
              {{ qualityLabels[qualityOf(row.id)] }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="$t('realtimeView.k89b4aa')" width="170">
          <template #default="{ row }">{{ formatTime(valueOf(row.id)?.ts) }}</template>
        </el-table-column>
        <el-table-column :label="$t('realtimeView.k412f54')" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">{{ row.description }}</template>
        </el-table-column>
        <el-table-column :label="$t('realtimeView.kf3ea6d')" width="110" fixed="right">
          <template #default="{ row }">
            <el-button v-if="row.writable && hasAny(auth.user, 'Control')" link type="primary" @click="openWrite(row)">
              {{ $t('realtimeView.k2fb871') }}
            </el-button>
            <span v-else class="muted tiny">{{ row.writable ? $t('realtimeView.kb44182') : $t('realtimeView.kffc1d0') }}</span>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <CommandReceipt v-if="receiptId && !writeVisible" :id="receiptId" />
    <ControlConfirmDialog ref="confirmDialog" />
    <el-dialog v-model="writeVisible" :title="$t('realtimeView.k859e7c', [writeTag?.name ?? ''])" width="420px">
      <el-form label-width="90px">
        <el-form-item :label="$t('realtimeView.k8b0db6')">
          <span class="value-strong">{{ display(writeTag?.name ?? '') }} {{ writeTag?.unit }}</span>
        </el-form-item>
        <el-form-item :label="$t('realtimeView.kba60a3')">
          <el-input-number v-model="writeValue" :disabled="!!writeRequestId" :min="writeTag?.minValue ?? undefined" :max="writeTag?.maxValue ?? undefined" style="width: 100%" />
          <div class="tiny muted" style="width: 100%; margin-top: 4px" data-test="write-hints">
            {{ $t('realtimeView.writeRange', [writeTag?.minValue ?? '-', writeTag?.maxValue ?? '-', writeTag?.unit ?? '']) }}
            · {{ $t('realtimeView.writeCurrent', [writeCurrentValue, writeTag?.unit ?? '']) }}
          </div>
        </el-form-item>
        <el-alert type="warning" :closable="false" show-icon :title="writeRequestId ? $t('realtimeView.ke50c4d') : $t('realtimeView.k7868f7')" />
        <CommandReceipt v-if="receiptId && writeVisible" :id="receiptId" />
      </el-form>
      <template #footer>
        <el-button @click="writeVisible = false">{{ $t('realtimeView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="writing" @click="submitWrite">{{ $t('realtimeView.ka1468f') }}</el-button>
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
import { hubState as realtimeHubState, onHubReconnected, subscribeTags, type TagSubscription } from '../realtime/hub'
import { commandApi, deviceApi, realtimeApi, tagApi } from '../api'
import ControlConfirmDialog from '../components/ControlConfirmDialog.vue'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { Device, Tag, TagQuality, TagValue } from '../types'
import {
  hasAny,
  formatTime,
  formatValue,
  qualityLabels,
  qualityType
} from '../utils'
import { tr } from '../i18n'

const auth = useAuthStore()
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const filterDevice = ref<number | undefined>()
const search = ref('')
// 现场排查时先看坏质量：按质量筛选 + 异常行高亮，比逐个扫表快得多
const qualityFilter = ref<'all' | 'abnormal' | 'good'>('all')
// 暂停刷新：数值不断变化时冻结表格，便于读数与截图
const frozen = ref(false)
const values = reactive<Record<number, TagValue>>({})
// 连接状态由共享连接模块统一维护（disconnected / connecting / connected / reconnecting）
const hubState = realtimeHubState()

let subscription: TagSubscription | undefined
let disposeReconnected: (() => void) | undefined

function matchesQuality(tagId: number): boolean {
  if (qualityFilter.value === 'all') return true
  const good = qualityOf(tagId) === 'Good'
  return qualityFilter.value === 'good' ? good : !good
}

const visibleTags = computed(() => {
  const kw = search.value.trim()
  return tags.value.filter((tag) => {
    if (filterDevice.value && tag.deviceId !== filterDevice.value) return false
    if (!matchesQuality(tag.id)) return false
    if (!kw) return true
    // 现场通常按设备名或描述找测点，不只看测点名
    return tag.name.includes(kw) || (tag.description ?? '').includes(kw) || deviceName(tag.deviceId).includes(kw)
  })
})

// 当前筛选范围内质量非良好的测点数（未采集到值的测点也算），用于顶部一眼可见
const abnormalCount = computed(() => visibleTags.value.filter((tag) => qualityOf(tag.id) !== 'Good').length)

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

// 异常质量整行着色，避免只靠数值颜色在长表里漏看
function rowClass({ row }: { row: Tag }): string {
  const quality = qualityOf(row.id)
  if (quality === 'Good') return ''
  return quality === 'Uncertain' ? 'row-quality-warn' : 'row-quality-bad'
}

function deviceName(id: number | null): string {
  if (id === null) return tr('realtimeView.k4e0387')
  return devices.value.find((x) => x.id === id)?.name ?? `#${id}`
}

async function loadSnapshot(): Promise<void> {
  try {
    const snapshot = await realtimeApi.get(tags.value.map((x) => x.id))
    for (const item of snapshot) values[item.tagId] = item
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('realtimeView.k990c88')))
  }
}

function toggleFreeze(): void {
  frozen.value = !frozen.value
  // 恢复时补一次快照，冻结期间的变化不会丢
  if (!frozen.value) void loadSnapshot()
}

function applyValue(value: TagValue): void {
  // 暂停期间丢弃推送，恢复时统一拉快照，避免读数时表格跳动
  if (frozen.value) return
  values[value.tagId] = value
}

// 共享连接按 tagId 只回传本页订阅的测点，无需再自行过滤；
// 订阅走引用计数，离开页面时释放，不影响其他页面订阅的同一测点。
async function startSubscription(): Promise<void> {
  const next = await subscribeTags(tags.value.map((x) => x.id), applyValue)
  // 先订阅新的再释放旧的：重叠测点不会出现取消/重订抖动
  subscription?.dispose()
  subscription = next
}

const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
const writeVisible = ref(false)
const writing = ref(false)
const writeTag = ref<Tag | null>(null)
const writeValue = ref(0)
const writeRequestId = ref<string | null>(null), receiptId = ref<string | null>(null)

// 写值前给出当前值参考：避免操作人凭记忆改数，也便于判断写入是否真的变化
const writeCurrentValue = computed(() => {
  const tag = writeTag.value
  if (!tag) return '--'
  const value = values[tag.id]
  if (!value) return '--'
  return tag.dataType === 'Bool' ? (value.value !== 0 ? '1' : '0') : formatValue(value.value, 2)
})

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
    if (!writeRequestId.value && !await confirmDialog.value?.open(writeTag.value.id, writeValue.value, tr('realtimeView.k71fb1d', [writeTag.value.name]))) return
    writeRequestId.value ??= createCommandId()
    const result = await commandApi.write(writeTag.value.id, writeValue.value, writeRequestId.value, true)
    receiptId.value = result.commandId ?? null
    if (result.pending) {
      ElMessage.warning(tr('realtimeView.kf1787c')); writeVisible.value = false
    } else if (result.success) {
      ElMessage.success(tr('realtimeView.kf2b186', [formatValue(result.readBack)]))
      writeVisible.value = false
      await loadSnapshot()
    } else {
      ElMessage.error(result.message)
    }
  } catch (error) {
    if (devices.value.find(x => x.id === writeTag.value?.deviceId)?.gatewayId != null) receiptId.value = writeRequestId.value
    ElMessage.error(errorMessage(error, tr('realtimeView.ke92dc2')))
  } finally {
    writing.value = false
  }
}

onMounted(async () => {
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('realtimeView.k9060ce')))
    return
  }
  await loadSnapshot()
  await startSubscription()
  // 重连后补拉快照，补齐断线期间缺失的值（重新订阅由共享连接模块负责）
  disposeReconnected = onHubReconnected(() => {
    void loadSnapshot()
  })
})

onBeforeUnmount(() => {
  subscription?.dispose()
  disposeReconnected?.()
})
</script>

<style scoped>
/* 窄屏工艺图保持可读尺寸，允许左右滑动，而不是缩成看不清的缩略图 */
.process-wrap {
  position: relative;
}

.process-hint {
  display: none;
}

@media (max-width: 768px) {
  .process-wrap {
    overflow-x: auto;
    -webkit-overflow-scrolling: touch;
  }

  .process-svg {
    min-width: 760px;
  }

  .process-hint {
    display: block;
    margin-bottom: 6px;
    font-size: var(--scada-fs-caption);
    color: var(--scada-text-muted);
  }
}

/* 异常质量的整行着色：长表里比只看数值颜色更容易发现 */
:deep(.row-quality-bad) {
  background: rgba(245, 108, 108, 0.10);
}
:deep(.row-quality-warn) {
  background: rgba(230, 162, 60, 0.10);
}
</style>

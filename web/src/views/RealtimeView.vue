<template>
  <div class="page">
    <el-alert v-if="loadError" type="error" :closable="false" show-icon :title="loadError" data-test="realtime-load-error" />
    <el-alert v-if="snapshotError" type="error" :closable="false" show-icon :title="snapshotError" data-test="realtime-snapshot-error" />
    <div class="panel">
      <div class="toolbar">
        <el-select v-model="filterDevice" clearable filterable :placeholder="$t('realtimeView.kfb9b2c')" style="width: 180px" data-test="device-filter">
          <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
        </el-select>
        <el-input v-model="search" clearable :placeholder="$t('realtimeView.kc23497')" style="width: 200px" data-test="realtime-search" />
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
        <el-button size="small" :icon="Refresh" :loading="snapshotLoading" :disabled="frozen" data-test="snapshot-refresh" @click="loadSnapshot">{{ $t('realtimeView.ka4fa6a') }}</el-button>
      </div>
      <el-alert v-if="frozen" type="warning" :closable="false" show-icon :title="$t('realtimeView.frozenHint', [formatTime(frozenAt)])" data-test="realtime-frozen">
        <el-button link type="primary" @click="toggleFreeze">{{ $t('realtimeView.resume') }}</el-button>
      </el-alert>

    </div>

    <div class="panel">
      <div class="toolbar">
        <strong>{{ $t('realtimeView.k60a3fe') }}</strong>
        <span class="muted tiny">{{ $t('realtimeView.kc253d0') }}</span>
        <span class="spacer" />
        <el-tag type="info" size="small">{{ $t('realtimeView.keb7bc0', [visibleTags.length]) }}</el-tag>
        <el-tag v-if="abnormalCount" type="danger" size="small" data-test="abnormal-count">{{ $t('realtimeView.abnormalCount', [abnormalCount]) }}</el-tag>
      </div>
      <el-table :data="pagedTags" row-key="id" size="small" max-height="calc(100vh - 330px)" :empty-text="loadError || $t('realtimeView.k1b4bde')" :row-class-name="rowClass">
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
            <el-tag v-if="unread(row.id)" type="info" size="small" data-test="quality-unread">{{ $t('realtimeView.unread') }}</el-tag>
            <el-tag v-else :type="qualityType(qualityOf(row.id))" size="small">
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
        <el-table-column :label="$t('realtimeView.kf3ea6d')" width="150" fixed="right">
          <template #default="{ row }">
            <router-link :to="{ path: '/trends', query: { tagId: String(row.id) } }" class="tiny" data-test="realtime-trend">{{ $t('realtimeView.viewTrend') }}</router-link>
            <el-button v-if="row.writable && canControlTag(row)" link type="primary" data-test="realtime-write" @click="openWrite(row)">
              {{ $t('realtimeView.k2fb871') }}
            </el-button>
            <span v-else class="muted tiny">{{ row.writable ? $t('realtimeView.kb44182') : $t('realtimeView.kffc1d0') }}</span>
          </template>
        </el-table-column>
      </el-table>
      <el-pagination :current-page="page" :page-size="100" :total="visibleTags.length" :pager-count="5" layout="total, prev, pager, next" hide-on-single-page data-test="tag-pagination" @update:current-page="page = $event" />
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
        <el-alert v-if="writeError" type="error" :closable="false" show-icon :title="writeError" style="margin-top: 8px" data-test="realtime-write-error" />
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
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
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
  canActOnDevice,
  formatTime,
  formatValue,
  qualityLabels,
  qualityType
} from '../utils'
import { tr } from '../i18n'

const auth = useAuthStore()
const devices = ref<Device[]>([])
const tags = ref<Tag[]>([])
const deviceIndex = computed(() => new Map(devices.value.map(device => [device.id, device])))
const tagIndex = computed(() => new Map(tags.value.map(tag => [tag.name, tag])))
const loadError = ref('')
const snapshotError = ref('')
function canControlTag(tag: Tag): boolean {
  return canActOnDevice(auth.user, 'Control', devices.value, tag.deviceId)
}
const filterDevice = ref<number | undefined>()
const search = ref('')
// 现场排查时先看坏质量：按质量筛选 + 异常行高亮，比逐个扫表快得多
const qualityFilter = ref<'all' | 'abnormal' | 'good'>('all')
// 暂停刷新：数值不断变化时冻结表格，便于读数与截图
const frozen = ref(false)
const frozenAt = ref<number | null>(null), snapshotLoading = ref(false)
let snapshotSeq = 0, disposed = false
const values = reactive<Record<number, TagValue>>({})
// 连接状态由共享连接模块统一维护（disconnected / connecting / connected / reconnecting）
const hubState = realtimeHubState()

let subscription: TagSubscription | undefined
let disposeReconnected: (() => void) | undefined

function matchesQuality(tagId: number): boolean {
  if (qualityFilter.value === 'all') return true
  if (unread(tagId)) return false
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

const page = ref(1)
const pagedTags = computed(() => visibleTags.value.slice((page.value - 1) * 100, page.value * 100))
watch([filterDevice, search, qualityFilter], () => { page.value = 1 }, { flush: 'sync' })
watch(() => visibleTags.value.length, total => {
  page.value = Math.min(page.value, Math.max(1, Math.ceil(total / 100)))
})

// 当前筛选范围内质量非良好的测点数（未采集到值的测点也算），用于顶部一眼可见
const abnormalCount = computed(() => visibleTags.value.filter((tag) => !unread(tag.id) && qualityOf(tag.id) !== 'Good').length)

function tagByName(name: string): Tag | undefined {
  return tagIndex.value.get(name)
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


function qualityOf(tagId: number): TagQuality {
  return values[tagId]?.quality ?? 'Bad'
}

function unread(tagId: number): boolean {
  return !!snapshotError.value && values[tagId] == null
}

function valueClass(tagId: number): string {
  if (unread(tagId)) return ''
  const quality = qualityOf(tagId)
  if (quality === 'Good') return 'tag-good'
  if (quality === 'CommFailure') return 'tag-fail'
  return 'tag-bad'
}

// 异常质量整行着色，避免只靠数值颜色在长表里漏看
function rowClass({ row }: { row: Tag }): string {
  if (unread(row.id)) return ''
  const quality = qualityOf(row.id)
  if (quality === 'Good') return ''
  return quality === 'Uncertain' ? 'row-quality-warn' : 'row-quality-bad'
}

function deviceName(id: number | null): string {
  if (id === null) return tr('realtimeView.k4e0387')
  return deviceIndex.value.get(id)?.name ?? `#${id}`
}

async function loadSnapshot(): Promise<void> {
  if (frozen.value || disposed) return
  const seq = ++snapshotSeq
  snapshotLoading.value = true
  try {
    const snapshot = await realtimeApi.get(tags.value.map((x) => x.id))
    if (seq !== snapshotSeq || disposed || frozen.value) return
    for (const item of snapshot) {
      if (!values[item.tagId] || item.ts >= values[item.tagId].ts) values[item.tagId] = item
    }
    snapshotError.value = ''
  } catch (error) {
    if (seq !== snapshotSeq || disposed || frozen.value) return
    snapshotError.value = errorMessage(error, tr('realtimeView.k990c88'))
  } finally {
    if (seq === snapshotSeq) snapshotLoading.value = false
  }
}

function toggleFreeze(): void {
  frozen.value = !frozen.value
  if (frozen.value) {
    frozenAt.value = Date.now()
    snapshotSeq++
    snapshotLoading.value = false
  } else frozenAt.value = null
  // 恢复时补一次快照，冻结期间的变化不会丢
  if (!frozen.value) void loadSnapshot()
}

function applyValue(value: TagValue): void {
  // 暂停期间丢弃推送，恢复时统一拉快照，避免读数时表格跳动
  if (frozen.value || disposed) return
  if (values[value.tagId] && value.ts < values[value.tagId].ts) return
  values[value.tagId] = value
}

// 共享连接按 tagId 只回传本页订阅的测点，无需再自行过滤；
// 订阅走引用计数，离开页面时释放，不影响其他页面订阅的同一测点。
async function startSubscription(): Promise<void> {
  const next = await subscribeTags(tags.value.map((x) => x.id), applyValue)
  if (disposed) { next.dispose(); return }
  // 先订阅新的再释放旧的：重叠测点不会出现取消/重订抖动
  subscription?.dispose()
  subscription = next
}

const confirmDialog = ref<InstanceType<typeof ControlConfirmDialog> | null>(null)
const writeVisible = ref(false)
const writing = ref(false)
const writeError = ref('')
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
  writeRequestId.value = null; receiptId.value = null; writeError.value = ''
  writeTag.value = tag
  writeValue.value = values[tag.id]?.value ?? tag.minValue ?? 0
  writeVisible.value = true
}

async function submitWrite(): Promise<void> {
  if (!writeTag.value) return
  writing.value = true
  writeError.value = ''
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
      writeError.value = result.message || tr('realtimeView.ke92dc2')
    }
  } catch (error) {
    if (disposed) return
    if (devices.value.find(x => x.id === writeTag.value?.deviceId)?.gatewayId != null) receiptId.value = writeRequestId.value
    writeError.value = errorMessage(error, tr('realtimeView.ke92dc2'))
  } finally {
    writing.value = false
  }
}

onMounted(async () => {
  try {
    ;[devices.value, tags.value] = await Promise.all([deviceApi.list(), tagApi.list()])
    loadError.value = ''
  } catch (error) {
    loadError.value = errorMessage(error, tr('realtimeView.k9060ce'))
    return
  }
  if (disposed) return
  await loadSnapshot()
  if (disposed) return
  await startSubscription()
  if (disposed) return
  // 重连后补拉快照，补齐断线期间缺失的值（重新订阅由共享连接模块负责）
  disposeReconnected = onHubReconnected(() => {
    void loadSnapshot()
  })
})

onBeforeUnmount(() => {
  disposed = true
  snapshotSeq++
  subscription?.dispose()
  disposeReconnected?.()
})
</script>

<style scoped>

/* 异常质量的整行着色：长表里比只看数值颜色更容易发现 */
:deep(.row-quality-bad) {
  background: rgba(245, 108, 108, 0.10);
}
:deep(.row-quality-warn) {
  background: rgba(230, 162, 60, 0.10);
}
</style>

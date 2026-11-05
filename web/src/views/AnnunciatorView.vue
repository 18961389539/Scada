<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <span class="hint">{{ $t('annunciatorView.subtitle') }}</span>
        <span class="spacer" />
        <el-button data-test="ann-refresh" @click="load">{{ $t('annunciatorView.refresh') }}</el-button>
        <el-button v-if="canConfigure" type="primary" data-test="ann-add" @click="openEdit()">{{ $t('annunciatorView.add') }}</el-button>
      </div>
      <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="annunciator-load-error" />
      <el-alert v-if="catalogError" :title="catalogError" type="error" :closable="false" show-icon data-test="ann-catalog-error" />
      <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="ann-action-error" />
      <div data-test="ann-table">
      <el-table :data="panels" size="small" v-loading="loading" :empty-text="loadError && !panels.length ? loadError : $t('annunciatorView.empty')">
        <el-table-column prop="name" :label="$t('annunciatorView.name')" min-width="140" show-overflow-tooltip />
        <el-table-column :label="$t('annunciatorView.area')" width="120" show-overflow-tooltip>
          <template #default="{ row }">{{ row.areaId ? areaName(row.areaId) : $t('annunciatorView.global') }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.enabled')" width="88" show-overflow-tooltip>
          <template #default="{ row }"><el-tag :class="{ 'tag-quiet': row.enabled }" :type="row.enabled ? undefined : 'warning'" size="small">{{ row.enabled ? $t('annunciatorView.yes') : $t('annunciatorView.no') }}</el-tag></template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.latch')" width="100" show-overflow-tooltip>
          <template #default="{ row }">{{ row.latchMode === 'Latch' ? $t('annunciatorView.latchOn') : $t('annunciatorView.follow') }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.outputs')" width="90" show-overflow-tooltip>
          <template #default="{ row }">{{ row.outputs?.length ?? 0 }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.status')" min-width="180" show-overflow-tooltip>
          <template #default="{ row }">
            <template v-if="statusOf(row.id)">
              <el-tag v-if="statusOf(row.id)!.lastError" type="danger" size="small" :title="statusOf(row.id)!.lastError!" data-test="ann-last-error">{{ $t('designReview.annOutputError') }}</el-tag>
              <el-tag v-if="statusOf(row.id)!.silenced" type="info" size="small" data-test="ann-silenced">{{ statusOf(row.id)!.silenceUntilTs ? $t('designReview.silencedUntil', [formatClock(statusOf(row.id)!.silenceUntilTs!)]) : $t('annunciatorView.silenced') }}</el-tag>
              <el-tag v-else-if="statusOf(row.id)!.drivingLevel" :type="levelType(statusOf(row.id)!.drivingLevel!)" size="small">{{ levelLabel(statusOf(row.id)!.drivingLevel) }}</el-tag>
              <span v-else class="muted tiny">{{ $t('annunciatorView.idle') }}</span>
              <span v-if="statusOf(row.id)!.horn" class="tiny"> · {{ $t('annunciatorView.hornOn') }}</span>
            </template>
          </template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.actions')" width="260" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canAckPanel(row.areaId)" link :type="statusOf(row.id)?.horn || statusOf(row.id)?.drivingLevel ? 'warning' : undefined" data-test="ann-silence" @click="silence(row)">{{ $t('annunciatorView.silence') }}</el-button>
            <el-button v-if="canAckPanel(row.areaId) && statusOf(row.id)?.silenced" link @click="clearSilence(row)">{{ $t('annunciatorView.clearSilence') }}</el-button>
            <el-button v-if="canAckPanel(row.areaId)" link :loading="testing === row.id" data-test="ann-test" @click="test(row)">{{ $t('annunciatorView.test') }}</el-button>
            <el-button v-if="canConfigurePanel(row.areaId)" link type="primary" data-test="ann-edit" @click="openEdit(row)">{{ $t('annunciatorView.edit') }}</el-button>
            <el-popconfirm v-if="canConfigurePanel(row.areaId)" :title="$t('annunciatorView.confirmDelete')" @confirm="remove(row.id)">
              <template #reference><el-button link type="danger">{{ $t('annunciatorView.delete') }}</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
      </div>
    </div>

    <el-dialog v-model="dialog" :title="form.id ? $t('annunciatorView.editTitle') : $t('annunciatorView.addTitle')" width="720px" destroy-on-close>
      <el-alert v-if="saveError" :title="saveError" type="error" :closable="false" show-icon data-test="ann-save-error" />
      <el-form label-width="120px" size="small">
        <el-form-item :label="$t('annunciatorView.name')"><el-input v-model="form.name" data-test="ann-name" /></el-form-item>
        <el-form-item :label="$t('annunciatorView.area')">
          <el-select v-model="form.areaId" data-test="ann-area" :clearable="isGlobalConfigure" :placeholder="$t('annunciatorView.global')" style="width: 100%">
            <el-option v-for="a in configurableAreas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('annunciatorView.enabled')"><el-switch v-model="form.enabled" /></el-form-item>
        <el-form-item :label="$t('annunciatorView.latch')">
          <el-radio-group v-model="form.latchMode">
            <el-radio-button value="Follow">{{ $t('annunciatorView.follow') }}</el-radio-button>
            <el-radio-button value="Latch">{{ $t('annunciatorView.latchOn') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="$t('annunciatorView.silenceMin')"><el-input-number v-model="form.silenceMinutes" :min="1" :max="120" /></el-form-item>
        <el-form-item :label="$t('annunciatorView.browserSound')"><el-switch v-model="form.browserSound" /></el-form-item>
        <el-divider>{{ $t('annunciatorView.outputs') }}</el-divider>
        <div v-for="(o, i) in form.outputs" :key="i" class="out-row">
          <el-select v-model="o.kind" style="width: 130px" data-test="ann-output-kind">
            <el-option v-for="k in kinds" :key="k" :label="k" :value="k" :disabled="form.outputs.some((x, j) => j !== i && x.kind === k)" />
          </el-select>
          <el-select v-model="o.tagId" filterable style="width: 220px" :placeholder="$t('annunciatorView.tag')">
            <el-option v-for="tg in writableTags" :key="tg.id" :label="`#${tg.id} ${tg.name}`" :value="tg.id" />
          </el-select>
          <el-input-number v-model="o.activeValue" :step="1" />
          <el-input-number v-model="o.inactiveValue" :step="1" />
          <el-button link type="danger" @click="form.outputs.splice(i, 1)">{{ $t('annunciatorView.delete') }}</el-button>
        </div>
        <el-button v-if="freeKind" size="small" data-test="ann-add-output" @click="form.outputs.push({ kind: freeKind, tagId: writableTags[0]?.id ?? 0, activeValue: 1, inactiveValue: 0, enabled: true })">{{ $t('annunciatorView.addOutput') }}</el-button>
      </el-form>
      <template #footer>
        <el-button @click="dialog = false">{{ $t('annunciatorView.cancel') }}</el-button>
        <el-button type="primary" data-test="ann-save" :loading="saving" @click="save">{{ $t('annunciatorView.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { errorMessage } from '../api/http'
import { annunciatorApi, type AnnunciatorPanel, type AnnunciatorStatus, type AnnunciatorChannelKind, type AnnunciatorLevelMap } from '../api/annunciators'
import { areaApi, deviceApi, tagApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { canActOnDevice, levelLabel, levelType } from '../utils'
import { can, canGlobal } from '../access/policies'
import { currentLocale, tr } from '../i18n'
import type { Area, Device, Tag } from '../types'

const auth = useAuthStore()
const canConfigure = computed(() => can(auth.user, 'Configure'))
const canAck = computed(() => can(auth.user, 'AckAlarm'))
// 服务端按面板的 AreaId 判权：区域内面板要该区域权限，全局面板（areaId 为空）配置需全局 Configure、
// 操作只需任意区域的 AckAlarm。界面必须同口径，否则区域操作员会对着必被拒的按钮点。
const isGlobalConfigure = computed(() => canGlobal(auth.user, 'Configure'))
const canConfigurePanel = (areaId?: number | null): boolean =>
  areaId == null ? isGlobalConfigure.value : can(auth.user, 'Configure', areaId)
const canAckPanel = (areaId?: number | null): boolean =>
  areaId == null ? canAck.value : can(auth.user, 'AckAlarm', areaId)
const loading = ref(false), saving = ref(false), dialog = ref(false), loadError = ref(''), catalogError = ref(''), actionError = ref(''), saveError = ref('')
const panels = ref<AnnunciatorPanel[]>([])
const statuses = ref<AnnunciatorStatus[]>([])
const areas = ref<Area[]>([])
const devices = ref<Device[]>([])
const configurableAreas = computed(() => areas.value.filter(a => can(auth.user, 'Configure', a.id)))
const writableTags = ref<Tag[]>([])
function canBindOutput(tag: Tag): boolean {
  return tag.writable && tag.kind !== 'Calculated' && tag.kind !== 'Derived' && canActOnDevice(auth.user, 'Configure', devices.value, tag.deviceId)
}
const kinds: AnnunciatorChannelKind[] = ['Horn', 'Beacon', 'TowerRed', 'TowerYellow', 'TowerBlue', 'TowerGreen']

const form = reactive({
  id: 0 as number,
  name: '',
  areaId: null as number | null,
  enabled: true,
  latchMode: 'Follow' as 'Follow' | 'Latch',
  silenceMinutes: 5,
  flashOnMs: 500,
  flashOffMs: 500,
  criticalFlashOnMs: 250,
  criticalFlashOffMs: 250,
  browserSound: true,
  outputs: [] as { kind: AnnunciatorChannelKind; tagId: number; activeValue: number; inactiveValue: number; enabled: boolean }[],
  // 本页不编辑等级映射，但保存是整体覆盖：必须原样带回，否则每次保存都被重置为默认
  levels: [] as AnnunciatorLevelMap[]
})
// 服务端每个通道只接受一个输出
const freeKind = computed(() => kinds.find(k => !form.outputs.some(o => o.kind === k)))

function areaName(id: number) { return areas.value.find((a) => a.id === id)?.name ?? `#${id}` }
// 新建时按当前用户权限给默认区域：有全局 Configure 才默认「全局」，否则落到其可配置区域，
// 避免区域维护员填完表单保存时才被 403。
function defaultPanelArea(): number | null {
  if (canGlobal(auth.user, 'Configure')) return null
  return configurableAreas.value[0]?.id ?? null
}
function statusOf(id: number) { return statuses.value.find((s) => s.panelId === id) }

async function load() {
  loading.value = true
  try {
    ;[panels.value, statuses.value] = await Promise.all([annunciatorApi.list(), annunciatorApi.status()])
    loadError.value = ''
  } catch (e) {
    loadError.value = errorMessage(e, tr('annunciatorView.loadFail'))
  }
  finally { loading.value = false }
}

function openEdit(row?: AnnunciatorPanel) {
  form.id = row?.id ?? 0
  form.name = row?.name ?? ''
  form.areaId = row?.areaId ?? defaultPanelArea()
  form.enabled = row?.enabled ?? true
  form.latchMode = row?.latchMode ?? 'Follow'
  form.silenceMinutes = row?.silenceMinutes ?? 5
  form.browserSound = row?.browserSound ?? true
  form.flashOnMs = row?.flashOnMs ?? 500
  form.flashOffMs = row?.flashOffMs ?? 500
  form.criticalFlashOnMs = row?.criticalFlashOnMs ?? 250
  form.criticalFlashOffMs = row?.criticalFlashOffMs ?? 250
  form.outputs = (row?.outputs ?? []).map((o) => ({ kind: o.kind, tagId: o.tagId, activeValue: o.activeValue, inactiveValue: o.inactiveValue, enabled: o.enabled }))
  form.levels = (row?.levels ?? []).map(({ id: _id, ...level }) => level)
  saveError.value = ''
  dialog.value = true
}

async function save() {
  saving.value = true
  saveError.value = ''
  try {
    const input = {
      name: form.name, areaId: form.areaId, enabled: form.enabled, latchMode: form.latchMode, silenceMinutes: form.silenceMinutes,
      flashOnMs: form.flashOnMs, flashOffMs: form.flashOffMs, criticalFlashOnMs: form.criticalFlashOnMs, criticalFlashOffMs: form.criticalFlashOffMs,
      browserSound: form.browserSound, outputs: form.outputs, levels: form.levels
    }
    if (form.id) await annunciatorApi.update(form.id, input)
    else await annunciatorApi.create(input)
    saveError.value = ''
    dialog.value = false
    await load()
    ElMessage.success(tr('annunciatorView.saved'))
  } catch (e) { saveError.value = errorMessage(e, tr('annunciatorView.saveFail')) }
  finally { saving.value = false }
}

async function remove(id: number) {
  actionError.value = ''
  try { await annunciatorApi.remove(id); actionError.value = ''; await load() }
  catch (e) { actionError.value = errorMessage(e, tr('annunciatorView.deleteFail')) }
}
async function silence(row: AnnunciatorPanel) {
  actionError.value = ''
  try { await annunciatorApi.silence(row.id); actionError.value = ''; await load(); ElMessage.success(tr('annunciatorView.silencedOk')) }
  catch (e) { actionError.value = errorMessage(e, tr('annunciatorView.silenceFail')) }
}
async function clearSilence(row: AnnunciatorPanel) {
  actionError.value = ''
  try { await annunciatorApi.clearSilence(row.id); actionError.value = ''; await load() }
  catch (e) { actionError.value = errorMessage(e, tr('annunciatorView.silenceFail')) }
}
// 试验会真实写现场输出（服务端约 1.5 秒后交回正常联动），必须先说清楚影响
const testing = ref<number | null>(null)
async function test(row: AnnunciatorPanel) {
  if (testing.value !== null) return
  const outputs = (row.outputs ?? []).filter(o => o.enabled)
  const message = outputs.length
    ? tr('designReview.annTestConfirm', [row.name, outputs.map(o => o.kind).join('、')])
    : tr('designReview.annTestNoOutputs', [row.name])
  try { await ElMessageBox.confirm(message, tr('annunciatorView.test'), { type: 'warning' }) } catch { return }
  testing.value = row.id
  actionError.value = ''
  try { await annunciatorApi.test(row.id); actionError.value = ''; ElMessage.success(tr('annunciatorView.testOk')); await load() }
  catch (e) { actionError.value = errorMessage(e, tr('annunciatorView.testFail')) }
  finally { testing.value = null }
}
function formatClock(ts: number): string {
  return new Date(ts).toLocaleTimeString(currentLocale(), { hour: '2-digit', minute: '2-digit' })
}

onMounted(async () => {
  try {
    const [areaList, tags, deviceList] = await Promise.all([areaApi.list(), tagApi.list(), deviceApi.list()])
    areas.value = areaList
    devices.value = deviceList
    writableTags.value = tags.filter(canBindOutput)
    catalogError.value = ''
  } catch (e) { catalogError.value = errorMessage(e, tr('annunciatorView.catalogFailed')) }
  await load()
})
</script>

<style scoped>
.out-row { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; flex-wrap: wrap; }
</style>

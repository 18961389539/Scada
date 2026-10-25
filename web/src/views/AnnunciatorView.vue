<template>
  <div class="page">
    <div class="panel">
      <div class="toolbar">
        <h2>{{ $t('annunciatorView.title') }}</h2>
        <span class="muted">{{ $t('annunciatorView.subtitle') }}</span>
        <span class="spacer" />
        <el-button data-test="ann-refresh" @click="load">{{ $t('annunciatorView.refresh') }}</el-button>
        <el-button v-if="canConfigure" type="primary" data-test="ann-add" @click="openEdit()">{{ $t('annunciatorView.add') }}</el-button>
      </div>
      <div data-test="ann-table">
      <el-table :data="panels" size="small" v-loading="loading" :empty-text="$t('annunciatorView.empty')">
        <el-table-column prop="name" :label="$t('annunciatorView.name')" min-width="140" />
        <el-table-column :label="$t('annunciatorView.area')" width="120">
          <template #default="{ row }">{{ row.areaId ? areaName(row.areaId) : $t('annunciatorView.global') }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.enabled')" width="80">
          <template #default="{ row }">{{ row.enabled ? $t('annunciatorView.yes') : $t('annunciatorView.no') }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.latch')" width="100">
          <template #default="{ row }">{{ row.latchMode === 'Latch' ? $t('annunciatorView.latchOn') : $t('annunciatorView.follow') }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.outputs')" width="90">
          <template #default="{ row }">{{ row.outputs?.length ?? 0 }}</template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.status')" min-width="180">
          <template #default="{ row }">
            <template v-if="statusOf(row.id)">
              <el-tag v-if="statusOf(row.id)!.silenced" type="info" size="small">{{ $t('annunciatorView.silenced') }}</el-tag>
              <el-tag v-else-if="statusOf(row.id)!.drivingLevel" :type="levelType(statusOf(row.id)!.drivingLevel!)" size="small">{{ statusOf(row.id)!.drivingLevel }}</el-tag>
              <span v-else class="muted tiny">{{ $t('annunciatorView.idle') }}</span>
              <span v-if="statusOf(row.id)!.horn" class="tiny"> · {{ $t('annunciatorView.hornOn') }}</span>
            </template>
          </template>
        </el-table-column>
        <el-table-column :label="$t('annunciatorView.actions')" width="260" fixed="right">
          <template #default="{ row }">
            <el-button v-if="canAck" link type="warning" data-test="ann-silence" @click="silence(row)">{{ $t('annunciatorView.silence') }}</el-button>
            <el-button v-if="canAck && statusOf(row.id)?.silenced" link @click="clearSilence(row)">{{ $t('annunciatorView.clearSilence') }}</el-button>
            <el-button v-if="canAck" link data-test="ann-test" @click="test(row)">{{ $t('annunciatorView.test') }}</el-button>
            <el-button v-if="canConfigure" link type="primary" data-test="ann-edit" @click="openEdit(row)">{{ $t('annunciatorView.edit') }}</el-button>
            <el-popconfirm v-if="canConfigure" :title="$t('annunciatorView.confirmDelete')" @confirm="remove(row.id)">
              <template #reference><el-button link type="danger">{{ $t('annunciatorView.delete') }}</el-button></template>
            </el-popconfirm>
          </template>
        </el-table-column>
      </el-table>
      </div>
    </div>

    <el-dialog v-model="dialog" :title="form.id ? $t('annunciatorView.editTitle') : $t('annunciatorView.addTitle')" width="720px" destroy-on-close>
      <el-form label-width="120px" size="small">
        <el-form-item :label="$t('annunciatorView.name')"><el-input v-model="form.name" data-test="ann-name" /></el-form-item>
        <el-form-item :label="$t('annunciatorView.area')">
          <el-select v-model="form.areaId" clearable :placeholder="$t('annunciatorView.global')" style="width: 100%">
            <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
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
          <el-select v-model="o.kind" style="width: 130px">
            <el-option v-for="k in kinds" :key="k" :label="k" :value="k" />
          </el-select>
          <el-select v-model="o.tagId" filterable style="width: 220px" :placeholder="$t('annunciatorView.tag')">
            <el-option v-for="tg in writableTags" :key="tg.id" :label="`#${tg.id} ${tg.name}`" :value="tg.id" />
          </el-select>
          <el-input-number v-model="o.activeValue" :step="1" />
          <el-input-number v-model="o.inactiveValue" :step="1" />
          <el-button link type="danger" @click="form.outputs.splice(i, 1)">{{ $t('annunciatorView.delete') }}</el-button>
        </div>
        <el-button size="small" @click="form.outputs.push({ kind: 'Horn', tagId: writableTags[0]?.id ?? 0, activeValue: 1, inactiveValue: 0, enabled: true })">{{ $t('annunciatorView.addOutput') }}</el-button>
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
import { ElMessage } from 'element-plus'
import { annunciatorApi, type AnnunciatorPanel, type AnnunciatorStatus, type AnnunciatorChannelKind } from '../api/annunciators'
import { areaApi, tagApi } from '../api'
import { useAuthStore } from '../stores/auth'
import { hasAny, levelType } from '../utils'
import { tr } from '../i18n'
import type { Area, Tag } from '../types'

const auth = useAuthStore()
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
const canAck = computed(() => hasAny(auth.user, 'AckAlarm'))
const loading = ref(false), saving = ref(false), dialog = ref(false)
const panels = ref<AnnunciatorPanel[]>([])
const statuses = ref<AnnunciatorStatus[]>([])
const areas = ref<Area[]>([])
const writableTags = ref<Tag[]>([])
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
  outputs: [] as { kind: AnnunciatorChannelKind; tagId: number; activeValue: number; inactiveValue: number; enabled: boolean }[]
})

function areaName(id: number) { return areas.value.find((a) => a.id === id)?.name ?? `#${id}` }
function statusOf(id: number) { return statuses.value.find((s) => s.panelId === id) }

async function load() {
  loading.value = true
  try {
    ;[panels.value, statuses.value] = await Promise.all([annunciatorApi.list(), annunciatorApi.status()])
  } catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('annunciatorView.loadFail')) }
  finally { loading.value = false }
}

function openEdit(row?: AnnunciatorPanel) {
  form.id = row?.id ?? 0
  form.name = row?.name ?? ''
  form.areaId = row?.areaId ?? null
  form.enabled = row?.enabled ?? true
  form.latchMode = row?.latchMode ?? 'Follow'
  form.silenceMinutes = row?.silenceMinutes ?? 5
  form.browserSound = row?.browserSound ?? true
  form.outputs = (row?.outputs ?? []).map((o) => ({ kind: o.kind, tagId: o.tagId, activeValue: o.activeValue, inactiveValue: o.inactiveValue, enabled: o.enabled }))
  dialog.value = true
}

async function save() {
  saving.value = true
  try {
    const input = {
      name: form.name, areaId: form.areaId, enabled: form.enabled, latchMode: form.latchMode, silenceMinutes: form.silenceMinutes,
      flashOnMs: form.flashOnMs, flashOffMs: form.flashOffMs, criticalFlashOnMs: form.criticalFlashOnMs, criticalFlashOffMs: form.criticalFlashOffMs,
      browserSound: form.browserSound, outputs: form.outputs, levels: [] as any[]
    }
    if (form.id) await annunciatorApi.update(form.id, input)
    else await annunciatorApi.create(input)
    dialog.value = false
    await load()
    ElMessage.success(tr('annunciatorView.saved'))
  } catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('annunciatorView.saveFail')) }
  finally { saving.value = false }
}

async function remove(id: number) {
  try { await annunciatorApi.remove(id); await load() }
  catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('annunciatorView.deleteFail')) }
}
async function silence(row: AnnunciatorPanel) {
  try { await annunciatorApi.silence(row.id); await load(); ElMessage.success(tr('annunciatorView.silencedOk')) }
  catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('annunciatorView.silenceFail')) }
}
async function clearSilence(row: AnnunciatorPanel) {
  try { await annunciatorApi.clearSilence(row.id); await load() }
  catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('annunciatorView.silenceFail')) }
}
async function test(row: AnnunciatorPanel) {
  try { await annunciatorApi.test(row.id); ElMessage.success(tr('annunciatorView.testOk')) }
  catch (e: any) { ElMessage.error(e?.response?.data?.message ?? tr('annunciatorView.testFail')) }
}

onMounted(async () => {
  try {
    const [areaList, tags] = await Promise.all([areaApi.list(), tagApi.list()])
    areas.value = areaList
    writableTags.value = tags.filter((t) => t.writable && t.kind !== 'Calculated' && t.kind !== 'Derived')
  } catch { /* ignore */ }
  await load()
})
</script>

<style scoped>
.out-row { display: flex; gap: 8px; align-items: center; margin-bottom: 8px; flex-wrap: wrap; }
</style>

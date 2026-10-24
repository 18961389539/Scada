<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>计算任务</strong>
      <el-input v-model="search" placeholder="名称或类型" clearable style="width: 220px" data-test="calc-job-search" /><span class="spacer" />
      <el-switch v-model="automatic" active-text="每 3 秒刷新" />
      <el-button :loading="loading" @click="refresh()">刷新</el-button>
      <el-button v-if="canConfigure" type="primary" data-test="calc-job-new" @click="open(null)">新建计算任务</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
    <p class="tiny muted">累计器、设备运行小时、能耗积分与受限脚本。结果写入自动生成的派生测点（名称为“任务名.输出”），可用于趋势、报警、报表和画面。运行状态每 10 秒落盘，重启后继续累计；周期复位前自动留快照。</p>
    <el-table :data="filtered" v-loading="loading" row-key="job.id" empty-text="尚无计算任务" data-test="calc-job-table">
      <el-table-column label="任务" min-width="170"><template #default="{ row }"><strong>#{{ row.job.id }} · {{ row.job.name }}</strong>
        <div class="tiny muted">{{ kindLabels[row.job.kind as CalcJobKind] }} · {{ areaName(row.job.areaId) }} · 版本 {{ row.job.revision }}</div></template></el-table-column>
      <el-table-column label="输入 / 周期" min-width="160"><template #default="{ row }">
        <span v-if="row.job.kind !== 'Script'">#{{ row.job.inputTagId }} {{ tagName(row.job.inputTagId) }}</span><span v-else>读 {{ row.job.readTags || '无' }} · 写 {{ row.job.writeTags || '无' }}</span>
        <div class="tiny muted">{{ row.job.enabled ? '启用' : '停用' }} · {{ row.job.intervalMs }} ms · 复位 {{ resetLabels[row.job.resetPeriod as CalcResetPeriod] }}</div></template></el-table-column>
      <el-table-column label="当前结果" min-width="260"><template #default="{ row }">
        <div v-for="output in row.outputs" :key="output.tagId" class="tiny"><code>{{ output.name }}</code> = <strong>{{ value(row, output.name) }}</strong> <span class="muted">#{{ output.tagId }}</span></div>
        <el-tag v-if="row.status?.values?.maintenanceDue === 1" type="danger" size="small">到期需维护</el-tag></template></el-table-column>
      <el-table-column label="周期 / 提示" min-width="200"><template #default="{ row }"><div class="tiny">{{ row.status?.periodKey || '--' }}</div>
        <div class="tiny" :class="row.status?.lastError ? 'danger' : 'muted'">{{ row.status?.lastError || row.status?.lastEvent || '' }}</div>
        <div v-if="row.status?.lastScriptMs != null" class="tiny muted">脚本 {{ row.status.lastScriptSteps }} 步 / {{ row.status.lastScriptMs.toFixed(2) }} ms</div></template></el-table-column>
      <el-table-column label="操作" width="230"><template #default="{ row }">
        <el-button v-if="canConfigure" link type="primary" @click="open(row.job)">编辑</el-button>
        <el-button link @click="showSnapshots(row.job)">快照</el-button>
        <el-button v-if="canControl && row.job.kind !== 'Script'" link type="warning" @click="reset(row.job)">复位</el-button>
        <el-button v-if="canControl && row.job.kind === 'RunHours'" link type="success" @click="maintenance(row.job)">维护完成</el-button>
        <el-button v-if="canConfigure" link type="danger" @click="remove(row.job)">删除</el-button></template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="editor" :title="editing === null ? '新建计算任务' : '编辑计算任务'" width="860px" :close-on-click-modal="false">
    <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" data-test="calc-job-error" />
    <el-form label-width="130px" :disabled="saving" @submit.prevent>
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item label="名称" required><el-input v-model="form.name" maxlength="80" data-test="calc-job-name" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item label="类型"><el-select v-model="form.kind" :disabled="editing !== null" data-test="calc-job-kind">
          <el-option v-for="(label, key) in kindLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
        <el-col :span="12"><el-form-item label="区域" required><el-select v-model="form.areaId" data-test="calc-job-area"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item></el-col>
        <el-col :span="6"><el-form-item label="启用"><el-switch v-model="form.enabled" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item label="周期(ms)" label-width="80px"><el-input-number v-model="form.intervalMs" :min="200" :max="3600000" :step="500" controls-position="right" /></el-form-item></el-col>
      </el-row>
      <template v-if="form.kind !== 'Script'">
        <el-form-item :label="inputLabel" required><el-select v-model="form.inputTagId" filterable style="width: 100%" data-test="calc-job-input">
          <el-option v-for="t in tags" :key="t.id" :value="t.id" :label="`#${t.id} · ${t.name}${t.unit ? ` (${t.unit})` : ''}`" /></el-select></el-form-item>
        <el-row :gutter="12">
          <el-col v-if="form.kind === 'Accumulator'" :span="12"><el-form-item label="输入方式"><el-radio-group v-model="form.mode"><el-radio-button value="Pulse">脉冲计数</el-radio-button><el-radio-button value="Flow">瞬时流量积分</el-radio-button></el-radio-group></el-form-item></el-col>
          <el-col v-if="form.kind !== 'RunHours'" :span="12"><el-form-item :label="form.kind === 'Accumulator' && form.mode === 'Pulse' ? '脉冲当量' : '换算系数'"><el-input-number v-model="form.factor" :min="0" :step="0.1" :precision="8" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'Accumulator' && form.mode === 'Pulse'" :span="12"><el-form-item label="计数器满量程"><el-input-number v-model="form.counterMax" :min="1" placeholder="空=回落视为复位" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'Accumulator' && form.mode === 'Pulse'" :span="12"><el-form-item label="单次增量上限"><el-input-number v-model="form.maxDelta" :min="0" placeholder="空=不限制" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'RunHours'" :span="12"><el-form-item label="运行阈值 (>)"><el-input-number v-model="form.runThreshold" :step="0.5" controls-position="right" /></el-form-item></el-col>
          <el-col v-if="form.kind === 'RunHours'" :span="12"><el-form-item label="维护阈值(小时)"><el-input-number v-model="form.maintenanceHours" :min="0" placeholder="空=不提醒" controls-position="right" data-test="calc-job-maintenance" /></el-form-item></el-col>
          <el-col v-if="!(form.kind === 'Accumulator' && form.mode === 'Pulse')" :span="12"><el-form-item label="坏质量处理"><el-select v-model="form.badQuality">
            <el-option value="Hold" label="沿用最后好值（不超过最大间隔）" /><el-option value="Skip" label="跳过该区间" /><el-option value="Zero" label="按 0 计" /></el-select></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="最大间隔(秒)"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="周期复位"><el-select v-model="form.resetPeriod" data-test="calc-job-reset"><el-option v-for="(label, key) in resetLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
          <el-col v-if="form.resetPeriod === 'Shift'" :span="12"><el-form-item label="班次起点"><el-input v-model="form.shiftStarts" placeholder="08:00,20:00" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="单位"><el-input v-model="form.unit" maxlength="32" placeholder="如 m³、kWh" /></el-form-item></el-col>
        </el-row>
        <el-form-item v-if="form.kind === 'Energy'" label="分时时段">
          <div style="width: 100%">
            <div v-for="(p, i) in tou" :key="i" class="tou-row">
              <el-select v-model="p.band" style="width: 110px"><el-option v-for="(label, key) in bandLabels" :key="key" :value="key" :label="label" /></el-select>
              <el-input v-model="p.start" style="width: 90px" placeholder="08:00" /> 至 <el-input v-model="p.end" style="width: 90px" placeholder="11:00" />
              <el-button link type="danger" @click="tou.splice(i, 1)">移除</el-button>
            </div>
            <el-button size="small" data-test="calc-job-add-tou" @click="tou.push({ band: 'peak', start: '08:00', end: '11:00' })">添加时段</el-button>
            <div class="tiny muted">未覆盖的时段计入“平”。起点晚于终点表示跨零点。</div>
          </div>
        </el-form-item>
        <p class="tiny muted">{{ help[form.kind] }}</p>
      </template>
      <template v-else>
        <el-row :gutter="12">
          <el-col :span="12"><el-form-item label="可读测点"><el-input v-model="form.readTags" placeholder="编号，逗号分隔" data-test="calc-job-read" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="可写测点"><el-input v-model="form.writeTags" placeholder="需要控制权限" data-test="calc-job-write" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="输入有效期(秒)"><el-input-number v-model="form.maxGapSec" :min="1" :max="86400" controls-position="right" /></el-form-item></el-col>
          <el-col :span="12"><el-form-item label="输出单位"><el-input v-model="form.unit" maxlength="32" /></el-form-item></el-col>
        </el-row>
        <el-form-item label="脚本" required><el-input v-model="form.script" type="textarea" :rows="8" maxlength="16384" show-word-limit class="mono" data-test="calc-job-script"
          placeholder="$avg = (#1 + #2) / 2;&#10;@avg = $avg;&#10;#9 = if($avg > 80, 1, 0)" /></el-form-item>
        <p class="tiny muted">每行一条或用分号分隔：<code>$变量 = 表达式</code>、<code>@输出 = 表达式</code>（生成派生测点）、<code>#编号 = 表达式</code>（写现场测点，仅值变化时下发，走控制权限与审计，以保存人身份执行）。
          表达式语法同计算测点；内置 $dt（距上次秒数）、$hour、$minute、$weekday、$day、$month。无循环、字符串、文件或网络；每次执行限 {{ limits }}，任一输入坏质量则整次不执行。</p>
      </template>
    </el-form>
    <template #footer><el-button :disabled="saving" @click="editor = false">取消</el-button><el-button type="primary" :loading="saving" data-test="calc-job-save" @click="save">保存</el-button></template>
  </el-dialog>

  <el-dialog v-model="snapshotDialog" :title="`复位快照 · ${snapshotJob?.name ?? ''}`" width="760px">
    <el-table :data="snapshots" empty-text="暂无快照" max-height="420">
      <el-table-column label="时间" width="170"><template #default="{ row }">{{ formatTime(row.ts) }}</template></el-table-column>
      <el-table-column prop="reason" label="原因" width="100" />
      <el-table-column prop="periodKey" label="周期" width="150" />
      <el-table-column label="复位前数值"><template #default="{ row }"><code class="tiny">{{ snapshotText(row.valuesJson) }}</code></template></el-table-column>
    </el-table>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { calcJobApi, type CalcJob, type CalcJobInfo, type CalcJobInput, type CalcJobKind, type CalcJobSnapshot, type CalcResetPeriod } from '../api/calcJobs'
import { areaApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { formatTime, formatValue, hasAny } from '../utils'
import { useAuthStore } from '../stores/auth'
import type { Area, Tag } from '../types'

const auth = useAuthStore()
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
const canControl = computed(() => hasAny(auth.user, 'Control'))
const kindLabels: Record<CalcJobKind, string> = { Accumulator: '累计器', RunHours: '运行小时', Energy: '能耗积分', Script: '受限脚本' }
const resetLabels: Record<CalcResetPeriod, string> = { None: '不复位', Shift: '按班', Day: '按日', Month: '按月' }
const bandLabels: Record<string, string> = { sharp: '尖', peak: '峰', flat: '平', valley: '谷' }
const help: Record<CalcJobKind, string> = {
  Accumulator: '脉冲：按计数差累计，满量程回绕自动补足，计数回落视为复位从 0 起算；坏质量期间保持基准，恢复后补差。流量：梯形积分，系数把流量单位换算到“每秒”（如 m³/h 填 1/3600≈0.000277778）。',
  RunHours: '输入大于阈值视为运行。输出累计小时、启停次数、本周期值、距上次维护小时与“到期需维护”(1/0)，可再对后者配置报警。',
  Energy: '功率梯形积分为电量（kW 填 1/3600 得 kWh），按区间中点时刻归入尖/峰/平/谷，周期量分时段统计。',
  Script: ''
}
const limits = '10 万步 / 50 ms'
const rows = ref<CalcJobInfo[]>([]), tags = ref<Tag[]>([]), areas = ref<Area[]>([]), search = ref(''), automatic = ref(true)
const loading = ref(false), saving = ref(false), readError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null)
const snapshotDialog = ref(false), snapshots = ref<CalcJobSnapshot[]>([]), snapshotJob = ref<CalcJob | null>(null)
const tou = ref<{ band: string; start: string; end: string }[]>([])
const empty = (): CalcJobInput => ({ name: '', kind: 'Accumulator', enabled: true, areaId: 1, intervalMs: 1000, revision: 0, inputTagId: null, mode: 'Pulse', factor: 1, counterMax: null, maxDelta: null,
  badQuality: 'Hold', maxGapSec: 60, runThreshold: 0.5, maintenanceHours: null, resetPeriod: 'Day', shiftStarts: '08:00,20:00', touJson: '[]', script: '', readTags: '', writeTags: '', unit: '' })
const form = reactive<CalcJobInput>(empty())
const inputLabel = computed(() => form.kind === 'RunHours' ? '运行状态测点' : form.kind === 'Energy' ? '功率测点' : form.mode === 'Pulse' ? '脉冲计数测点' : '瞬时流量测点')
const filtered = computed(() => rows.value.filter(x => `${x.job.id} ${x.job.name} ${kindLabels[x.job.kind]}`.toLowerCase().includes(search.value.trim().toLowerCase())))
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? `区域 #${id}`
const tagName = (id: number | null) => tags.value.find(t => t.id === id)?.name ?? ''
const value = (row: CalcJobInfo, name: string) => { const v = row.status?.values?.[name]; return v === undefined ? '--' : formatValue(v) }
const snapshotText = (json: string) => { try { return Object.entries(JSON.parse(json) as Record<string, number>).map(([k, v]) => `${k}=${formatValue(v)}`).join('  ') } catch { return json } }

let timer: number | undefined, alive = true
async function refresh(): Promise<void> {
  loading.value = true
  try {
    const [list, tagList, areaList] = await Promise.all([calcJobApi.list(), tagApi.list(), areaApi.list().catch(() => areas.value)])
    if (!alive) return
    rows.value = list; tags.value = tagList; areas.value = areaList; readError.value = ''
  } catch (e) { readError.value = errorMessage(e, '读取计算任务失败') }
  finally { loading.value = false }
}
function open(job: CalcJob | null): void {
  editing.value = job?.id ?? null
  const unit = job ? (tags.value.find(t => t.jobId === job.id && !['hours', 'periodHours', 'sinceMaintenance', 'starts', 'periodStarts', 'maintenanceDue'].includes(t.jobOutput ?? ''))?.unit ?? '') : ''
  Object.assign(form, job ? { ...empty(), ...job, unit } : { ...empty(), areaId: areas.value[0]?.id ?? 1 })
  try { tou.value = JSON.parse(form.touJson || '[]') } catch { tou.value = [] }
  mutationError.value = ''; editor.value = true
}
async function save(): Promise<void> {
  saving.value = true; mutationError.value = ''
  try {
    form.touJson = JSON.stringify(tou.value)
    await calcJobApi.save(editing.value, { ...form })
    editor.value = false; ElMessage.success('已保存'); await refresh()
  } catch (e) { mutationError.value = errorMessage(e, '保存失败') }
  finally { saving.value = false }
}
async function remove(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(`删除任务“${job.name}”及其派生测点？累计值与快照将一并删除。`, '确认删除', { type: 'warning' }) } catch { return }
  try { await calcJobApi.remove(job); ElMessage.success('已删除'); await refresh() } catch (e) { ElMessage.error(errorMessage(e, '删除失败')) }
}
async function reset(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(`手动复位“${job.name}”的本周期数值？复位前数值会留快照。`, '确认复位', { type: 'warning' }) } catch { return }
  try { await calcJobApi.reset(job.id); ElMessage.success('已复位'); await refresh() } catch (e) { ElMessage.error(errorMessage(e, '复位失败')) }
}
async function maintenance(job: CalcJob): Promise<void> {
  try { await ElMessageBox.confirm(`确认“${job.name}”已完成维护？维护计时将从当前累计小时重新开始。`, '维护完成', { type: 'info' }) } catch { return }
  try { await calcJobApi.maintenanceDone(job.id); ElMessage.success('已记录维护'); await refresh() } catch (e) { ElMessage.error(errorMessage(e, '操作失败')) }
}
async function showSnapshots(job: CalcJob): Promise<void> {
  snapshotJob.value = job; snapshots.value = []; snapshotDialog.value = true
  try { snapshots.value = await calcJobApi.snapshots(job.id) } catch (e) { ElMessage.error(errorMessage(e, '读取快照失败')) }
}
function schedule(): void { window.clearInterval(timer); if (automatic.value) timer = window.setInterval(() => { if (!editor.value && !loading.value) void refresh() }, 3000) }
watch(automatic, schedule)
onMounted(async () => { await refresh(); schedule() })
onBeforeUnmount(() => { alive = false; window.clearInterval(timer) })
</script>
<style scoped>
.tou-row { display: flex; gap: 8px; align-items: center; margin-bottom: 6px; }
.danger { color: var(--el-color-danger); }
.mono :deep(textarea) { font-family: ui-monospace, Consolas, monospace; }
</style>

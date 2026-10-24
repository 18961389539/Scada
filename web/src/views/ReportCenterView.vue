<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>报表中心</strong><span class="spacer" />
      <el-button :loading="loading" @click="refresh()">刷新</el-button>
      <el-button v-if="canConfigure" type="primary" data-test="report-template-new" @click="open(null)">新建报表模板</el-button>
    </div>
    <el-alert v-if="readError" :title="readError" type="error" :closable="false" />
    <p class="tiny muted">班报/日报/月报模板：按区域授权。生成后归档为 Excel 与 PDF，内容不可修改；同一周期重新生成会产生新版本，旧版本保留。下载时校验 SHA256。</p>
    <el-table :data="templates" v-loading="loading" row-key="id" empty-text="尚无报表模板" data-test="report-template-table">
      <el-table-column label="模板" min-width="180"><template #default="{ row }"><strong>{{ row.name }}</strong>
        <div class="tiny muted">{{ periodLabels[row.period as ReportPeriod] }} · {{ areaName(row.areaId) }} · 版本 {{ row.revision }}</div></template></el-table-column>
      <el-table-column label="列" min-width="240"><template #default="{ row }"><span class="tiny">{{ row.columns.map((c: ReportColumn) => c.title).join('、') }}</span></template></el-table-column>
      <el-table-column label="定时 / 邮件" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.scheduleEnabled ? `周期结束后 ${row.scheduleDelayMin} 分钟` : '手动' }}</span>
        <div class="tiny muted">{{ row.emailChannelId ? row.emailRecipients : '不发送' }}</div></template></el-table-column>
      <el-table-column label="操作" width="230"><template #default="{ row }">
        <el-button link @click="selectTemplate(row)">历史</el-button>
        <el-button v-if="canConfigure" link type="success" :data-test="`report-generate-${row.id}`" @click="generate(row)">生成上一周期</el-button>
        <el-button v-if="canConfigure" link type="primary" @click="open(row)">编辑</el-button>
        <el-button v-if="canConfigure" link type="danger" @click="remove(row)">删除</el-button></template></el-table-column>
    </el-table>
  </div>

  <div class="panel" style="margin-top: 12px">
    <div class="toolbar"><strong>已归档报表</strong><span class="tiny muted">{{ selected ? `模板：${selected.name}` : '全部模板' }}</span><span class="spacer" />
      <el-button v-if="selected" link @click="selectTemplate(null)">显示全部</el-button></div>
    <el-table :data="runs" row-key="id" empty-text="尚无归档报表" data-test="report-run-table">
      <el-table-column label="报表" min-width="200"><template #default="{ row }"><strong>{{ row.templateName }}</strong> <el-tag size="small">v{{ row.version }}</el-tag>
        <div class="tiny muted">{{ row.periodKey }} · 模板版本 {{ row.templateRevision }}</div></template></el-table-column>
      <el-table-column label="生成" min-width="200"><template #default="{ row }"><span class="tiny">{{ formatTime(row.generatedTs) }} · {{ triggerLabels[row.trigger] ?? row.trigger }} · {{ row.generatedBy }}</span>
        <el-tag v-if="row.purgedTs" size="small" type="info">文件已按保留期删除</el-tag>
        <div class="tiny muted mono">SHA256 {{ row.pdfSha256.slice(0, 12) }}…</div></template></el-table-column>
      <el-table-column label="操作" width="200"><template #default="{ row }">
        <el-button link type="primary" :data-test="`report-view-${row.id}`" @click="preview(row)">查看</el-button>
        <el-button link :disabled="!!row.purgedTs" @click="download(row, 'xlsx')">Excel</el-button>
        <el-button link :disabled="!!row.purgedTs" @click="download(row, 'pdf')">PDF</el-button></template></el-table-column>
    </el-table>
  </div>

  <el-dialog v-model="viewer" :title="table?.title ?? '报表'" width="960px">
    <el-table v-if="table" :data="[...table.rows, { ...table.summary }]" size="small" max-height="520" data-test="report-preview">
      <el-table-column prop="label" label="时段" width="140" fixed />
      <el-table-column v-for="(c, i) in table.columns" :key="c.key" :label="c.unit ? `${c.title} (${c.unit})` : c.title" min-width="110">
        <template #default="{ row }">{{ row.values[i] == null ? '--' : Number(row.values[i]).toFixed(c.decimals) }}</template></el-table-column>
    </el-table>
  </el-dialog>

  <el-dialog v-model="editor" :title="editing === null ? '新建报表模板' : '编辑报表模板'" width="980px" :close-on-click-modal="false">
    <el-alert v-if="mutationError" :title="mutationError" type="error" :closable="false" data-test="report-template-error" />
    <el-form label-width="110px" :disabled="saving" @submit.prevent>
      <el-row :gutter="12">
        <el-col :span="12"><el-form-item label="名称" required><el-input v-model="form.name" maxlength="80" data-test="report-template-name" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item label="周期"><el-select v-model="form.period" data-test="report-template-period"><el-option v-for="(label, key) in periodLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item></el-col>
        <el-col :span="6"><el-form-item label="区域" label-width="60px"><el-select v-model="form.areaId"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select></el-form-item></el-col>
        <el-col v-if="form.period === 'Shift'" :span="12"><el-form-item label="班次起点"><el-input v-model="form.shiftStarts" placeholder="08:00,20:00" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item label="定时生成"><el-switch v-model="form.scheduleEnabled" /></el-form-item></el-col>
        <el-col :span="6"><el-form-item label="延迟(分)" label-width="80px"><el-input-number v-model="form.scheduleDelayMin" :min="0" :max="1440" controls-position="right" /></el-form-item></el-col>
        <el-col :span="12"><el-form-item label="邮件通道"><el-select v-model="form.emailChannelId" clearable placeholder="不发送"><el-option v-for="c in emailChannels" :key="c.id" :value="c.id" :label="c.name" /></el-select></el-form-item></el-col>
        <el-col :span="12"><el-form-item label="收件人"><el-input v-model="form.emailRecipients" placeholder="分号或逗号分隔" /></el-form-item></el-col>
      </el-row>
      <el-form-item label="列">
        <div style="width: 100%">
          <div v-for="(c, i) in form.columns" :key="i" class="col-row" :data-test="`report-column-${i}`">
            <el-input v-model="c.key" placeholder="键" style="width: 90px" />
            <el-input v-model="c.title" placeholder="标题" style="width: 120px" />
            <el-select v-model="c.kind" style="width: 120px"><el-option v-for="(label, key) in kindLabels" :key="key" :value="key" :label="label" /></el-select>
            <el-select v-if="c.kind.startsWith('Tag')" v-model="c.tagId" filterable placeholder="测点" style="width: 180px"><el-option v-for="t in tags" :key="t.id" :value="t.id" :label="`#${t.id} · ${t.name}`" /></el-select>
            <template v-if="c.kind === 'CalcSnapshot'"><el-select v-model="c.jobId" placeholder="计算任务" style="width: 140px"><el-option v-for="j in jobs" :key="j.job.id" :value="j.job.id" :label="j.job.name" /></el-select>
              <el-input v-model="c.output" placeholder="输出名" style="width: 90px" /></template>
            <el-select v-if="c.kind === 'AlarmCount'" v-model="c.minLevel" clearable placeholder="最低级别" style="width: 110px"><el-option value="Info" label="提示" /><el-option value="Warning" label="警告" /><el-option value="Critical" label="严重" /></el-select>
            <el-input v-if="c.kind === 'Formula'" v-model="c.formula" placeholder="如 $a - $b" style="width: 180px" />
            <el-input-number v-if="c.kind === 'TagSum'" v-model="c.factor" :step="0.1" placeholder="系数" controls-position="right" style="width: 100px" />
            <el-input-number v-model="c.decimals" :min="0" :max="6" controls-position="right" style="width: 80px" />
            <el-input v-model="c.unit" placeholder="单位" style="width: 70px" />
            <el-button link type="danger" @click="form.columns.splice(i, 1)">移除</el-button>
          </div>
          <el-button size="small" data-test="report-add-column" @click="addColumn()">添加列</el-button>
          <div class="tiny muted">差值=周期末值−周期初值（计数/表底）；累计=原始样本梯形时间加权积分（值×小时）×系数，间隔超过 1 小时不桥接；差值遇读数回落按表底复位处理；公式列只能引用前面的列（$键）。合计行对均值/最大/最小/差值按整周期重算，累计与快照求和，公式重算。</div>
        </div>
      </el-form-item>
    </el-form>
    <template #footer><el-button @click="editor = false">取消</el-button><el-button type="primary" :loading="saving" data-test="report-template-save" @click="save()">保存</el-button></template>
  </el-dialog>
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { reportCenterApi, type ReportColumn, type ReportColumnKind, type ReportPeriod, type ReportRun, type ReportTableData, type ReportTemplate, type ReportTemplateInput } from '../api/reports'
import { calcJobApi, type CalcJobInfo } from '../api/calcJobs'
import { areaApi, notificationApi, tagApi } from '../api'
import { errorMessage } from '../api/http'
import { formatTime, hasAny } from '../utils'
import { useAuthStore } from '../stores/auth'
import type { Area, NotificationChannel, Tag } from '../types'

const auth = useAuthStore()
const canConfigure = computed(() => hasAny(auth.user, 'Configure'))
const triggerLabels: Record<string, string> = { schedule: '定时', backfill: '停机回补', manual: '手动' }
const periodLabels: Record<ReportPeriod, string> = { Shift: '班报', Day: '日报', Month: '月报' }
const kindLabels: Record<ReportColumnKind, string> = { TagAvg: '均值', TagMax: '最大值', TagMin: '最小值', TagDiff: '差值', TagSum: '累计', CalcSnapshot: '计算快照', AlarmCount: '报警次数', Formula: '公式' }
const templates = ref<ReportTemplate[]>([]), runs = ref<ReportRun[]>([]), areas = ref<Area[]>([]), tags = ref<Tag[]>([]), jobs = ref<CalcJobInfo[]>([]), channels = ref<NotificationChannel[]>([])
const selected = ref<ReportTemplate | null>(null), loading = ref(false), saving = ref(false), readError = ref(''), mutationError = ref(''), editor = ref(false), editing = ref<number | null>(null)
const viewer = ref(false), table = ref<ReportTableData | null>(null)
const empty = (): ReportTemplateInput => ({ name: '', period: 'Day', areaId: areas.value[0]?.id ?? 1, shiftStarts: '08:00,20:00', columns: [], scheduleEnabled: false, scheduleDelayMin: 10, emailChannelId: null, emailRecipients: '', revision: 0 })
const form = reactive<ReportTemplateInput>(empty())
const emailChannels = computed(() => channels.value.filter(c => c.type === 'Email'))
const areaName = (id: number) => areas.value.find(a => a.id === id)?.name ?? `区域 #${id}`

async function refresh(): Promise<void> {
  loading.value = true
  try {
    const [list, runList, areaList, tagList, jobList, channelList] = await Promise.all([reportCenterApi.templates(), reportCenterApi.runs(selected.value?.id),
      areaApi.list().catch(() => areas.value), tagApi.list().catch(() => tags.value), calcJobApi.list().catch(() => jobs.value), canConfigure.value ? notificationApi.channels().catch(() => channels.value) : Promise.resolve(channels.value)])
    templates.value = list; runs.value = runList; areas.value = areaList; tags.value = tagList; jobs.value = jobList; channels.value = channelList; readError.value = ''
  } catch (e) { readError.value = errorMessage(e, '读取报表失败') }
  finally { loading.value = false }
}
async function selectTemplate(t: ReportTemplate | null): Promise<void> { selected.value = t; await refresh() }
function addColumn(): void { const n = form.columns.length + 1; form.columns.push({ key: `c${n}`, title: `列${n}`, kind: 'TagAvg', tagId: tags.value[0]?.id ?? null, decimals: 2, unit: '' }) }
function open(t: ReportTemplate | null): void {
  editing.value = t?.id ?? null
  Object.assign(form, t ? { ...empty(), ...t, columns: t.columns.map(c => ({ ...c })) } : empty())
  mutationError.value = ''; editor.value = true
}
async function save(): Promise<void> {
  saving.value = true; mutationError.value = ''
  try { await reportCenterApi.save(editing.value, { ...form }); editor.value = false; ElMessage.success('已保存'); await refresh() }
  catch (e) { mutationError.value = errorMessage(e, '保存失败') }
  finally { saving.value = false }
}
async function remove(t: ReportTemplate): Promise<void> {
  try { await ElMessageBox.confirm(`删除模板“${t.name}”？已归档的报表仍保留。`, '确认删除', { type: 'warning' }) } catch { return }
  try { await reportCenterApi.remove(t); ElMessage.success('已删除'); await refresh() } catch (e) { ElMessage.error(errorMessage(e, '删除失败')) }
}
async function generate(t: ReportTemplate): Promise<void> {
  const existing = runs.value.filter(r => r.templateId === t.id).length
  try {
    await ElMessageBox.confirm(`为“${t.name}”生成上一个完整周期的报表？若该周期已有报表，将追加一个新版本（旧版本保留，不可删除）。${existing ? `该模板已有 ${existing} 份归档。` : ''}`, '确认生成', { type: 'warning', confirmButtonText: '生成' })
  } catch { return }
  try { const run = await reportCenterApi.generate(t.id); ElMessage.success(`已生成 ${run.periodKey} v${run.version}`); await refresh() }
  catch (e) { ElMessage.error(errorMessage(e, '生成失败')) }
}
async function preview(run: ReportRun): Promise<void> {
  try { table.value = (await reportCenterApi.run(run.id)).table; viewer.value = true } catch (e) { ElMessage.error(errorMessage(e, '读取报表失败')) }
}
async function download(run: ReportRun, format: 'xlsx' | 'pdf'): Promise<void> {
  try {
    const blob = await reportCenterApi.download(run.id, format)
    const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = `${run.templateName}_${run.periodKey}_v${run.version}.${format}`; a.click(); URL.revokeObjectURL(a.href)
  } catch (e) { ElMessage.error(errorMessage(e, '下载失败（文件缺失或校验不一致）')) }
}
onMounted(refresh)
</script>
<style scoped>
.col-row { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; margin-bottom: 6px; }
.mono { font-family: ui-monospace, Consolas, monospace; }
</style>

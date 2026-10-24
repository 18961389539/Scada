<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>现场操作</strong><span class="spacer" /><el-button @click="refresh()">刷新</el-button></div>
    <p class="tiny muted">实时监视、运行画面与顺控的每一次下发都经过：控制权（就地/远程）→ 挂牌 → 限幅与变化率 → 许可/互锁条件 → 操作确认 → 双人复核（按策略）→ 下发与回读 → 独立反馈等待。全部按区域权限并写审计。</p>
    <el-tabs v-model="tab">
      <el-tab-pane :label="`待复核 (${pending.length})`" name="requests">
        <el-table :data="requests" size="small" row-key="request.id" data-test="control-requests" empty-text="没有复核申请">
          <el-table-column label="内容" min-width="220"><template #default="{ row }">
            <span v-if="row.request.kind === 'command'">{{ tagName(row.request.tagId) }} ← <strong>{{ row.request.value }}</strong></span>
            <span v-else>顺控：{{ row.sequenceName }}</span>
            <div class="tiny muted">{{ row.request.source }}</div>
          </template></el-table-column>
          <el-table-column label="申请" min-width="150"><template #default="{ row }"><span class="tiny">{{ row.request.requestedBy }} · {{ formatTime(row.request.requestedTs) }}</span></template></el-table-column>
          <el-table-column label="状态" width="100"><template #default="{ row }"><el-tag size="small" :type="statusType[row.request.status] ?? 'info'">{{ statusLabels[row.request.status] ?? row.request.status }}</el-tag></template></el-table-column>
          <el-table-column label="结果" min-width="200"><template #default="{ row }"><span class="tiny">{{ row.request.decidedBy ? `${row.request.decidedBy}：` : '' }}{{ row.request.resultMessage ?? row.request.comment ?? '' }}</span></template></el-table-column>
          <el-table-column label="操作" width="190"><template #default="{ row }">
            <template v-if="row.request.status === 'Pending'">
              <el-button v-if="!row.mine" size="small" type="primary" :data-test="`approve-${row.request.id}`" @click="decide(row.request, true)">批准执行</el-button>
              <el-button size="small" @click="decide(row.request, false)">{{ row.mine ? '撤回' : '驳回' }}</el-button>
            </template>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="挂牌" name="lockouts">
        <div class="toolbar">
          <el-select v-model="lockForm.target" filterable placeholder="设备或测点" style="width: 260px" data-test="lock-target">
            <el-option-group label="设备"><el-option v-for="d in devices" :key="`d${d.id}`" :value="`d${d.id}`" :label="d.name" /></el-option-group>
            <el-option-group label="测点"><el-option v-for="t in writableTags" :key="`t${t.id}`" :value="`t${t.id}`" :label="tagName(t.id)" /></el-option-group>
          </el-select>
          <el-select v-model="lockForm.kind" style="width: 120px"><el-option value="NoOperate" label="禁止操作" /><el-option value="Maintenance" label="检修中" /></el-select>
          <el-input v-model="lockForm.reason" placeholder="挂牌原因" style="width: 240px" maxlength="200" data-test="lock-reason" />
          <el-button type="danger" :disabled="!lockForm.target || !lockForm.reason.trim()" data-test="lock-place" @click="placeLock()">挂牌</el-button>
          <span class="spacer" /><el-checkbox v-model="showRemoved" @change="loadLockouts()">显示已摘牌</el-checkbox>
        </div>
        <el-table :data="lockouts" size="small" data-test="lockouts" empty-text="当前无挂牌">
          <el-table-column label="对象" min-width="180"><template #default="{ row }">{{ row.deviceId ? `设备 ${deviceName(row.deviceId)}` : `测点 ${tagName(row.tagId)}` }}</template></el-table-column>
          <el-table-column label="牌" width="100"><template #default="{ row }"><el-tag size="small" :type="row.kind === 'NoOperate' ? 'danger' : 'warning'">{{ row.kind === 'NoOperate' ? '禁止操作' : '检修中' }}</el-tag></template></el-table-column>
          <el-table-column prop="reason" label="原因" min-width="160" />
          <el-table-column label="挂牌" min-width="160"><template #default="{ row }"><span class="tiny">{{ row.placedBy }} · {{ formatTime(row.placedTs) }}</span></template></el-table-column>
          <el-table-column label="摘牌" min-width="160"><template #default="{ row }">
            <span v-if="row.removedTs" class="tiny">{{ row.removedBy }} · {{ formatTime(row.removedTs) }} · {{ row.removeReason }}</span>
            <el-button v-else size="small" :data-test="`unlock-${row.id}`" @click="removeLock(row)">摘牌</el-button>
          </template></el-table-column>
        </el-table>
        <p class="tiny muted">禁止操作牌拒绝任何人下发；检修牌只允许挂牌人本人操作（检修调试）。摘牌须由挂牌人，或该区域的配置管理员代摘，均需填写说明。</p>
      </el-tab-pane>

      <el-tab-pane label="控制权" name="modes">
        <el-table :data="devices" size="small" data-test="modes">
          <el-table-column prop="name" label="设备" min-width="160" />
          <el-table-column label="控制权" width="120"><template #default="{ row }"><el-tag size="small" :type="modeOf(row.id)?.mode === 'Local' ? 'warning' : 'success'">{{ modeOf(row.id)?.mode === 'Local' ? '就地' : '远程' }}</el-tag></template></el-table-column>
          <el-table-column label="最近切换" min-width="220"><template #default="{ row }"><span v-if="modeOf(row.id)" class="tiny">{{ modeOf(row.id)!.changedBy }} · {{ formatTime(modeOf(row.id)!.changedTs) }} · {{ modeOf(row.id)!.reason }}</span></template></el-table-column>
          <el-table-column label="操作" width="140"><template #default="{ row }">
            <el-button size="small" :data-test="`mode-${row.id}`" @click="toggleMode(row.id)">切换为{{ modeOf(row.id)?.mode === 'Local' ? '远程' : '就地' }}</el-button>
          </template></el-table-column>
        </el-table>
        <p class="tiny muted">就地：现场接管，中心（含画面、顺控、脚本）一律不能下发；切回远程需填写原因。</p>
      </el-tab-pane>

      <el-tab-pane label="顺控" name="sequences">
        <div class="toolbar"><el-button type="primary" data-test="sequence-new" @click="editSequence()">新建顺控</el-button></div>
        <el-table :data="sequences" size="small" data-test="sequences" empty-text="尚无顺控">
          <el-table-column prop="name" label="名称" min-width="160" />
          <el-table-column label="步骤" min-width="260"><template #default="{ row }"><span class="tiny">{{ row.steps.map((s: ControlStep, i: number) => `${i + 1}.${s.name}`).join(' → ') }}</span></template></el-table-column>
          <el-table-column label="复核" width="80"><template #default="{ row }">{{ row.dualApproval ? '双人' : '-' }}</template></el-table-column>
          <el-table-column label="操作" width="230"><template #default="{ row }">
            <el-button size="small" type="primary" :data-test="`sequence-start-${row.id}`" @click="startSequence(row)">执行</el-button>
            <el-button size="small" @click="editSequence(row)">编辑</el-button>
            <el-button size="small" type="danger" @click="removeSequence(row)">删除</el-button>
          </template></el-table-column>
        </el-table>
        <h4>运行记录</h4>
        <el-table :data="runs" size="small" data-test="runs" empty-text="尚无运行记录">
          <el-table-column label="运行" min-width="160"><template #default="{ row }">#{{ row.id }} {{ row.sequenceName }}</template></el-table-column>
          <el-table-column label="状态" width="90"><template #default="{ row }"><el-tag size="small" :type="runType[row.status] ?? 'info'" :data-test="`run-status-${row.id}`">{{ runLabels[row.status] ?? row.status }}</el-tag></template></el-table-column>
          <el-table-column label="步骤" min-width="320"><template #default="{ row }">
            <div v-for="s in row.steps" :key="s.index" class="tiny"><el-tag size="small" :type="stepType[s.status] ?? 'info'">{{ stepLabels[s.status] ?? s.status }}</el-tag> {{ s.index }}. {{ s.name }} <span class="muted">{{ s.message }}</span></div>
          </template></el-table-column>
          <el-table-column label="执行" min-width="170"><template #default="{ row }"><span class="tiny">{{ row.startedBy }}{{ row.approvedBy ? `（复核 ${row.approvedBy}）` : '' }} · {{ formatTime(row.startedTs) }}</span><div class="tiny muted">{{ row.message }}</div></template></el-table-column>
          <el-table-column label="" width="90"><template #default="{ row }"><el-button v-if="row.status === 'Running'" size="small" type="danger" @click="abort(row)">中止</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="控制策略" name="points">
        <div class="toolbar">
          <el-select v-model="pointTag" filterable placeholder="选择可写测点" style="width: 280px" data-test="point-tag"><el-option v-for="t in writableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select>
          <el-button :disabled="!pointTag" data-test="point-edit" @click="editPoint(pointTag!)">配置</el-button>
        </div>
        <el-table :data="points" size="small" data-test="points" empty-text="尚无控制策略（未配置的测点只做量程、类型与回读校验）">
          <el-table-column label="测点" min-width="180"><template #default="{ row }">{{ tagName(row.tagId) }}</template></el-table-column>
          <el-table-column label="策略" min-width="360"><template #default="{ row }"><span class="tiny">{{ pointSummary(row) }}</span></template></el-table-column>
          <el-table-column label="操作" width="150"><template #default="{ row }">
            <el-button size="small" @click="editPoint(row.tagId)">编辑</el-button><el-button size="small" type="danger" @click="removePoint(row.tagId)">删除</el-button>
          </template></el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>
  </div>

  <el-dialog v-model="pointVisible" :title="`控制策略：${pointForm ? tagName(pointForm.tagId) : ''}`" width="560px" data-test="point-dialog">
    <el-form v-if="pointForm" label-width="120px" size="small">
      <el-form-item label="操作确认"><el-switch v-model="pointForm.requireConfirm" /><span class="tiny muted">　接口请求必须带确认标记</span></el-form-item>
      <el-form-item label="双人复核"><el-switch v-model="pointForm.dualApproval" data-test="point-dual" />　有效期 <el-input-number v-model="pointForm.approvalTimeoutSec" :min="30" :max="3600" /> 秒</el-form-item>
      <el-form-item label="操作限幅"><el-input-number v-model="pointForm.limitMin" :controls="false" placeholder="下限" style="width: 110px" /> ～ <el-input-number v-model="pointForm.limitMax" :controls="false" placeholder="上限" style="width: 110px" data-test="point-max" /></el-form-item>
      <el-form-item label="单次变化上限"><el-input-number v-model="pointForm.maxStep" :controls="false" :min="0" placeholder="不限" style="width: 110px" /></el-form-item>
      <el-form-item label="许可/互锁条件"><el-input v-model="pointForm.permitExpression" placeholder="例如 #12 == 1 && #15 < 80（为真才允许）" data-test="point-permit" /></el-form-item>
      <el-form-item label="不满足时提示"><el-input v-model="pointForm.permitMessage" maxlength="200" placeholder="例如 出口阀未全开" /></el-form-item>
      <el-form-item label="反馈条件"><el-input v-model="pointForm.feedbackExpression" placeholder="例如 #20 >= 95（写入回读后在超时内须为真）" /></el-form-item>
      <el-form-item label="反馈超时"><el-input-number v-model="pointForm.feedbackTimeoutMs" :min="100" :max="600000" :step="1000" /> 毫秒</el-form-item>
    </el-form>
    <template #footer><el-button @click="pointVisible = false">取消</el-button><el-button type="primary" data-test="point-save" @click="savePoint()">保存</el-button></template>
  </el-dialog>

  <el-dialog v-model="sequenceVisible" :title="sequenceForm.id ? '编辑顺控' : '新建顺控'" width="900px" data-test="sequence-dialog">
    <el-form label-width="90px" size="small">
      <el-form-item label="名称"><el-input v-model="sequenceForm.name" maxlength="100" data-test="sequence-name" /></el-form-item>
      <el-form-item label="区域"><el-select v-model="sequenceForm.areaId" style="width: 200px"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select>
        <el-checkbox v-model="sequenceForm.dualApproval" style="margin-left: 16px">执行前需双人复核</el-checkbox></el-form-item>
      <el-form-item label="说明"><el-input v-model="sequenceForm.description" maxlength="500" /></el-form-item>
    </el-form>
    <el-table :data="sequenceForm.steps" size="small" data-test="sequence-steps">
      <el-table-column label="#" width="40"><template #default="{ $index }">{{ $index + 1 }}</template></el-table-column>
      <el-table-column label="步骤名" width="130"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column label="写测点" width="200"><template #default="{ row }"><el-select v-model="row.tagId" filterable size="small"><el-option v-for="t in writableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></template></el-table-column>
      <el-table-column label="值" width="100"><template #default="{ row }"><el-input-number v-model="row.value" :controls="false" size="small" style="width: 80px" /></template></el-table-column>
      <el-table-column label="完成条件" min-width="180"><template #default="{ row }"><el-input v-model="row.confirmExpression" size="small" placeholder="如 #20 == 1" /></template></el-table-column>
      <el-table-column label="超时ms" width="100"><template #default="{ row }"><el-input-number v-model="row.timeoutMs" :controls="false" size="small" style="width: 80px" /></template></el-table-column>
      <el-table-column label="" width="60"><template #default="{ $index }"><el-button size="small" link type="danger" @click="sequenceForm.steps.splice($index, 1)">删</el-button></template></el-table-column>
    </el-table>
    <el-button size="small" style="margin-top: 8px" data-test="sequence-add-step" @click="sequenceForm.steps.push({ name: `步骤${sequenceForm.steps.length + 1}`, tagId: writableTags[0]?.id ?? 0, value: 0, confirmExpression: null, timeoutMs: 10000, delayMs: 0 })">添加步骤</el-button>
    <p class="tiny muted">每一步都走完整控制检查与回读；写入成功后在超时内等待“完成条件”为真，失败即停止，其余步骤跳过。步骤测点若要求双人复核，顺控本身须勾选双人复核。</p>
    <template #footer><el-button @click="sequenceVisible = false">取消</el-button><el-button type="primary" data-test="sequence-save" @click="saveSequence()">保存</el-button></template>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, deviceApi, tagApi } from '../api'
import { controlApi, type ControlLockout, type ControlPoint, type ControlRequest, type ControlRun, type ControlSequence, type ControlStep, type DeviceControlState } from '../api/control'
import { errorMessage } from '../api/http'
import { formatTime } from '../utils'

interface NamedDevice { id: number; name: string }
interface NamedTag { id: number; name: string; deviceId: number | null; writable: boolean; kind?: string }
const tab = ref('requests')
const devices = ref<NamedDevice[]>([]), tags = ref<NamedTag[]>([]), areas = ref<{ id: number; name: string }[]>([])
const requests = ref<{ request: ControlRequest; sequenceName: string | null; mine: boolean }[]>([])
const lockouts = ref<ControlLockout[]>([]), modes = ref<DeviceControlState[]>([]), points = ref<ControlPoint[]>([])
const sequences = ref<ControlSequence[]>([]), runs = ref<ControlRun[]>([])
const showRemoved = ref(false)
const pending = computed(() => requests.value.filter(x => x.request.status === 'Pending'))
const writableTags = computed(() => tags.value.filter(x => x.writable && x.deviceId != null))
const statusLabels: Record<string, string> = { Pending: '待复核', Executed: '已执行', Failed: '失败', Rejected: '已驳回', Expired: '已超时', Cancelled: '已撤回' }
const statusType: Record<string, string> = { Pending: 'warning', Executed: 'success', Failed: 'danger', Rejected: 'info', Expired: 'info', Cancelled: 'info' }
const runLabels: Record<string, string> = { Running: '运行中', Completed: '完成', Failed: '失败', Aborted: '已中止' }
const runType: Record<string, string> = { Running: 'warning', Completed: 'success', Failed: 'danger', Aborted: 'info' }
const stepLabels: Record<string, string> = { pending: '等待', running: '执行中', done: '完成', failed: '失败', skipped: '跳过' }
const stepType: Record<string, string> = { running: 'warning', done: 'success', failed: 'danger' }
const deviceName = (id: number | null) => devices.value.find(x => x.id === id)?.name ?? `#${id}`
function tagName(id: number | null): string {
  const t = tags.value.find(x => x.id === id); if (!t) return `#${id}`
  return t.deviceId ? `${deviceName(t.deviceId)}/${t.name}` : t.name
}
const modeOf = (deviceId: number) => modes.value.find(x => x.deviceId === deviceId)
function pointSummary(p: ControlPoint): string {
  const parts: string[] = []
  if (p.requireConfirm) parts.push('须确认'); if (p.dualApproval) parts.push(`双人复核(${p.approvalTimeoutSec}s)`)
  if (p.limitMin != null || p.limitMax != null) parts.push(`限幅[${p.limitMin ?? '-'}, ${p.limitMax ?? '-'}]`)
  if (p.maxStep != null) parts.push(`单次≤${p.maxStep}`); if (p.permitExpression) parts.push(`许可 ${p.permitExpression}`)
  if (p.feedbackExpression) parts.push(`反馈 ${p.feedbackExpression} ≤${p.feedbackTimeoutMs}ms`)
  return parts.join('；') || '仅基础校验'
}

async function guard(action: () => Promise<unknown>, fallback: string): Promise<boolean> {
  try { await action(); return true } catch (e) { ElMessage.error(errorMessage(e, fallback)); return false }
}
async function loadLockouts(): Promise<void> { lockouts.value = await controlApi.lockouts(showRemoved.value) }
async function refresh(): Promise<void> {
  await guard(async () => {
    const [d, t, a] = await Promise.all([deviceApi.list(), tagApi.list(), areaApi.list().catch(() => [])])
    devices.value = d as unknown as NamedDevice[]; tags.value = t as unknown as NamedTag[]; areas.value = a as { id: number; name: string }[]
    ;[requests.value, modes.value, points.value, sequences.value, runs.value] = await Promise.all([controlApi.requests(), controlApi.modes(), controlApi.points(), controlApi.sequences(), controlApi.runs()])
    await loadLockouts()
  }, '加载失败')
}

async function decide(r: ControlRequest, approve: boolean): Promise<void> {
  let comment = ''
  try { comment = (await ElMessageBox.prompt(approve ? '核对目标与现场后批准，系统将立即执行。复核意见：' : '说明：', approve ? '批准执行' : '驳回/撤回', { inputValue: '', confirmButtonText: approve ? '批准' : '确定' })).value ?? '' }
  catch { return }
  await guard(async () => {
    if (approve) {
      const result = await controlApi.approve(r.id, comment)
      if (result.runId) ElMessage.success(`顺控已启动，运行 #${result.runId}`)
      else if (result.result?.success) ElMessage.success(`已执行，回读 ${result.result.readBack}`)
      else ElMessage.error(result.result?.message ?? result.request.resultMessage ?? '执行失败')
    } else await controlApi.reject(r.id, comment)
  }, '操作失败')
  await refresh()
}

const lockForm = reactive({ target: '', kind: 'NoOperate', reason: '' })
async function placeLock(): Promise<void> {
  const id = Number(lockForm.target.slice(1))
  if (await guard(() => controlApi.placeLockout({ deviceId: lockForm.target.startsWith('d') ? id : null, tagId: lockForm.target.startsWith('t') ? id : null, kind: lockForm.kind, reason: lockForm.reason.trim() }), '挂牌失败')) {
    ElMessage.success('已挂牌'); lockForm.reason = ''; await loadLockouts()
  }
}
async function removeLock(row: ControlLockout): Promise<void> {
  let reason = ''
  try { reason = (await ElMessageBox.prompt('摘牌说明（如：检修完成，已确认现场可操作）', '摘牌', { inputPattern: /\S/, inputErrorMessage: '请填写说明' })).value }
  catch { return }
  if (await guard(() => controlApi.removeLockout(row.id, reason), '摘牌失败')) { ElMessage.success('已摘牌'); await loadLockouts() }
}
async function toggleMode(deviceId: number): Promise<void> {
  const next = modeOf(deviceId)?.mode === 'Local' ? 'Remote' : 'Local'
  let reason = ''
  try { reason = (await ElMessageBox.prompt(`将 ${deviceName(deviceId)} 切换为${next === 'Local' ? '就地（中心不能下发）' : '远程'}，原因：`, '切换控制权', { inputPattern: /\S/, inputErrorMessage: '请填写原因' })).value }
  catch { return }
  if (await guard(() => controlApi.setMode(deviceId, next, reason), '切换失败')) { ElMessage.success('已切换'); modes.value = await controlApi.modes() }
}

const pointVisible = ref(false), pointTag = ref<number | null>(null), pointForm = ref<ControlPoint | null>(null)
function editPoint(tagId: number): void {
  const existing = points.value.find(x => x.tagId === tagId)
  pointForm.value = existing ? { ...existing } : { tagId, requireConfirm: true, dualApproval: false, approvalTimeoutSec: 300, permitExpression: null, permitMessage: null, limitMin: null, limitMax: null, maxStep: null, feedbackExpression: null, feedbackTimeoutMs: 10000 }
  pointVisible.value = true
}
async function savePoint(): Promise<void> {
  if (!pointForm.value) return
  if (await guard(() => controlApi.savePoint(pointForm.value!), '保存失败')) { ElMessage.success('已保存'); pointVisible.value = false; points.value = await controlApi.points() }
}
async function removePoint(tagId: number): Promise<void> {
  try { await ElMessageBox.confirm(`删除 ${tagName(tagId)} 的控制策略？删除后只做基础校验。`, '删除', { type: 'warning' }) } catch { return }
  if (await guard(() => controlApi.removePoint(tagId), '删除失败')) points.value = await controlApi.points()
}

const sequenceVisible = ref(false)
const sequenceForm = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; steps: ControlStep[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, steps: [] })
function editSequence(row?: ControlSequence): void {
  Object.assign(sequenceForm, row ? { id: row.id, name: row.name, areaId: row.areaId, description: row.description, dualApproval: row.dualApproval, revision: row.revision, steps: row.steps.map(s => ({ ...s })) }
    : { id: undefined, name: '', areaId: areas.value[0]?.id ?? 1, description: null, dualApproval: false, revision: 0, steps: [] })
  sequenceVisible.value = true
}
async function saveSequence(): Promise<void> {
  if (await guard(() => controlApi.saveSequence({ ...sequenceForm, steps: sequenceForm.steps.map(s => ({ ...s, confirmExpression: s.confirmExpression || null })) }), '保存失败')) {
    ElMessage.success('已保存'); sequenceVisible.value = false; sequences.value = await controlApi.sequences()
  }
}
async function removeSequence(row: ControlSequence): Promise<void> {
  try { await ElMessageBox.confirm(`删除顺控 ${row.name}？运行记录保留。`, '删除', { type: 'warning' }) } catch { return }
  if (await guard(() => controlApi.removeSequence(row.id), '删除失败')) sequences.value = await controlApi.sequences()
}
async function startSequence(row: ControlSequence): Promise<void> {
  const text = row.steps.map((s, i) => `${i + 1}. ${s.name}：${tagName(s.tagId)} ← ${s.value}${s.confirmExpression ? `，等待 ${s.confirmExpression}` : ''}`).join('；')
  try { await ElMessageBox.confirm(text, `执行顺控 ${row.name}${row.dualApproval ? '（需双人复核）' : ''}`, { type: 'warning', confirmButtonText: row.dualApproval ? '提交复核' : '确认执行' }) } catch { return }
  await guard(async () => {
    const result = await controlApi.start(row.id)
    if (result.pending) ElMessage.warning('已提交双人复核'); else ElMessage.success(`已启动运行 #${result.runId}`)
  }, '启动失败')
  await refresh()
}
async function abort(row: ControlRun): Promise<void> {
  try { await ElMessageBox.confirm('当前步骤结束后停止，其余步骤跳过。', `中止运行 #${row.id}`, { type: 'warning' }) } catch { return }
  await guard(() => controlApi.abort(row.id), '中止失败'); await refresh()
}

let timer: number | undefined
onMounted(async () => {
  await refresh()
  timer = window.setInterval(async () => { if (runs.value.some(x => x.status === 'Running')) runs.value = await controlApi.runs().catch(() => runs.value) }, 1000)
})
onBeforeUnmount(() => window.clearInterval(timer))
</script>

<style scoped>
h4 { margin: 16px 0 6px; }
</style>

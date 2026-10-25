<template>
  <div class="page"><div class="panel">
    <div class="toolbar"><strong>现场操作</strong><span class="spacer" /><el-button @click="refresh()">刷新</el-button></div>
    <p class="tiny muted">实时监视、运行画面与顺控的每一次下发都经过：控制权（就地/远程）→ 挂牌 → 限幅与变化率 → 许可/互锁条件 → 操作确认 → 双人复核（按策略）→ 下发与回读 → 独立反馈等待。全部按区域权限并写审计。</p>
    <el-tabs v-model="tab">
      <el-tab-pane :label="`待复核 (${pending.length})`" name="requests">
        <el-table :data="requests" size="small" row-key="request.id" data-test="control-requests" empty-text="没有复核申请">
          <el-table-column label="内容" min-width="220"><template #default="{ row }">
            <span v-if="row.request.kind === 'command'">{{ tagName(row.request.tagId) }} ← <strong>{{ row.request.value }}</strong></span>
            <span v-else-if="row.request.kind === 'recipe'">配方：{{ row.recipe?.recipeName }} v{{ row.recipe?.version }}
              <div v-for="r in row.recipe?.results ?? []" :key="r.key" class="tiny">{{ r.name }} ← <strong>{{ r.target }}</strong></div></span>
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
          <el-table-column label="步骤" min-width="260"><template #default="{ row }"><span class="tiny">{{ stepsSummary(row.steps) }}</span></template></el-table-column>
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
            <div v-for="s in row.steps" :key="s.index" class="tiny"><el-tag size="small" :type="stepType[s.status] ?? 'info'">{{ stepLabels[s.status] ?? s.status }}</el-tag> {{ s.stepId ?? s.index }}. {{ s.name }}<span v-if="(s.executions ?? 0) > 1" class="muted">（{{ s.executions }} 次）</span> <span class="muted">{{ s.message }}</span></div>
          </template></el-table-column>
          <el-table-column label="执行" min-width="170"><template #default="{ row }"><span class="tiny">{{ row.startedBy }}{{ row.approvedBy ? `（复核 ${row.approvedBy}）` : '' }} · {{ formatTime(row.startedTs) }}</span><div class="tiny muted">{{ row.message }}</div></template></el-table-column>
          <el-table-column label="" width="130"><template #default="{ row }"><el-button size="small" link :data-test="`run-flow-${row.id}`" @click="openRunFlow(row)">流程图</el-button><el-button v-if="row.status === 'Running'" size="small" type="danger" @click="abort(row)">中止</el-button></template></el-table-column>
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

  <el-dialog v-model="sequenceVisible" :title="sequenceForm.id ? '编辑顺控' : '新建顺控'" width="1240px" data-test="sequence-dialog">
    <el-form label-width="90px" size="small">
      <el-form-item label="名称"><el-input v-model="sequenceForm.name" maxlength="100" data-test="sequence-name" /></el-form-item>
      <el-form-item label="区域"><el-select v-model="sequenceForm.areaId" style="width: 200px"><el-option v-for="a in areas" :key="a.id" :value="a.id" :label="a.name" /></el-select>
        <el-checkbox v-model="sequenceForm.dualApproval" style="margin-left: 16px">执行前需双人复核</el-checkbox></el-form-item>
      <el-form-item label="说明"><el-input v-model="sequenceForm.description" maxlength="500" /></el-form-item>
    </el-form>
    <div class="toolbar">
      <el-radio-group v-model="editorMode" size="small" data-test="sequence-editor-mode"><el-radio-button value="flow">流程图</el-radio-button><el-radio-button value="table">表格</el-radio-button></el-radio-group>
      <template v-if="editorMode === 'flow'">
        <el-dropdown trigger="click" @command="(type: ControlStepType) => addStep(type)"><el-button size="small" data-test="flow-add">添加步骤 ▾</el-button>
          <template #dropdown><el-dropdown-menu><el-dropdown-item v-for="(label, key) in stepTypeLabels" :key="key" :command="key" :data-test="`flow-add-${key}`">{{ label }}</el-dropdown-item></el-dropdown-menu></template>
        </el-dropdown>
      </template>
      <span class="spacer" />
      <el-tag v-if="validation" size="small" :type="validation.ok ? 'success' : 'danger'" data-test="sequence-validation">{{ validation.ok ? '校验通过' : validation.message }}</el-tag>
    </div>
    <div v-if="editorMode === 'flow'" class="flow-editor">
      <SequenceFlow v-model="selectedStepId" :steps="sequenceForm.steps" editable :error-ids="validation?.stepIds ?? []" :describe="describeStep" :height="520" style="flex: 1; min-width: 0" @changed="scheduleValidate()" />
      <div class="flow-props" data-test="flow-props">
        <template v-if="selectedStep">
          <el-form label-width="76px" size="small">
            <el-form-item label="编号"><el-input :model-value="selectedStep.id ?? ''" data-test="prop-id" @change="(v: string) => renameStep(selectedStep!, v)" /></el-form-item>
            <el-form-item label="类型"><el-select v-model="selectedStep.type" data-test="prop-type" @change="scheduleValidate()"><el-option v-for="(label, key) in stepTypeLabels" :key="key" :value="key" :label="label" /></el-select></el-form-item>
            <el-form-item label="步骤名"><el-input v-model="selectedStep.name" data-test="prop-name" /></el-form-item>
            <template v-if="selectedStep.type === 'write'">
              <el-form-item label="写测点"><el-select v-model="selectedStep.tagId" filterable data-test="prop-tag" @change="scheduleValidate()"><el-option v-for="t in writableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select></el-form-item>
              <el-form-item label="写入值"><el-input-number v-model="selectedStep.value" :controls="false" data-test="prop-value" @change="scheduleValidate()" /></el-form-item>
              <el-form-item label="完成条件"><el-input v-model="selectedStep.confirmExpression" placeholder="如 #20 == 1" @change="scheduleValidate()" /></el-form-item>
            </template>
            <el-form-item v-if="selectedStep.type === 'wait' || selectedStep.type === 'condition'" label="条件"><el-input v-model="selectedStep.expression" placeholder="如 #20 >= 95" data-test="prop-expression" @change="scheduleValidate()" /></el-form-item>
            <template v-if="selectedStep.type === 'condition'">
              <el-form-item label="成立→"><el-select v-model="selectedStep.then" clearable @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item label="否则→"><el-select v-model="selectedStep.else" clearable placeholder="下一步" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            </template>
            <template v-if="selectedStep.type === 'parallel'">
              <el-form-item label="分支"><el-select v-model="selectedStep.branches" multiple @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item label="汇合"><el-select v-model="selectedStep.join" @change="scheduleValidate()"><el-option v-for="id in joinIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            </template>
            <el-form-item v-if="selectedStep.type === 'join'" label="汇合方式"><el-radio-group v-model="selectedStep.joinMode" @change="scheduleValidate()"><el-radio-button value="all">全部</el-radio-button><el-radio-button value="any">任一</el-radio-button></el-radio-group></el-form-item>
            <template v-if="selectedStep.type === 'jump'">
              <el-form-item label="转到"><el-select v-model="selectedStep.target" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
              <el-form-item label="最多次数"><el-input-number v-model="selectedStep.maxIterations" :min="1" :max="1000" data-test="prop-max" @change="scheduleValidate()" /></el-form-item>
            </template>
            <el-form-item v-if="['write', 'wait', 'join'].includes(selectedStep.type ?? 'write')" label="下一步"><el-select v-model="selectedStep.next" clearable placeholder="顺延" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></el-form-item>
            <el-form-item v-if="['write', 'wait'].includes(selectedStep.type ?? 'write')" label="超时ms"><el-input-number v-model="selectedStep.timeoutMs" :min="100" :max="600000" :step="1000" data-test="prop-timeout" @change="scheduleValidate()" /></el-form-item>
            <el-form-item v-if="!['end', 'join'].includes(selectedStep.type ?? 'write')" label="失败处理">
              <el-select v-model="selectedStep.onFailure" clearable placeholder="停止" style="width: 90px" data-test="prop-failure" @change="scheduleValidate()"><el-option value="stop" label="停止" /><el-option value="continue" label="继续" /><el-option value="goto" label="转到" /></el-select>
              <el-select v-if="selectedStep.onFailure === 'goto'" v-model="selectedStep.onFailureGoto" style="width: 90px" @change="scheduleValidate()"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
            </el-form-item>
          </el-form>
          <el-button size="small" type="danger" data-test="prop-delete" @click="deleteStep(selectedStep!)">删除步骤</el-button>
        </template>
        <p v-else class="tiny muted">点选节点编辑属性。第一步（列表首项）为起点；新增步骤追加在列表末尾。</p>
      </div>
    </div>
    <el-table v-else :data="sequenceForm.steps" size="small" data-test="sequence-steps">
      <el-table-column label="编号" width="90"><template #default="{ row }"><el-input v-model="row.id" size="small" /></template></el-table-column>
      <el-table-column label="类型" width="105"><template #default="{ row }"><el-select v-model="row.type" size="small" data-test="step-type"><el-option v-for="(label, key) in stepTypeLabels" :key="key" :value="key" :label="label" /></el-select></template></el-table-column>
      <el-table-column label="步骤名" width="120"><template #default="{ row }"><el-input v-model="row.name" size="small" /></template></el-table-column>
      <el-table-column label="内容" min-width="330"><template #default="{ row }">
        <div v-if="row.type === 'write'" class="step-fields">
          <el-select v-model="row.tagId" filterable size="small" style="width: 170px" placeholder="写测点"><el-option v-for="t in writableTags" :key="t.id" :value="t.id" :label="tagName(t.id)" /></el-select>
          ← <el-input-number v-model="row.value" :controls="false" size="small" style="width: 70px" />
          <el-input v-model="row.confirmExpression" size="small" placeholder="完成条件 如 #20 == 1" style="width: 170px" />
        </div>
        <div v-else-if="row.type === 'wait'" class="step-fields"><el-input v-model="row.expression" size="small" placeholder="等待条件 如 #20 >= 95" style="width: 260px" /></div>
        <div v-else-if="row.type === 'condition'" class="step-fields">
          <el-input v-model="row.expression" size="small" placeholder="条件 如 #20 == 1" style="width: 150px" data-test="step-expression" />
          成立→<el-select v-model="row.then" size="small" style="width: 90px" data-test="step-then"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          否则→<el-select v-model="row.else" size="small" clearable placeholder="下一步" style="width: 90px" data-test="step-else"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
        </div>
        <div v-else-if="row.type === 'parallel'" class="step-fields">
          分支<el-select v-model="row.branches" multiple size="small" style="width: 170px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          汇合<el-select v-model="row.join" size="small" style="width: 90px"><el-option v-for="id in joinIds" :key="id" :value="id" :label="id" /></el-select>
        </div>
        <div v-else-if="row.type === 'join'" class="step-fields"><el-radio-group v-model="row.joinMode" size="small"><el-radio-button value="all">全部完成</el-radio-button><el-radio-button value="any">任一完成</el-radio-button></el-radio-group></div>
        <div v-else-if="row.type === 'jump'" class="step-fields">
          转到<el-select v-model="row.target" size="small" style="width: 90px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
          最多<el-input-number v-model="row.maxIterations" :min="1" :max="1000" size="small" style="width: 90px" />次
        </div>
        <span v-else class="tiny muted">结束本次运行</span>
      </template></el-table-column>
      <el-table-column label="下一步" width="95"><template #default="{ row }"><el-select v-if="['write', 'wait', 'join'].includes(row.type)" v-model="row.next" size="small" clearable placeholder="顺延"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select></template></el-table-column>
      <el-table-column label="超时ms" width="90"><template #default="{ row }"><el-input-number v-if="['write', 'wait'].includes(row.type)" v-model="row.timeoutMs" :controls="false" size="small" style="width: 75px" /></template></el-table-column>
      <el-table-column label="失败处理" width="170"><template #default="{ row }">
        <template v-if="!['end', 'join'].includes(row.type)">
          <el-select v-model="row.onFailure" size="small" style="width: 75px" clearable placeholder="停止"><el-option value="stop" label="停止" /><el-option value="continue" label="继续" /><el-option value="goto" label="转到" /></el-select>
          <el-select v-if="row.onFailure === 'goto'" v-model="row.onFailureGoto" size="small" style="width: 80px"><el-option v-for="id in stepIds" :key="id" :value="id" :label="id" /></el-select>
        </template>
      </template></el-table-column>
      <el-table-column label="" width="70"><template #default="{ $index }">
        <el-button size="small" link :disabled="$index === 0" @click="moveStep($index)">↑</el-button><el-button size="small" link type="danger" @click="sequenceForm.steps.splice($index, 1)">删</el-button>
      </template></el-table-column>
    </el-table>
    <el-button v-if="editorMode === 'table'" size="small" style="margin-top: 8px" data-test="sequence-add-step" @click="addStep()">添加步骤</el-button>
    <p class="tiny muted">从第一步开始执行；“下一步”为空时顺延列表中的下一步。写步骤走完整控制检查与回读，并在超时内等待完成条件。条件分支的引用测点无有效值时按失败处理；并行分支须汇合到同一汇合步骤（全部/任一）；循环只能用设置了次数上限的跳转步骤。失败处理：停止（默认）、继续（记失败后顺延）、转到（如安全停机步骤，运行最终记为失败）。保存时检查不可达步骤、缺少汇合和无上限循环。步骤测点若要求双人复核，顺控本身须勾选双人复核。</p>
    <template #footer><el-button @click="sequenceVisible = false">取消</el-button><el-button type="primary" data-test="sequence-save" @click="saveSequence()">保存</el-button></template>
  </el-dialog>

  <el-dialog v-model="runFlowVisible" :title="runFlow ? `运行 #${runFlow.id} ${runFlow.sequenceName} · ${runLabels[runFlow.status] ?? runFlow.status}` : ''" width="1000px" data-test="run-flow-dialog">
    <template v-if="runFlow && runFlowSteps">
      <SequenceFlow :steps="runFlowSteps" :results="runFlow.steps" :describe="describeStep" :height="520" />
      <p v-if="runFlowRevisionChanged" class="tiny muted">顺控定义在本次运行后已被修改，流程图按当前定义显示。</p>
    </template>
    <p v-else class="muted">顺控定义已删除，无法显示流程图。</p>
  </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, deviceApi, tagApi } from '../api'
import { controlApi, type ControlLockout, type ControlPoint, type ControlRequest, type ControlRun, type ControlSequence, type ControlStep, type ControlStepType, type DeviceControlState } from '../api/control'
import { errorMessage } from '../api/http'
import type { RecipeDownload } from '../api/recipes'
import { formatTime } from '../utils'
import SequenceFlow from '../components/sequence/SequenceFlow.vue'

interface NamedDevice { id: number; name: string }
interface NamedTag { id: number; name: string; deviceId: number | null; writable: boolean; kind?: string }
const tab = ref('requests')
const devices = ref<NamedDevice[]>([]), tags = ref<NamedTag[]>([]), areas = ref<{ id: number; name: string }[]>([])
const requests = ref<{ request: ControlRequest; sequenceName: string | null; recipe?: RecipeDownload | null; mine: boolean }[]>([])
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
      else if (r.kind === 'recipe') ElMessage.success(result.request.resultMessage ?? '已批准，开始下发配方')
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

const stepTypeLabels: Record<string, string> = { write: '写测点', wait: '等待', condition: '条件分支', parallel: '并行', join: '汇合', jump: '跳转/循环', end: '结束' }
const stepIds = computed(() => sequenceForm.steps.map(x => x.id).filter((x): x is string => !!x))
const joinIds = computed(() => sequenceForm.steps.filter(x => x.type === 'join').map(x => x.id).filter((x): x is string => !!x))
function addStep(type: ControlStepType = 'write'): void {
  const used = new Set(stepIds.value); let n = sequenceForm.steps.length + 1; while (used.has(`s${n}`)) n++
  const step: ControlStep = { id: `s${n}`, type, name: type === 'write' ? `步骤${n}` : `${stepTypeLabels[type]}${n}`, tagId: writableTags.value[0]?.id ?? 0, value: 0, confirmExpression: null, timeoutMs: 10000, delayMs: 0, joinMode: 'all', maxIterations: 3 }
  // 图形模式下新节点放在现有节点下方，避免与自动布局重叠。
  if (editorMode.value === 'flow') { const ys = sequenceForm.steps.map(s => s.y ?? 0); step.x = 20; step.y = (ys.length ? Math.max(...ys) : 0) + 112 }
  sequenceForm.steps.push(step); selectedStepId.value = step.id!; scheduleValidate()
}
const editorMode = ref<'flow' | 'table'>('flow')
const selectedStepId = ref<string | null>(null)
const selectedStep = computed(() => sequenceForm.steps.find(s => s.id === selectedStepId.value) ?? null)
const validation = ref<{ ok: boolean; message: string | null; stepIds: string[] } | null>(null)
let validateTimer: number | undefined
function scheduleValidate(): void {
  window.clearTimeout(validateTimer)
  validateTimer = window.setTimeout(async () => {
    if (!sequenceForm.steps.length) { validation.value = null; return }
    try { validation.value = await controlApi.validateSequence({ ...sequenceForm, name: sequenceForm.name || 'x', steps: cleanSteps() }) } catch { /* 校验接口失败时不阻断编辑，保存时后端仍会校验 */ }
  }, 300)
}
// 改编号时同步改写其他步骤对它的引用，避免断链。
function renameStep(step: ControlStep, value: string): void {
  const old = step.id, id = value.trim(); if (!id || id === old) return
  if (sequenceForm.steps.some(s => s.id === id)) { ElMessage.error(`编号 ${id} 已存在`); return }
  step.id = id
  for (const s of sequenceForm.steps) {
    for (const k of ['next', 'then', 'else', 'join', 'target', 'onFailureGoto'] as const) if (s[k] === old) s[k] = id
    if (s.branches) s.branches = s.branches.map(b => (b === old ? id : b))
  }
  selectedStepId.value = id; scheduleValidate()
}
function deleteStep(step: ControlStep): void {
  const i = sequenceForm.steps.indexOf(step); if (i < 0) return
  sequenceForm.steps.splice(i, 1)
  for (const s of sequenceForm.steps) {
    for (const k of ['next', 'then', 'else', 'join', 'target'] as const) if (s[k] === step.id) s[k] = null
    if (s.onFailureGoto === step.id) { s.onFailureGoto = null; s.onFailure = null }
    if (s.branches) s.branches = s.branches.filter(b => b !== step.id)
  }
  selectedStepId.value = null; scheduleValidate()
}
const runFlowVisible = ref(false), runFlowId = ref<number | null>(null)
const runFlow = computed(() => runs.value.find(r => r.id === runFlowId.value) ?? null)
const runFlowSequence = computed(() => sequences.value.find(s => s.id === runFlow.value?.sequenceId) ?? null)
const runFlowSteps = computed(() => runFlowSequence.value?.steps ?? null)
const runFlowRevisionChanged = computed(() => !!runFlow.value && !!runFlowSequence.value && runFlowSequence.value.updatedTs > runFlow.value.startedTs)
function openRunFlow(row: ControlRun): void { runFlowId.value = row.id; runFlowVisible.value = true }
function moveStep(i: number): void { const [s] = sequenceForm.steps.splice(i, 1); sequenceForm.steps.splice(i - 1, 0, s!) }
const sequenceVisible = ref(false)
const sequenceForm = reactive<{ id?: number; name: string; areaId: number; description: string | null; dualApproval: boolean; revision: number; steps: ControlStep[] }>({ name: '', areaId: 1, description: null, dualApproval: false, revision: 0, steps: [] })
function editSequence(row?: ControlSequence): void {
  Object.assign(sequenceForm, row ? { id: row.id, name: row.name, areaId: row.areaId, description: row.description, dualApproval: row.dualApproval, revision: row.revision, steps: row.steps.map(s => ({ ...s })) }
    : { id: undefined, name: '', areaId: areas.value[0]?.id ?? 1, description: null, dualApproval: false, revision: 0, steps: [] })
  selectedStepId.value = null; validation.value = null; sequenceVisible.value = true
  if (row) scheduleValidate()
}
function cleanSteps(): ControlStep[] {
  return sequenceForm.steps.map(s => ({ ...s, confirmExpression: s.confirmExpression || null, expression: s.expression || null, next: s.next || null, else: s.else || null, onFailure: s.onFailure || null, onFailureGoto: s.onFailure === 'goto' ? s.onFailureGoto || null : null, tagId: (s.type ?? 'write') === 'write' ? s.tagId : null }))
}
async function saveSequence(): Promise<void> {
  if (await guard(() => controlApi.saveSequence({ ...sequenceForm, steps: cleanSteps() }), '保存失败')) {
    ElMessage.success('已保存'); sequenceVisible.value = false; sequences.value = await controlApi.sequences()
  }
}
async function removeSequence(row: ControlSequence): Promise<void> {
  try { await ElMessageBox.confirm(`删除顺控 ${row.name}？运行记录保留。`, '删除', { type: 'warning' }) } catch { return }
  if (await guard(() => controlApi.removeSequence(row.id), '删除失败')) sequences.value = await controlApi.sequences()
}
function describeStep(s: ControlStep): string {
  switch (s.type ?? 'write') {
    case 'write': return `${tagName(s.tagId)} ← ${s.value}${s.confirmExpression ? `，等待 ${s.confirmExpression}` : ''}`
    case 'wait': return `等待 ${s.expression}`
    case 'condition': return `若 ${s.expression} 则 ${s.then} 否则 ${s.else ?? '下一步'}`
    case 'parallel': return `并行 ${(s.branches ?? []).join('、')} → 汇合 ${s.join}`
    case 'join': return `汇合（${s.joinMode === 'any' ? '任一' : '全部'}）`
    case 'jump': return `转到 ${s.target}（最多 ${s.maxIterations} 次）`
    default: return '结束'
  }
}
function stepsSummary(steps: ControlStep[]): string { return steps.map(s => `${s.id}.${s.name}${s.type && s.type !== 'write' ? `[${stepTypeLabels[s.type] ?? s.type}]` : ''}`).join(' → ') }
async function startSequence(row: ControlSequence): Promise<void> {
  const text = row.steps.map(s => `${s.id}. ${s.name}：${describeStep(s)}`).join('；')
  try { await ElMessageBox.confirm(text, `执行顺控 ${row.name}${row.dualApproval ? '（需双人复核）' : ''}`, { type: 'warning', confirmButtonText: row.dualApproval ? '提交复核' : '确认执行' }) } catch { return }
  await guard(async () => {
    const result = await controlApi.start(row.id)
    if (result.pending) ElMessage.warning('已提交双人复核'); else ElMessage.success(`已启动运行 #${result.runId}`)
  }, '启动失败')
  await refresh()
}
async function abort(row: ControlRun): Promise<void> {
  try { await ElMessageBox.confirm('正在进行的写入完成后停止（等待中的条件立即结束），其余步骤跳过；中止不会触发失败处理步骤。', `中止运行 #${row.id}`, { type: 'warning' }) } catch { return }
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
.step-fields { display: flex; gap: 4px; align-items: center; flex-wrap: wrap; }
h4 { margin: 16px 0 6px; }
.flow-editor { display: flex; gap: 10px; align-items: stretch; }
.flow-props { width: 300px; flex-shrink: 0; border: 1px solid var(--el-border-color-lighter); border-radius: 4px; padding: 8px; overflow: auto; max-height: 560px; }
</style>

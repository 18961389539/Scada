<template>
  <div class="page">
    <div class="panel">
      <el-tabs v-model="tab">
        <el-tab-pane label="通知路由" name="routes">
          <div class="toolbar">
            <span class="tiny muted">按等级、区域、班次把报警发到通道；设置“升级时间”的路由只在报警持续未确认达到该时间时发送一次。</span>
            <span class="spacer" />
            <el-button type="primary" :icon="Plus" :disabled="!channels.length" data-test="route-add" @click="openRoute()">新增路由</el-button>
          </div>
          <el-table :data="routes" size="small" empty-text="暂无路由">
            <el-table-column prop="name" label="名称" min-width="120" />
            <el-table-column label="区域" width="110"><template #default="{ row }">{{ row.areaId ? areaName(row.areaId) : '全部区域' }}</template></el-table-column>
            <el-table-column label="最低等级" width="90"><template #default="{ row }">{{ levelLabel(row.minLevel) }}</template></el-table-column>
            <el-table-column label="班次" width="100"><template #default="{ row }">{{ row.shiftId ? shiftName(row.shiftId) : '不限' }}</template></el-table-column>
            <el-table-column label="通道" width="130"><template #default="{ row }">{{ channelName(row.channelId) }}</template></el-table-column>
            <el-table-column label="触发" width="160">
              <template #default="{ row }">
                <span v-if="row.escalateAfterMinutes">未确认 {{ row.escalateAfterMinutes }} 分钟升级</span>
                <span v-else>{{ [row.onRaise ? '报警' : '', row.onRecover ? '恢复' : ''].filter(Boolean).join(' / ') }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="recipients" label="收件人" min-width="160" show-overflow-tooltip />
            <el-table-column label="启用" width="70"><template #default="{ row }">{{ row.enabled ? '是' : '否' }}</template></el-table-column>
            <el-table-column label="操作" width="120" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openRoute(row)">编辑</el-button>
                <el-popconfirm title="删除该路由？" @confirm="removeRoute(row.id)">
                  <template #reference><el-button link type="danger">删除</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane label="通知通道" name="channels">
          <div class="toolbar">
            <span class="tiny muted">口令、鉴权头等凭据加密保存，界面与接口只显示 ***。</span>
            <span class="spacer" />
            <el-button v-if="globalConfigure" type="primary" :icon="Plus" data-test="channel-add" @click="openChannel()">新增通道</el-button>
          </div>
          <el-table :data="channels" size="small" empty-text="暂无通道">
            <el-table-column prop="name" label="名称" min-width="120" />
            <el-table-column label="类型" width="140"><template #default="{ row }">{{ channelTypeLabels[row.type] }}</template></el-table-column>
            <el-table-column label="地址" min-width="200" show-overflow-tooltip>
              <template #default="{ row }">{{ row.type === 'Email' ? `${row.settings.host}:${row.settings.port}` : row.settings.url }}</template>
            </el-table-column>
            <el-table-column label="凭据" width="80"><template #default="{ row }">{{ row.hasSecret ? '***' : '无' }}</template></el-table-column>
            <el-table-column label="启用" width="70"><template #default="{ row }">{{ row.enabled ? '是' : '否' }}</template></el-table-column>
            <el-table-column v-if="globalConfigure" label="操作" width="170" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openChannel(row)">编辑</el-button>
                <el-button link @click="testChannel(row)">测试</el-button>
                <el-popconfirm title="删除该通道？" @confirm="removeChannel(row.id)">
                  <template #reference><el-button link type="danger">删除</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane label="班次" name="shifts">
          <div class="toolbar">
            <span class="spacer" />
            <el-button v-if="globalConfigure" type="primary" :icon="Plus" @click="openShift()">新增班次</el-button>
          </div>
          <el-table :data="shifts" size="small" empty-text="暂无班次">
            <el-table-column prop="name" label="名称" />
            <el-table-column label="时间" width="160"><template #default="{ row }">{{ minuteToTime(row.startMinute) }} - {{ minuteToTime(row.endMinute) }}</template></el-table-column>
            <el-table-column label="星期" min-width="200"><template #default="{ row }">{{ dayNames.filter((_, i) => row.days & (1 << i)).join(' ') }}</template></el-table-column>
            <el-table-column label="启用" width="70"><template #default="{ row }">{{ row.enabled ? '是' : '否' }}</template></el-table-column>
            <el-table-column v-if="globalConfigure" label="操作" width="120">
              <template #default="{ row }">
                <el-button link type="primary" @click="openShift(row)">编辑</el-button>
                <el-popconfirm title="删除该班次？" @confirm="removeShift(row.id)">
                  <template #reference><el-button link type="danger">删除</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="`投递失败${failedRows.length ? ` (${failedRows.length})` : ''}`" name="failed">
          <div class="toolbar"><span class="tiny muted">重试用尽的通知。最近 24 小时内未知悉的失败会使 /health 告警；知悉后不再告警，记录保留至清理期（默认 30 天）。</span><span class="spacer" />
            <el-button @click="loadFailed">刷新</el-button>
            <el-button type="warning" :disabled="!failedRows.length" data-test="outbox-ack-all" @click="acknowledge(null)">全部已知悉</el-button></div>
          <el-table :data="failedRows" empty-text="没有未知悉的投递失败" data-test="outbox-failed-table">
            <el-table-column label="报警" min-width="200"><template #default="{ row }">#{{ row.alarmEventId }} {{ row.alarmMessage }}</template></el-table-column>
            <el-table-column prop="action" label="动作" width="90" />
            <el-table-column prop="attempts" label="次数" width="70" />
            <el-table-column prop="lastError" label="最后错误" min-width="220" />
            <el-table-column label="失败时间" width="170"><template #default="{ row }">{{ row.finishedTs ? new Date(row.finishedTs).toLocaleString() : '--' }}</template></el-table-column>
            <el-table-column label="操作" width="100"><template #default="{ row }"><el-button link type="warning" @click="acknowledge([row.id])">已知悉</el-button></template></el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="routeDialog.visible" :title="routeDialog.id ? '编辑通知路由' : '新增通知路由'" width="560px">
      <el-form label-width="100px">
        <el-form-item label="名称" required><el-input v-model="routeForm.name" data-test="route-name" /></el-form-item>
        <el-form-item label="通道" required>
          <el-select v-model="routeForm.channelId" style="width: 100%">
            <el-option v-for="c in channels" :key="c.id" :label="`${c.name}（${channelTypeLabels[c.type]}）`" :value="c.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="区域">
          <el-select v-model="routeForm.areaId" clearable placeholder="全部区域（需全局配置权限）" style="width: 100%" data-test="route-area">
            <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="最低等级">
          <el-radio-group v-model="routeForm.minLevel">
            <el-radio-button value="Info">提示</el-radio-button>
            <el-radio-button value="Warning">警告</el-radio-button>
            <el-radio-button value="Critical">严重</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="班次">
          <el-select v-model="routeForm.shiftId" clearable placeholder="不限" style="width: 100%">
            <el-option v-for="s in shifts" :key="s.id" :label="`${s.name} ${minuteToTime(s.startMinute)}-${minuteToTime(s.endMinute)}`" :value="s.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="收件人">
          <el-input v-model="routeForm.recipients" type="textarea" :rows="2" placeholder="邮箱或手机号，逗号/换行分隔；webhook 可留空" />
        </el-form-item>
        <el-form-item label="升级">
          <el-switch v-model="routeEscalate" active-text="未确认超时升级" />
          <el-input-number v-if="routeEscalate" v-model="routeEscalateMinutes" :min="1" :max="10080" style="margin-left: 12px" />
          <span v-if="routeEscalate" class="tiny muted" style="margin-left: 6px">分钟</span>
        </el-form-item>
        <el-form-item v-if="!routeEscalate" label="发送时机">
          <el-checkbox v-model="routeForm.onRaise">报警</el-checkbox>
          <el-checkbox v-model="routeForm.onRecover">恢复</el-checkbox>
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="routeForm.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="routeDialog.visible = false">取消</el-button>
        <el-button type="primary" data-test="route-save" @click="saveRoute">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="channelDialog.visible" :title="channelDialog.id ? '编辑通道' : '新增通道'" width="560px">
      <el-form label-width="110px">
        <el-form-item label="名称" required><el-input v-model="channelForm.name" data-test="channel-name" /></el-form-item>
        <el-form-item label="类型">
          <el-select v-model="channelForm.type" style="width: 100%" data-test="channel-type">
            <el-option v-for="(label, value) in channelTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <template v-if="channelForm.type === 'Email'">
          <el-form-item label="SMTP 服务器" required><el-input v-model="channelForm.settings.host" /></el-form-item>
          <el-form-item label="端口"><el-input-number v-model="channelForm.settings.port" :min="1" :max="65535" /> <el-checkbox v-model="channelForm.settings.useSsl" style="margin-left: 12px">SSL/STARTTLS</el-checkbox></el-form-item>
          <el-form-item label="发件人" required><el-input v-model="channelForm.settings.from" placeholder="scada@plant.local" /></el-form-item>
          <el-form-item label="登录账号"><el-input v-model="channelForm.settings.userName" /></el-form-item>
          <el-form-item label="登录口令"><el-input v-model="channelForm.secret" type="password" show-password :placeholder="channelDialog.hasSecret ? '已设置，留空保持不变' : ''" autocomplete="new-password" /></el-form-item>
        </template>
        <template v-else>
          <el-form-item label="URL" required><el-input v-model="channelForm.settings.url" placeholder="https://..." data-test="channel-url" /></el-form-item>
          <el-form-item label="鉴权头名称"><el-input v-model="channelForm.settings.headerName" placeholder="例如 Authorization 或 X-Api-Key" data-test="channel-header" /></el-form-item>
          <el-form-item label="鉴权头的值"><el-input v-model="channelForm.secret" type="password" show-password :placeholder="channelDialog.hasSecret ? '已设置，留空保持不变' : ''" autocomplete="new-password" data-test="channel-secret" /></el-form-item>
        </template>
        <el-form-item label="超时 (毫秒)"><el-input-number v-model="channelForm.settings.timeoutMs" :min="500" :max="channelForm.type === 'Email' ? 120000 : 30000" :step="500" /></el-form-item>
        <el-form-item label="启用"><el-switch v-model="channelForm.enabled" /></el-form-item>
        <el-form-item v-if="channelDialog.hasSecret" label="">
          <el-checkbox v-model="channelDialog.clearSecret">清除已保存的凭据</el-checkbox>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="channelDialog.visible = false">取消</el-button>
        <el-button type="primary" data-test="channel-save" @click="saveChannel">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="shiftDialog.visible" :title="shiftDialog.id ? '编辑班次' : '新增班次'" width="480px">
      <el-form label-width="80px">
        <el-form-item label="名称" required><el-input v-model="shiftForm.name" /></el-form-item>
        <el-form-item label="时间">
          <el-time-select v-model="shiftStart" start="00:00" end="23:30" step="00:30" style="width: 120px" />
          <span style="margin: 0 8px">至</span>
          <el-time-select v-model="shiftEnd" start="00:00" end="23:30" step="00:30" style="width: 120px" />
        </el-form-item>
        <el-form-item label="星期">
          <el-checkbox-group v-model="shiftDays">
            <el-checkbox v-for="(d, i) in dayNames" :key="i" :value="i">{{ d }}</el-checkbox>
          </el-checkbox-group>
        </el-form-item>
        <el-form-item label="启用"><el-switch v-model="shiftForm.enabled" /></el-form-item>
        <div class="tiny muted">结束早于开始表示跨零点（如夜班 20:00-08:00）；按班次开始那天判断星期。</div>
      </el-form>
      <template #footer>
        <el-button @click="shiftDialog.visible = false">取消</el-button>
        <el-button type="primary" @click="saveShift">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus } from '@element-plus/icons-vue'
import { areaApi, notificationApi, type OutboxFailure } from '../api'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { Area, DutyShift, NotificationChannel, NotificationChannelInput, NotificationRoute } from '../types'
import { channelTypeLabels, hasGlobal, levelLabel, minuteToTime, timeToMinute } from '../utils'

const auth = useAuthStore()
const globalConfigure = computed(() => hasGlobal(auth.user, 'Configure'))
const tab = ref('routes')
const dayNames = ['周日', '周一', '周二', '周三', '周四', '周五', '周六']
const routes = ref<NotificationRoute[]>([])
const channels = ref<NotificationChannel[]>([])
const shifts = ref<DutyShift[]>([])
const areas = ref<Area[]>([])

const routeDialog = reactive({ visible: false, id: 0 })
const emptyRoute = (): Omit<NotificationRoute, 'id'> => ({ name: '', enabled: true, channelId: 0, areaId: null, minLevel: 'Warning', shiftId: null, recipients: '', onRaise: true, onRecover: false, escalateAfterMinutes: null })
const routeForm = reactive(emptyRoute())
const routeEscalate = ref(false)
const routeEscalateMinutes = ref(15)

const channelDialog = reactive({ visible: false, id: 0, hasSecret: false, clearSecret: false })
const emptyChannel = (): NotificationChannelInput => ({ name: '', type: 'Webhook', enabled: true, settings: { port: 25, useSsl: false, timeoutMs: 5000, url: '', host: '', from: '', userName: '', headerName: '' }, secret: '' })
const channelForm = reactive<NotificationChannelInput>(emptyChannel())

const shiftDialog = reactive({ visible: false, id: 0 })
const shiftForm = reactive({ name: '', enabled: true })
const shiftStart = ref('08:00')
const shiftEnd = ref('20:00')
const shiftDays = ref<number[]>([0, 1, 2, 3, 4, 5, 6])

const areaName = (id: number) => areas.value.find((x) => x.id === id)?.name ?? `#${id}`
const shiftName = (id: number) => shifts.value.find((x) => x.id === id)?.name ?? `#${id}`
const channelName = (id: number) => channels.value.find((x) => x.id === id)?.name ?? `#${id}`

async function load(): Promise<void> {
  try {
    const [r, c, s, a] = await Promise.all([notificationApi.routes(), notificationApi.channels(), notificationApi.shifts(), areaApi.list().catch(() => [] as Area[])])
    routes.value = r
    channels.value = c
    shifts.value = s
    areas.value = a
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载通知配置失败'))
  }
}

function openRoute(route?: NotificationRoute): void {
  routeDialog.id = route?.id ?? 0
  Object.assign(routeForm, route ? { ...route } : { ...emptyRoute(), channelId: channels.value[0]?.id ?? 0 })
  routeEscalate.value = !!route?.escalateAfterMinutes
  routeEscalateMinutes.value = route?.escalateAfterMinutes ?? 15
  routeDialog.visible = true
}

async function saveRoute(): Promise<void> {
  const input = { ...routeForm, escalateAfterMinutes: routeEscalate.value ? routeEscalateMinutes.value : null }
  delete (input as Partial<NotificationRoute>).id
  try {
    if (routeDialog.id) await notificationApi.updateRoute(routeDialog.id, input)
    else await notificationApi.createRoute(input)
    ElMessage.success('已保存')
    routeDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  }
}

async function removeRoute(id: number): Promise<void> {
  try {
    await notificationApi.removeRoute(id)
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
  }
}

function openChannel(channel?: NotificationChannel): void {
  channelDialog.id = channel?.id ?? 0
  channelDialog.hasSecret = !!channel?.hasSecret
  channelDialog.clearSecret = false
  const base = emptyChannel()
  Object.assign(channelForm, channel ? { name: channel.name, type: channel.type, enabled: channel.enabled, settings: { ...base.settings, ...channel.settings }, secret: '' } : base)
  channelDialog.visible = true
}

async function saveChannel(): Promise<void> {
  // 留空 = 保持原凭据（发 ***），勾选清除 = 发空串。
  const secret = channelDialog.clearSecret ? '' : channelForm.secret ? channelForm.secret : channelDialog.id ? '***' : null
  const input: NotificationChannelInput = { ...channelForm, settings: { ...channelForm.settings }, secret }
  try {
    if (channelDialog.id) await notificationApi.updateChannel(channelDialog.id, input)
    else await notificationApi.createChannel(input)
    ElMessage.success('已保存')
    channelDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  }
}

async function testChannel(channel: NotificationChannel): Promise<void> {
  let recipients = ''
  if (channel.type !== 'Webhook') {
    try {
      recipients = (await ElMessageBox.prompt('测试收件人（邮箱或手机号）', `测试通道：${channel.name}`)).value
    } catch {
      return
    }
  }
  try {
    const result = await notificationApi.testChannel(channel.id, recipients)
    if (result.success) ElMessage.success('测试消息已发送')
    else ElMessage.error(result.message)
  } catch (error) {
    ElMessage.error(errorMessage(error, '测试失败'))
  }
}

async function removeChannel(id: number): Promise<void> {
  try {
    await notificationApi.removeChannel(id)
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
  }
}

function openShift(shift?: DutyShift): void {
  shiftDialog.id = shift?.id ?? 0
  shiftForm.name = shift?.name ?? ''
  shiftForm.enabled = shift?.enabled ?? true
  shiftStart.value = minuteToTime(shift?.startMinute ?? 480)
  shiftEnd.value = minuteToTime(shift?.endMinute ?? 1200)
  shiftDays.value = [0, 1, 2, 3, 4, 5, 6].filter((i) => (shift?.days ?? 127) & (1 << i))
  shiftDialog.visible = true
}

async function saveShift(): Promise<void> {
  const input = { name: shiftForm.name, enabled: shiftForm.enabled, startMinute: timeToMinute(shiftStart.value), endMinute: timeToMinute(shiftEnd.value), days: shiftDays.value.reduce((acc, i) => acc | (1 << i), 0) }
  try {
    if (shiftDialog.id) await notificationApi.updateShift(shiftDialog.id, input)
    else await notificationApi.createShift(input)
    ElMessage.success('已保存')
    shiftDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  }
}

async function removeShift(id: number): Promise<void> {
  try {
    await notificationApi.removeShift(id)
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
  }
}

const failedRows = ref<OutboxFailure[]>([])
async function loadFailed(): Promise<void> {
  try { failedRows.value = await notificationApi.failed() } catch (e) { ElMessage.error(errorMessage(e, '读取投递失败记录失败')) }
}
async function acknowledge(ids: number[] | null): Promise<void> {
  try {
    const result = await notificationApi.acknowledge(ids ?? failedRows.value.map(x => x.id))
    ElMessage.success(`已知悉 ${result.acknowledged} 条`); await loadFailed()
  } catch (e) { ElMessage.error(errorMessage(e, '操作失败')) }
}
onMounted(() => { void load(); void loadFailed() })
</script>

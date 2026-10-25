<template>
  <div class="page">
    <div class="panel">
      <el-tabs v-model="tab">
        <el-tab-pane :label="$t('notificationsView.k77d257')" name="routes">
          <div class="toolbar">
            <span class="tiny muted">{{ $t('notificationsView.kc73ef7') }}</span>
            <span class="spacer" />
            <span v-if="!channels.length" class="tiny muted">{{ $t('notificationsView.routeNeedsChannel') }}</span>
            <el-button type="primary" :icon="Plus" :disabled="!channels.length" data-test="route-add" @click="openRoute()">{{ $t('notificationsView.kdc3efe') }}</el-button>
          </div>
          <el-table :data="routes" size="small" :empty-text="$t('notificationsView.kf4733f')">
            <el-table-column prop="name" :label="$t('notificationsView.k1be7ae')" min-width="120" />
            <el-table-column :label="$t('notificationsView.k17fc93')" width="110"><template #default="{ row }">{{ row.areaId ? areaName(row.areaId) : $t('notificationsView.k486677') }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.k224396')" width="90"><template #default="{ row }">{{ levelLabel(row.minLevel) }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.k851d63')" width="100"><template #default="{ row }">{{ row.shiftId ? shiftName(row.shiftId) : $t('notificationsView.k09c4fc') }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.kf3c5b9')" width="130"><template #default="{ row }">{{ channelName(row.channelId) }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.k27eccf')" width="160">
              <template #default="{ row }">
                <span v-if="row.escalateAfterMinutes">{{ $t('notificationsView.k7d9878', [row.escalateAfterMinutes]) }}</span>
                <span v-else>{{ [row.onRaise ? $t('notificationsView.k9f45b3') : '', row.onRecover ? $t('notificationsView.k79748c') : ''].filter(Boolean).join(' / ') }}</span>
              </template>
            </el-table-column>
            <el-table-column prop="recipients" :label="$t('notificationsView.k529414')" min-width="160" show-overflow-tooltip />
            <el-table-column :label="$t('notificationsView.kd4e9ca')" width="70"><template #default="{ row }">{{ row.enabled ? $t('notificationsView.k30160a') : $t('notificationsView.k8bf5c1') }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.kf3ea6d')" width="120" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openRoute(row)">{{ $t('notificationsView.ka7f814') }}</el-button>
                <el-popconfirm :title="$t('notificationsView.kcfe004')" @confirm="removeRoute(row.id)">
                  <template #reference><el-button link type="danger">{{ $t('notificationsView.k3755f5') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="$t('notificationsView.k010a5f')" name="channels">
          <div class="toolbar">
            <span class="tiny muted">{{ $t('notificationsView.k0dd0a5') }}</span>
            <span class="spacer" />
            <el-button v-if="globalConfigure" type="primary" :icon="Plus" data-test="channel-add" @click="openChannel()">{{ $t('notificationsView.k427a9b') }}</el-button>
          </div>
          <el-table :data="channels" size="small" :empty-text="$t('notificationsView.k40caa6')">
            <el-table-column prop="name" :label="$t('notificationsView.k1be7ae')" min-width="120" />
            <el-table-column :label="$t('notificationsView.ke4e46c')" width="140"><template #default="{ row }">{{ channelTypeLabels[row.type] }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.k67d2d7')" min-width="200" show-overflow-tooltip>
              <template #default="{ row }">{{ row.type === 'Email' ? `${row.settings.host}:${row.settings.port}` : row.settings.url }}</template>
            </el-table-column>
            <el-table-column :label="$t('notificationsView.k131caf')" width="80"><template #default="{ row }">{{ row.hasSecret ? '***' : $t('notificationsView.k720777') }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.kd4e9ca')" width="70"><template #default="{ row }">{{ row.enabled ? $t('notificationsView.k30160a') : $t('notificationsView.k8bf5c1') }}</template></el-table-column>
            <el-table-column v-if="globalConfigure" :label="$t('notificationsView.kf3ea6d')" width="170" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openChannel(row)">{{ $t('notificationsView.ka7f814') }}</el-button>
                <el-button link @click="testChannel(row)">{{ $t('notificationsView.k0b5d7e') }}</el-button>
                <el-popconfirm :title="$t('notificationsView.k276aa6')" @confirm="removeChannel(row.id)">
                  <template #reference><el-button link type="danger">{{ $t('notificationsView.k3755f5') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane :label="$t('notificationsView.k851d63')" name="shifts">
          <div class="toolbar">
            <span class="spacer" />
            <el-button v-if="globalConfigure" type="primary" :icon="Plus" @click="openShift()">{{ $t('notificationsView.k724c0a') }}</el-button>
          </div>
          <el-table :data="shifts" size="small" :empty-text="$t('notificationsView.k6f52d8')">
            <el-table-column prop="name" :label="$t('notificationsView.k1be7ae')" />
            <el-table-column :label="$t('notificationsView.k89b4aa')" width="160"><template #default="{ row }">{{ minuteToTime(row.startMinute) }} - {{ minuteToTime(row.endMinute) }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.ke35269')" min-width="200"><template #default="{ row }">{{ dayNames.filter((_, i) => row.days & (1 << i)).join(' ') }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.kd4e9ca')" width="70"><template #default="{ row }">{{ row.enabled ? $t('notificationsView.k30160a') : $t('notificationsView.k8bf5c1') }}</template></el-table-column>
            <el-table-column v-if="globalConfigure" :label="$t('notificationsView.kf3ea6d')" width="120">
              <template #default="{ row }">
                <el-button link type="primary" @click="openShift(row)">{{ $t('notificationsView.ka7f814') }}</el-button>
                <el-popconfirm :title="$t('notificationsView.k6e0bb2')" @confirm="removeShift(row.id)">
                  <template #reference><el-button link type="danger">{{ $t('notificationsView.k3755f5') }}</el-button></template>
                </el-popconfirm>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>
        <el-tab-pane :label="$t('notificationsView.kae95e7', [failedRows.length ? ` (${failedRows.length})` : ''])" name="failed">
          <div class="toolbar"><span class="tiny muted">{{ $t('notificationsView.k5882d5') }}</span><span class="spacer" />
            <el-button @click="loadFailed">{{ $t('notificationsView.k38108e') }}</el-button>
            <el-button type="warning" :disabled="!failedRows.length" data-test="outbox-ack-all" @click="acknowledge(null)">{{ $t('notificationsView.k9b1c0d') }}</el-button></div>
          <el-table :data="failedRows" :empty-text="$t('notificationsView.kca08f2')" data-test="outbox-failed-table">
            <el-table-column :label="$t('notificationsView.k9f45b3')" min-width="200"><template #default="{ row }">#{{ row.alarmEventId }} {{ row.alarmMessage }}</template></el-table-column>
            <el-table-column prop="action" :label="$t('notificationsView.kd9d982')" width="90" />
            <el-table-column prop="attempts" :label="$t('notificationsView.kd4a97e')" width="70" />
            <el-table-column prop="lastError" :label="$t('notificationsView.k03af10')" min-width="220" />
            <el-table-column :label="$t('notificationsView.k09e8b7')" width="170"><template #default="{ row }">{{ row.finishedTs ? formatTime(row.finishedTs) : '--' }}</template></el-table-column>
            <el-table-column :label="$t('notificationsView.kf3ea6d')" width="100"><template #default="{ row }"><el-button link type="warning" @click="acknowledge([row.id])">{{ $t('notificationsView.kfa4d47') }}</el-button></template></el-table-column>
          </el-table>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="routeDialog.visible" :title="routeDialog.id ? $t('notificationsView.kf6f2dc') : $t('notificationsView.kb17731')" width="560px">
      <el-form label-width="100px">
        <el-form-item :label="$t('notificationsView.k1be7ae')" required><el-input v-model="routeForm.name" data-test="route-name" /></el-form-item>
        <el-form-item :label="$t('notificationsView.kf3c5b9')" required>
          <el-select v-model="routeForm.channelId" style="width: 100%">
            <el-option v-for="c in channels" :key="c.id" :label="`${c.name}（${channelTypeLabels[c.type]}）`" :value="c.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('notificationsView.k17fc93')">
          <el-select v-model="routeForm.areaId" clearable :placeholder="$t('notificationsView.kb3e2f6')" style="width: 100%" data-test="route-area">
            <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('notificationsView.k224396')">
          <el-radio-group v-model="routeForm.minLevel">
            <el-radio-button value="Info">{{ $t('notificationsView.kab3656') }}</el-radio-button>
            <el-radio-button value="Warning">{{ $t('notificationsView.k5521e3') }}</el-radio-button>
            <el-radio-button value="Critical">{{ $t('notificationsView.k81ffc6') }}</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item :label="$t('notificationsView.k851d63')">
          <el-select v-model="routeForm.shiftId" clearable :placeholder="$t('notificationsView.k09c4fc')" style="width: 100%">
            <el-option v-for="s in shifts" :key="s.id" :label="`${s.name} ${minuteToTime(s.startMinute)}-${minuteToTime(s.endMinute)}`" :value="s.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('notificationsView.k529414')">
          <el-input v-model="routeForm.recipients" type="textarea" :rows="2" :placeholder="$t('notificationsView.k48135d')" />
        </el-form-item>
        <el-form-item :label="$t('notificationsView.k2e8e70')">
          <el-switch v-model="routeEscalate" :active-text="$t('notificationsView.ka5c346')" />
          <el-input-number v-if="routeEscalate" v-model="routeEscalateMinutes" :min="1" :max="10080" style="margin-left: 12px" />
          <span v-if="routeEscalate" class="tiny muted" style="margin-left: 6px">{{ $t('notificationsView.k28bf22') }}</span>
        </el-form-item>
        <el-form-item v-if="!routeEscalate" :label="$t('notificationsView.k043f90')">
          <el-checkbox v-model="routeForm.onRaise">{{ $t('notificationsView.k9f45b3') }}</el-checkbox>
          <el-checkbox v-model="routeForm.onRecover">{{ $t('notificationsView.k79748c') }}</el-checkbox>
        </el-form-item>
        <el-form-item :label="$t('notificationsView.kd4e9ca')"><el-switch v-model="routeForm.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="routeDialog.visible = false">{{ $t('notificationsView.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="route-save" @click="saveRoute">{{ $t('notificationsView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="channelDialog.visible" :title="channelDialog.id ? $t('notificationsView.k30f46b') : $t('notificationsView.k427a9b')" width="560px">
      <el-form label-width="110px">
        <el-form-item :label="$t('notificationsView.k1be7ae')" required><el-input v-model="channelForm.name" data-test="channel-name" /></el-form-item>
        <el-form-item :label="$t('notificationsView.ke4e46c')">
          <el-select v-model="channelForm.type" style="width: 100%" data-test="channel-type">
            <el-option v-for="(label, value) in channelTypeLabels" :key="value" :label="label" :value="value" />
          </el-select>
        </el-form-item>
        <template v-if="channelForm.type === 'Email'">
          <el-form-item :label="$t('notificationsView.k941346')" required><el-input v-model="channelForm.settings.host" /></el-form-item>
          <el-form-item :label="$t('notificationsView.k6cbb73')"><el-input-number v-model="channelForm.settings.port" :min="1" :max="65535" /> <el-checkbox v-model="channelForm.settings.useSsl" style="margin-left: 12px">SSL/STARTTLS</el-checkbox></el-form-item>
          <el-form-item :label="$t('notificationsView.k388b42')" required><el-input v-model="channelForm.settings.from" placeholder="scada@plant.local" /></el-form-item>
          <el-form-item :label="$t('notificationsView.k450070')"><el-input v-model="channelForm.settings.userName" /></el-form-item>
          <el-form-item :label="$t('notificationsView.k3e3f89')"><el-input v-model="channelForm.secret" type="password" show-password :placeholder="channelDialog.hasSecret ? $t('notificationsView.kc79793') : ''" autocomplete="new-password" /></el-form-item>
        </template>
        <template v-else>
          <el-form-item label="URL" required><el-input v-model="channelForm.settings.url" placeholder="https://..." data-test="channel-url" /></el-form-item>
          <el-form-item :label="$t('notificationsView.k8d89f3')"><el-input v-model="channelForm.settings.headerName" :placeholder="$t('notificationsView.k69fda5')" data-test="channel-header" /></el-form-item>
          <el-form-item :label="$t('notificationsView.kc4ad57')"><el-input v-model="channelForm.secret" type="password" show-password :placeholder="channelDialog.hasSecret ? $t('notificationsView.kc79793') : ''" autocomplete="new-password" data-test="channel-secret" /></el-form-item>
        </template>
        <el-form-item :label="$t('notificationsView.k9f6d02')"><el-input-number v-model="channelForm.settings.timeoutMs" :min="500" :max="channelForm.type === 'Email' ? 120000 : 30000" :step="500" /></el-form-item>
        <el-form-item :label="$t('notificationsView.kd4e9ca')"><el-switch v-model="channelForm.enabled" /></el-form-item>
        <el-form-item v-if="channelDialog.hasSecret" label="">
          <el-checkbox v-model="channelDialog.clearSecret">{{ $t('notificationsView.kbb965c') }}</el-checkbox>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="channelDialog.visible = false">{{ $t('notificationsView.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="channel-save" @click="saveChannel">{{ $t('notificationsView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="shiftDialog.visible" :title="shiftDialog.id ? $t('notificationsView.kf339ae') : $t('notificationsView.k724c0a')" width="480px">
      <el-form label-width="80px">
        <el-form-item :label="$t('notificationsView.k1be7ae')" required><el-input v-model="shiftForm.name" /></el-form-item>
        <el-form-item :label="$t('notificationsView.k89b4aa')">
          <el-time-select v-model="shiftStart" start="00:00" end="23:30" step="00:30" style="width: 120px" />
          <span style="margin: 0 8px">{{ $t('notificationsView.k43401e') }}</span>
          <el-time-select v-model="shiftEnd" start="00:00" end="23:30" step="00:30" style="width: 120px" />
        </el-form-item>
        <el-form-item :label="$t('notificationsView.ke35269')">
          <el-checkbox-group v-model="shiftDays">
            <el-checkbox v-for="(d, i) in dayNames" :key="i" :value="i">{{ d }}</el-checkbox>
          </el-checkbox-group>
        </el-form-item>
        <el-form-item :label="$t('notificationsView.kd4e9ca')"><el-switch v-model="shiftForm.enabled" /></el-form-item>
        <div class="tiny muted">{{ $t('notificationsView.k63b715') }}</div>
      </el-form>
      <template #footer>
        <el-button @click="shiftDialog.visible = false">{{ $t('notificationsView.k4d0b46') }}</el-button>
        <el-button type="primary" @click="saveShift">{{ $t('notificationsView.kfadf24') }}</el-button>
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
import { channelTypeLabels, formatTime, hasGlobal, levelLabel, minuteToTime, timeToMinute } from '../utils'
import { tr } from '../i18n'

const auth = useAuthStore()
const globalConfigure = computed(() => hasGlobal(auth.user, 'Configure'))
const tab = ref('routes')
const dayNames = [tr('notificationsView.kc34057'), tr('notificationsView.k792c34'), tr('notificationsView.k8f0344'), tr('notificationsView.k254556'), tr('notificationsView.k18f1fd'), tr('notificationsView.k4344fc'), tr('notificationsView.kf0c619')]
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
    ElMessage.error(errorMessage(error, tr('notificationsView.k24bf56')))
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
    ElMessage.success(tr('notificationsView.kcdfab9'))
    routeDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k40525a')))
  }
}

async function removeRoute(id: number): Promise<void> {
  try {
    await notificationApi.removeRoute(id)
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k72250c')))
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
    ElMessage.success(tr('notificationsView.kcdfab9'))
    channelDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k40525a')))
  }
}

async function testChannel(channel: NotificationChannel): Promise<void> {
  let recipients = ''
  if (channel.type !== 'Webhook') {
    try {
      recipients = (await ElMessageBox.prompt(tr('notificationsView.kbf93ab'), tr('notificationsView.ka79500', [channel.name]))).value
    } catch {
      return
    }
  }
  try {
    const result = await notificationApi.testChannel(channel.id, recipients)
    if (result.success) ElMessage.success(tr('notificationsView.k09b30e'))
    else ElMessage.error(result.message)
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k4184cd')))
  }
}

async function removeChannel(id: number): Promise<void> {
  try {
    await notificationApi.removeChannel(id)
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k72250c')))
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
    ElMessage.success(tr('notificationsView.kcdfab9'))
    shiftDialog.visible = false
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k40525a')))
  }
}

async function removeShift(id: number): Promise<void> {
  try {
    await notificationApi.removeShift(id)
    await load()
  } catch (error) {
    ElMessage.error(errorMessage(error, tr('notificationsView.k72250c')))
  }
}

const failedRows = ref<OutboxFailure[]>([])
async function loadFailed(): Promise<void> {
  try { failedRows.value = await notificationApi.failed() } catch (e) { ElMessage.error(errorMessage(e, tr('notificationsView.kdbc902'))) }
}
async function acknowledge(ids: number[] | null): Promise<void> {
  try {
    const result = await notificationApi.acknowledge(ids ?? failedRows.value.map(x => x.id))
    ElMessage.success(tr('notificationsView.k76339f', [result.acknowledged])); await loadFailed()
  } catch (e) { ElMessage.error(errorMessage(e, tr('notificationsView.k09e424'))) }
}
onMounted(() => { void load(); void loadFailed() })
</script>

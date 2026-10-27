<template>
  <div class="page">
    <div class="panel">
      <el-tabs v-model="activeTab" class="system-tabs">
        <el-tab-pane v-if="canUsers" :label="$t('systemView.kbaf847')" name="users">
          <div class="toolbar">
            <el-button type="primary" :icon="Plus" @click="openCreate">{{ $t('systemView.k304164') }}</el-button>
            <span class="spacer" />
            <el-button :icon="Refresh" @click="loadUsers">{{ $t('systemView.k38108e') }}</el-button>
          </div>
          <el-alert v-if="usersError" :title="usersError" type="error" :closable="false" show-icon data-test="users-error" style="margin-bottom: 12px" />
          <el-alert v-if="actionError" :title="actionError" type="error" :closable="false" show-icon data-test="users-action-error" style="margin-bottom: 12px" />
          <el-table :data="users" v-loading="usersLoading" border stripe :empty-text="usersError || undefined">
            <el-table-column prop="id" label="ID" width="70" />
            <el-table-column prop="userName" :label="$t('systemView.ka1aaf3')" min-width="140" />
            <el-table-column prop="displayName" :label="$t('systemView.kc10bbf')" min-width="140" />
            <el-table-column :label="$t('systemView.k6b2669')" width="110">
              <template #default="{ row }">
                <el-tag :type="row.role === 'Admin' ? 'danger' : row.role === 'Operator' ? 'warning' : 'info'">
                  {{ roleLabels[row.role as Role] ?? row.role }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('systemView.k62e951')" width="100">
              <template #default="{ row }">
                <el-tag :type="row.enabled ? 'success' : 'info'">{{ row.enabled ? $t('systemView.kd4e9ca') : $t('systemView.kd989e5') }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column :label="$t('systemView.kf3ea6d')" width="340" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openEdit(row)">{{ $t('systemView.ka7f814') }}</el-button>
                <el-button link type="success" data-test="user-assign" @click="openAssign(row)">{{ $t('systemView.kdf6c37') }}</el-button>
                <el-button link type="warning" @click="openPassword(row)">{{ $t('systemView.k7e4221') }}</el-button>
                <el-button link type="danger" :disabled="row.id === auth.user?.id" @click="removeUser(row)">
                  {{ $t('systemView.k3755f5') }}
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane v-if="canSystem" :label="$t('systemView.kf4bc87')" name="logs">
          <div class="toolbar">
            <el-date-picker
              v-model="logRange"
              type="datetimerange"
              :start-placeholder="$t('systemView.ke8868a')"
              :end-placeholder="$t('systemView.ka0bb9f')"
              style="width: 360px"
            />
            <el-input
              v-model="logKeyword"
              :placeholder="$t('systemView.k405cf1')"
              clearable
              style="width: 220px"
              @keyup.enter="searchLogs"
            />
            <el-button type="primary" :icon="Search" @click="searchLogs">{{ $t('systemView.k711363') }}</el-button>
            <span class="spacer" />
            <el-button :icon="Refresh" @click="loadLogs">{{ $t('systemView.k38108e') }}</el-button>
          </div>
          <el-alert v-if="logsError" :title="logsError" type="error" :closable="false" show-icon data-test="logs-error" style="margin-bottom: 12px" />
          <el-table :data="logs" v-loading="logsLoading" border stripe :empty-text="logsError || undefined">
            <el-table-column :label="$t('systemView.k89b4aa')" width="180">
              <template #default="{ row }">{{ formatTime(row.ts) }}</template>
            </el-table-column>
            <el-table-column prop="userName" :label="$t('systemView.k06858d')" width="130" />
            <el-table-column prop="action" :label="$t('systemView.kd9d982')" min-width="160" show-overflow-tooltip />
            <el-table-column prop="tagId" :label="$t('systemView.k8cff1c')" width="90">
              <template #default="{ row }">{{ row.tagId ?? '-' }}</template>
            </el-table-column>
            <el-table-column prop="oldValue" :label="$t('systemView.kefb4f3')" width="120" show-overflow-tooltip>
              <template #default="{ row }">{{ row.oldValue ?? '-' }}</template>
            </el-table-column>
            <el-table-column prop="newValue" :label="$t('systemView.kc4f3da')" width="120" show-overflow-tooltip>
              <template #default="{ row }">{{ row.newValue ?? '-' }}</template>
            </el-table-column>
            <el-table-column :label="$t('systemView.k675b71')" min-width="200">
              <template #default="{ row }">
                <el-popover v-if="row.details" placement="left" :width="460" trigger="click">
                  <template #reference><el-button link type="primary" size="small">{{ row.targetType ?? $t('systemView.kf611e2') }} #{{ row.targetId ?? '' }}</el-button></template>
                  <pre class="diff">{{ prettyDetails(row.details) }}</pre>
                </el-popover>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column prop="result" :label="$t('systemView.k0a2c91')" min-width="180" show-overflow-tooltip>
              <template #default="{ row }">
                <el-tag v-if="row.result" :type="resultType(row.result)" size="small" data-test="log-result">{{ resultLabel(row.result) }}</el-tag>
                <span v-else>-</span>
              </template>
            </el-table-column>
          </el-table>
          <div class="pager">
            <el-pagination
              v-model:current-page="logPage"
              v-model:page-size="logPageSize"
              :total="logTotal"
              :page-sizes="[20, 50, 100, 200]"
              layout="total, sizes, prev, pager, next, jumper"
              @current-change="loadLogs"
              @size-change="searchLogs"
            />
          </div>
        </el-tab-pane>

        <el-tab-pane v-if="canAccess" :label="$t('systemView.k393769')" name="access">
          <AccessManagement />
        </el-tab-pane>

        <el-tab-pane v-if="canSystem" :label="$t('systemView.kd27e7c')" name="health">
          <div class="toolbar">
            <el-button :icon="Refresh" :loading="healthLoading" @click="loadHealth()">{{ $t('systemView.k38108e') }}</el-button>
            <span class="muted">{{ $t('systemView.k0c62f6') }}</span>
          </div>
          <!-- 失败就地提示并保留上次数据。手动刷新额外弹一次；后台轮询不再连弹 toast。 -->
          <el-alert v-if="healthError" :title="healthError" type="warning" :closable="false" show-icon style="margin-bottom: 12px" data-test="health-error" />
          <template v-if="health">
            <div class="card-grid">
              <div class="stat-card">
                <div class="label">{{ $t('systemView.k2a4080') }}</div>
                <div class="value" :class="health.status === 'ok' ? 'ok' : 'bad'">
                  {{ health.status === 'ok' ? $t('systemView.kf78d03') : $t('systemView.kc6fdea') }}
                </div>
                <div class="sub">{{ health.environment }}</div>
              </div>
              <div class="stat-card">
                <div class="label">{{ $t('systemView.k66836c') }}</div>
                <div class="value">{{ uptimeText }}</div>
                <div class="sub">{{ $t('systemView.kf0ccc2', [formatTime(serverStartTs)]) }} · {{ $t('systemView.hostUptime', [hostUptimeText]) }}</div>
              </div>
              <div class="stat-card">
                <div class="label">{{ $t('systemView.k9c0cfc') }}</div>
                <div class="value">{{ health.onlineDevices }} / {{ health.totalDevices }}</div>
                <div class="sub">{{ $t('systemView.kfead4c', [health.configuredTags]) }}</div>
              </div>
              <div class="stat-card">
                <div class="label">{{ $t('systemView.k9ef115') }}</div>
                <div class="value">{{ health.busBacklog }}</div>
                <div class="sub">{{ $t('systemView.k901918') }}</div>
              </div>
              <div class="stat-card">
                <div class="label">{{ $t('systemView.k6e7fe2') }}</div>
                <div class="value">{{ health.disk.freeGb.toFixed(1) }} GB</div>
                <div class="sub">{{ $t('systemView.kfcc67b', [health.disk.totalGb.toFixed(1), health.disk.root]) }}</div>
              </div>
              <div class="stat-card">
                <div class="label">{{ $t('systemView.k0090e4') }}</div>
                <div class="value">{{ formatBytes(health.storage.historyBytes) }}</div>
                <div class="sub">{{ $t('systemView.k287238', [formatBytes(health.storage.configDbBytes)]) }}</div>
              </div>
            </div>

            <section v-if="health.historyCapacity" class="history-capacity" data-test="history-capacity">
              <div class="toolbar">
                <h3>{{ $t('systemView.historyCapacity') }}</h3>
                <el-tag :type="health.historyCapacity.scanComplete ? 'success' : 'warning'" size="small">{{ $t(health.historyCapacity.scanComplete ? 'systemView.capacityComplete' : 'systemView.capacityPartial') }}</el-tag>
                <span class="muted tiny">{{ formatTime(Date.parse(health.historyCapacity.checkedAt)) }}</span>
              </div>
              <dl class="capacity-grid">
                <div><dt>{{ $t('systemView.capacityTotal') }}</dt><dd>{{ formatBytes(health.historyCapacity.totalBytes) }}</dd></div>
                <div><dt>{{ $t('systemView.historyDiskFree') }}</dt><dd>{{ health.historyCapacity.disk.freeBytes === null ? $t('systemView.capacityUnknown') : formatBytes(health.historyCapacity.disk.freeBytes) }} <small>{{ health.historyCapacity.disk.root }}</small></dd></div>
                <div><dt>{{ $t('systemView.capacityRaw') }}</dt><dd>{{ formatBytes(health.historyCapacity.rawBytes) }} <small>{{ $t('systemView.capacityFiles', [health.historyCapacity.rawFiles]) }}</small></dd></div>
                <div><dt>{{ $t('systemView.capacityAggregates') }}</dt><dd>{{ formatBytes(health.historyCapacity.aggregateBytes) }} <small>{{ $t('systemView.capacityFiles', [health.historyCapacity.aggregateFiles]) }}</small></dd></div>
                <div><dt>{{ $t('systemView.capacityJournals') }}</dt><dd>{{ formatBytes(health.historyCapacity.journalBytes) }}</dd></div>
                <div><dt>{{ $t('systemView.capacitySpool') }}</dt><dd>{{ formatBytes(health.historyCapacity.spoolBytes) }}</dd></div>
                <div><dt>{{ $t('systemView.capacityOther') }}</dt><dd>{{ formatBytes(health.historyCapacity.otherBytes) }}</dd></div>
                <div><dt>{{ $t('systemView.capacityGrowth') }}</dt><dd>{{ health.historyCapacity.growthBytesPerDay === null ? $t('systemView.capacityUnknown') : $t('systemView.capacityPerDay', [formatBytes(health.historyCapacity.growthBytesPerDay)]) }}</dd></div>
                <div><dt>{{ $t('systemView.capacityForecast') }}</dt><dd>{{ health.historyCapacity.daysUntilDiskWarn === null ? $t('systemView.capacityUnknown') : $t('systemView.capacityDays', [health.historyCapacity.daysUntilDiskWarn.toFixed(1)]) }}</dd></div>
                <div><dt>{{ $t('systemView.retentionRaw') }}</dt><dd>{{ $t('systemView.capacityMonths', [health.historyCapacity.rawRetentionMonths]) }}</dd></div>
                <div><dt>{{ $t('systemView.retentionFiveMinute') }}</dt><dd>{{ $t('systemView.capacityDays', [health.historyCapacity.fiveMinuteRetentionDays]) }}</dd></div>
                <div><dt>{{ $t('systemView.retentionHour') }}</dt><dd>{{ health.historyCapacity.hourRetentionDays ? $t('systemView.capacityDays', [health.historyCapacity.hourRetentionDays]) : $t('systemView.capacityForever') }}</dd></div>
                <template v-if="health.historyQueries">
                  <div><dt>{{ $t('systemView.historyQueryActive') }}</dt><dd>{{ health.historyQueries.active }} / {{ health.historyQueries.maxConcurrent }}</dd></div>
                  <div><dt>{{ $t('systemView.historyQueryTimeout') }}</dt><dd>{{ $t('systemView.capacitySeconds', [health.historyQueries.timeoutSeconds]) }}</dd></div>
                  <div><dt>{{ $t('systemView.historyReadLimit') }}</dt><dd>{{ health.historyQueries.maxReadPoints.toLocaleString() }}</dd></div>
                  <div><dt>{{ $t('systemView.historyAnalysisLimit') }}</dt><dd>{{ health.historyQueries.analysisMaxPoints.toLocaleString() }}</dd></div>
                  <div><dt>{{ $t('systemView.historyQueryRejected') }}</dt><dd>{{ health.historyQueries.rejected }} / {{ health.historyQueries.timedOut }} / {{ health.historyQueries.readLimited }}</dd></div>
                </template>
              </dl>
            </section>
            <el-alert v-for="warning in health.warnings ?? []" :key="warning.code" :title="warning.message" type="warning" :closable="false" show-icon class="capacity-warning" />

            <el-table :data="health.devices" border stripe style="margin-top: 16px">
              <el-table-column prop="name" :label="$t('systemView.k01f2c1')" min-width="160" />
              <el-table-column :label="$t('systemView.k62e951')" width="110">
                <template #default="{ row }">
                  <el-tag :type="row.online ? 'success' : 'danger'">{{ row.online ? $t('systemView.k0373ff') : $t('systemView.k211357') }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column :label="$t('systemView.k6443e3')" width="180">
                <template #default="{ row }">{{ formatTime(row.lastPollTs) }}</template>
              </el-table-column>
              <el-table-column :label="$t('systemView.k9b415a')" width="120" align="right">
                <template #default="{ row }">{{ row.lastCycleMs ? `${row.lastCycleMs} ms` : '-' }}</template>
              </el-table-column>
              <el-table-column prop="tagCount" :label="$t('systemView.k217dc2')" width="100" align="right" />
              <el-table-column prop="reconnectAttempts" :label="$t('systemView.kc95ae0')" width="110" align="right" />
              <el-table-column prop="commFailures" :label="$t('systemView.k6ac251')" width="110" align="right" />
              <el-table-column prop="error" :label="$t('systemView.k5e8913')" min-width="220" show-overflow-tooltip>
                <template #default="{ row }">{{ row.error ?? '-' }}</template>
              </el-table-column>
            </el-table>
          </template>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="userDialog" :title="editingUser ? $t('systemView.k2b8f00') : $t('systemView.k304164')" width="460px">
      <el-alert v-if="userError" :title="userError" type="error" :closable="false" show-icon data-test="user-save-error" />
      <el-form :model="userForm" label-width="90px">
        <el-form-item :label="$t('systemView.ka1aaf3')">
          <el-input v-model="userForm.userName" :disabled="!!editingUser" maxlength="32" data-test="user-name" />
        </el-form-item>
        <el-form-item v-if="!editingUser" :label="$t('systemView.k961fcb')">
          <el-input v-model="userForm.password" type="password" show-password maxlength="64" data-test="user-password" />
        </el-form-item>
        <el-form-item :label="$t('systemView.kc10bbf')">
          <el-input v-model="userForm.displayName" maxlength="32" data-test="user-display" />
        </el-form-item>
        <el-form-item :label="$t('systemView.k6b2669')">
          <el-select v-model="userForm.role" style="width: 100%">
            <el-option :label="$t('systemView.kef84e7')" value="Admin" />
            <el-option :label="$t('systemView.k0c7c09')" value="Operator" />
            <el-option :label="$t('systemView.k41d997')" value="Viewer" />
            <el-option :label="$t('systemView.k4d7974')" value="None" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('systemView.kd4e9ca')">
          <el-switch v-model="userForm.enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="userDialog = false">{{ $t('systemView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="saving" @click="saveUser">{{ $t('systemView.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="passwordDialog" :title="$t('systemView.k7e4221')" width="400px">
      <el-alert v-if="passwordError" :title="passwordError" type="error" :closable="false" show-icon data-test="password-error" />
      <el-form label-width="90px" @submit.prevent="resetPassword">
        <el-form-item :label="$t('systemView.k9ba763')">
          <span>{{ passwordTarget?.userName }}</span>
        </el-form-item>
        <el-form-item :label="$t('systemView.kd22c9c')">
          <el-input v-model="newPassword" type="password" show-password maxlength="64" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="passwordDialog = false">{{ $t('systemView.k4d0b46') }}</el-button>
        <el-button type="primary" :loading="saving" @click="resetPassword">{{ $t('systemView.kf526c8') }}</el-button>
      </template>
    </el-dialog>
    <UserAssignmentsDialog v-model="assignDialog" :user="assignTarget" @saved="loadUsers" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh, Search } from '@element-plus/icons-vue'
import { healthApi, logApi, userApi } from '../api'
import AccessManagement from '../components/AccessManagement.vue'
import UserAssignmentsDialog from '../components/UserAssignmentsDialog.vue'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { Health, OperationLog, Role, UserItem } from '../types'
import { formatBytes, formatTime, roleLabels } from '../utils'
import { canGlobal } from '../access/policies'
import { usePolling } from '../composables/usePolling'
import { tr } from '../i18n'

const auth = useAuthStore()
const canUsers = computed(() => canGlobal(auth.user, 'ManageUsers'))
const canSystem = computed(() => canGlobal(auth.user, 'System'))
const canAccess = computed(() => canGlobal(auth.user, 'ManageUsers') || canGlobal(auth.user, 'Configure'))
const activeTab = ref(canUsers.value ? 'users' : canAccess.value ? 'access' : 'logs')
const assignDialog = ref(false)
const assignTarget = ref<UserItem | null>(null)

function openAssign(user: UserItem): void {
  assignTarget.value = user
  assignDialog.value = true
}

function prettyDetails(details: string): string {
  try { return JSON.stringify(JSON.parse(details), null, 2) } catch { return details }
}

const users = ref<UserItem[]>([])
const usersLoading = ref(false)
const usersError = ref('')
const actionError = ref('')
const userError = ref('')
const passwordError = ref('')
const userDialog = ref(false)
const editingUser = ref<UserItem | null>(null)
const saving = ref(false)
const userForm = ref({
  userName: '',
  password: '',
  displayName: '',
  role: 'Viewer' as Role,
  enabled: true
})

const passwordDialog = ref(false)
const passwordTarget = ref<UserItem | null>(null)
const newPassword = ref('')

const logs = ref<OperationLog[]>([])
const logsLoading = ref(false)
const logsError = ref('')
const logKeyword = ref('')
const logRange = ref<[Date, Date] | null>(null)
const logPage = ref(1)
const logPageSize = ref(50)
const logTotal = ref(0)

const health = ref<Health | null>(null)
const healthLoading = ref(false)
const healthError = ref('')
let healthRefreshing = false

const durationText = (total: number): string => {
  const days = Math.floor(total / 86400)
  const hours = Math.floor((total % 86400) / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = total % 60
  if (days > 0) return tr('systemView.k468f8c', [days, hours])
  if (hours > 0) return tr('systemView.k1cf4ee', [hours, minutes])
  return tr('systemView.k76dfb6', [minutes, seconds])
}
// uptimeSeconds 是进程运行时长（服务重启归零），hostUptimeSeconds 是主机开机时长（机器重启归零）。
// 两者并列才能区分「服务刚重启」和「整机刚重启」。
const uptimeText = computed(() => durationText(Math.floor(health.value?.uptimeSeconds ?? 0)))
const hostUptimeText = computed(() => durationText(Math.floor(health.value?.hostUptimeSeconds ?? 0)))

const serverStartTs = computed(() => {
  if (!health.value) return null
  return Date.now() - health.value.uptimeSeconds * 1000
})

function resultType(result: string): 'success' | 'warning' | 'danger' | 'info' {
  if (result.includes('成功') || result.startsWith('消音 ') || result === '已触发全部输出' || result === '管理员请求' || result === '网关已接受重连请求') return 'success' // i18n-ignore server result text
  if (result.includes('取消') || result.includes('无变化') || result.startsWith('待网关回执')) return 'info' // i18n-ignore server result text
  if (result.includes('失败') || result.includes('超时') || result.startsWith('拒绝')) return 'danger' // i18n-ignore server result text
  return 'warning'
}

const exactResults: Record<string, string> = {
  '成功': 'systemView.k51991a', // i18n-ignore server result text
  '成功（未启用签名）': 'systemView.resultOkUnsigned', // i18n-ignore server result text
  '成功，已签名': 'systemView.resultOkSigned', // i18n-ignore server result text
  '管理员请求': 'systemView.resultAdminRequest', // i18n-ignore server result text
  '已触发全部输出': 'systemView.resultOutputsTriggered', // i18n-ignore server result text
  '待网关回执；修改结果未确认时须查询实际状态': 'systemView.resultGatewayPending', // i18n-ignore server result text
  '连接取消或超时，结果未确认，请查询实际状态': 'systemView.resultGatewayUnconfirmed', // i18n-ignore server result text
  '网关已接受重连请求': 'systemView.resultGatewayReconnect', // i18n-ignore server result text
  '失败，结果未确认': 'systemView.resultFailedUnconfirmed', // i18n-ignore server result text
  '取消': 'systemView.k4d0b46', // i18n-ignore server result text
  '无变化': 'systemView.k208a9a', // i18n-ignore server result text
  '超时': 'systemView.resultTimeout' // i18n-ignore server result text
}

function resultLabel(result: string): string {
  const exact = exactResults[result]
  if (exact) return tr(exact)
  if (result.startsWith('成功，但')) return tr('systemView.resultOkBut', [result.slice(4)]) // i18n-ignore server result text
  if (result.startsWith('失败：')) return tr('systemView.resultFailedDetail', [result.slice(3)]) // i18n-ignore server result text
  if (result.startsWith('拒绝：')) return tr('systemView.resultDenied', [result.slice(3)]) // i18n-ignore server result text
  const silenced = /^消音 (\d+) 分钟$/.exec(result) // i18n-ignore server result text
  if (silenced) return tr('systemView.resultSilenced', [silenced[1]])
  return result
}

async function loadUsers(): Promise<void> {
  usersLoading.value = true
  try {
    users.value = await userApi.list()
    usersError.value = ''
  } catch (error) {
    usersError.value = errorMessage(error, tr('systemView.ka07db7'))
  } finally {
    usersLoading.value = false
  }
}

function openCreate(): void {
  editingUser.value = null
  userForm.value = { userName: '', password: '', displayName: '', role: 'Viewer', enabled: true }
  userError.value = ''
  userDialog.value = true
}

function openEdit(user: UserItem): void {
  editingUser.value = user
  userForm.value = {
    userName: user.userName,
    password: '',
    displayName: user.displayName,
    role: user.role,
    enabled: user.enabled
  }
  userError.value = ''
  userDialog.value = true
}

async function saveUser(): Promise<void> {
  userError.value = ''
  if (!editingUser.value) {
    if (userForm.value.userName.trim().length < 3) {
      userError.value = tr('systemView.k2d4a4b')
      return
    }
    if (userForm.value.password.length < 6) {
      userError.value = tr('systemView.kc0a16c')
      return
    }
  }

  saving.value = true
  try {
    if (editingUser.value) {
      await userApi.update(editingUser.value.id, {
        displayName: userForm.value.displayName,
        role: userForm.value.role,
        enabled: userForm.value.enabled
      })
    } else {
      await userApi.create({
        userName: userForm.value.userName.trim(),
        password: userForm.value.password,
        displayName: userForm.value.displayName.trim(),
        role: userForm.value.role,
        enabled: userForm.value.enabled
      })
    }
    userError.value = ''
    ElMessage.success(tr('systemView.kcdfab9'))
    userDialog.value = false
    await loadUsers()
  } catch (error) {
    userError.value = errorMessage(error, tr('systemView.k40525a'))
  } finally {
    saving.value = false
  }
}

function openPassword(user: UserItem): void {
  passwordTarget.value = user
  newPassword.value = ''
  passwordError.value = ''
  passwordDialog.value = true
}

async function resetPassword(): Promise<void> {
  if (!passwordTarget.value || saving.value) return
  passwordError.value = ''
  if (newPassword.value.length < 6) {
    passwordError.value = tr('systemView.kc0a16c')
    return
  }

  saving.value = true
  try {
    await userApi.resetPassword(passwordTarget.value.id, newPassword.value)
    passwordError.value = ''
    ElMessage.success(tr('systemView.kd9de45'))
    passwordDialog.value = false
  } catch (error) {
    passwordError.value = errorMessage(error, tr('systemView.kdd9a02'))
  } finally {
    saving.value = false
  }
}

async function removeUser(user: UserItem): Promise<void> {
  try {
    await ElMessageBox.confirm(tr('systemView.keb8334', [user.userName]), tr('systemView.kbcfca9'), { type: 'warning' })
  } catch {
    return
  }

  actionError.value = ''
  try {
    await userApi.remove(user.id)
    actionError.value = ''
    ElMessage.success(tr('systemView.kfb5fe1'))
    await loadUsers()
  } catch (error) {
    actionError.value = errorMessage(error, tr('systemView.k72250c'))
  }
}

function searchLogs(): void {
  logPage.value = 1
  void loadLogs()
}

async function loadLogs(): Promise<void> {
  logsLoading.value = true
  try {
    const result = await logApi.query({
      from: logRange.value?.[0].getTime(),
      to: logRange.value?.[1].getTime(),
      keyword: logKeyword.value.trim() || undefined,
      page: logPage.value,
      pageSize: logPageSize.value
    })
    logs.value = result.items
    logTotal.value = result.total
    logsError.value = ''
  } catch (error) {
    logsError.value = errorMessage(error, tr('systemView.k205db9'))
  } finally {
    logsLoading.value = false
  }
}

async function loadHealth(background = false): Promise<void> {
  if (background && healthRefreshing) return
  healthRefreshing = true
  if (!background) healthLoading.value = true
  try {
    health.value = await healthApi.get()
    healthError.value = ''
  } catch (error) {
    const message = errorMessage(error, tr('systemView.k1eccb5'))
    healthError.value = message
    if (!background) ElMessage.error(message)
  } finally {
    healthRefreshing = false
    if (!background) healthLoading.value = false
  }
}

// 系统健康 10s 兜底轮询；无 System 权限时不发请求
usePolling(() => loadHealth(true), 10_000, { enabled: () => canSystem.value })

onMounted(async () => {
  await Promise.all([canUsers.value ? loadUsers() : Promise.resolve(), canSystem.value ? loadLogs() : Promise.resolve(), canSystem.value ? loadHealth() : Promise.resolve()])
})
</script>

<style scoped>
.system-tabs :deep(.el-tabs__content) {
  padding-top: 8px;
}
.diff { max-height: 360px; overflow: auto; font-size: 12px; margin: 0; white-space: pre-wrap; }
.history-capacity { margin-top: 24px; }
.history-capacity h3 { margin: 0; font-size: 16px; }
.capacity-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 0 24px; margin: 0; }
.capacity-grid > div { display: flex; flex-wrap: wrap; justify-content: space-between; gap: 8px 16px; padding: 10px 0; border-bottom: 1px solid var(--el-border-color-lighter); }
.capacity-grid dt { color: var(--el-text-color-secondary); }
.capacity-grid dd { margin: 0; overflow-wrap: anywhere; }
.capacity-grid small { color: var(--el-text-color-secondary); margin-left: 8px; }
.capacity-warning { margin-top: 12px; }
@media (max-width: 680px) { .capacity-grid { grid-template-columns: minmax(0, 1fr); } }
</style>

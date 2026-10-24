<template>
  <div class="page">
    <div class="panel">
      <el-tabs v-model="activeTab" class="system-tabs">
        <el-tab-pane v-if="canUsers" label="用户管理" name="users">
          <div class="toolbar">
            <el-button type="primary" :icon="Plus" @click="openCreate">新建用户</el-button>
            <span class="spacer" />
            <el-button :icon="Refresh" @click="loadUsers">刷新</el-button>
          </div>
          <el-table :data="users" v-loading="usersLoading" border stripe>
            <el-table-column prop="id" label="ID" width="70" />
            <el-table-column prop="userName" label="用户名" min-width="140" />
            <el-table-column prop="displayName" label="显示名" min-width="140" />
            <el-table-column label="角色" width="110">
              <template #default="{ row }">
                <el-tag :type="row.role === 'Admin' ? 'danger' : row.role === 'Operator' ? 'warning' : 'info'">
                  {{ roleLabels[row.role as Role] ?? row.role }}
                </el-tag>
              </template>
            </el-table-column>
            <el-table-column label="状态" width="100">
              <template #default="{ row }">
                <el-tag :type="row.enabled ? 'success' : 'info'">{{ row.enabled ? '启用' : '停用' }}</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="340" fixed="right">
              <template #default="{ row }">
                <el-button link type="primary" @click="openEdit(row)">编辑</el-button>
                <el-button link type="success" data-test="user-assign" @click="openAssign(row)">区域授权</el-button>
                <el-button link type="warning" @click="openPassword(row)">重置密码</el-button>
                <el-button link type="danger" :disabled="row.id === auth.user?.id" @click="removeUser(row)">
                  删除
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </el-tab-pane>

        <el-tab-pane v-if="canSystem" label="操作日志" name="logs">
          <div class="toolbar">
            <el-date-picker
              v-model="logRange"
              type="datetimerange"
              start-placeholder="开始时间"
              end-placeholder="结束时间"
              style="width: 360px"
            />
            <el-input
              v-model="logKeyword"
              placeholder="用户名 / 动作 / 结果"
              clearable
              style="width: 220px"
              @keyup.enter="searchLogs"
            />
            <el-button type="primary" :icon="Search" @click="searchLogs">查询</el-button>
            <span class="spacer" />
            <el-button :icon="Refresh" @click="loadLogs">刷新</el-button>
          </div>
          <el-table :data="logs" v-loading="logsLoading" border stripe>
            <el-table-column label="时间" width="180">
              <template #default="{ row }">{{ formatTime(row.ts) }}</template>
            </el-table-column>
            <el-table-column prop="userName" label="操作人" width="130" />
            <el-table-column prop="action" label="动作" min-width="160" show-overflow-tooltip />
            <el-table-column prop="tagId" label="测点" width="90">
              <template #default="{ row }">{{ row.tagId ?? '-' }}</template>
            </el-table-column>
            <el-table-column prop="oldValue" label="原值" width="120" show-overflow-tooltip>
              <template #default="{ row }">{{ row.oldValue ?? '-' }}</template>
            </el-table-column>
            <el-table-column prop="newValue" label="新值" width="120" show-overflow-tooltip>
              <template #default="{ row }">{{ row.newValue ?? '-' }}</template>
            </el-table-column>
            <el-table-column label="变更明细" min-width="200">
              <template #default="{ row }">
                <el-popover v-if="row.details" placement="left" :width="460" trigger="click">
                  <template #reference><el-button link type="primary" size="small">{{ row.targetType ?? '差分' }} #{{ row.targetId ?? '' }}</el-button></template>
                  <pre class="diff">{{ prettyDetails(row.details) }}</pre>
                </el-popover>
                <span v-else>-</span>
              </template>
            </el-table-column>
            <el-table-column prop="result" label="结果" min-width="180" show-overflow-tooltip>
              <template #default="{ row }">
                <el-tag v-if="row.result" :type="resultType(row.result)" size="small">{{ row.result }}</el-tag>
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

        <el-tab-pane v-if="canAccess" label="区域与角色" name="access">
          <AccessManagement />
        </el-tab-pane>

        <el-tab-pane v-if="canSystem" label="系统状态" name="health">
          <div class="toolbar">
            <el-button :icon="Refresh" :loading="healthLoading" @click="loadHealth">刷新</el-button>
            <span class="muted">每 10 秒自动刷新</span>
          </div>
          <template v-if="health">
            <div class="card-grid">
              <div class="stat-card">
                <div class="label">运行状态</div>
                <div class="value" :class="health.status === 'ok' ? 'ok' : 'bad'">
                  {{ health.status === 'ok' ? '正常' : '部分设备离线' }}
                </div>
                <div class="sub">{{ health.environment }}</div>
              </div>
              <div class="stat-card">
                <div class="label">运行时长</div>
                <div class="value">{{ uptimeText }}</div>
                <div class="sub">启动于 {{ formatTime(serverStartTs) }}</div>
              </div>
              <div class="stat-card">
                <div class="label">在线设备</div>
                <div class="value">{{ health.onlineDevices }} / {{ health.totalDevices }}</div>
                <div class="sub">采集测点 {{ health.configuredTags }}</div>
              </div>
              <div class="stat-card">
                <div class="label">数据总线积压</div>
                <div class="value">{{ health.busBacklog }}</div>
                <div class="sub">重启期间历史数据量</div>
              </div>
              <div class="stat-card">
                <div class="label">磁盘空间</div>
                <div class="value">{{ health.disk.freeGb.toFixed(1) }} GB</div>
                <div class="sub">共 {{ health.disk.totalGb.toFixed(1) }} GB · {{ health.disk.root }}</div>
              </div>
              <div class="stat-card">
                <div class="label">历史数据</div>
                <div class="value">{{ formatBytes(health.storage.historyBytes) }}</div>
                <div class="sub">配置库 {{ formatBytes(health.storage.configDbBytes) }}</div>
              </div>
            </div>

            <el-table :data="health.devices" border stripe style="margin-top: 16px">
              <el-table-column prop="name" label="设备" min-width="160" />
              <el-table-column label="状态" width="110">
                <template #default="{ row }">
                  <el-tag :type="row.online ? 'success' : 'danger'">{{ row.online ? '在线' : '离线' }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column label="最后轮询" width="180">
                <template #default="{ row }">{{ formatTime(row.lastPollTs) }}</template>
              </el-table-column>
              <el-table-column label="采集周期" width="120" align="right">
                <template #default="{ row }">{{ row.lastCycleMs ? `${row.lastCycleMs} ms` : '-' }}</template>
              </el-table-column>
              <el-table-column prop="tagCount" label="测点数" width="100" align="right" />
              <el-table-column prop="reconnectAttempts" label="重连次数" width="110" align="right" />
              <el-table-column prop="commFailures" label="通信失败" width="110" align="right" />
              <el-table-column prop="error" label="最近错误" min-width="220" show-overflow-tooltip>
                <template #default="{ row }">{{ row.error ?? '-' }}</template>
              </el-table-column>
            </el-table>
          </template>
        </el-tab-pane>
      </el-tabs>
    </div>

    <el-dialog v-model="userDialog" :title="editingUser ? '编辑用户' : '新建用户'" width="460px">
      <el-form :model="userForm" label-width="90px">
        <el-form-item label="用户名">
          <el-input v-model="userForm.userName" :disabled="!!editingUser" maxlength="32" />
        </el-form-item>
        <el-form-item v-if="!editingUser" label="初始密码">
          <el-input v-model="userForm.password" type="password" show-password maxlength="64" />
        </el-form-item>
        <el-form-item label="显示名">
          <el-input v-model="userForm.displayName" maxlength="32" />
        </el-form-item>
        <el-form-item label="角色">
          <el-select v-model="userForm.role" style="width: 100%">
            <el-option label="管理员" value="Admin" />
            <el-option label="操作员" value="Operator" />
            <el-option label="查看员" value="Viewer" />
            <el-option label="无全局角色（仅区域授权）" value="None" />
          </el-select>
        </el-form-item>
        <el-form-item label="启用">
          <el-switch v-model="userForm.enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="userDialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="saveUser">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="passwordDialog" title="重置密码" width="400px">
      <el-form label-width="90px">
        <el-form-item label="用户">
          <span>{{ passwordTarget?.userName }}</span>
        </el-form-item>
        <el-form-item label="新密码">
          <el-input v-model="newPassword" type="password" show-password maxlength="64" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="passwordDialog = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="resetPassword">确定</el-button>
      </template>
    </el-dialog>
    <UserAssignmentsDialog v-model="assignDialog" :user="assignTarget" @saved="loadUsers" />
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Plus, Refresh, Search } from '@element-plus/icons-vue'
import { healthApi, logApi, userApi } from '../api'
import AccessManagement from '../components/AccessManagement.vue'
import UserAssignmentsDialog from '../components/UserAssignmentsDialog.vue'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { Health, OperationLog, Role, UserItem } from '../types'
import { formatBytes, formatTime, hasGlobal, roleLabels } from '../utils'

const auth = useAuthStore()
const canUsers = computed(() => hasGlobal(auth.user, 'ManageUsers'))
const canSystem = computed(() => hasGlobal(auth.user, 'System'))
const canAccess = computed(() => hasGlobal(auth.user, 'ManageUsers') || hasGlobal(auth.user, 'Configure'))
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
const logKeyword = ref('')
const logRange = ref<[Date, Date] | null>(null)
const logPage = ref(1)
const logPageSize = ref(50)
const logTotal = ref(0)

const health = ref<Health | null>(null)
const healthLoading = ref(false)
let healthTimer: number | undefined

const uptimeText = computed(() => {
  const total = Math.floor(health.value?.uptimeSeconds ?? 0)
  const days = Math.floor(total / 86400)
  const hours = Math.floor((total % 86400) / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = total % 60
  if (days > 0) return `${days} 天 ${hours} 小时`
  if (hours > 0) return `${hours} 小时 ${minutes} 分`
  return `${minutes} 分 ${seconds} 秒`
})

const serverStartTs = computed(() => {
  if (!health.value) return null
  return Date.now() - health.value.uptimeSeconds * 1000
})

function resultType(result: string): 'success' | 'warning' | 'danger' | 'info' {
  if (result.includes('成功')) return 'success'
  if (result.includes('取消') || result.includes('无变化')) return 'info'
  if (result.includes('失败') || result.includes('超时')) return 'danger'
  return 'warning'
}

async function loadUsers(): Promise<void> {
  usersLoading.value = true
  try {
    users.value = await userApi.list()
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载用户失败'))
  } finally {
    usersLoading.value = false
  }
}

function openCreate(): void {
  editingUser.value = null
  userForm.value = { userName: '', password: '', displayName: '', role: 'Viewer', enabled: true }
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
  userDialog.value = true
}

async function saveUser(): Promise<void> {
  if (!editingUser.value) {
    if (userForm.value.userName.trim().length < 3) {
      ElMessage.warning('用户名至少 3 个字符')
      return
    }
    if (userForm.value.password.length < 6) {
      ElMessage.warning('密码至少 6 个字符')
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
    ElMessage.success('已保存')
    userDialog.value = false
    await loadUsers()
  } catch (error) {
    ElMessage.error(errorMessage(error, '保存失败'))
  } finally {
    saving.value = false
  }
}

function openPassword(user: UserItem): void {
  passwordTarget.value = user
  newPassword.value = ''
  passwordDialog.value = true
}

async function resetPassword(): Promise<void> {
  if (!passwordTarget.value) return
  if (newPassword.value.length < 6) {
    ElMessage.warning('密码至少 6 个字符')
    return
  }

  saving.value = true
  try {
    await userApi.resetPassword(passwordTarget.value.id, newPassword.value)
    ElMessage.success('密码已重置')
    passwordDialog.value = false
  } catch (error) {
    ElMessage.error(errorMessage(error, '重置失败'))
  } finally {
    saving.value = false
  }
}

async function removeUser(user: UserItem): Promise<void> {
  try {
    await ElMessageBox.confirm(`确定删除用户「${user.userName}」吗？`, '删除用户', { type: 'warning' })
  } catch {
    return
  }

  try {
    await userApi.remove(user.id)
    ElMessage.success('已删除')
    await loadUsers()
  } catch (error) {
    ElMessage.error(errorMessage(error, '删除失败'))
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
  } catch (error) {
    ElMessage.error(errorMessage(error, '加载操作日志失败'))
  } finally {
    logsLoading.value = false
  }
}

async function loadHealth(): Promise<void> {
  healthLoading.value = true
  try {
    health.value = await healthApi.get()
  } catch (error) {
    ElMessage.error(errorMessage(error, '获取系统状态失败'))
  } finally {
    healthLoading.value = false
  }
}

onMounted(async () => {
  await Promise.all([canUsers.value ? loadUsers() : Promise.resolve(), canSystem.value ? loadLogs() : Promise.resolve(), canSystem.value ? loadHealth() : Promise.resolve()])
  healthTimer = window.setInterval(loadHealth, 10_000)
})

onBeforeUnmount(() => {
  if (healthTimer !== undefined) window.clearInterval(healthTimer)
})
</script>

<style scoped>
.system-tabs :deep(.el-tabs__content) {
  padding-top: 8px;
}
.diff { max-height: 360px; overflow: auto; font-size: 12px; margin: 0; white-space: pre-wrap; }
</style>

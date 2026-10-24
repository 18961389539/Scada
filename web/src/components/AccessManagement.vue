<template>
  <div>
    <el-tabs v-model="tab">
      <el-tab-pane v-if="canAreas" label="区域" name="areas">
        <div class="toolbar">
          <el-button type="primary" data-test="area-create" @click="openArea()">新建区域</el-button>
          <span class="spacer" />
          <el-button @click="loadAreas">刷新</el-button>
        </div>
        <el-table :data="areas" border stripe data-test="area-table">
          <el-table-column prop="id" label="ID" width="70" />
          <el-table-column prop="name" label="名称" min-width="160" />
          <el-table-column prop="description" label="说明" min-width="200" />
          <el-table-column prop="deviceCount" label="设备数" width="90" />
          <el-table-column label="操作" width="160">
            <template #default="{ row }">
              <el-button link type="primary" @click="openArea(row)">编辑</el-button>
              <el-button link type="danger" :disabled="row.isDefault" @click="removeArea(row)">删除</el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
      <el-tab-pane v-if="canRoles" label="角色" name="roles">
        <div class="toolbar">
          <el-button type="primary" data-test="role-create" @click="openRole()">新建角色</el-button>
          <span class="spacer" />
          <el-button @click="loadRoles">刷新</el-button>
        </div>
        <el-alert type="info" :closable="false" show-icon class="hint"
          title="内置角色（管理员/操作员/查看员）作用于全部区域；自定义角色在给用户授权时可限定到某个区域。" />
        <el-table :data="catalog?.roles ?? []" border stripe data-test="role-table">
          <el-table-column prop="name" label="角色" min-width="140" />
          <el-table-column label="权限" min-width="260">
            <template #default="{ row }">
              <el-tag v-for="p in row.permissions" :key="p" size="small" class="perm">{{ permissionLabels[p as Permission] }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="assignments" label="授权数" width="90" />
          <el-table-column label="操作" width="160">
            <template #default="{ row }">
              <el-button link type="primary" @click="openRole(row)">编辑</el-button>
              <el-button link type="danger" :disabled="row.assignments > 0" @click="removeRole(row)">删除</el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="areaDialog" :title="areaForm.id ? '编辑区域' : '新建区域'" width="420px">
      <el-form label-width="70px">
        <el-form-item label="名称"><el-input v-model="areaForm.name" maxlength="100" data-test="area-name" /></el-form-item>
        <el-form-item label="说明"><el-input v-model="areaForm.description" maxlength="500" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="areaDialog = false">取消</el-button>
        <el-button type="primary" data-test="area-save" @click="saveArea">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="roleDialog" :title="roleForm.id ? '编辑角色' : '新建角色'" width="480px">
      <el-form label-width="70px">
        <el-form-item label="名称"><el-input v-model="roleForm.name" maxlength="100" data-test="role-name" /></el-form-item>
        <el-form-item label="说明"><el-input v-model="roleForm.description" maxlength="500" /></el-form-item>
        <el-form-item label="权限">
          <el-checkbox-group v-model="roleForm.permissions" data-test="role-permissions">
            <el-checkbox v-for="p in allPermissions" :key="p" :value="p">{{ permissionLabels[p] }}</el-checkbox>
          </el-checkbox-group>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="roleDialog = false">取消</el-button>
        <el-button type="primary" data-test="role-save" @click="saveRole">保存</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { areaApi, roleApi } from '../api'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AccessRoleItem, Area, Permission, RoleCatalog } from '../types'
import { hasGlobal, permissionLabels } from '../utils'

const auth = useAuthStore()
const canAreas = computed(() => hasGlobal(auth.user, 'Configure'))
const canRoles = computed(() => hasGlobal(auth.user, 'ManageUsers'))
const tab = ref(canAreas.value ? 'areas' : 'roles')
const areas = ref<Area[]>([])
const catalog = ref<RoleCatalog | null>(null)
const allPermissions: Permission[] = ['View', 'Control', 'AckAlarm', 'Configure', 'ManageUsers', 'System']

const areaDialog = ref(false)
const areaForm = reactive<{ id: number | null; name: string; description: string }>({ id: null, name: '', description: '' })
const roleDialog = ref(false)
const roleForm = reactive<{ id: number | null; name: string; description: string; permissions: Permission[] }>({ id: null, name: '', description: '', permissions: [] })

async function loadAreas(): Promise<void> {
  try { areas.value = await areaApi.list() } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function loadRoles(): Promise<void> {
  if (!canRoles.value) return
  try { catalog.value = await roleApi.list() } catch (e) { ElMessage.error(errorMessage(e)) }
}
function openArea(area?: Area): void {
  areaForm.id = area?.id ?? null; areaForm.name = area?.name ?? ''; areaForm.description = area?.description ?? ''
  areaDialog.value = true
}
async function saveArea(): Promise<void> {
  try {
    const input = { name: areaForm.name, description: areaForm.description || null }
    if (areaForm.id) await areaApi.update(areaForm.id, input)
    else await areaApi.create(input)
    areaDialog.value = false
    ElMessage.success('已保存')
    await loadAreas()
  } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function removeArea(area: Area): Promise<void> {
  try {
    await ElMessageBox.confirm(`删除区域“${area.name}”？`, '确认', { type: 'warning' })
    await areaApi.remove(area.id)
    await loadAreas()
  } catch (e) { if (e !== 'cancel') ElMessage.error(errorMessage(e)) }
}
function openRole(role?: AccessRoleItem): void {
  roleForm.id = role?.id ?? null; roleForm.name = role?.name ?? ''; roleForm.description = role?.description ?? ''
  roleForm.permissions = [...(role?.permissions ?? ['View'])]
  roleDialog.value = true
}
async function saveRole(): Promise<void> {
  try {
    const input = { name: roleForm.name, description: roleForm.description || null, permissions: roleForm.permissions }
    if (roleForm.id) await roleApi.update(roleForm.id, input)
    else await roleApi.create(input)
    roleDialog.value = false
    ElMessage.success('已保存')
    await loadRoles()
  } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function removeRole(role: AccessRoleItem): Promise<void> {
  try {
    await ElMessageBox.confirm(`删除角色“${role.name}”？`, '确认', { type: 'warning' })
    await roleApi.remove(role.id)
    await loadRoles()
  } catch (e) { if (e !== 'cancel') ElMessage.error(errorMessage(e)) }
}

onMounted(async () => { await Promise.all([loadAreas(), loadRoles()]) })
</script>

<style scoped>
.toolbar { display: flex; gap: 8px; margin-bottom: 12px; }
.spacer { flex: 1; }
.hint { margin-bottom: 12px; }
.perm { margin-right: 4px; }
</style>

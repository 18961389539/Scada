<template>
  <div>
    <el-tabs v-model="tab">
      <el-tab-pane v-if="canAreas" :label="$t('accessManagement.k17fc93')" name="areas">
        <div class="toolbar">
          <el-button type="primary" data-test="area-create" @click="openArea()">{{ $t('accessManagement.k03c732') }}</el-button>
          <span class="spacer" />
          <el-button @click="loadAreas">{{ $t('accessManagement.k38108e') }}</el-button>
        </div>
        <el-table :data="areas" border stripe data-test="area-table">
          <el-table-column prop="id" label="ID" width="70" />
          <el-table-column prop="name" :label="$t('accessManagement.k1be7ae')" min-width="160" />
          <el-table-column prop="description" :label="$t('accessManagement.k26670d')" min-width="200" />
          <el-table-column prop="deviceCount" :label="$t('accessManagement.k7beb4b')" width="90" />
          <el-table-column :label="$t('accessManagement.kf3ea6d')" width="160">
            <template #default="{ row }">
              <el-button link type="primary" @click="openArea(row)">{{ $t('accessManagement.ka7f814') }}</el-button>
              <el-button link type="danger" :disabled="row.isDefault" @click="removeArea(row)">{{ $t('accessManagement.k3755f5') }}</el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
      <el-tab-pane v-if="canRoles" :label="$t('accessManagement.k6b2669')" name="roles">
        <div class="toolbar">
          <el-button type="primary" data-test="role-create" @click="openRole()">{{ $t('accessManagement.k7a0524') }}</el-button>
          <span class="spacer" />
          <el-button @click="loadRoles">{{ $t('accessManagement.k38108e') }}</el-button>
        </div>
        <el-alert type="info" :closable="false" show-icon class="hint"
          :title="$t('accessManagement.k003c57')" />
        <el-table :data="catalog?.roles ?? []" border stripe data-test="role-table">
          <el-table-column prop="name" :label="$t('accessManagement.k6b2669')" min-width="140" />
          <el-table-column :label="$t('accessManagement.k560165')" min-width="260">
            <template #default="{ row }">
              <el-tag v-for="p in row.permissions" :key="p" size="small" class="perm">{{ permissionLabels[p as Permission] }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="assignments" :label="$t('accessManagement.kf3640c')" width="90" />
          <el-table-column :label="$t('accessManagement.kf3ea6d')" width="160">
            <template #default="{ row }">
              <el-button link type="primary" @click="openRole(row)">{{ $t('accessManagement.ka7f814') }}</el-button>
              <el-button link type="danger" :disabled="row.assignments > 0" @click="removeRole(row)">{{ $t('accessManagement.k3755f5') }}</el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-tab-pane>
    </el-tabs>

    <el-dialog v-model="areaDialog" :title="areaForm.id ? $t('accessManagement.k4331ec') : $t('accessManagement.k03c732')" width="420px">
      <el-form label-width="70px">
        <el-form-item :label="$t('accessManagement.k1be7ae')"><el-input v-model="areaForm.name" maxlength="100" data-test="area-name" /></el-form-item>
        <el-form-item :label="$t('accessManagement.k26670d')"><el-input v-model="areaForm.description" maxlength="500" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="areaDialog = false">{{ $t('accessManagement.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="area-save" @click="saveArea">{{ $t('accessManagement.kfadf24') }}</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="roleDialog" :title="roleForm.id ? $t('accessManagement.kb9dcd8') : $t('accessManagement.k7a0524')" width="480px">
      <el-form label-width="70px">
        <el-form-item :label="$t('accessManagement.k1be7ae')"><el-input v-model="roleForm.name" maxlength="100" data-test="role-name" /></el-form-item>
        <el-form-item :label="$t('accessManagement.k26670d')"><el-input v-model="roleForm.description" maxlength="500" /></el-form-item>
        <el-form-item :label="$t('accessManagement.k560165')">
          <el-checkbox-group v-model="roleForm.permissions" data-test="role-permissions">
            <el-checkbox v-for="p in allPermissions" :key="p" :value="p">{{ permissionLabels[p] }}</el-checkbox>
          </el-checkbox-group>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="roleDialog = false">{{ $t('accessManagement.k4d0b46') }}</el-button>
        <el-button type="primary" data-test="role-save" @click="saveRole">{{ $t('accessManagement.kfadf24') }}</el-button>
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
import { tr } from '../i18n'

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
    ElMessage.success(tr('accessManagement.kcdfab9'))
    await loadAreas()
  } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function removeArea(area: Area): Promise<void> {
  try {
    await ElMessageBox.confirm(tr('accessManagement.k699f6a', [area.name]), tr('accessManagement.kb56d9a'), { type: 'warning' })
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
    ElMessage.success(tr('accessManagement.kcdfab9'))
    await loadRoles()
  } catch (e) { ElMessage.error(errorMessage(e)) }
}
async function removeRole(role: AccessRoleItem): Promise<void> {
  try {
    await ElMessageBox.confirm(tr('accessManagement.k662beb', [role.name]), tr('accessManagement.kb56d9a'), { type: 'warning' })
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

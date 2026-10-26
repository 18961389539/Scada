<template>
  <el-dialog :model-value="modelValue" :title="$t('userAssignmentsDialog.kcaae71', [user?.userName ?? ''])" width="620px" @update:model-value="emit('update:modelValue', $event)" @open="load">
    <p class="muted">{{ $t('userAssignmentsDialog.k46e6e4', [roleLabels[user?.role ?? 'None']]) }}</p>
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" show-icon data-test="assignment-load-error" style="margin-bottom: 12px" />
    <el-alert v-if="saveError" :title="saveError" type="error" :closable="false" show-icon data-test="assignment-save-error" style="margin-bottom: 12px" />
    <el-table :data="rows" border size="small" data-test="assignment-table" :empty-text="loadError || undefined">
      <el-table-column :label="$t('userAssignmentsDialog.k6b2669')" min-width="180">
        <template #default="{ row }">
          <el-select v-model="row.roleId" :placeholder="$t('userAssignmentsDialog.k215093')" data-test="assignment-role">
            <el-option v-for="r in roles" :key="r.id" :label="r.name" :value="r.id" />
          </el-select>
        </template>
      </el-table-column>
      <el-table-column :label="$t('userAssignmentsDialog.k5c5e19')" min-width="180">
        <template #default="{ row }">
          <el-select v-model="row.areaId" :placeholder="$t('userAssignmentsDialog.k486677')" clearable data-test="assignment-area">
            <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </template>
      </el-table-column>
      <el-table-column width="70">
        <template #default="{ $index }"><el-button link type="danger" @click="rows.splice($index, 1)">{{ $t('userAssignmentsDialog.k2f752c') }}</el-button></template>
      </el-table-column>
    </el-table>
    <el-button class="add" data-test="assignment-add" @click="rows.push({ roleId: roles[0]?.id ?? 0, areaId: null })" :disabled="!roles.length">{{ $t('userAssignmentsDialog.k4ff615') }}</el-button>
    <template #footer>
      <el-button @click="emit('update:modelValue', false)">{{ $t('userAssignmentsDialog.k4d0b46') }}</el-button>
      <el-button type="primary" data-test="assignment-save" @click="save">{{ $t('userAssignmentsDialog.kfadf24') }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage } from 'element-plus'
import { areaApi, roleApi } from '../api'
import { errorMessage } from '../api/http'
import type { AccessRoleItem, Area, UserItem } from '../types'
import { roleLabels } from '../utils'
import { tr } from '../i18n'

const props = defineProps<{ modelValue: boolean; user: UserItem | null }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: boolean): void; (e: 'saved'): void }>()
const rows = ref<{ roleId: number; areaId: number | null }[]>([])
const roles = ref<AccessRoleItem[]>([])
const areas = ref<Area[]>([])
const loadError = ref('')
const saveError = ref('')

async function load(): Promise<void> {
  if (!props.user) return
  saveError.value = ''
  try {
    const [catalog, areaList, current] = await Promise.all([roleApi.list(), areaApi.list(), roleApi.assignments(props.user.id)])
    roles.value = catalog.roles
    areas.value = areaList
    rows.value = current.map(x => ({ roleId: x.roleId, areaId: x.areaId }))
    loadError.value = ''
  } catch (e) { loadError.value = errorMessage(e, tr('userAssignmentsDialog.loadFailed')) }
}

async function save(): Promise<void> {
  if (!props.user) return
  saveError.value = ''
  try {
    await roleApi.saveAssignments(props.user.id, rows.value.filter(x => x.roleId > 0).map(x => ({ roleId: x.roleId, areaId: x.areaId ?? null })))
    saveError.value = ''
    ElMessage.success(tr('userAssignmentsDialog.k3faa95'))
    emit('saved')
    emit('update:modelValue', false)
  } catch (e) { saveError.value = errorMessage(e, tr('userAssignmentsDialog.saveFailed')) }
}
</script>

<style scoped>
.muted { color: #8a94a6; margin: 0 0 10px; }
.add { margin-top: 10px; }
</style>

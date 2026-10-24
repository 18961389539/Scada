<template>
  <el-dialog :model-value="modelValue" :title="`区域授权：${user?.userName ?? ''}`" width="620px" @update:model-value="emit('update:modelValue', $event)" @open="load">
    <p class="muted">内置角色：{{ roleLabels[user?.role ?? 'None'] }}（作用于全部区域）。下面的自定义角色授权会在此基础上叠加。</p>
    <el-table :data="rows" border size="small" data-test="assignment-table">
      <el-table-column label="角色" min-width="180">
        <template #default="{ row }">
          <el-select v-model="row.roleId" placeholder="选择角色" data-test="assignment-role">
            <el-option v-for="r in roles" :key="r.id" :label="r.name" :value="r.id" />
          </el-select>
        </template>
      </el-table-column>
      <el-table-column label="区域范围" min-width="180">
        <template #default="{ row }">
          <el-select v-model="row.areaId" placeholder="全部区域" clearable data-test="assignment-area">
            <el-option v-for="a in areas" :key="a.id" :label="a.name" :value="a.id" />
          </el-select>
        </template>
      </el-table-column>
      <el-table-column width="70">
        <template #default="{ $index }"><el-button link type="danger" @click="rows.splice($index, 1)">移除</el-button></template>
      </el-table-column>
    </el-table>
    <el-button class="add" data-test="assignment-add" @click="rows.push({ roleId: roles[0]?.id ?? 0, areaId: null })" :disabled="!roles.length">添加授权</el-button>
    <template #footer>
      <el-button @click="emit('update:modelValue', false)">取消</el-button>
      <el-button type="primary" data-test="assignment-save" @click="save">保存</el-button>
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

const props = defineProps<{ modelValue: boolean; user: UserItem | null }>()
const emit = defineEmits<{ (e: 'update:modelValue', value: boolean): void; (e: 'saved'): void }>()
const rows = ref<{ roleId: number; areaId: number | null }[]>([])
const roles = ref<AccessRoleItem[]>([])
const areas = ref<Area[]>([])

async function load(): Promise<void> {
  if (!props.user) return
  try {
    const [catalog, areaList, current] = await Promise.all([roleApi.list(), areaApi.list(), roleApi.assignments(props.user.id)])
    roles.value = catalog.roles
    areas.value = areaList
    rows.value = current.map(x => ({ roleId: x.roleId, areaId: x.areaId }))
  } catch (e) { ElMessage.error(errorMessage(e)) }
}

async function save(): Promise<void> {
  if (!props.user) return
  try {
    await roleApi.saveAssignments(props.user.id, rows.value.filter(x => x.roleId > 0).map(x => ({ roleId: x.roleId, areaId: x.areaId ?? null })))
    ElMessage.success('授权已保存')
    emit('saved')
    emit('update:modelValue', false)
  } catch (e) { ElMessage.error(errorMessage(e)) }
}
</script>

<style scoped>
.muted { color: #8a94a6; margin: 0 0 10px; }
.add { margin-top: 10px; }
</style>

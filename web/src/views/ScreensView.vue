<template>
  <div class="page">
    <div class="toolbar"><h2>工艺画面</h2><span class="muted">{{ admin ? '草稿仅管理员可见，发布后供现场运行' : '已发布的运行画面' }}</span><span class="spacer" />
      <el-button v-if="admin" type="primary" @click="create">新建画面</el-button><el-button @click="load">刷新</el-button></div>
    <div class="screen-list" v-loading="loading">
      <article v-for="screen in screens" :key="screen.id" class="screen-card">
        <div class="card-heading"><span class="screen-symbol">▧</span><el-tag :type="screen.publishedRevision ? 'success' : 'info'">{{ screen.publishedRevision ? '已发布' : '草稿' }}</el-tag></div>
        <h3>{{ screen.name }}</h3><p class="muted">{{ screen.updatedBy }} · {{ formatTime(screen.updatedTs) }}</p>
        <p class="muted">版本 {{ screen.revision }}<span v-if="screen.hasDraftChanges"> · 有未发布修改</span></p>
        <div class="card-actions"><el-button v-if="screen.publishedRevision" type="primary" @click="router.push(`/screens/${screen.id}`)">运行</el-button>
          <el-button v-if="admin" @click="router.push(`/config/screens/${screen.id}`)">编辑</el-button><el-button v-if="admin" type="danger" link @click="remove(screen)">删除</el-button></div>
      </article>
    </div>
    <el-empty v-if="!loading && !screens.length" :description="admin ? '创建第一个工艺画面，绑定设备测点后发布' : '尚无已发布画面'" />
  </div>
</template>
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import { formatTime , hasGlobal } from '../utils'
import type { ScreenSummary } from '../screens/model'
const auth = useAuthStore(), router = useRouter(), admin = computed(() => hasGlobal(auth.user, 'Configure'))
const screens = ref<ScreenSummary[]>([]), loading = ref(false)
async function load(): Promise<void> { loading.value = true; try { screens.value = await screenApi.list(admin.value) } catch (e) { ElMessage.error(errorMessage(e)) } finally { loading.value = false } }
async function create(): Promise<void> {
  try { const { value } = await ElMessageBox.prompt('输入画面名称', '新建工艺画面', { inputValidator: (v: string) => !!v?.trim() && v.trim().length <= 100 || '请输入 1～100 个字符' }); const screen = await screenApi.create(value); await router.push(`/config/screens/${screen.id}`) }
  catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) }
}
async function remove(screen: ScreenSummary): Promise<void> {
  try { await ElMessageBox.confirm(`确认删除“${screen.name}”？运行画面也将移除。`, '删除画面', { type: 'warning' }); await screenApi.remove(screen.id, screen.revision); await load() }
  catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) }
}
onMounted(load)
</script>
<style scoped>
.screen-list { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 20px; }
.screen-card { padding: 24px; background: white; border: 1px solid #e2e8f0; border-radius: 12px; }
.card-heading, .card-actions { display: flex; align-items: center; gap: 8px; } .card-heading { justify-content: space-between; }
.screen-symbol { font-size: 38px; color: #1668dc; } h3 { margin: 16px 0 8px; }
</style>

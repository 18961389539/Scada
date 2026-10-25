<template>
  <div class="page">
    <div class="toolbar"><h2>{{ $t('screensView.k8a1840') }}</h2><span class="muted">{{ admin ? $t('screensView.k2c00f4') : $t('screensView.k05a21f') }}</span><span class="spacer" />
      <el-button v-if="admin" type="primary" @click="create">{{ $t('screensView.kdc834f') }}</el-button><el-button @click="load">{{ $t('screensView.k38108e') }}</el-button></div>
    <div class="screen-list" v-loading="loading">
      <article v-for="screen in screens" :key="screen.id" class="screen-card">
        <div class="card-heading"><span class="screen-symbol">▧</span><el-tag :type="screen.publishedRevision ? 'success' : 'info'">{{ screen.publishedRevision ? $t('screensView.k176a2e') : $t('screensView.k0f4368') }}</el-tag></div>
        <h3>{{ screen.name }}</h3><p class="muted">{{ screen.updatedBy }} · {{ formatTime(screen.updatedTs) }}</p>
        <p class="muted">{{ $t('screensView.k55d3d8', [screen.revision]) }}<span v-if="screen.hasDraftChanges"> {{ $t('screensView.ka368a9') }}</span></p>
        <div class="card-actions"><el-button v-if="screen.publishedRevision" type="primary" @click="router.push(`/screens/${screen.id}`)">{{ $t('screensView.k0c3acd') }}</el-button>
          <el-button v-if="admin" @click="router.push(`/config/screens/${screen.id}`)">{{ $t('screensView.ka7f814') }}</el-button><el-button v-if="admin" type="danger" link @click="remove(screen)">{{ $t('screensView.k3755f5') }}</el-button></div>
      </article>
    </div>
    <el-empty v-if="!loading && !screens.length" :description="admin ? $t('screensView.k433980') : $t('screensView.k0a44b0')" />
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
import { tr } from '../i18n'
const auth = useAuthStore(), router = useRouter(), admin = computed(() => hasGlobal(auth.user, 'Configure'))
const screens = ref<ScreenSummary[]>([]), loading = ref(false)
async function load(): Promise<void> { loading.value = true; try { screens.value = await screenApi.list(admin.value) } catch (e) { ElMessage.error(errorMessage(e)) } finally { loading.value = false } }
async function create(): Promise<void> {
  try { const { value } = await ElMessageBox.prompt(tr('screensView.kaab7c5'), tr('screensView.kf95f8f'), { inputValidator: (v: string) => !!v?.trim() && v.trim().length <= 100 || tr('screensView.kaa0b13') }); const screen = await screenApi.create(value); await router.push(`/config/screens/${screen.id}`) }
  catch (e) { if (e !== 'cancel' && e !== 'close') ElMessage.error(errorMessage(e)) }
}
async function remove(screen: ScreenSummary): Promise<void> {
  try { await ElMessageBox.confirm(tr('screensView.k56f5f9', [screen.name]), tr('screensView.k783512'), { type: 'warning' }); await screenApi.remove(screen.id, screen.revision); await load() }
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

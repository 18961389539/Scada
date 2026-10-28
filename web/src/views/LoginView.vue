<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-title" data-test="login-site-name">{{ siteInfo?.siteName ?? $t('loginView.kb42eeb') }}</div>
      <div class="login-sub">{{ $t('loginView.k3e639d') }}</div>
      <el-alert v-if="siteError" :title="siteError" type="error" :closable="false" show-icon data-test="login-site-error"><el-button link type="primary" :loading="siteLoading" data-test="login-site-retry" @click="reloadSite">{{ $t('common.retry') }}</el-button></el-alert>
      <el-alert v-if="siteInfo?.demoMode" :title="$t('ux.login.demo')" type="warning" :closable="false" show-icon data-test="login-demo-mode" />
      <el-form ref="formRef" :model="form" :rules="rules" label-position="top" @keyup.enter="submit">
        <el-form-item :label="$t('loginView.ka1aaf3')" prop="userName">
          <el-input v-model="form.userName" size="large" autocomplete="username" data-test="login-user-name" :placeholder="$t('loginView.kad5f05')" :prefix-icon="User" />
        </el-form-item>
        <el-form-item :label="$t('loginView.kc839a8')" prop="password">
          <el-input
            v-model="form.password"
            type="password"
            size="large"
            show-password
            :placeholder="$t('loginView.k713b73')"
            :prefix-icon="Lock"
            autocomplete="current-password"
            data-test="login-password"
          />
        </el-form-item>
        <el-button type="primary" size="large" style="width: 100%" :loading="loading" @click="submit">
          {{ $t('loginView.ke37413') }}
        </el-button>
      </el-form>
      <el-alert v-if="loginError" :title="loginError" type="error" :closable="false" show-icon style="margin-top: 12px" data-test="login-error" />
      <el-select :model-value="locale" size="small" class="login-locale" data-test="login-locale" @change="switchLocale"><el-option v-for="l in locales" :key="l.value" :label="l.label" :value="l.value" /></el-select>
      <div v-if="siteInfo?.demoMode" class="login-tip" data-test="login-demo-credentials">{{ $t('loginView.k6475b9') }}<el-button link type="primary" @click="fillDemoCredentials">{{ $t('ux.login.fillDemo') }}</el-button></div>
      <div v-else class="login-tip">{{ $t('ux.login.productionHint') }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { applyLocale, currentLocale, locales, type AppLocale } from '../i18n'
const locale = currentLocale()
const switchLocale = (value: AppLocale) => applyLocale(value)
import { reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { FormInstance, FormRules } from 'element-plus'
import { Lock, User } from '@element-plus/icons-vue'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
import { defaultLandingPath } from '../access/policies'
import { useSiteInfo } from '../composables/useSiteInfo'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const formRef = ref<FormInstance>()
const loading = ref(false)
const loginError = ref('')
const form = reactive({ userName: '', password: '' })
const { siteInfo, loading: siteLoading, error: siteError, reload: reloadSite } = useSiteInfo()
function fillDemoCredentials() { form.userName = 'admin'; form.password = 'admin123'; formRef.value?.clearValidate() }
watch(siteInfo, info => {
  if (info?.demoMode && !form.userName && !form.password) fillDemoCredentials()
}, { immediate: true })

// Same-origin paths only. An absolute redirect would leave the site after login.
function safeNext(raw: unknown): string {
  const landing = defaultLandingPath(auth.user)
  if (typeof raw !== 'string') return landing
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return landing
  if (raw.includes('\\') || raw.includes('://') || /[\s\0]/.test(raw)) return landing
  return raw
}

const rules: FormRules = {
  userName: [{ required: true, message: tr('loginView.kad5f05'), trigger: 'blur' }],
  password: [{ required: true, message: tr('loginView.k713b73'), trigger: 'blur' }]
}

async function submit(): Promise<void> {
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return

  loading.value = true
  loginError.value = ''
  try {
    const changed = await auth.login(form.userName, form.password)
    const redirect = safeNext(route.query.redirect)
    if (changed) { location.href = redirect; return }
    await router.replace(redirect)
  } catch (error) {
    loginError.value = errorMessage(error, tr('loginView.kaeb6c8'))
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.login-tip {
  margin-top: var(--scada-space-5);
  color: var(--scada-text-muted);
  font-size: var(--scada-fs-caption);
  text-align: center;
  line-height: 1.5;
}
.login-locale {
  width: 132px;
  margin-top: var(--scada-space-4);
}
.login-card :deep(.el-form-item__label) {
  font-weight: 500;
  color: var(--scada-text-secondary);
}
.login-card :deep(.el-button--primary) {
  font-weight: 600;
  letter-spacing: 0.04em;
  margin-top: 4px;
}
</style>

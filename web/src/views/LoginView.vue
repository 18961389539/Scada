<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-title">{{ $t('loginView.kb42eeb') }}</div>
      <div class="login-sub">{{ $t('loginView.k3e639d') }}</div>
      <el-form ref="formRef" :model="form" :rules="rules" label-position="top" @keyup.enter="submit">
        <el-form-item :label="$t('loginView.ka1aaf3')" prop="userName">
          <el-input v-model="form.userName" size="large" :placeholder="$t('loginView.kad5f05')" :prefix-icon="User" />
        </el-form-item>
        <el-form-item :label="$t('loginView.kc839a8')" prop="password">
          <el-input
            v-model="form.password"
            type="password"
            size="large"
            show-password
            :placeholder="$t('loginView.k713b73')"
            :prefix-icon="Lock"
          />
        </el-form-item>
        <el-button type="primary" size="large" style="width: 100%" :loading="loading" @click="submit">
          {{ $t('loginView.ke37413') }}
        </el-button>
      </el-form>
      <el-alert v-if="loginError" :title="loginError" type="error" :closable="false" show-icon style="margin-top: 12px" data-test="login-error" />
      <el-select :model-value="locale" size="small" class="login-locale" data-test="login-locale" @change="switchLocale"><el-option v-for="l in locales" :key="l.value" :label="l.label" :value="l.value" /></el-select>
      <div class="login-tip">{{ $t('loginView.k6475b9') }}</div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { applyLocale, currentLocale, locales, type AppLocale } from '../i18n'
const locale = currentLocale()
const switchLocale = (value: AppLocale) => applyLocale(value)
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import type { FormInstance, FormRules } from 'element-plus'
import { Lock, User } from '@element-plus/icons-vue'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const formRef = ref<FormInstance>()
const loading = ref(false)
const loginError = ref('')
const form = reactive({ userName: 'admin', password: 'admin123' })

// Same-origin paths only. An absolute redirect would leave the site after login.
function safeNext(raw: unknown): string {
  if (typeof raw !== 'string') return '/overview'
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return '/overview'
  if (raw.includes('\\') || raw.includes('://') || /[\s\0]/.test(raw)) return '/overview'
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

<template>
  <div class="pw-page">
    <el-card class="pw-card" shadow="never">
      <header class="pw-head">
        <h1>{{ tr('passwordChange.title') }}</h1>
        <p>{{ tr('passwordChange.hint') }}</p>
      </header>

      <el-form ref="formRef" :model="form" :rules="rules" label-position="top" @submit.prevent="submit">
        <el-form-item :label="tr('passwordChange.current')" prop="currentPassword">
          <el-input
            v-model="form.currentPassword"
            type="password"
            show-password
            autocomplete="current-password"
            data-test="change-current"
            @keydown.enter.prevent="submit"
          />
        </el-form-item>
        <el-form-item :label="tr('passwordChange.next')" prop="newPassword">
          <el-input
            v-model="form.newPassword"
            type="password"
            show-password
            autocomplete="new-password"
            data-test="change-new"
            @keydown.enter.prevent="submit"
          />
        </el-form-item>
        <el-form-item :label="tr('passwordChange.confirm')" prop="confirmPassword">
          <el-input
            v-model="form.confirmPassword"
            type="password"
            show-password
            autocomplete="new-password"
            data-test="change-confirm"
            @keydown.enter.prevent="submit"
          />
        </el-form-item>

        <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="pw-error" />
        <el-button type="primary" native-type="submit" :loading="busy" data-test="change-submit" class="pw-submit">
          {{ tr('passwordChange.submit') }}
        </el-button>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { tr } from '../i18n'
import { defaultLandingPath } from '../access/policies'
import { safeRedirect } from '../nav/query'

const auth = useAuthStore()
const router = useRouter()
const route = useRoute()
const formRef = ref<FormInstance>()
const busy = ref(false)
const error = ref('')
const form = reactive({ currentPassword: '', newPassword: '', confirmPassword: '' })

const rules: FormRules = {
  currentPassword: [{ required: true, message: tr('passwordChange.currentRequired'), trigger: 'blur' }],
  newPassword: [
    { required: true, message: tr('passwordChange.nextRequired'), trigger: 'blur' },
    { min: 6, message: tr('passwordChange.tooShort'), trigger: 'blur' }
  ],
  confirmPassword: [
    { required: true, message: tr('passwordChange.confirmRequired'), trigger: 'blur' },
    {
      validator: (_rule: unknown, value: string, callback: (error?: Error) => void) =>
        value === form.newPassword ? callback() : callback(new Error(tr('passwordChange.mismatch'))),
      trigger: 'blur'
    }
  ]
}

async function submit(): Promise<void> {
  if (busy.value) return
  const valid = await formRef.value?.validate().catch(() => false)
  if (!valid) return
  busy.value = true
  error.value = ''
  try {
    const localeChanged = await auth.changePassword(form.currentPassword, form.newPassword)
    ElMessage.success(tr('passwordChange.done'))
    // 与登录页共用同源校验：localeChanged 时也会走 location.href，不能让 //host 之类跳到外域。
    const redirect = safeRedirect(route.query.redirect, defaultLandingPath(auth.user))
    // 服务端保存的语言偏好与本机不同时，整页跳转才能让模块级文案一起换掉。
    if (localeChanged) { location.href = redirect; return }
    await router.replace(redirect)
  } catch (cause) {
    error.value = errorMessage(cause, tr('passwordChange.failed'))
  } finally {
    busy.value = false
  }
}
</script>

<style scoped>
.pw-page {
  min-height: 100vh;
  display: grid;
  place-items: center;
  padding: var(--scada-space-6);
  background: var(--scada-bg);
}
.pw-card {
  width: min(420px, 100%);
  border-radius: var(--scada-radius-lg);
}
.pw-head h1 {
  margin: 0 0 var(--scada-space-2);
  font-size: var(--scada-fs-title);
  font-weight: 600;
  color: var(--scada-text);
}
.pw-head p {
  margin: 0 0 var(--scada-space-5);
  color: var(--scada-text-muted);
  font-size: var(--scada-fs-caption);
  line-height: 1.6;
}
.pw-error {
  margin-bottom: var(--scada-space-3);
}
.pw-submit {
  width: 100%;
  margin-top: var(--scada-space-2);
}
</style>

<template>
  <el-config-provider :locale="locale">
    <main v-if="showProtocolError" role="alert" data-test="protocol-catalog-error" style="padding: 32px">
      <h1>{{ $t('protocolRecovery.title') }}</h1>
      <router-link v-if="route.path.startsWith('/config/')" to="/overview">{{ $t('index.kc122e1') }}</router-link>
      <p>{{ $t('protocolRecovery.hint') }}</p>
      <el-button type="primary" :loading="protocolCatalogLoading" data-test="protocol-catalog-retry" @click="retryProtocols">{{ $t('protocolRecovery.retry') }}</el-button>
    </main>
    <router-view v-if="!showProtocolError || !route.path.startsWith('/config/')" />
  </el-config-provider>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import en from 'element-plus/es/locale/lang/en'
import { currentLocale } from './i18n'
import { ensureProtocolCatalog, protocolCatalogError, protocolCatalogLoading } from './protocols'
const route = useRoute()
const showProtocolError = computed(() => protocolCatalogError.value && !route.meta.public && route.name !== 'change-password')
async function retryProtocols(): Promise<void> {
  try { await ensureProtocolCatalog() } catch { /* Keep the recovery screen; never expose an incomplete form. */ }
}

const locale = currentLocale() === 'en-US' ? en : zhCn
</script>

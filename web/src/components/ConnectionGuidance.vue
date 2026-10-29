<template>
  <div class="connection-guidance" data-test="connection-guidance" role="note">
    <strong>{{ $t('configurationUx.guidanceTitle') }}</strong>
    <p>{{ $t(`configurationUx.${category}Help`) }}</p>
    <p>{{ $t(protocol === 'OpcUa' ? 'configurationUx.opcHelp' : protocol?.startsWith('Modbus') ? 'configurationUx.modbusHelp' : 'configurationUx.protocolHelp') }}</p>
    <slot />
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ message?: string; protocol?: string; quality?: string }>()
const category = computed(() => {
  const message = props.message ?? ''
  if (/certificate|securitychecks|证书|trust|信任/i.test(message)) return 'certificate' // i18n-ignore: server error vocabulary
  if (/unauthori[sz]ed|accessdenied|forbidden|权限|认证|身份验证/i.test(message)) return 'permission' // i18n-ignore: server error vocabulary
  if (/address|nodeid|unknownnode|地址|节点不存在|illegal data/i.test(message)) return 'address' // i18n-ignore: server error vocabulary
  if (/timeout|timed out|refused|unreachable|超时|拒绝连接|无法连接/i.test(message)) return 'network' // i18n-ignore: server error vocabulary
  if (props.quality && props.quality !== 'Good') return 'quality'
  return 'unknown'
})
</script>

<style scoped>
.connection-guidance { margin-top: 12px; padding: 12px; border: 1px solid var(--el-border-color); border-radius: 6px; overflow-wrap: anywhere; }
.connection-guidance p { margin: 8px 0; }
</style>

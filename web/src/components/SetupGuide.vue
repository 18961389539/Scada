<template>
  <details class="setup-guide" :open="devices.length === 0" data-test="setup-guide">
    <summary>{{ $t('ux.setup.title') }} · {{ $t('configurationUx.setupProgress', [completed, steps.length]) }}</summary>
    <p class="tiny muted">{{ $t('configurationUx.setupScope') }}</p>
    <div class="toolbar">
      <el-select v-model="selectedId" :aria-label="$t('configurationUx.setupDevice')" :placeholder="$t('configurationUx.setupDevice')" data-test="setup-device" style="width: 240px; max-width: 100%">
        <el-option v-for="device in devices" :key="device.id" :label="device.name" :value="device.id" />
      </el-select>
      <el-button :loading="loading" data-test="setup-verify" @click="refresh">{{ $t('configurationUx.verify') }}</el-button>
      <span v-if="checkedAt" class="tiny muted">{{ $t('configurationUx.checkedAt', [formatTime(checkedAt)]) }}</span>
    </div>
    <el-alert v-if="deviceError" :title="deviceError" type="error" :closable="false" show-icon data-test="setup-load-error" />
    <ol class="setup-steps">
      <li v-for="step in steps" :key="step.id" :data-test="`setup-step-${step.id}`">
        <strong>{{ step.title }}</strong>
        <el-tag :type="step.state === 'done' ? 'success' : step.state === 'unknown' ? 'warning' : 'info'" size="small">{{ $t(`configurationUx.${step.state}`) }}</el-tag>
        <p class="tiny">{{ step.hint }}</p>
        <el-button v-if="step.path" link type="primary" @click="router.push(step.path)">{{ $t('configurationUx.openStep') }}</el-button>
        <el-button v-if="step.id === 'connected' && selectedDevice" link type="primary" data-test="setup-diagnostics" @click="diagnosticsVisible = true">{{ $t('devicesView.ke7a75c') }}</el-button>
      </li>
    </ol>
    <el-button v-if="nextStep?.path" type="primary" data-test="setup-continue" @click="router.push(nextStep.path)">{{ $t('configurationUx.continueSetup', [nextStep.title]) }}</el-button>
    <p class="tiny muted">{{ $t('configurationUx.manualChecks') }}</p>
    <router-link to="/config/project">{{ $t('ux.setup.import') }}</router-link>
    <span> · </span>
    <router-link v-if="canGlobal(auth.user, 'System')" to="/system">{{ $t('ux.setup.backup') }}</router-link>
    <span v-else>{{ $t('ux.setup.backupNeedAdmin') }}</span>
    <CollectorDiagnosticsDialog v-if="selectedDevice" v-model="diagnosticsVisible" :device="selectedDevice" />
  </details>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter, type RouteLocationRaw } from 'vue-router'
import { deviceApi, tagApi } from '../api'
import { diagnosticsApi, type CollectorDiagnostics } from '../api/diagnostics'
import { screenApi } from '../api/screens'
import { errorMessage } from '../api/http'
import { can, canGlobal } from '../access/policies'
import { useAuthStore } from '../stores/auth'
import { useNowTicker } from '../composables/useNowTicker'
import { positiveQueryId } from '../nav/query'
import { tr } from '../i18n'
import { formatTime } from '../utils'
import type { Device, Tag } from '../types'
import CollectorDiagnosticsDialog from './CollectorDiagnosticsDialog.vue'

const auth = useAuthStore(), route = useRoute(), router = useRouter()
const devices = ref<Device[]>([]), tags = ref<Tag[]>([])
const snapshot = ref<CollectorDiagnostics | null>(null)
const deviceError = ref(''), tagError = ref(''), diagnosticError = ref(''), screenError = ref('')
const publishedCount = ref<number | null>(null), checkedAt = ref(0), loading = ref(false)
const diagnosticsVisible = ref(false)
const { now } = useNowTicker()
const selectedId = computed({
  get: () => positiveQueryId(route.query.setupDeviceId) ?? devices.value[0]?.id,
  set: value => { void router.replace({ query: { ...route.query, setupDeviceId: value ? String(value) : undefined } }) }
})
const selectedDevice = computed(() => devices.value.find(device => device.id === selectedId.value))
const fresh = computed(() => {
  const value = snapshot.value
  return !!value && value.capturedTs <= now.value + 1000 && now.value - value.capturedTs <= value.staleAfterMs && !value.configurationPending
})
type StepState = 'done' | 'pending' | 'unknown'
interface Step { id: string; title: string; state: StepState; hint: string; path?: RouteLocationRaw }
const steps = computed<Step[]>(() => {
  const device = selectedDevice.value, diagnostic = snapshot.value
  const tagsPath = { path: '/config/tags', query: { deviceId: device ? String(device.id) : undefined } }
  const diagnosticsUnknown = !!diagnosticError.value || (!!diagnostic && !fresh.value)
  const ready = !!device?.enabled && !!diagnostic?.enabled && diagnostic.deviceId === device.id && fresh.value
  const connected = ready && !!diagnostic && ['Online', 'Degraded'].includes(diagnostic.state)
  const good = connected && tags.value.length > 0 && diagnostic?.pointCount === tags.value.length &&
    diagnostic.quality.good === tags.value.length && diagnostic.quality.stale === 0 && diagnostic.quality.unknown === 0 &&
    diagnostic.quality.bad === 0 && diagnostic.quality.uncertain === 0 && diagnostic.quality.commFailure === 0
  return [
    { id: 'device', title: tr('configurationUx.deviceSaved'), state: deviceError.value ? 'unknown' : device ? 'done' : 'pending',
      hint: deviceError.value || tr('configurationUx.deviceSavedHint'), path: '/config/devices' },
    { id: 'tags', title: tr('configurationUx.tagsSaved'), state: deviceError.value || tagError.value ? 'unknown' : tags.value.length ? 'done' : 'pending',
      hint: tagError.value || tr('configurationUx.tagsSavedHint', [tags.value.length]), path: device ? tagsPath : '/config/devices' },
    { id: 'connected', title: tr('configurationUx.connected'), state: deviceError.value || diagnosticsUnknown ? 'unknown' : connected ? 'done' : 'pending',
      hint: diagnosticError.value || (!fresh.value && diagnostic ? tr('configurationUx.expired') : diagnostic?.currentError || tr('configurationUx.connectedHint')),
      path: device ? (!tags.value.length ? tagsPath : { path: '/config/devices', query: { diagnoseId: String(device.id) } }) : '/config/devices' },
    { id: 'values', title: tr('configurationUx.values'), state: deviceError.value || tagError.value || diagnosticsUnknown ? 'unknown' : good ? 'done' : 'pending',
      hint: diagnosticError.value || tagError.value || tr('configurationUx.valuesHint', [diagnostic?.quality.good ?? 0, tags.value.length]),
      path: can(auth.user, 'View') ? { path: '/realtime', query: { deviceId: device ? String(device.id) : undefined } } : tagsPath },
    { id: 'screen', title: tr('configurationUx.screenPublished'), state: screenError.value || publishedCount.value === null ? 'unknown' : publishedCount.value > 0 ? 'done' : 'pending',
      hint: !can(auth.user, 'View') ? tr('configurationUx.noViewPermission') : screenError.value || tr('configurationUx.screenHint', [publishedCount.value ?? 0]),
      path: can(auth.user, 'View') ? '/screens' : undefined }
  ]
})
const completed = computed(() => steps.value.filter(step => step.state === 'done').length)
const nextStep = computed(() => steps.value.find(step => step.state !== 'done' && step.path))
let generation = 0
async function refresh(): Promise<void> {
  const version = ++generation
  loading.value = true
  snapshot.value = null
  tags.value = []
  publishedCount.value = null
  deviceError.value = ''; tagError.value = ''; diagnosticError.value = ''; screenError.value = ''
  const screenRequest = can(auth.user, 'View')
    ? screenApi.list().then(list => { if (version === generation) publishedCount.value = list.filter(screen => (screen.publishedRevision ?? 0) > 0).length })
      .catch(error => { if (version === generation) screenError.value = errorMessage(error, tr('configurationUx.screensFailed')) })
    : Promise.resolve()
  try {
    const list = await deviceApi.list()
    if (version !== generation) return
    devices.value = list.filter(device => can(auth.user, 'Configure', device.areaId))
    const device = selectedDevice.value
    if (device) {
      await Promise.all([
        tagApi.list({ deviceId: device.id }).then(list => {
          if (version === generation) tags.value = list.filter(tag => tag.kind === 'Physical' && tag.deviceId === device.id)
        }).catch(error => { if (version === generation) tagError.value = errorMessage(error, tr('configurationUx.tagsFailed')) }),
        diagnosticsApi.get(device.id, { page: 1, pageSize: 1 }).then(result => {
          if (version === generation) snapshot.value = result.diagnostics
        }).catch(error => { if (version === generation) diagnosticError.value = errorMessage(error, tr('configurationUx.diagnosticsFailed')) })
      ])
    }
  } catch (error) {
    if (version === generation) { devices.value = []; deviceError.value = errorMessage(error, tr('ux.setup.loadFailed')) }
  } finally {
    await screenRequest
    if (version === generation) { loading.value = false; checkedAt.value = Date.now() }
  }
}
watch(() => route.query.setupDeviceId, () => { diagnosticsVisible.value = false; void refresh() }, { immediate: true })
onBeforeUnmount(() => { generation++ })
</script>

<style scoped>
.setup-guide { padding: 16px; border: 1px solid var(--el-border-color); border-radius: 8px; margin-bottom: 16px; }
summary { cursor: pointer; font-weight: 600; }
.setup-steps { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 240px), 1fr)); gap: 16px; padding-left: 24px; }
.setup-steps .el-tag { margin-left: 8px; }
</style>

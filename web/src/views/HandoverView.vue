<template>
  <div class="page handover-page">
    <div class="panel handover-toolbar no-print">
      <div>
        <h1 class="handover-title">{{ $t('dutyHandover.title') }}</h1>
        <p class="hint">{{ $t('dutyHandover.hint') }}</p>
      </div>
      <span class="spacer" />
      <el-button data-test="handover-refresh" :loading="loading" @click="load">{{ $t('dutyHandover.refresh') }}</el-button>
      <el-button type="primary" data-test="handover-print" @click="print">{{ $t('dutyHandover.print') }}</el-button>
    </div>
    <el-alert v-if="error" class="no-print" :title="error" type="error" :closable="false" show-icon data-test="handover-error" />
    <div class="panel handover-sheet" data-test="handover-sheet">
      <p class="meta">{{ $t('dutyHandover.generatedAt', [generatedAt]) }} · {{ $t('dutyHandover.operator', [operator]) }}</p>

      <section>
        <h2>{{ $t('dutyHandover.unacked') }}</h2>
        <p><router-link data-test="handover-unacked-link" to="/alarms?state=unack">{{ summaryLine }}</router-link></p>
        <ul class="level-list" data-test="handover-unacked">
          <li v-for="row in unackedByLevel" :key="row.level">
            <router-link :to="{ path: '/alarms', query: { state: 'unack' } }">{{ row.label }}：{{ row.count }}</router-link>
          </li>
        </ul>
        <p v-if="simulatedUnacked > 0" data-test="handover-simulated-unacked">{{ $t('dutyHandover.simulatedUnacked') }}：{{ simulatedUnacked }}</p>
        <p><router-link data-test="handover-rtn-link" to="/alarms?state=rtnUnack">{{ $t('dutyHandover.rtnUnack') }}：{{ rtnUnack }}</router-link></p>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.shelved') }} / {{ $t('dutyHandover.suppressed') }} / {{ $t('dutyHandover.outOfService') }}</h2>
        <p>
          <router-link data-test="handover-shelved-link" to="/alarms?state=shelved">{{ $t('dutyHandover.shelved') }}：{{ shelved }}</router-link>
          ·
          <router-link data-test="handover-suppressed-link" to="/alarms?state=suppressed">{{ $t('dutyHandover.suppressed') }}：{{ suppressed }}</router-link>
          ·
          <router-link data-test="handover-oos-link" to="/config/rules?state=outOfService">{{ $t('dutyHandover.outOfService') }}：{{ outOfService }}</router-link>
        </p>
        <p v-if="outOfService === 0" data-test="handover-oos-empty" class="tiny muted">{{ $t('dutyHandover.noOutOfService') }}</p>
        <ul v-else data-test="handover-oos-reasons">
          <li v-for="rule in outOfServiceRules" :key="rule.id">
            <router-link :to="{ path: '/config/rules', query: { state: 'outOfService' } }">{{ rule.message || ('#' + rule.id) }}<template v-if="rule.outOfServiceReason"> — {{ rule.outOfServiceReason }}</template></router-link>
          </li>
        </ul>
        <p v-if="outOfService > outOfServiceRules.length" class="tiny muted">{{ $t('dutyHandover.outOfServiceMore', [outOfService - outOfServiceRules.length]) }}</p>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.pendingReview') }}</h2>
        <p v-if="!pending.length" data-test="handover-pending-empty">{{ $t('dutyHandover.noPending') }}</p>
        <ul v-else data-test="handover-pending">
          <li v-for="row in pending" :key="row.request.id">
            <router-link data-test="handover-pending-link" to="/control">
              {{ kindLabel(row.request.kind) }} · {{ row.request.requestedBy }} · {{ row.sequenceName || row.recipe?.recipeName || row.request.id }}
            </router-link>
          </li>
        </ul>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.runningSequences') }}</h2>
        <p v-if="!runningSequences.length">{{ $t('dutyHandover.noneRunning') }}</p>
        <ul v-else data-test="handover-sequences">
          <li v-for="run in runningSequences" :key="run.id">
            <router-link data-test="handover-sequence-link" :to="{ path: '/control', query: { tab: 'sequences' } }">
              {{ run.sequenceName }} · {{ run.startedBy }} · {{ runStatusLabel(run.status) }}
            </router-link>
          </li>
        </ul>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.runningRecipes') }}</h2>
        <p v-if="!runningRecipes.length">{{ $t('dutyHandover.noneRunning') }}</p>
        <ul v-else data-test="handover-recipes">
          <li v-for="download in runningRecipes" :key="download.id">
            <router-link data-test="handover-recipe-link" to="/recipes#downloads">
              {{ download.recipeName || download.id }} · {{ download.statusLabel || runStatusLabel(download.status) }}
            </router-link>
          </li>
        </ul>
      </section>
      <section>
        <h2>{{ $t('dutyHandover.attention') }}</h2>
        <RuntimeBanners :health="health" />
        <p v-if="!attentionItems.length" data-test="handover-attention-empty">{{ $t('dutyHandover.noAttention') }}</p>
        <ul v-else data-test="handover-attention">
          <li v-for="item in attentionItems" :key="item.key">
            <router-link :data-test="item.test" :to="item.to">{{ item.label }}</router-link>
          </li>
        </ul>
      </section>
      <section>
        <h2>{{ $t('dutyHandover.workOrders') }}</h2>
        <p v-if="!openWorkOrders.length" data-test="handover-work-orders-empty">{{ $t('dutyHandover.noOpenWorkOrders') }}</p>
        <ul v-else data-test="handover-work-orders">
          <li v-for="order in openWorkOrders" :key="order.id">
            <router-link data-test="handover-work-order-link" :to="{ path: '/assets', query: { tab: 'workOrders' } }">
              {{ order.number }} · {{ order.title }}
              <span v-if="isOverdueOrder(order)"> · {{ $t('dutyHandover.overdue') }} · {{ $t('dutyHandover.plannedEnd') }} {{ order.plannedEndTs ? formatTime(order.plannedEndTs) : '-' }}</span>
            </router-link>
          </li>
        </ul>
      </section>
      <section>
        <h2>{{ $t('dutyHandover.externalWrites') }}</h2>
        <p v-if="!externalWrites.length" data-test="handover-external-writes-empty">{{ $t('dutyHandover.noExternalWrites') }}</p>
        <ul v-else data-test="handover-external-writes">
          <li v-for="item in externalWrites" :key="item.id">
            {{ formatTime(item.ts) }} · {{ item.tagName }} · {{ item.source || item.origin }} · {{ item.result }}
            <span v-if="item.userName"> · {{ $t('dutyHandover.byUser', [item.userName]) }}</span>
          </li>
        </ul>
        <p><router-link data-test="handover-audit-link" to="/system?tab=logs">{{ $t('dutyHandover.viewAudit') }}</router-link></p>
      </section>
      <section>
        <h2>{{ $t('dutyHandover.sound') }}</h2>
        <p data-test="handover-sound">{{ soundEnabled ? $t('dutyHandover.unmuted') : $t('dutyHandover.muted') }}</p>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.dataFreshness') }}</h2>
        <p data-test="handover-freshness">{{ freshnessText }}</p>
        <p v-if="isStandby">{{ $t('dutyHandover.standby', [primaryUrl || '--']) }}</p>
      </section>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import RuntimeBanners from '../components/RuntimeBanners.vue'
import { alarmApi, ruleApi, workOrderApi } from '../api'
import { controlApi, type ControlLockout, type ControlRun, type DeviceControlState, type ExternalWrite } from '../api/control'
import { recipeApi, type RecipeDownload } from '../api/recipes'
import { pendingControlRequests, type ControlRequestRow } from '../composables/pendingApprovals'
import { useAlarmSound } from '../composables/useAlarmSound'
import { useServiceStatus } from '../composables/useServiceStatus'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { formatTime, levelLabel } from '../utils'
import { tr } from '../i18n'
import type { AlarmEvent, AlarmLevel, AlarmRule, MaintenanceWorkOrder } from '../types'

const auth = useAuthStore()
const { soundEnabled } = useAlarmSound()
const { health, healthError, isStandby, primaryUrl, refreshHealth } = useServiceStatus()
const loading = ref(false)
const error = ref('')
const generatedAt = ref('--')
const operator = computed(() => auth.user?.displayName || auth.user?.userName || '--')
const unackedByLevel = ref<{ level: string; label: string; count: number }[]>([])
const simulatedUnacked = ref(0)
const rtnUnack = ref(0)
const shelved = ref(0)
const suppressed = ref(0)
const outOfService = ref(0)
const outOfServiceRules = ref<AlarmRule[]>([])
const pending = ref<ControlRequestRow[]>([])
const runningSequences = ref<ControlRun[]>([])
const openWorkOrders = ref<MaintenanceWorkOrder[]>([])
const externalWrites = ref<ExternalWrite[]>([])
const runningRecipes = ref<RecipeDownload[]>([])
const lockouts = ref<ControlLockout[]>([])
const localDevices = ref<DeviceControlState[]>([])
const interruptedSequences = ref<ControlRun[]>([])
const partialRecipes = ref<RecipeDownload[]>([])

function kindLabel(kind: string): string {
  if (kind === 'command') return tr('dutyHandover.kindCommand')
  if (kind === 'recipe') return tr('dutyHandover.kindRecipe')
  if (kind === 'sequence') return tr('dutyHandover.kindSequence')
  return kind
}

function runStatusLabel(status: string): string {
  const labels: Record<string, string> = {
    Running: tr('controlView.k594249'),
    Completed: tr('controlView.k33246f'),
    Failed: tr('controlView.k3e3c80'),
    Aborted: tr('controlView.k5c1747'),
    Pending: tr('controlView.k4607ba'),
    PartiallyApplied: tr('dutyHandover.partiallyApplied'),
    Unknown: tr('dutyHandover.unknownResult')
  }
  return labels[status] ?? status
}

function isOverdueOrder(order: MaintenanceWorkOrder): boolean {
  if (!order.plannedEndTs) return false
  if (order.status === 'Completed' || order.status === 'Cancelled') return false
  return order.plannedEndTs < Date.now()
}
function isInterruptedSequence(run: ControlRun): boolean {
  if (run.status !== 'Failed' && run.status !== 'Aborted') return false
  const message = (run as ControlRun & { message?: string }).message || ''
  return message.includes('结果未知') || message.includes('顺控中断') // i18n-ignore: server run message
}

function isAttentionRecipe(download: RecipeDownload): boolean {
  if (download.status === 'PartiallyApplied') return true
  if (download.status !== 'Failed' && download.status !== 'Aborted') return false
  const message = `${download.statusLabel || ''} ${download.message || ''}`
  return message.includes('结果未知') || message.includes('配方下发中断') // i18n-ignore: server download message
}

const attentionItems = computed(() => {
  const items: { key: string; to: { path: string; query?: Record<string, string>; hash?: string } | string; label: string; test: string }[] = []
  for (const lock of lockouts.value) {
    const target = lock.deviceId != null ? `${tr('dutyHandover.device')} #${lock.deviceId}` : `${tr('dutyHandover.tag')} #${lock.tagId}`
    const kind = lock.kind === 'NoOperate' ? tr('dutyHandover.lockoutNoOperate') : tr('dutyHandover.lockoutMaintenance')
    items.push({ key: `lock-${lock.id}`, to: { path: '/control', query: { tab: 'lockouts' } }, label: `${tr('dutyHandover.activeLockout')}：${kind} · ${target} · ${lock.placedBy}`, test: 'handover-lockout-link' })
  }
  for (const state of localDevices.value) {
    items.push({ key: `local-${state.deviceId}`, to: { path: '/control', query: { tab: 'modes' } }, label: `${tr('dutyHandover.localDevice')}：#${state.deviceId}${state.reason ? ' · ' + state.reason : ''}`, test: 'handover-local-link' })
  }
  for (const run of interruptedSequences.value) {
    items.push({ key: `seq-${run.id}`, to: { path: '/control', query: { tab: 'sequences' } }, label: `${tr('dutyHandover.interruptedSequence')}：${run.sequenceName} · ${runStatusLabel(run.status)}`, test: 'handover-interrupted-sequence-link' })
  }
  for (const download of partialRecipes.value) {
    items.push({ key: `recipe-${download.id}`, to: '/recipes#downloads', label: `${tr('dutyHandover.partialRecipe')}：${download.recipeName || download.id} · ${download.statusLabel || runStatusLabel(download.status)}`, test: 'handover-partial-recipe-link' })
  }
  return items
})

const summaryLine = computed(() => {
  const total = unackedByLevel.value.reduce((sum, row) => sum + row.count, 0)
  return `${tr('dutyHandover.unacked')}：${total}`
})

const freshnessText = computed(() => {
  if (healthError.value) return healthError.value
  if (!health.value) return tr('dutyHandover.unknown')
  return health.value.status === 'ok' ? tr('dutyHandover.fresh') : tr('dutyHandover.stale')
})

function print(): void {
  window.print()
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const [events, summary, shelves, oosPage, requests, runs, downloads, lockoutPage, modes, , orderRows, writeRows] = await Promise.all([
      alarmApi.query({ live: true, unackedOnly: true, includeShelved: true, priorityFirst: true, limit: 2000 }),
      alarmApi.summary().catch(() => null),
      ruleApi.shelves().catch(() => []),
      ruleApi.page({ state: 'outOfService', take: 20 }).catch(() => ({ items: [] as AlarmRule[], total: 0 })),
      controlApi.requests().catch(() => []),
      controlApi.runs().catch(() => ({ items: [] as ControlRun[] })),
      recipeApi.downloads().catch(() => ({ items: [] as RecipeDownload[] })),
      controlApi.lockouts().catch(() => ({ items: [] as ControlLockout[] })),
      controlApi.modes().catch(() => [] as DeviceControlState[]),
      refreshHealth(),
      workOrderApi.list({ take: 100 }).catch(() => [] as MaintenanceWorkOrder[]),
      controlApi.externalWrites(20).catch(() => [] as ExternalWrite[])
    ])
    const counts: Record<string, number> = { Critical: 0, Warning: 0, Info: 0 }
    let simulated = 0
    for (const alarm of events as AlarmEvent[]) {
      if (alarm.endTs) continue
      if (alarm.simulated) {
        simulated += 1
        continue
      }
      const level = (alarm.level || 'Info') as AlarmLevel
      counts[level] = (counts[level] ?? 0) + 1
    }
    simulatedUnacked.value = summary?.simulatedUnack ?? simulated
    unackedByLevel.value = (['Critical', 'Warning', 'Info'] as AlarmLevel[]).map(level => ({
      level,
      label: levelLabel(level),
      count: counts[level] ?? 0
    }))
    rtnUnack.value = summary?.rtnUnack ?? (events as AlarmEvent[]).filter(alarm => alarm.endTs && !alarm.ackTs && !alarm.simulated).length
    shelved.value = summary?.shelved ?? shelves.length
    suppressed.value = summary?.suppressed ?? 0
    outOfService.value = summary?.outOfServiceRules ?? oosPage.total
    outOfServiceRules.value = oosPage.items
    pending.value = pendingControlRequests(requests)
    const runItems = Array.isArray(runs) ? runs : (runs as { items?: ControlRun[] }).items ?? []
    const downloadItems = Array.isArray(downloads) ? downloads : (downloads as { items?: RecipeDownload[] }).items ?? []
    runningSequences.value = runItems.filter(run => run.status === 'Running')
    const orderList = Array.isArray(orderRows) ? orderRows : []
    openWorkOrders.value = orderList.filter(order => order.status !== 'Completed' && order.status !== 'Cancelled')
      .sort((a, b) => Number(isOverdueOrder(b)) - Number(isOverdueOrder(a)) || (a.plannedEndTs ?? 0) - (b.plannedEndTs ?? 0))
    externalWrites.value = Array.isArray(writeRows) ? writeRows : []

    interruptedSequences.value = runItems.filter(isInterruptedSequence)
    runningRecipes.value = downloadItems.filter(download => download.status === 'Pending' || download.status === 'Running')
    partialRecipes.value = downloadItems.filter(isAttentionRecipe)
    const lockoutItems = Array.isArray(lockoutPage) ? lockoutPage : (lockoutPage as { items?: ControlLockout[] }).items ?? []
    lockouts.value = lockoutItems.filter(row => !row.removedTs)
    localDevices.value = (modes as DeviceControlState[]).filter(row => row.mode === 'Local')
    generatedAt.value = formatTime(Date.now())
  } catch (err) {
    error.value = errorMessage(err, tr('dutyHandover.loadFailed'))
  } finally {
    loading.value = false
  }
}

onMounted(() => { void load() })
</script>

<style scoped>
.handover-toolbar { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 8px 12px; margin-bottom: 12px; }
.handover-title { margin: 0 0 4px; font-size: 20px; }
.hint { margin: 0; color: var(--el-text-color-secondary); max-width: 720px; }
.spacer { flex: 1; }
.handover-sheet section { margin: 0 0 20px; }
.handover-sheet h2 { margin: 0 0 8px; font-size: 16px; }
.meta, .handover-sheet p, .handover-sheet li { line-height: 1.6; }
.level-list, .handover-sheet ul { margin: 0; padding-left: 1.2em; }
@media print {
  :global(.no-print), :global(.el-menu), :global(header), :global(.app-header), :global(.nav) { display: none !important; }
  .handover-page { background: white; color: black; }
  .handover-sheet { box-shadow: none; border: none; }
  .handover-sheet a { color: inherit; text-decoration: none; }
}
</style>

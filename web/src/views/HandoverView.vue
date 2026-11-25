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
        <p>{{ summaryLine }}</p>
        <ul class="level-list" data-test="handover-unacked">
          <li v-for="row in unackedByLevel" :key="row.level">{{ row.label }}：{{ row.count }}</li>
        </ul>
        <p>{{ $t('dutyHandover.rtnUnack') }}：{{ rtnUnack }}</p>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.shelved') }} / {{ $t('dutyHandover.suppressed') }}</h2>
        <p>{{ $t('dutyHandover.shelved') }}：{{ shelved }} · {{ $t('dutyHandover.suppressed') }}：{{ suppressed }}</p>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.pendingReview') }}</h2>
        <p v-if="!pending.length" data-test="handover-pending-empty">{{ $t('dutyHandover.noPending') }}</p>
        <ul v-else data-test="handover-pending">
          <li v-for="row in pending" :key="row.request.id">
            {{ row.request.kind }} · {{ row.request.requestedBy }} · {{ row.sequenceName || row.recipe?.recipeName || row.request.id }}
          </li>
        </ul>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.runningSequences') }}</h2>
        <p v-if="!runningSequences.length">{{ $t('dutyHandover.noneRunning') }}</p>
        <ul v-else data-test="handover-sequences">
          <li v-for="run in runningSequences" :key="run.id">{{ run.sequenceName }} · {{ run.startedBy }} · {{ run.status }}</li>
        </ul>
      </section>

      <section>
        <h2>{{ $t('dutyHandover.runningRecipes') }}</h2>
        <p v-if="!runningRecipes.length">{{ $t('dutyHandover.noneRunning') }}</p>
        <ul v-else data-test="handover-recipes">
          <li v-for="download in runningRecipes" :key="download.id">{{ download.recipeName || download.id }} · {{ download.status }}</li>
        </ul>
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
import { alarmApi, ruleApi } from '../api'
import { controlApi, type ControlRun } from '../api/control'
import { recipeApi, type RecipeDownload } from '../api/recipes'
import { pendingControlRequests, type ControlRequestRow } from '../composables/pendingApprovals'
import { useAlarmSound } from '../composables/useAlarmSound'
import { useServiceStatus } from '../composables/useServiceStatus'
import { useAuthStore } from '../stores/auth'
import { errorMessage } from '../api/http'
import { formatTime, levelLabel } from '../utils'
import { tr } from '../i18n'
import type { AlarmEvent, AlarmLevel } from '../types'

const auth = useAuthStore()
const { soundEnabled } = useAlarmSound()
const { health, healthError, isStandby, primaryUrl, refreshHealth } = useServiceStatus()
const loading = ref(false)
const error = ref('')
const generatedAt = ref('--')
const operator = computed(() => auth.user?.displayName || auth.user?.userName || '--')
const unackedByLevel = ref<{ level: string; label: string; count: number }[]>([])
const rtnUnack = ref(0)
const shelved = ref(0)
const suppressed = ref(0)
const pending = ref<ControlRequestRow[]>([])
const runningSequences = ref<ControlRun[]>([])
const runningRecipes = ref<RecipeDownload[]>([])

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
    const [events, summary, shelves, requests, runs, downloads] = await Promise.all([
      alarmApi.query({ live: true, unackedOnly: true, includeShelved: true, priorityFirst: true, limit: 2000 }),
      alarmApi.summary().catch(() => null),
      ruleApi.shelves().catch(() => []),
      controlApi.requests().catch(() => []),
      controlApi.runs().catch(() => ({ items: [] as ControlRun[] })),
      recipeApi.downloads().catch(() => ({ items: [] as RecipeDownload[] })),
      refreshHealth()
    ])
    const counts: Record<string, number> = { Critical: 0, Warning: 0, Info: 0 }
    for (const alarm of events as AlarmEvent[]) {
      if (alarm.endTs) continue
      const level = (alarm.level || 'Info') as AlarmLevel
      counts[level] = (counts[level] ?? 0) + 1
    }
    unackedByLevel.value = (['Critical', 'Warning', 'Info'] as AlarmLevel[]).map(level => ({
      level,
      label: levelLabel(level),
      count: counts[level] ?? 0
    }))
    rtnUnack.value = summary?.rtnUnack ?? (events as AlarmEvent[]).filter(alarm => alarm.endTs && !alarm.ackTs).length
    shelved.value = summary?.shelved ?? shelves.length
    suppressed.value = summary?.suppressed ?? 0
    pending.value = pendingControlRequests(requests)
    const runItems = Array.isArray(runs) ? runs : (runs as { items?: ControlRun[] }).items ?? []
    const downloadItems = Array.isArray(downloads) ? downloads : (downloads as { items?: RecipeDownload[] }).items ?? []
    runningSequences.value = runItems.filter(run => run.status === 'Running')
    runningRecipes.value = downloadItems.filter(download => download.status === 'Pending' || download.status === 'Running')
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
}
</style>

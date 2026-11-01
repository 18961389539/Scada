import { computed, reactive, ref, type ComputedRef } from 'vue'
import { historyApi } from '../api'
import { controlApi, type ControlSequence } from '../api/control'
import { oeeApi, type OeeKpi } from '../api/oee'
import { recipeApi, type Recipe, type RecipeTemplate } from '../api/recipes'
import { errorMessage } from '../api/http'
import { ACTIVE_ALARMS_LIMIT, refreshActiveAlarms } from '../composables/useActiveAlarms'
import { useNowTicker } from '../composables/useNowTicker'
import { tr } from '../i18n'
import type { AlarmEvent, HistoryPoint } from '../types'
import type { ScreenNode } from './model'

const historyTypes = ['chart-line', 'chart-area', 'history-table']

// 历史、报警、OEE、配方与顺控：按画面引用按需拉取，失败只影响对应图元/控件。
export function useScreenAuxData(ctx: { isDisposed: () => boolean; generation: () => number; runtimeNodes: ComputedRef<ScreenNode[]> }) {
  const history = reactive<Record<number, HistoryPoint[]>>({})
  const alarmEvents = ref<AlarmEvent[]>([])
  const alarmLimited = ref(false)
  const oeeByLine = reactive<Record<number, OeeKpi>>({})
  const recipeTemplates = ref<RecipeTemplate[]>([]), recipes = ref<Recipe[]>([]), sequences = ref<ControlSequence[]>([])
  const alarmError = ref(''), historyError = ref(''), oeeError = ref('')
  const recipeError = ref(''), sequenceError = ref('')
  const { now: sourceNow } = useNowTicker()
  const alarmVerifiedAt = ref(0), historyVerifiedAt = ref(0), oeeVerifiedAt = ref(0)
  const alarmUnavailable = computed(() => !!alarmError.value || !alarmVerifiedAt.value || sourceNow.value - alarmVerifiedAt.value > 30000)
  const historyUnavailable = computed(() => !!historyError.value || !historyVerifiedAt.value || sourceNow.value - historyVerifiedAt.value > 30000)
  const oeeUnavailable = computed(() => !!oeeError.value || !oeeVerifiedAt.value || sourceNow.value - oeeVerifiedAt.value > 90000)

  const lineTagIds = computed(() => [...new Set(ctx.runtimeNodes.value.filter(n => historyTypes.includes(n.type)).flatMap(n => n.tags ?? []))])
  const lineWindowSec = computed(() => Math.max(60, ...ctx.runtimeNodes.value.filter(n => historyTypes.includes(n.type)).map(n => n.windowSec ?? 300)))
  const oeeLineIds = computed(() => [...new Set(ctx.runtimeNodes.value.filter(n => n.type === 'oee-kpi' && n.lineId != null).map(n => n.lineId!))])
  const oeeWindowSec = computed(() => Math.max(3600, ...ctx.runtimeNodes.value.filter(n => n.type === 'oee-kpi').map(n => n.windowSec ?? 86400)))
  const recipeTemplateIds = computed(() => [...new Set(ctx.runtimeNodes.value.map(node => node.type === 'recipe' ? node.recipeTemplateId : null).filter((id): id is number => id != null))])
  const recipeActionIds = computed(() => [...new Set(ctx.runtimeNodes.value.map(node => node.action.type === 'recipe' ? node.action.recipeId : null).filter((id): id is number => id != null))])
  const sequenceActionIds = computed(() => [...new Set(ctx.runtimeNodes.value.map(node => node.action.type === 'sequence' ? node.action.sequenceId : null).filter((id): id is number => id != null))])
  const usesSequences = computed(() => sequenceActionIds.value.length > 0)

  function reset(): void {
    alarmError.value = ''; historyError.value = ''; oeeError.value = ''
    alarmLimited.value = false
    alarmVerifiedAt.value = 0; historyVerifiedAt.value = 0; oeeVerifiedAt.value = 0
    alarmEvents.value = []
    for (const key of Object.keys(history)) delete history[Number(key)]
    for (const key of Object.keys(oeeByLine)) delete oeeByLine[Number(key)]
  }

  async function loadHistory(): Promise<void> {
    const ids = lineTagIds.value
    if (ctx.isDisposed() || !ids.length) return
    const currentGeneration = ctx.generation()
    const to = Date.now(), from = to - lineWindowSec.value * 1000
    try {
      const result = await historyApi.query(ids, from, to)
      if (ctx.isDisposed() || currentGeneration !== ctx.generation()) return
      for (const series of result.series) history[series.tagId] = series.points
      historyVerifiedAt.value = Date.now(); historyError.value = ''
    } catch (e) { if (!ctx.isDisposed() && currentGeneration === ctx.generation()) historyError.value = errorMessage(e, tr('screenMonitor.historyFailed')) }
  }

  // 报警图元（alarm-banner / alarm-stats）：拉取活动报警并按等级汇总，定时刷新。
  async function loadAlarms(force = false): Promise<void> {
    if (ctx.isDisposed() || !usesAlarmNodes()) return
    const currentGeneration = ctx.generation()
    try {
      const list = await refreshActiveAlarms(force ? 0 : 8000)
      if (ctx.isDisposed() || currentGeneration !== ctx.generation()) return
      alarmEvents.value = list
      alarmLimited.value = list.length >= ACTIVE_ALARMS_LIMIT
      alarmVerifiedAt.value = Date.now(); alarmError.value = ''
    } catch (e) { if (!ctx.isDisposed() && currentGeneration === ctx.generation()) alarmError.value = errorMessage(e, tr('screenMonitor.alarmsFailed')) }
  }
  function usesAlarmNodes(): boolean { return ctx.runtimeNodes.value.some(node => ['alarm-banner', 'alarm-list', 'alarm-stats'].includes(node.type)) }

  async function loadOee(): Promise<void> {
    const ids = oeeLineIds.value
    if (ctx.isDisposed() || !ids.length) return
    const currentGeneration = ctx.generation(), failures: string[] = []
    const to = Date.now(), from = to - oeeWindowSec.value * 1000
    await Promise.all(ids.map(async lineId => {
      try { const report = await oeeApi.analytics({ from, to, lineId }); if (!ctx.isDisposed() && currentGeneration === ctx.generation()) oeeByLine[lineId] = report.kpi }
      catch (e) { failures.push(errorMessage(e, tr('screenMonitor.oeeFailed'))) }
    }))
    if (!ctx.isDisposed() && currentGeneration === ctx.generation()) {
      oeeError.value = failures.join('; ')
      if (!failures.length) oeeVerifiedAt.value = Date.now()
    }
  }

  async function loadRecipes(): Promise<void> {
    const templateIds = recipeTemplateIds.value, actionIds = recipeActionIds.value
    if (ctx.isDisposed()) return
    if (!templateIds.length && !actionIds.length) { recipeTemplates.value = []; recipes.value = []; recipeError.value = ''; return }
    const currentGeneration = ctx.generation()
    try {
      const [allTemplates, allRecipes] = await Promise.all([recipeApi.templates(), recipeApi.list()])
      if (ctx.isDisposed() || currentGeneration !== ctx.generation()) return
      recipeTemplates.value = allTemplates.filter(template => templateIds.includes(template.id))
      recipes.value = allRecipes.filter(recipe => templateIds.includes(recipe.templateId) || actionIds.includes(recipe.id))
      recipeError.value = ''
    } catch (e) { if (!ctx.isDisposed() && currentGeneration === ctx.generation()) recipeError.value = errorMessage(e, tr('hmi.recipeLoadFailed')) }
  }

  async function loadSequences(): Promise<void> {
    if (ctx.isDisposed()) return
    if (!usesSequences.value) { sequences.value = []; sequenceError.value = ''; return }
    const currentGeneration = ctx.generation()
    try {
      const list = await controlApi.sequences()
      if (ctx.isDisposed() || currentGeneration !== ctx.generation()) return
      sequences.value = list; sequenceError.value = ''
    } catch (e) { if (!ctx.isDisposed() && currentGeneration === ctx.generation()) sequenceError.value = errorMessage(e, tr('hmi.sequenceLoadFailed')) }
  }

  async function loadAll(retry: boolean): Promise<void> {
    await Promise.all([loadHistory(), loadAlarms(retry), loadOee(), loadRecipes(), loadSequences()])
  }

  let historyTimer: ReturnType<typeof setInterval> | undefined
  let alarmTimer: ReturnType<typeof setInterval> | undefined
  let oeeTimer: ReturnType<typeof setInterval> | undefined
  function startTimers(): void {
    historyTimer = setInterval(() => void loadHistory(), 10000)
    alarmTimer = setInterval(() => void loadAlarms(), 10000)
    oeeTimer = setInterval(() => void loadOee(), 30000)
  }
  function stopTimers(): void { clearInterval(historyTimer); clearInterval(alarmTimer); clearInterval(oeeTimer) }

  return { history, alarmEvents, alarmLimited, oeeByLine, recipeTemplates, recipes, sequences,
    alarmError, historyError, oeeError, recipeError, sequenceError,
    alarmUnavailable, historyUnavailable, oeeUnavailable,
    lineTagIds, recipeTemplateIds, sequenceActionIds, usesSequences,
    reset, loadHistory, loadAlarms, loadOee, loadRecipes, loadSequences, loadAll, startTimers, stopTimers }
}

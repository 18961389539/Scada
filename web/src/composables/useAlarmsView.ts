import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type InputInstance, type TableInstance } from 'element-plus'
import { annunciatorApi } from '../api/annunciators'
import { alarmApi, ruleApi } from '../api'
import { collectScreenTags } from '../screens/screenTags'
import { useAlarmScreenLocator } from './useAlarmScreenLocator'
import { errorMessage } from '../api/http'
import { useAuthStore } from '../stores/auth'
import type { AlarmCategory, AlarmEvent, AlarmLevel, AlarmListState, AlarmPageQuery, AlarmShelve, AlarmSummary, AlarmTimeline } from '../types'
import { alarmActionLabels, alarmCategoryLabels, alarmMessage, alarmStateLabels, alarmStateType, alarmTrendQuery, formatTime, levelLabel, levelType, localizedText } from '../utils'
import { runBatch, summarizeBatch, type BatchReport } from '../composables/batchRunner'
import { useAlarmSourceCatalog } from '../composables/useAlarmSourceCatalog'
import { can } from '../access/policies'
import { serverFormErrors } from '../composables/formErrors'
import { usePolling } from '../composables/usePolling'
import { useNowTicker } from '../composables/useNowTicker'
import { onHubEvent, onHubReconnected } from '../realtime/hub'
import { tr } from '../i18n'
import { positiveQueryId } from '../nav/query'
import { readLastNumber, rememberNumber } from '../composables/lastChoice'
import { openShortcutHelp, registerShortcuts } from '../shortcuts/registry'
import type { PageIssue } from '../components/PageStatusBar.vue'
import { modalOpen } from '../ui/overlay'

export function useAlarmsView() {
  const auth = useAuthStore()
  let listSeq = 0, summarySeq = 0, timelineSeq = 0, disposed = false
  const {
    canAck, deviceAreas, sourceDevices, sourceTags, sourceCatalogLoading, sourceCatalogError,
    alarmSource, alarmValueText, loadSourceCatalog, canAckAlarm, canSilencePanel
  } = useAlarmSourceCatalog(auth, () => disposed)
  // 从报警跳趋势要带上「报警前后那一段」：见 utils.alarmTrendQuery（与总览页同源）。
  const trendQuery = alarmTrendQuery
  const tab = ref<'live' | 'shelved' | 'history'>('live')
  const alarms = ref<AlarmEvent[]>([])
  const HISTORY_PAGE_SIZE = 100
  // 每页条数按用户记住：交班或刷新后不必每次重选；只接受下拉里提供的档位。
  const PAGE_SIZES = [50, 100, 200, 500]
  const rememberedPageSize = (name: string, fallback: number): number => {
    const value = readLastNumber(name)
    return value != null && PAGE_SIZES.includes(value) ? value : fallback
  }
  const livePageSize = ref(rememberedPageSize('alarms-live-page-size', 100))
  const livePage = ref(1)
  const liveTotal = ref<number | null>(null)
  const historyPage = ref(1)
  const historyPageSize = ref(rememberedPageSize('alarms-history-page-size', HISTORY_PAGE_SIZE))
  const historyTotal = ref(0)
  const historyCapped = ref(false)
  const shelves = ref<AlarmShelve[]>([])
  const summary = ref<AlarmSummary | null>(null)
  const summaryError = ref('')
  const loading = ref(false)
  const loadError = ref('')
  const actionError = ref('')
  const ackError = ref('')
  const keyword = ref('')
  // 默认只看「全部活动」（未恢复、未暂挂/抑制）。已恢复未确认的存量最多，
  // 全量默认展示会把首屏刷成几千行；需要时点对应状态筛选即可。
  const stateFilter = ref<AlarmListState | 'live'>('active')
  const liveSort = ref<'priority' | 'time'>('priority')
  const route = useRoute()
  const router = useRouter()
  if (route.query.tab === 'history') tab.value = 'history'
  // 清除测点筛选与写回地址栏是两条异步导航：置位期间 syncUrl 不再把 tagId 加回去
  let tagFilterClearing = false
  function queryTagIdsParam(): string | undefined {
    const raw = route.query.tagIds ?? route.query.tagId
    const value = Array.isArray(raw) ? raw.filter(x => typeof x === 'string').join(',') : raw
    return typeof value === 'string' && value.trim() ? value.trim() : undefined
  }
  function clearTagFilter(): void {
    const query = { ...route.query }
    delete query.tagId
    delete query.tagIds
    tagFilterClearing = true
    void router.replace({ query }).finally(() => { tagFilterClearing = false })
  }

  // 列表/统计/来源目录的失败合并到一条状态条：保留每条的重试入口，但不再各占一整行。
  const pageIssues = computed<PageIssue[]>(() => {
    const issues: PageIssue[] = []
    if (loadError.value) issues.push({ id: 'list', test: 'alarms-load-error', level: 'error', message: loadError.value, action: tr('common.retry'), actionTest: 'alarms-list-retry' })
    if (summaryError.value) issues.push({ id: 'summary', test: 'alarms-summary-error', level: 'warning', message: summaryError.value, action: tr('common.retry'), actionTest: 'alarms-summary-retry' })
    if (actionError.value) issues.push({ id: 'action', test: 'alarms-action-error', level: 'error', message: actionError.value })
    if (sourceCatalogError.value) issues.push({ id: 'source', test: 'alarm-source-error', level: 'warning', message: tr('alarmListReview.sourceCatalogFailed'), detail: sourceCatalogError.value, action: tr('common.retry'), actionTest: 'alarm-source-retry' })
    if (historyCapped.value && tab.value === 'history') issues.push({ id: 'capped', test: 'alarms-history-capped', level: 'warning', message: tr('alarmsView.historyCapped') })
    return issues
  })

  function onStatusAction(id: string): void {
    if (id === 'list') void reload()
    else if (id === 'summary') void loadSummary()
    else if (id === 'source') void loadSourceCatalog()
  }
  watch(() => route.query.state, value => {
    const states = ['active', 'live', 'unack', 'rtnUnack', 'acked', 'shelved', 'suppressed'] as const
    stateFilter.value = states.find(state => state === value) ?? 'active'
  }, { immediate: true })
  const historyFromQuery = Number(route.query.from)
  const historyToQuery = Number(route.query.to)
  const historyRange = ref<[Date, Date] | null>(
    route.query.tab === 'history' && Number.isFinite(historyFromQuery) && Number.isFinite(historyToQuery) && historyFromQuery < historyToQuery
      ? [new Date(historyFromQuery), new Date(historyToQuery)]
      : [new Date(Date.now() - 24 * 3600_000), new Date()]
  )
  const ackDialog = reactive({ visible: false, mode: 'ack' as 'ack' | 'note', alarm: null as AlarmEvent | null, note: '', saving: false })
  const shelveDialog = reactive({ visible: false, alarm: null as AlarmEvent | null, minutes: 60, reason: '', saving: false })
  const timeline = ref<AlarmTimeline | null>(null)
  const timelineAlarm = ref<AlarmEvent | null>(null)
  const timelineVisible = ref(false)
  const timelineLoading = ref(false), timelineError = ref('')
  const shelveError = ref('')
  // 批量确认：多选行 + 进度状态；等级与关键词过滤同时服务实时页和历史页
  const selected = ref<AlarmEvent[]>([])
  const batchRunning = ref(false)
  const batchProgress = ref('')
  const tableRef = ref<TableInstance>()
  const keywordInputRef = ref<InputInstance>()
  // 键盘聚焦用 id 锚定而不是行号：30s 轮询刷新后行号会变，id 不会
  const focusedId = ref<number | null>(null)
  const focusedIndex = computed(() => focusedId.value === null ? -1 : alarms.value.findIndex(alarm => alarm.id === focusedId.value))
  const focusedAlarm = computed(() => focusedIndex.value < 0 ? null : alarms.value[focusedIndex.value] ?? null)
  const levelOptions = ['Critical', 'Warning', 'Info'] as const
  const levelFilter = ref<AlarmLevel | ''>('')
  const categoryFilter = ref<AlarmCategory | ''>('')
  const rangePreset = ref<number | ''>('')
  const loadedHistoryRange = ref<[number, number] | null>(null)
  const historyRangeDirty = computed(() => !!loadedHistoryRange.value &&
    (!historyRange.value || historyRange.value.some((date, index) => date.getTime() !== loadedHistoryRange.value?.[index])))
  const batchOutcome = ref<(BatchReport & { failedIds: number[]; skipped: number }) | null>(null)
  // 批量确认是逐条串行的：几十上百条在慢速网络下要跑很久，期间页面所有按钮都被禁用。
  // 给一个「停止」，让操作员能腾出手处理新的严重报警，已确认的照常计成功。
  const batchCancel = ref(false)

  // 地址栏是视图状态的唯一来源：刷新、复制链接、交接班描述都依赖它。
  // 这里必须在下面的合并 watch 建立之前完成初始化，否则逐项赋值会各触发一次重查。
  const initialQuery = route.query
  const initialLevel = initialQuery.level
  const initialCategory = initialQuery.category
  const initialKeyword = initialQuery.keyword
  const initialPage = positiveQueryId(initialQuery.page)
  if (typeof initialKeyword === 'string') keyword.value = initialKeyword
  if (typeof initialLevel === 'string' && levelOptions.some(level => level === initialLevel)) levelFilter.value = initialLevel as AlarmLevel
  if (typeof initialCategory === 'string' && initialCategory in alarmCategoryLabels) categoryFilter.value = initialCategory as AlarmCategory
  if (initialQuery.sort === 'time' || initialQuery.sort === 'priority') liveSort.value = initialQuery.sort
  if (initialPage) livePage.value = initialPage

  // 活动报警的“已持续”必须每秒走动：只在推送或轮询时重算会让人误判报警刚发生
  const { now } = useNowTicker()

  let pushTimer: number | undefined
  let filterTimer: number | undefined
  let disposeAlarmHandler: (() => void) | undefined
  let disposeReconnect: (() => void) | undefined
  let disposeShortcuts: (() => void) | undefined

  // 报警声音统一由 AppLayout 经 handleAlarmPush 播放（走 alarmAudio 的静音与过滤）。
  // 这里曾有一份自建 AudioContext 的发声实现：它绕过了静音开关，并且与 AppLayout 的订阅
  // 重复消费同一条 alarm 事件，导致静音失效、报警页上双重发声。请勿再加回来。

  const stateLabel = computed(() => tr({
    active: 'alarmsView.filterActive', live: 'alarmWorkbench.liveScope', unack: 'overviewStatus.activeUnack', rtnUnack: 'overviewStatus.returned',
    acked: 'overviewStatus.acked', shelved: 'alarmsView.k06d561', suppressed: 'alarmsView.kaa2e2b'
  }[stateFilter.value]))
  const hasFilters = computed(() => !!queryTagIdsParam() || !!keyword.value || !!levelFilter.value || !!categoryFilter.value || tab.value === 'live' && stateFilter.value !== 'active')
  const emptyText = computed(() => {
    if (loadError.value) return loadError.value
    if (loading.value || liveTotal.value === null && tab.value === 'live') return tr('alarmsView.loadingResults')
    if (keyword.value.trim() || levelFilter.value || categoryFilter.value || queryTagIdsParam()) return tr('alarmsView.noFilteredResults')
    return tab.value === 'live' ? tr('alarmsView.noStateResults', [stateLabel.value]) : tr('alarmsView.noHistoryResults')
  })
  function canSelectAlarm(alarm: AlarmEvent): boolean { return !alarm.ackTs && canAckAlarm(alarm) }
  const selectableAlarms = computed(() => alarms.value.filter(canSelectAlarm))
  const visibleAckable = computed(() => selectableAlarms.value.filter(alarm => alarm.level !== 'Critical'))
  const filtersOpen = ref(false)
  const rowMenuId = ref<number | null>(null)
  const pendingNotice = ref<'' | 'critical' | 'update'>('')
  const silenceUntil = ref<number | null>(null)
  const { screenPicker, screenMatches, ensureScreenIndex, openAlarmScreen, goScreen } = useAlarmScreenLocator(actionError)
  let pushedCritical = false
  let pendingLocateId: number | null = null
  let appliedFocus: number | null = null
  let timelineReturn: HTMLElement | null = null
  let timelineReturnId: number | null = null
  const selectedAckable = computed(() => {
    const ids = new Set(selected.value.map(alarm => alarm.id))
    return selectableAlarms.value.filter(alarm => ids.has(alarm.id))
  })
  const retryRows = computed(() => {
    const ids = new Set(batchOutcome.value?.failedIds ?? [])
    return selectableAlarms.value.filter(alarm => ids.has(alarm.id))
  })
  function clearSelection(): void { selected.value = []; tableRef.value?.clearSelection() }
  function resetFilters(): void { keyword.value = ''; levelFilter.value = ''; categoryFilter.value = ''; clearTagFilter(); if (tab.value === 'live') stateFilter.value = 'active' }

  // 把当前筛选/排序/页码写回地址栏。用 replace 保留历史栈，值未变化时不动 URL。
  function syncUrl(): void {
    if (disposed) return
    const query: Record<string, string> = {}
    if (!tagFilterClearing && queryTagIdsParam()) query.tagIds = queryTagIdsParam()
    if (tab.value === 'live' && stateFilter.value !== 'active') query.state = stateFilter.value
    if (levelFilter.value) query.level = levelFilter.value
    if (categoryFilter.value) query.category = categoryFilter.value
    if (keyword.value.trim()) query.keyword = keyword.value.trim()
    if (tab.value === 'live' && liveSort.value !== 'priority') query.sort = liveSort.value
    if (tab.value === 'live' && livePage.value > 1) query.page = String(livePage.value)
    if (tab.value === 'history') {
      query.tab = 'history'
      const range = loadedHistoryRange.value ?? historyRange.value?.map(date => date.getTime())
      if (range?.length === 2) {
        query.from = String(range[0])
        query.to = String(range[1])
      }
    }
    const focus = positiveQueryId(route.query.focus)
    if (focus) query.focus = String(focus)
    const current = route.query
    const unchanged = Object.keys(query).length === Object.keys(current).length &&
      Object.entries(query).every(([key, value]) => current[key] === value)
    if (unchanged) return
    void router.replace({ query })
  }

  watch([keyword, levelFilter, categoryFilter, stateFilter, () => queryTagIdsParam(), liveSort], (values, previous) => {
    listSeq++
    if (filterTimer !== undefined) window.clearTimeout(filterTimer)
    clearSelection()
    livePage.value = 1
    historyPage.value = 1
    alarms.value = []
    liveTotal.value = null
    historyTotal.value = 0
    syncUrl()
    if (tab.value === 'shelved') return
    loading.value = true
    if (values[0] !== previous[0]) {
      filterTimer = window.setTimeout(() => { filterTimer = undefined; void reload() }, 250)
    } else {
      filterTimer = undefined
      void reload()
    }
  })
  watch(selectableAlarms, list => {
    const ids = new Set(list.map(alarm => alarm.id))
    if (selected.value.some(alarm => !ids.has(alarm.id))) clearSelection()
  })

  function onSelectionChange(rows: AlarmEvent[]): void {
    const ids = new Set(selectableAlarms.value.map(alarm => alarm.id))
    selected.value = rows.filter(alarm => ids.has(alarm.id))
  }

  // ---- 键盘操作 ----
  // HMI 惯例：↑↓/JK 选行、A 确认、N 备注、T/Enter 时间线、X 勾选、/ 搜索。
  // 这里只做「进入已有流程」的快捷键：确认仍走弹窗，绝不因按键直接改变现场状态。
  function isTypingTarget(target: EventTarget | null): boolean {
    const element = target as HTMLElement | null
    return !!element && (element.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(element.tagName))
  }

  function editingOpen(): boolean {
    return ackDialog.visible || shelveDialog.visible || timelineVisible.value
  }
  function overlayOpen(): boolean {
    return editingOpen() || modalOpen()
  }

  // 用 Tab 走到按钮/链接上时，Enter/Space 归浏览器原生激活，不能被行快捷键抢走
  function onInteractiveElement(): boolean {
    const element = document.activeElement as HTMLElement | null
    return !!element && /^(BUTTON|A)$/.test(element.tagName)
  }

  function focusRow(index: number): void {
    if (index < 0 || index >= alarms.value.length) return
    focusedId.value = alarms.value[index].id
    void nextTick(() => {
      const root = tableRef.value?.$el as HTMLElement | undefined
      root?.querySelectorAll<HTMLElement>('.el-table__body-wrapper tbody tr')[index]?.scrollIntoView({ block: 'nearest' })
    })
  }

  function moveFocus(delta: number): void {
    if (!alarms.value.length) return
    if (focusedIndex.value < 0) { focusRow(delta > 0 ? 0 : alarms.value.length - 1); return }
    focusRow(Math.min(alarms.value.length - 1, Math.max(0, focusedIndex.value + delta)))
  }

  function toggleSelection(alarm: AlarmEvent): void {
    tableRef.value?.toggleRowSelection(alarm, !selected.value.some(item => item.id === alarm.id))
  }

  function onKeyboard(e: KeyboardEvent): void {
    if (disposed || e.defaultPrevented || e.ctrlKey || e.metaKey || e.altKey) return
    // 手机/平板隐藏了桌面表格，行聚焦不可见，键盘流程只保留桌面端
    if (window.matchMedia?.('(max-width: 768px)').matches) return
    if (isTypingTarget(e.target) || overlayOpen()) return
    if (tab.value === 'shelved') return
    if (e.key === '/') { e.preventDefault(); keywordInputRef.value?.focus(); return }
    if (e.key === 'ArrowDown' || e.key === 'j' || e.key === 'J') { e.preventDefault(); moveFocus(1); return }
    if (e.key === 'ArrowUp' || e.key === 'k' || e.key === 'K') { e.preventDefault(); moveFocus(-1); return }
    if (e.key === 'Home') { e.preventDefault(); focusRow(0); return }
    if (e.key === 'End') { e.preventDefault(); focusRow(alarms.value.length - 1); return }
    if (e.key === 'Escape') {
      if (focusedId.value !== null) { e.preventDefault(); focusedId.value = null }
      return
    }
    const alarm = focusedAlarm.value
    if (!alarm) return
    if (e.key === 'Enter') {
      if (onInteractiveElement()) return
      e.preventDefault()
      openTimeline(alarm)
      return
    }
    if (e.key === 't' || e.key === 'T') { e.preventDefault(); openTimeline(alarm); return }
    if (e.key === 'a' || e.key === 'A') {
      if (alarm.ackTs || !canAckAlarm(alarm)) return
      e.preventDefault()
      openAck(alarm)
      return
    }
    if (e.key === 'n' || e.key === 'N') {
      if (!canAckAlarm(alarm)) return
      e.preventDefault()
      openNote(alarm)
      return
    }
    if (e.key === 'x' || e.key === 'X' || e.key === ' ') {
      if (e.key === ' ' && onInteractiveElement()) return
      if (tab.value !== 'live' || alarm.ackTs || !canAckAlarm(alarm)) return
      e.preventDefault()
      toggleSelection(alarm)
    }
  }

  // 只确认未确认的选中项：已确认的跳过，避免整批因个别 400 失败。
  // 用最新列表回填，防止选中后列表刷新留下过期对象。
  async function batchAck(): Promise<void> {
    await confirmAndAck(selectedAckable.value, 'selected')
  }

  async function confirmAndAck(targets: AlarmEvent[], mode: 'selected' | 'visible'): Promise<void> {
    if (disposed || batchRunning.value || loading.value || tab.value !== 'live' || !targets.length) return
    const ids = new Set(targets.map(alarm => alarm.id))
    batchRunning.value = true
    batchCancel.value = false
    try {
      const needsNote = targets.some(alarm => alarm.level === 'Critical')
      let batchNote = ''
      // 范围说明（多少条、含不含隐藏项）和「不可撤销」在两种弹窗里都要给：
      // 只弹一个要求填说明的输入框，会让操作员在不知道自己要确认几条的情况下签字。
      const scope = tr(mode === 'visible' ? 'alarmsView.ackVisibleConfirm' : 'alarmsView.batchAckConfirm', [ids.size])
        + (mode === 'visible' ? `\n${tr('alarmUx.ackVisibleCritical')}` : '')
      if (needsNote) {
        // 严重报警确认必须留下处理说明，批量确认时统一填写一次。
        try {
          const result = await ElMessageBox.prompt(`${scope} ${tr('alarmsView.k8ea5fe')}`, tr('alarmsView.confirmTitle'), {
            inputType: 'textarea',
            inputValidator: (value: string) => (value && value.trim() ? true : tr('alarmsView.k8ea5fe')),
            confirmButtonText: tr(mode === 'visible' ? 'alarmsView.ackVisible' : 'alarmsView.batchAck'),
            cancelButtonText: tr('alarmsView.k4d0b46')
          })
          batchNote = result.value.trim()
        } catch { return }
      } else {
        try {
          await ElMessageBox.confirm(
            scope,
            tr('alarmsView.confirmTitle'),
            { type: 'warning', confirmButtonText: tr(mode === 'visible' ? 'alarmsView.ackVisible' : 'alarmsView.batchAck'), cancelButtonText: tr('alarmsView.k4d0b46') }
          )
        } catch { return }
      }
      if (disposed || tab.value !== 'live') return
      const rows = selectableAlarms.value.filter(alarm => ids.has(alarm.id))
      const failedIds: number[] = []
      actionError.value = ''
      batchOutcome.value = null
      const report = await runBatch(
        rows,
        (a) => `#${a.id} ${alarmMessage(a)}`,
        async alarm => {
          try { await alarmApi.ack(alarm.id, batchNote) }
          catch (error) { failedIds.push(alarm.id); throw error }
        },
        (p) => { batchProgress.value = tr('alarmsView.batchAckProgress', [p.done + (p.current ? 1 : 0), p.total, p.current ? ` · ${p.current}` : '']) },
        () => batchCancel.value
      )
      if (disposed) return
      batchOutcome.value = { ...report, failedIds, skipped: ids.size - rows.length }
      if (report.failed.length === 0 && !report.canceled) ElMessage.success(tr('alarmsView.batchAckOk', [report.ok, report.total]))
      await reload()
    } finally {
      batchRunning.value = false
      batchProgress.value = ''
    }
  }

  function applyRangePreset(hours: number | ''): void {
    if (typeof hours !== 'number') return
    historyRange.value = [new Date(Date.now() - hours * 3600_000), new Date()]
    searchHistory()
  }

  function rowClass({ row, rowIndex }: { row: AlarmEvent; rowIndex: number }): string {
    const classes: string[] = []
    if (row.endTs) classes.push('alarm-row-returned')
    else if (!row.shelved && row.level === 'Critical') classes.push('alarm-row-critical')
    else if (!row.shelved && row.level === 'Warning') classes.push('alarm-row-warning')
    else if (!row.shelved && row.level === 'Info') classes.push('alarm-row-info')
    if (rowIndex === focusedIndex.value) classes.push('alarm-row-focused')
    return classes.join(' ')
  }

  // 行被刷新掉（确认后转历史、过滤变化）时顺势释放聚焦，避免指针停在别的行上
  watch(alarms, list => {
    if (focusedId.value !== null && !list.some(alarm => alarm.id === focusedId.value)) focusedId.value = null
    const focus = positiveQueryId(route.query.focus)
    if (focus != null && appliedFocus !== focus && list.some(alarm => alarm.id === focus)) {
      appliedFocus = focus
      focusedId.value = focus
      void nextTick(() => focusAlarm(focus))
    }
    if (pendingLocateId == null) return
    const id = pendingLocateId
    if (loading.value || !list.some(alarm => alarm.id === id)) return
    pendingLocateId = null
    void nextTick(() => focusAlarm(id))
  })
  watch(loading, busy => {
    const focus = positiveQueryId(route.query.focus)
    if (!busy && focus != null && appliedFocus !== focus && !alarms.value.some(alarm => alarm.id === focus)) {
      appliedFocus = focus
      actionError.value = tr('alarmUx.focusMissing')
    }
    if (busy || pendingLocateId == null) return
    const id = pendingLocateId
    if (alarms.value.some(alarm => alarm.id === id)) {
      pendingLocateId = null
      void nextTick(() => focusAlarm(id))
    } else {
      pendingLocateId = null
      actionError.value = tr('alarmUx.firstOutMissing')
    }
  })

  function chooseRowAction(action: () => void): void {
    rowMenuId.value = null
    action()
  }
  function shelfSource(row: AlarmShelve): string {
    const tag = sourceTags.value.get(row.tagId)
    const deviceId = tag?.deviceId
    const device = deviceId ? sourceDevices.value.get(deviceId) : undefined
    return [device?.name ?? (deviceId ? tr('alarmListReview.deviceId', [deviceId]) : ''),
      tag?.name ?? tr('alarmListReview.tagId', [row.tagId])]
      .filter(Boolean).join(' / ')
  }
  function shelfRemaining(row: AlarmShelve): string {
    const left = row.untilTs - now.value
    if (left <= 0) return tr('alarmUx.shelfExpired')
    const minutes = Math.max(1, Math.ceil(left / 60000))
    if (minutes < 60) return tr('alarmUx.shelfMinutes', [minutes])
    const hours = Math.floor(minutes / 60)
    return tr('alarmUx.shelfHours', [hours, minutes % 60])
  }

  function focusAlarm(id: number): void {
    const row = document.querySelector(`[data-alarm-id="${id}"]`)?.closest('tr')
    if (!row) { actionError.value = tr('alarmUx.firstOutMissing'); return }
    row.scrollIntoView({ block: 'center' })
    const target = row.querySelector('[data-test="ack"], [data-test="alarm-row-more"]') as HTMLElement | null
    target?.focus()
  }
  function locateFirstOut(id: number): void {
    actionError.value = ''
    if (alarms.value.some(alarm => alarm.id === id)) { void nextTick(() => focusAlarm(id)); return }
    pendingLocateId = id
    if (tab.value !== 'live') tab.value = 'live'
    if (stateFilter.value !== 'live') stateFilter.value = 'live'
    else void reload()
  }
  function duration(alarm: AlarmEvent): string {
    const end = alarm.endTs ?? now.value
    const seconds = Math.max(0, Math.floor((end - alarm.startTs) / 1000))
    if (seconds < 60) return tr('alarmsView.k99598b', [seconds])
    if (seconds < 3600) return tr('alarmsView.k76dfb6', [Math.floor(seconds / 60), seconds % 60])
    return tr('alarmsView.k1cf4ee', [Math.floor(seconds / 3600), Math.floor((seconds % 3600) / 60)])
  }

  async function loadSummary(): Promise<void> {
    const seq = ++summarySeq
    try {
      const result = await alarmApi.summary()
      if (seq === summarySeq && !disposed) { summary.value = result; summaryError.value = '' }
    } catch (error) {
      if (seq === summarySeq && !disposed) {
        summary.value = null
        summaryError.value = errorMessage(error, tr('overviewView.summaryFailed'))
      }
    }
  }

  function queryFilters(): Pick<AlarmPageQuery, 'level' | 'category' | 'keyword' | 'tagId' | 'tagIds'> {
    const raw = queryTagIdsParam()
    if (raw) {
      const parts = raw.split(',').map(x => x.trim()).filter(Boolean)
      if (parts.some(part => !/^[1-9]\d*$/.test(part))) throw new Error(tr('alarmsView.invalidTag'))
    }
    return { level: levelFilter.value || undefined, category: categoryFilter.value || undefined, keyword: keyword.value.trim() || undefined, tagIds: raw }
  }

  // background=true 用于推送与定时刷新：不点亮全表遮罩（遮罩会挡住确认操作）。失败留在页面上，不再额外弹提示。
  async function load(background = false): Promise<void> {
    if (disposed || tab.value === 'history') return
    const seq = ++listSeq, mode = tab.value
    if (!background) loading.value = true
    try {
      const [result, shelf] = await Promise.all([alarmApi.queryPage({
        live: true, priorityFirst: liveSort.value === 'priority', state: stateFilter.value === 'live' ? undefined : stateFilter.value, ...queryFilters(),
        offset: (livePage.value - 1) * livePageSize.value, limit: livePageSize.value
      }), ruleApi.shelves()])
      if (disposed || seq !== listSeq || tab.value !== mode) return
      if (livePage.value > 1 && !result.items.length && result.total <= (livePage.value - 1) * livePageSize.value) {
        livePage.value = Math.max(1, Math.ceil(result.total / livePageSize.value))
        // 页码被纠正后同步地址栏，否则刷新会回到已经空掉的页
        syncUrl()
        await load(background)
        return
      }
      alarms.value = result.items
      liveTotal.value = result.total
      shelves.value = shelf
      loadError.value = ''
    } catch (error) {
      if (disposed || seq !== listSeq || tab.value !== mode) return
      loadError.value = errorMessage(error, tr('alarmsView.k620b67'))
    } finally {
      if (seq === listSeq) loading.value = false
    }
    if (!disposed && seq === listSeq) await loadSummary()
  }

  // 报警洪峰时推送会连成一片，每条都触发一次全量重查会把列表刷成请求风暴。
  // 这里做在途去重 + 400ms 合批：同一波事件只查一次，落下的那波用 queued 补上。
  let backgroundBusy = false
  let backgroundQueued = false

  async function backgroundRefresh(): Promise<void> {
    if (disposed || tab.value === 'history' || filterTimer !== undefined || editingOpen()) return
    pendingNotice.value = ''
    pushedCritical = false
    if (backgroundBusy) {
      backgroundQueued = true
      return
    }
    backgroundBusy = true
    try {
      await load(true)
    } finally {
      backgroundBusy = false
    }
    if (backgroundQueued) {
      backgroundQueued = false
      await backgroundRefresh()
    }
  }

  function schedulePushRefresh(): void {
    if (tab.value === 'history') return
    if (pushTimer !== undefined) window.clearTimeout(pushTimer)
    pushTimer = window.setTimeout(() => {
      pushTimer = undefined
      if (editingOpen()) {
        pendingNotice.value = pushedCritical ? 'critical' : 'update'
        return
      }
      void backgroundRefresh()
    }, 400)
  }
  async function applyPendingPush(): Promise<void> {
    pendingNotice.value = ''
    pushedCritical = false
    if (tab.value === 'history') {
      tab.value = 'live'
      await onTabChange()
      return
    }
    await load(true)
  }

  function handleReconnect(): void {
    if (disposed) return
    if (tab.value === 'history' || editingOpen() || filterTimer !== undefined) {
      if (pendingNotice.value !== 'critical') pendingNotice.value = 'update'
      return
    }
    void backgroundRefresh()
  }

  async function loadHistory(useDraftRange = false): Promise<void> {
    if (disposed || tab.value !== 'history') return
    const seq = ++listSeq
    loading.value = true
    try {
      const range = !useDraftRange && loadedHistoryRange.value
        ? loadedHistoryRange.value
        : (historyRange.value ?? [new Date(Date.now() - 24 * 3600_000), new Date()]).map(date => date.getTime())
      const [from, to] = range
      if (!Number.isFinite(from) || !Number.isFinite(to) || from >= to) {
        loadError.value = tr('trendsView.invalidRange')
        return
      }
      const pageSize = historyPageSize.value
      const result = await alarmApi.queryPage({
        from,
        to,
        ...queryFilters(),
        offset: (historyPage.value - 1) * pageSize,
        limit: pageSize
      })
      if (disposed || seq !== listSeq || tab.value !== 'history') return
      alarms.value = result.items
      historyTotal.value = result.total
      historyCapped.value = result.capped
      loadedHistoryRange.value = [from, to]
      loadError.value = ''
    } catch (error) {
      if (disposed || seq !== listSeq || tab.value !== 'history') return
      loadError.value = errorMessage(error, tr('alarmsView.k620b67'))
    } finally {
      if (seq === listSeq) loading.value = false
    }
  }

  function searchHistory(): void {
    historyPage.value = 1
    void loadHistory(true)
  }

  function onLivePageChange(page: number): void {
    if (disposed || tab.value !== 'live' || livePage.value === page) return
    livePage.value = page
    clearSelection()
    alarms.value = []
    syncUrl()
    void load()
  }

  // 洪峰时希望一屏看更多：直播页与历史页共用同一组可选条数
  function onLiveSizeChange(size: number): void {
    if (disposed || tab.value !== 'live' || livePageSize.value === size) return
    livePageSize.value = size
    rememberNumber('alarms-live-page-size', size)
    livePage.value = 1
    clearSelection()
    alarms.value = []
    syncUrl()
    void load()
  }

  function onHistoryPageChange(page: number): void {
    if (disposed || tab.value !== 'history' || historyPage.value === page) return
    historyPage.value = page
    clearSelection()
    alarms.value = []
    void loadHistory()
  }

  function onHistorySizeChange(size: number): void {
    if (disposed || tab.value !== 'history' || historyPageSize.value === size) return
    historyPageSize.value = size
    rememberNumber('alarms-history-page-size', size)
    historyPage.value = 1
    clearSelection()
    alarms.value = []
    void loadHistory()
  }

  // 切到历史页时把默认区间刷新到“最近 24 小时至现在”，避免页面停留后漏掉新报警。
  function onTabChange(): Promise<void> {
    listSeq++
    if (filterTimer !== undefined) window.clearTimeout(filterTimer)
    filterTimer = undefined
    clearSelection()
    focusedId.value = null
    livePage.value = 1
    alarms.value = []
    liveTotal.value = null
    loadError.value = ''
    loadedHistoryRange.value = null
    rangePreset.value = ''
    historyCapped.value = false
    void nextTick(() => tableRef.value?.scrollTo({ left: 0, top: 0 }))
    // 选中行按 row-key 保留在表格内部，切页时要显式清掉，避免跨页误批量
    if (tab.value === 'history') {
      historyRange.value = [new Date(Date.now() - 24 * 3600_000), new Date()]
      historyPage.value = 1
    }
    syncUrl()
    return reload()
  }

  async function ackVisible(): Promise<void> {
    await confirmAndAck(visibleAckable.value, 'visible')
  }

  async function silenceAnnunciators(): Promise<void> {
    actionError.value = ''
    let list: Awaited<ReturnType<typeof annunciatorApi.list>>
    try {
      list = await annunciatorApi.list()
    } catch (error) {
      actionError.value = errorMessage(error, tr('alarmsView.silenceAnnFail'))
      return
    }
    const ackable = list.filter((p) => canSilencePanel(p.areaId))
    if (!ackable.length) {
      actionError.value = tr('alarmsView.silenceAnnNone')
      return
    }
    // 静音会停掉现场声光，属安全相关操作，必须先确认。只包含当前用户有确认权的面板。
    try {
      await ElMessageBox.confirm(tr('alarmsView.silenceAnnConfirm', [ackable.length]), tr('alarmsView.confirmTitle'), {
        type: 'warning',
        confirmButtonText: tr('alarmsView.silenceAnn'),
        cancelButtonText: tr('alarmsView.k4d0b46')
      })
    } catch {
      return
    }
    // 逐个面板静音，部分失败时列出明细，不静默吞错
    const untils: number[] = []
    const report = await runBatch(ackable, (p) => p.name, async (p) => {
      const status = await annunciatorApi.silence(p.id)
      if (status.silenceUntilTs) untils.push(status.silenceUntilTs)
    })
    if (untils.length) silenceUntil.value = Math.max(...untils)
    if (report.failed.length === 0) ElMessage.success(tr('alarmsView.silenceAnnOk'))
    else await ElMessageBox.alert(summarizeBatch(report, ''), tr('alarmsView.confirmTitle'), { type: report.ok ? 'warning' : 'error', confirmButtonText: tr('alarmsView.k4d0b46') })
  }
  function reload(): Promise<void> {
    return tab.value === 'history' ? loadHistory() : load()
  }

  function openAck(alarm: AlarmEvent): void {
    if (disposed || ackDialog.saving || batchRunning.value) return
    ackError.value = ''
    Object.assign(ackDialog, { visible: true, mode: 'ack', alarm, note: '', saving: false })
  }

  function openNote(alarm: AlarmEvent): void {
    if (disposed || ackDialog.saving || batchRunning.value) return
    ackError.value = ''
    Object.assign(ackDialog, { visible: true, mode: 'note', alarm, note: '', saving: false })
  }

  async function submitAck(): Promise<void> {
    if (disposed || !ackDialog.visible || !ackDialog.alarm || ackDialog.saving || batchRunning.value) return
    const { alarm, mode } = ackDialog, note = ackDialog.note.trim()
    ackError.value = ''
    if (mode === 'note' && !note) {
      ackError.value = tr('alarmsView.kc31ec2')
      return
    }
    if (mode === 'ack' && alarm.level === 'Critical' && !note) {
      ackError.value = tr('alarmsView.k8ea5fe')
      return
    }
    ackDialog.saving = true
    try {
      if (mode === 'ack') await alarmApi.ack(alarm.id, note)
      else await alarmApi.note(alarm.id, note)
      if (disposed) return
      ElMessage.success(mode === 'ack' ? tr('alarmsView.kd9fea6') : tr('alarmsView.kbad4a8'))
      ackDialog.visible = false
      await reload()
    } catch (error) {
      if (!disposed) ackError.value = errorMessage(error, tr('alarmsView.k09e424'))
    } finally {
      ackDialog.saving = false
    }
  }

  function openShelve(alarm: AlarmEvent): void {
    if (disposed || shelveDialog.saving || batchRunning.value) return
    shelveError.value = ''
    Object.assign(shelveDialog, { visible: true, alarm, minutes: 60, reason: '', saving: false })
  }

  async function submitShelve(): Promise<void> {
    if (disposed || !shelveDialog.visible || !shelveDialog.alarm?.ruleId || shelveDialog.saving || batchRunning.value) return
    const ruleId = shelveDialog.alarm.ruleId, minutes = shelveDialog.minutes, reason = shelveDialog.reason.trim()
    shelveError.value = ''
    if (!reason) {
      shelveError.value = tr('alarmsView.shelveReasonRequired')
      return
    }
    shelveDialog.saving = true
    try {
      await ruleApi.shelve(ruleId, minutes, reason)
      if (disposed) return
      ElMessage.success(tr('alarmsView.ka7f94c'))
      shelveDialog.visible = false
      await reload()
    } catch (error) {
      // 服务端 400 尽量落到原因字段旁，避免只弹一条全局提示
      if (!disposed) shelveError.value = serverFormErrors(error, tr('alarmsView.k2fe047')).message
    } finally {
      shelveDialog.saving = false
    }
  }

  async function unshelve(ruleId: number): Promise<void> {
    // 解除后该规则的新报警会重新通知，属易误触操作
    try {
      await ElMessageBox.confirm(tr('alarmsView.unshelveConfirm'), tr('alarmsView.confirmTitle'), {
        type: 'warning',
        confirmButtonText: tr('alarmsView.kcd308e'),
        cancelButtonText: tr('alarmsView.k4d0b46')
      })
    } catch {
      return
    }
    actionError.value = ''
    try {
      await ruleApi.unshelve(ruleId)
      ElMessage.success(tr('alarmsView.kdaf18d'))
      await reload()
    } catch (error) {
      actionError.value = errorMessage(error, tr('alarmsView.k7286b4'))
    }
  }

  async function openTimeline(alarm: AlarmEvent): Promise<void> {
    const active = document.activeElement
    timelineReturn = active instanceof HTMLElement ? active : null
    timelineReturnId = alarm.id
    if (disposed) return
    const seq = ++timelineSeq
    timelineAlarm.value = alarm
    timeline.value = null; timelineError.value = ''; timelineLoading.value = true; timelineVisible.value = true
    try {
      const result = await alarmApi.timeline(alarm.id)
      if (!disposed && seq === timelineSeq) timeline.value = result
    } catch (error) {
      if (!disposed && seq === timelineSeq) timelineError.value = errorMessage(error, tr('alarmsView.k760fd5'))
    } finally { if (!disposed && seq === timelineSeq) timelineLoading.value = false }
  }
  function retryTimeline(): void {
    if (timelineVisible.value && timelineAlarm.value && !timelineLoading.value) void openTimeline(timelineAlarm.value)
  }
  watch(timelineVisible, visible => {
    if (visible) return
    timelineSeq++
    timelineLoading.value = false
    timeline.value = null
    timelineAlarm.value = null
    const target = timelineReturn
    const id = timelineReturnId
    timelineReturn = null
    timelineReturnId = null
    void nextTick(() => {
      if (target?.isConnected) { target.focus(); return }
      const row = id == null ? null : document.querySelector(`[data-alarm-id="${id}"]`)?.closest('tr')
      ;(row?.querySelector('[data-test="alarm-row-more"]') as HTMLElement | null)?.focus()
    })
  }, { flush: 'sync' })

  function registerAlarmHandler(): void {
    // 只负责刷新数据；发声由 AppLayout 统一处理，避免重复消费同一条 alarm 事件。
    disposeAlarmHandler = onHubEvent<{ action: string; alarm: AlarmEvent }>('alarm', (payload) => {
      const alarm = payload?.alarm
      if (alarm && alarm.level === 'Critical' && !alarm.ackTs && !alarm.endTs && !alarm.shelved) pushedCritical = true
      schedulePushRefresh()
    })
  }

  // 30s 兜底轮询：推送丢包时列表不至于长期停摆；隐藏页自动暂停，回到前台补一次。
  usePolling(backgroundRefresh, 30000)

  onMounted(async () => {
    disposeShortcuts = registerShortcuts({
      id: 'alarms',
      titleKey: 'shortcuts.alarms',
      items: [
        { keys: ['↓', 'J'], descKey: 'shortcuts.alarmDown' },
        { keys: ['↑', 'K'], descKey: 'shortcuts.alarmUp' },
        { keys: ['A'], descKey: 'shortcuts.alarmAck' },
        { keys: ['N'], descKey: 'shortcuts.alarmNote' },
        { keys: ['T'], descKey: 'shortcuts.alarmTimeline' },
        { keys: ['X'], descKey: 'shortcuts.alarmSelect' },
        { keys: ['/'], descKey: 'shortcuts.alarmSearch' },
        { keys: ['Esc'], descKey: 'shortcuts.closeLayer' }
      ]
    })
    window.addEventListener('keydown', onKeyboard)
    void loadSourceCatalog()
    await reload()
    if (disposed) return
    registerAlarmHandler()
    disposeReconnect = onHubReconnected(() => { handleReconnect() })
  })

  onBeforeUnmount(() => {
    disposed = true
    listSeq++
    summarySeq++
    timelineSeq++
    disposeShortcuts?.()
    window.removeEventListener('keydown', onKeyboard)
    if (pushTimer !== undefined) window.clearTimeout(pushTimer)
    if (filterTimer !== undefined) window.clearTimeout(filterTimer)
    disposeAlarmHandler?.()
    disposeReconnect?.()
  })
  return {
    auth, canAck, deviceAreas, sourceDevices, sourceTags, sourceCatalogLoading, sourceCatalogError, alarmSource, trendQuery, alarmValueText, loadSourceCatalog, canAckAlarm, canSilencePanel, tab, alarms, HISTORY_PAGE_SIZE, livePageSize, livePage, liveTotal, historyPage, historyPageSize, historyTotal, historyCapped, shelves, summary, summaryError, loading, loadError, actionError, ackError, keyword, stateFilter, liveSort, route, router, clearTagFilter, pageIssues, onStatusAction, historyFromQuery, historyToQuery, historyRange, ackDialog, shelveDialog, timeline, timelineAlarm, timelineVisible, timelineLoading, timelineError, shelveError, selected, batchRunning, batchProgress, tableRef, keywordInputRef, focusedId, focusedIndex, focusedAlarm, levelOptions, levelFilter, categoryFilter, rangePreset, loadedHistoryRange, historyRangeDirty, batchOutcome, batchCancel, initialQuery, initialLevel, initialCategory, initialKeyword, initialPage, now, stateLabel, hasFilters, emptyText, canSelectAlarm, selectableAlarms, visibleAckable, filtersOpen, rowMenuId, pendingNotice, silenceUntil, screenPicker, screenMatches, selectedAckable, retryRows, clearSelection, resetFilters, syncUrl, onSelectionChange, isTypingTarget, editingOpen, overlayOpen, onInteractiveElement, focusRow, moveFocus, toggleSelection, onKeyboard, batchAck, confirmAndAck, applyRangePreset, rowClass, chooseRowAction, shelfSource, shelfRemaining, collectScreenTags, ensureScreenIndex, openAlarmScreen, goScreen, focusAlarm, locateFirstOut, duration, loadSummary, queryFilters, load, backgroundRefresh, schedulePushRefresh, applyPendingPush, loadHistory, searchHistory, onLivePageChange, onLiveSizeChange, onHistoryPageChange, onHistorySizeChange, onTabChange, ackVisible, silenceAnnunciators, reload, openAck, openNote, submitAck, openShelve, submitShelve, unshelve, openTimeline, retryTimeline, registerAlarmHandler, ref, alarmActionLabels, alarmCategoryLabels, alarmMessage, alarmStateLabels, alarmStateType, formatTime, levelLabel, levelType, localizedText, can, openShortcutHelp
  }
}

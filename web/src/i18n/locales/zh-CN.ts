export default {
  configurationUx: {
    query: '查询', pendingQuery: '条件已修改，下方仍为上次结果；点击查询或按 Enter 应用。',
    resultScope: '当前结果：{0} · 第 {1} 页 · {2} 条', all: '全部', loadHelp: '读取失败时保留上次结果。请重新加载；若持续失败，请检查服务连接和当前账号权限。',
    basic: '基础连接', advanced: '高级采集参数', advancedHint: '通常保留默认值即可；修改不会改变其他协议参数。',
    savedNext: '已保存设备：{0}。保存配置不等于连接成功，可继续测试或添加测点。',
    addTags: '添加 / 查看测点', testConnection: '测试读取',
    guidanceTitle: '排查建议（不代表已确认故障原因）',
    networkHelp: '提示涉及连接或超时：核对主机、端口 / 串口、网络可达性和设备运行状态，再重试读取。不要直接放宽超时掩盖问题。',
    certificateHelp: '提示涉及证书：核对端点、安全策略、证书有效期和指纹；确认来源后再处理信任，不要关闭安全校验。',
    permissionHelp: '提示涉及权限或认证：核对当前账号、设备凭据及区域授权；请管理员授权后重试。',
    addressHelp: '提示涉及地址：核对寄存器区、地址基准、数据类型、DB / 位偏移或 OPC NodeId。',
    qualityHelp: '连接响应不等于有效数据。当前质量不是 Good，请检查测点地址、类型及设备诊断中的质量和时间。',
    unknownHelp: '暂不能确定故障原因。请保留上方原始错误，并打开采集诊断核对最近失败、配置状态和测点质量。',
    opcHelp: 'OPC UA：核对端点、NodeId、安全模式及证书信任。不要因为读取失败而直接改为无安全模式。',
    modbusHelp: 'Modbus：核对站号、寄存器区域和零基地址；连接成功但数值异常时检查数据类型及字节序。',
    protocolHelp: '按设备协议手册核对连接参数和采集地址；采集诊断可查看配置是否生效。',
    verify: '重新验证', setupDevice: '本次验证的设备', setupProgress: '已验证 {0} / {1} 项',
    setupScope: '按所选设备验证保存、连接及测点质量；画面发布检查为当前账号可见的站点范围。状态来自实际数据，不使用人工勾选。',
    checkedAt: '检查于 {0}', done: '已验证', pending: '待完成', unknown: '待验证',
    deviceSaved: '设备配置已保存', deviceSavedHint: '保存配置不等于连接成功；请继续建立测点并验证采集。',
    tagsSaved: '测点配置已保存', tagsSavedHint: '所选设备已配置 {0} 个物理测点。',
    connected: '设备采集已连接', connectedHint: '启用设备并至少配置一个测点后，再验证采集连接；不会自动发送测试或重连指令。',
    values: '测点数据有效', valuesHint: 'Good 测点 {0} / 配置测点 {1}。全部测点必须有有效且未过期的数据。',
    screenPublished: '站点已有发布画面', screenHint: '当前可见的已发布画面：{0}。此项不验证画面与所选设备的绑定或显示效果。',
    expired: '诊断快照已过期或配置尚未生效，请重新验证。',
    openStep: '前往处理', continueSetup: '继续配置：{0}',
    manualChecks: '仍需人工核对：画面绑定、单位、量程、运行效果以及备份恢复。自动验证不替代现场验收。',
    tagsFailed: '测点配置读取失败，请重新验证。',
    screensFailed: '画面发布状态读取失败，请重新验证。',
    diagnosticsFailed: '采集诊断暂不可用，请检查设备是否已启用、配置是否生效及网关连接，再重新验证。',
    noViewPermission: '当前账号无查看画面权限，请有权限的人员验证。',
    testUnknown: '读取未通过，服务未提供错误详情。'
  },
  recipeListReview: {
    moreParameters: '另有 {0} 项参数'
  },
  traceQueryReview: {
    conditionsChanged: '查询条件已修改，下方仍显示上次查询结果；请点击查询更新。',
    resultContext: '当前结果：SN {0} · 任务 {1} · 结果 {2} · 时间 {3} · 第 {4} 页 / 共 {5} 条 · 查询于 {6}',
    allSn: '全部 SN', allTime: '不限时间',
    notQueried: '请点击查询，获取当前条件下的追溯记录。',
    invalidRange: '请选择有效的时间范围，结束时间不能早于开始时间。',
    failedKeepsPrevious: '本次查询失败，下方保留上次成功查询的结果。'
  },
  oeeHierarchy: {
    coreMetrics: '核心生产指标', supportingMetrics: '辅助统计', target: '目标 {0}',
    onTarget: '达标', belowTarget: '低于目标', noData: '无数据',
    viewDay: '查看当日', backToRange: '返回原范围'
  },
  alarmListReview: {
    sort: '报警排序', priorityFirst: '严重度优先', newestFirst: '最新发生优先',
    source: '来源', deviceId: '设备 #{0}', tagId: '测点 #{0}',
    sourceCatalogFailed: '设备或测点目录读取失败，部分来源暂以编号显示。',
  },
  alarmWorkbench: {
    title: '报警工作台', states: '实时报警状态', filters: '报警筛选条件', actions: '报警处理操作',
    liveScope: '活动与待确认',
    table: '报警记录列表', scrollHint: '聚焦表格后，可用方向键滚动查看记录',
    silenceHint: '对有权限的声光面板消音；不确认报警，也不改变浏览器提示音开关。',
    rangeChanged: '时间范围已修改，下方仍显示上次查询结果；请点击查询更新。',
    pageSize: '每页条数', pageSizeOption: '{0} 条/页',
  },
  common: {
    retry: '重新加载',
    notFound: '对象不存在或已被删除',
    timeRange: '时间范围',
    compareFrom: '对比基准版本',
    compareTo: '对比目标版本',
    screenRun: '画面运行',
    kiosk: '大屏展示',
  },
  alarmReview: {
    conditionsChanged: '筛选条件已更改，当前仍显示上次分析结果，请重新查询后导出。',
    resultContext: '结果范围：{0} 至 {1}；区域：{2}；Top {3}'
  },
  oeeReview: {
    conditionsChanged: '筛选条件已更改，当前仍显示上次统计结果，请重新查询后导出。',
    resultContext: '结果范围：{0} 至 {1}；区域：{2}；产线：{3}'
  },
  operationsReview: {
    modeUnknown: '模式待校验', modesFailed: '设备控制模式读取失败，请刷新重试。',
    requestChanged: '申请已过期或已处理，请刷新列表后核对。'
  },
  layoutMonitor: {
    notFound: '指定的运行布局不存在或尚未发布，请重新选择。', unassigned: '本机工作站尚未分配已发布的布局，请联系管理员。', stationFailed: '本机工作站解析失败',
    targetRegion: '导航目标分区', summaryFailed: '报警汇总读取失败', summaryStale: '报警汇总待校验，数量未知', alarmsStale: '报警明细待校验',
    screensFailed: '画面目录读取失败', versionFailed: '布局版本校验失败', updateAvailable: '发现布局新版本 V{0}', applyUpdate: '应用新版本',
    applyConfirm: '应用新版本将重置当前分区导航并关闭浮动面板，是否继续？', updatedAt: '报警明细更新：{0}；汇总更新：{1}',
    stationsFailed: '工作站读取失败', unsaved: '未保存修改', discardChanges: '当前修改尚未保存，确定放弃？',
    replacePreset: '套用预设将替换全部显示器和分区，是否继续？', removeMonitor: '此显示器及其全部分区将被移除，是否继续？',
    closePanel: '关闭浮动面板', monitorMapping: '显示器映射', currentWindow: '当前窗口', regularWindow: '普通新窗口（不定位）',
    monitorFallback: '物理屏不足或定位权限不可用，请核对映射；未映射的逻辑屏将打开普通新窗口。',
    monitorDuplicates: '多个逻辑屏映射到同一物理屏，窗口可能重叠。',
    returnedUnack: '已恢复·待确认', activeUnack: '活动·未确认', activeAcked: '活动·已确认', returnedCount: '恢复待确认 {0}',
    alarmTitle: '活动与待确认报警', alarmRegion: '分区：{0}', alarmLimit: '显示前 {0} 条（非全量）', allAlarms: '查看全部报警',
    alarmLevel: '级别', alarmState: '状态', alarmStartedAt: '发生时间', alarmMessage: '报警内容', alarmAction: '操作',
    alarmTable: '报警明细，可用方向键滚动', ackAlarm: '确认报警：{0}', ackUnavailable: '不可确认', emptyAlarms: '暂无活动或待确认报警'
  },
  screenMonitor: {
    unavailable: '数据不可用', alarmsUnavailable: '报警数据不可用', historyUnavailable: '历史数据不可用', oeeUnavailable: 'OEE 数据不可用',
    alarmsFailed: '报警读取失败', historyFailed: '历史读取失败', oeeFailed: 'OEE 读取失败', tagsFailed: '测点目录读取失败，请刷新重试；控制暂不可用。',
    systemAlarms: '系统活动报警', alarmCount: '系统活动报警：{0} 条', partialAlarmCount: '系统活动报警：至少 {0} 条',
    alarmsLimited: '系统报警列表已达查询上限，仅显示部分记录，请到报警页核对。',
    publishedVersion: '运行版本 V{0}', draftVersion: '草稿版本 V{0}', draftName: '草稿名称', runVersion: '运行已发布版本 V{0}', more: '更多操作', moreFor: '画面操作：{0}'
  },
  realtimeMonitor: {
    retry: '重新加载', verified: '快照校验：{0}', quality: '数据质量', good: '良好', sampleTime: '最近采样',
    stale: '待校验', staleCount: '{0} 个测点采样过期或尚未完成校验', loading: '正在加载', noMatch: '没有符合筛选条件的测点',
    bitOne: '置位 (1)', bitZero: '未置位 (0)', sort: '排序', configurationOrder: '配置顺序', abnormalFirst: '异常优先', clearFilters: '清空筛选',
    activeAlarm: '关联活动报警', alarmCount: '活动报警 {0}', alarmUnavailable: '无权查看', alarmUnknown: '待确认', noAlarmObserved: '未见活动报警', tagFilter: '关联测点 #{0}',
    protectedAlarm: '含抑制/暂挂', alarmsLimited: '关联报警列表已截断，请到报警页核对', alarmsFailed: '关联报警加载失败',
    writeReferenceWarning: '参考值质量异常或尚未校验，请核对采样时间；下发仍需通过服务端检查。',
    invalidWrite: '请输入量程内的有效数值；整数测点不接受小数。',
    connection: { connected: '推送已连接', connecting: '推送连接中', reconnecting: '推送重连中', disconnected: '推送已断开' }
  },
  overviewStatus: {
    updated: '最后完整更新：{0}', unknown: '等待数据', stale: '数据已过期', loadedOnly: '仅已加载部分',
    activeUnack: '活动未确认', returned: '已恢复待确认', protection: '抑制 / 暂挂', review: '查看处理', flood: '报警泛滥',
    alarmScope: '活动未确认不含已恢复待确认、暂挂和抑制的报警；报警泛滥按近期触发频率判断，与当前活动数不同。',
    listed: '显示 {0} / {1} 条，严重优先', triggerValue: '触发值', state: '状态', acked: '活动已确认',
    locate: '定位', trend: '趋势', abnormalOnly: '仅异常', noDeviceIssues: '未发现通信异常',
    maintenance: '维护指标', systemEvent: '系统事件', serviceOk: '服务可用', serviceIssue: '服务异常', serviceUnknown: '服务状态未知'
  },
  hmi: {
    polyline: '折线管道', panel: '设备操作面板', input: '数值输入', apply: '下发', value: '设定值', invalidInput: '请输入量程内的有效数值',
    states: '多状态映射', stateValue: '状态值', stateLabel: '状态名称', addState: '添加状态', stopped: '停止', running: '运行', moving: '动作中', fault: '故障', unknown: '未知状态',
    points: '管道折点', addPoint: '添加折点', source: '起点设备', target: '终点设备', port: '连接端口', left: '左', right: '右', top: '上', bottom: '下',
    commands: '面板操作', addCommand: '添加操作', commandLabel: '操作名称', commandTag: '控制测点', commandValue: '命令值', remove: '删除', start: '启动', stop: '停止',
    enableStates: '添加常用状态', feedback: '反馈', noCommands: '未配置操作', stateColor: '状态颜色'
  },
  traceability: {
    title: '生产追溯', keywords: '生产追溯 SN 二维码 扫码 电流 电压 traceability barcode QR',
    records: '追溯查询', scan: '扫码绑定', config: '任务配置', refresh: '刷新', snQuery: '扫描或输入 SN',
    allJobs: '全部任务', from: '开始时间', to: '结束时间', query: '查询', empty: '暂无记录',
    job: '采集任务', time: '采集时间', ack: 'PLC 应答', acknowledged: '已回复 2', awaitingReply: '已入库，等待应答',
    fieldName: '字段名称', value: '采集值', unit: '单位', scanSn: '待采集产品 SN', bind: '绑定 SN',
    state: '状态', pendingSn: '待采集 SN', expires: '有效期至', reply: '应答值', clearScan: '清除待采集 SN',
    noScanJobs: '暂无可接收扫码的已启用任务', add: '新增任务', edit: '编辑任务', trigger: '检测地址', snSource: 'SN 来源',
    tcp: 'TCP 扫码枪', serial: 'RS232 扫码枪', connection: '扫码连接', tcpMode: 'TCP 模式', tcpClient: '主动连接', tcpServer: '监听接入',
    tcpHost: '扫码枪地址', listenHost: '本机监听地址', port: '端口', serialPort: '串口名称', baudRate: '波特率', dataBits: '数据位', parity: '校验位', stopBits: '停止位',
    parityNone: '无校验', parityOdd: '奇校验', parityEven: '偶校验', terminator: '报文结束符', encoding: '字符编码',
    scannerStates: { stopped: '已停止', connecting: '连接中', listening: '等待扫码枪连接', connected: '已连接', disconnected: '已断开，重连中', error: '配置异常' },
    plc: 'PLC 寄存器', webScan: '网页扫码', enabled: '启用', yes: '是', no: '否', device: 'PLC 设备',
    snFormat: 'SN 格式', numeric: '数值', asciiLow: 'ASCII（低字节在前）', asciiHigh: 'ASCII（高字节在前）',
    snRegisters: 'SN 寄存器（按编码顺序选择）', scanExpiry: '扫码有效期（秒）', fields: '采集字段', addField: '添加字段',
    tag: '采集点位', remove: '移除', cancel: '取消', save: '保存', required: '请填写任务、检测地址、SN 来源和采集字段。',
    loadFailed: '任务或点位加载失败', queryFailed: '追溯记录查询失败', scanFailed: '扫码绑定操作失败', saveFailed: '任务保存失败',
    states: { idle: '等待触发', ready: 'SN 已就绪', capturing: '采集中', awaitingReply: '等待 PLC 应答', completed: '采集完成', failed: '采集失败', error: '异常', offline: '设备离线' }
  },
  flow: {
    fit: '适应', zoomIn: '放大', zoomOut: '缩小', autoLayout: '自动布局', deleteEdge: '删除连线', then: '是', else: '否', fail: '失败',
    hintEdit: '拖动节点移动；从节点下方圆点拖到目标节点连线；点选连线后可删除；滚轮缩放，拖动空白处平移。虚线为“顺延下一步”。',
    hintRun: '实时显示运行中各步状态：黄色执行中、绿色完成、红色失败、淡色跳过。',
    port: { next: '下一步', then: '条件成立', else: '条件不成立', branch: '并行分支', join: '汇合步骤', target: '跳转目标', fail: '失败转到' }
  },
  requestErrors: {
    network: '网络连接失败，请检查网络或服务是否正在运行',
    timeout: '请求超时，服务可能繁忙，请稍后重试',
    status: '（HTTP {0}）',
    withTrace: '（追踪号：{0}）'
  }
}

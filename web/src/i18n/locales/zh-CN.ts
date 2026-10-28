export default {
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
    notFound: '指定的运行布局不存在，请重新选择。', unassigned: '本机工作站尚未分配布局，请联系管理员。', stationFailed: '本机工作站解析失败',
    targetRegion: '导航目标分区', summaryFailed: '报警汇总读取失败', summaryStale: '报警汇总待校验，数量未知', alarmsStale: '报警明细待校验',
    screensFailed: '画面目录读取失败', versionFailed: '布局版本校验失败', updateAvailable: '发现布局新版本 V{0}', applyUpdate: '应用新版本',
    applyConfirm: '应用新版本将重置当前分区导航并关闭浮动面板，是否继续？', updatedAt: '报警明细更新：{0}；汇总更新：{1}',
    stationsFailed: '工作站读取失败', unsaved: '未保存修改', discardChanges: '当前修改尚未保存，确定放弃？',
    replacePreset: '套用预设将替换全部显示器和分区，是否继续？', removeMonitor: '此显示器及其全部分区将被移除，是否继续？',
    closePanel: '关闭浮动面板', monitorMapping: '显示器映射', currentWindow: '当前窗口', regularWindow: '普通新窗口（不定位）',
    monitorFallback: '物理屏不足或定位权限不可用，请核对映射；未映射的逻辑屏将打开普通新窗口。',
    monitorDuplicates: '多个逻辑屏映射到同一物理屏，窗口可能重叠。',
    returnedUnack: '已恢复·待确认', activeUnack: '活动·未确认', activeAcked: '活动·已确认', returnedCount: '恢复待确认 {0}'
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
    returned: '恢复未确认', protection: '抑制 / 暂挂', review: '查看处理', flood: '报警泛滥',
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
  }
}

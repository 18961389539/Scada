export default {
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

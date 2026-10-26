export default {
  traceability: {
    title: '生产追溯', keywords: '生产追溯 SN 二维码 扫码 电流 电压 traceability barcode QR',
    records: '追溯查询', scan: '扫码绑定', config: '任务配置', refresh: '刷新', snQuery: '扫描或输入 SN',
    allJobs: '全部任务', from: '开始时间', to: '结束时间', query: '查询', empty: '暂无记录',
    job: '采集任务', time: '采集时间', ack: 'PLC 应答', acknowledged: '已回复 2', awaitingReply: '已入库，等待应答',
    fieldName: '字段名称', value: '采集值', unit: '单位', scanSn: '待采集产品 SN', bind: '绑定 SN',
    state: '状态', pendingSn: '待采集 SN', expires: '有效期至', reply: '应答值', clearScan: '清除待采集 SN',
    noScanJobs: '暂无可扫码的已启用任务', add: '新增任务', edit: '编辑任务', trigger: '检测地址', snSource: 'SN 来源',
    plc: 'PLC 寄存器', webScan: '网页扫码', enabled: '启用', yes: '是', no: '否', device: 'PLC 设备',
    snFormat: 'SN 格式', numeric: '数值', asciiLow: 'ASCII（低字节在前）', asciiHigh: 'ASCII（高字节在前）',
    snRegisters: 'SN 寄存器（按编码顺序选择）', scanExpiry: '扫码有效期（秒）', fields: '采集字段', addField: '添加字段',
    tag: '采集点位', remove: '移除', cancel: '取消', save: '保存', required: '请填写任务、检测地址、SN 来源和采集字段。',
    loadFailed: '任务或点位加载失败', queryFailed: '追溯记录查询失败', scanFailed: '扫码绑定操作失败', saveFailed: '任务保存失败',
    states: { idle: '等待触发', ready: 'SN 已就绪', capturing: '采集中', awaitingReply: '等待 PLC 应答', completed: '采集完成', failed: '采集失败', error: '异常', offline: '设备离线' }
  },
  flow: {
    fit: '适应', autoLayout: '自动布局', deleteEdge: '删除连线', then: '是', else: '否', fail: '失败',
    hintEdit: '拖动节点移动；从节点下方圆点拖到目标节点连线；点选连线后可删除；滚轮缩放，拖动空白处平移。虚线为“顺延下一步”。',
    hintRun: '实时显示运行中各步状态：黄色执行中、绿色完成、红色失败、淡色跳过。',
    port: { next: '下一步', then: '条件成立', else: '条件不成立', branch: '并行分支', join: '汇合步骤', target: '跳转目标', fail: '失败转到' }
  }
}

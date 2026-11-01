export default {
  uxShell: {
    notFoundTitle: '页面不存在或已被移除', notFoundBody: '地址可能有误，或该页面已随版本更新调整。可以返回总览，也可以用命令面板搜索目标页面。',
    notFoundBack: '返回总览', notFoundSearch: '打开命令面板',
    deniedMessage: '当前账号无权访问 {0}，已返回可访问的首页。'
  },
  overviewUx: {
    tagsFailed: '测点目录读取失败',
    tagsRecovery: '报警来源与数值单位可能不完整；点击右上角刷新可重试。'
  },
  trendsUx: {
    recentTags: '最近使用', allTags: '全部测点',
    limitReached: '已达 50 个测点上限，先移除再添加'
  },
  tagsUx: {
    batchCreatedFiltered: '已创建 {0} 个测点，列表已筛选到 {1}'
  },
  loginUx: {
    capsLockOn: '大写锁定已开启'
  },
  designerUx: {
    documentUnreadable: '画面数据不完整（缺少图元列表），已停止打开以免覆盖原内容。请联系管理员从版本历史恢复。'
  },
  alarmsUx: {
    stopBatch: '停止',
    stopping: '正在停止…（等待当前这条返回）',
    batchStopped: '已停止：还有 {0} 条未确认，可重新勾选后再确认。'
  },
  batchUx: {
    stopBatch: '停止',
    stopping: '正在停止…（等待当前这条返回）',
    stoppedOnly: '已停止：完成 {0}/{1} 条，还有 {2} 条未处理。',
    stoppedSuffix: '已停止：还有 {0} 条未处理，可重新勾选后继续。',
    unitItem: '条'
  },
  controlUx: {
    close: '关闭',
    rerun: '重新执行',
    runFailedAt: '步骤 {0}「{1}」失败',
    runAbortedHeadline: '运行已被中止',
    runExecutedCount: '已完成 {0} 步：{1}',
    runNotExecutedCount: '未执行 {0} 步：{1}',
    runReason: '原因：{0}',
    runReasonUnknown: '没有记录具体原因，请展开下方各步骤状态查看。',
    runAdviceFailed: '已执行步骤对应的设备可能已被改变，且不会自动回退。请先核对现场状态，确认安全后再重新执行；无法确认时先挂牌停用。',
    runAdviceAborted: '已中止的运行不会自动回退已执行的步骤。请核对现场状态后再决定是否重新执行。'
  },
  commandUx: {
    outcomeUnknown: '操作结果未知：请求已发出，但没收到服务器回执。请先核对现场设备状态或测点当前值（网关操作可查「命令回执」），确认没有生效再重发。',
    checkFailed: '预检查没有完成，命令未下发：{0}',
    recipeOutcomeUnknown: '配方下发结果未知：请求已发出，但没收到服务器回执，参数可能已开始写入。请先关闭此窗口，在本页「下发记录」里核对是否已有这次下发，确认没有再重新检查并下发。',
    jogReleaseUnknown: '点动松开没有得到确认：设备可能仍在动作。服务端会在该按钮的最长保持时间到达后自动释放；请立即核对现场，必要时就地停机。'
  },
  alarmToast: {
    openHint: '点击查看报警'
  },
  workbench: {
    runtimeTitle: '运行工作台', configurationTitle: '工作台配置', configure: '配置工作台', create: '快速创建工作台', createFromScreen: '用此画面创建工作台',
    emptyTitle: '暂无可运行的已发布工作台', emptyHelp: '画面已发布不代表工作台已配置。选择一个已发布画面，预览并发布工作台后即可运行；也可直接运行单个画面。',
    contactAdmin: '当前账号没有工作台配置权限，请联系管理员创建并发布工作台。', openScreens: '运行单个画面',
    quickTitle: '快速创建工作台', quickHelp: '选择已发布画面即可开始，无需设置网格或登记工作站。画面发布与工作台发布相互独立。',
    chooseScreen: '已发布画面', workbenchName: '工作台名称', chooseTemplate: '展示方式', quickName: '{0} 工作台',
    screenStep: '选择画面', previewStep: '预览效果', publishStep: '审阅并发布', nextPreview: '下一步：预览效果', back: '上一步', saveAndReview: '保存并审阅发布',
    screenLoadFailed: '已发布画面读取失败，请重试。', noPublishedScreens: '暂无已发布画面，请先在画面列表中创建并发布画面。', retry: '重新读取',
    publishHelp: '预览不会执行控制操作。下一步先保存草稿，再核对引用和影响范围、填写说明并确认发布；取消发布会保留草稿，不会改变现有运行工作台。',
    draftSaved: '草稿已保存，尚未发布。请完成发布审阅，也可稍后在工作台配置中继续。', saveFailed: '工作台草稿保存失败，请核对后重试。',
    screenUnavailable: '所选画面不存在或尚未发布，请重新选择已发布画面。', configureDenied: '当前账号没有全局组态配置权限。',
    configHelp: '首次使用建议快速创建：选择画面 → 预览效果 → 审阅并发布。复杂分区和多显示器可使用高级布局编辑；工作站分配为可选操作。',
    advancedEditor: '高级布局编辑', advancedHelp: '此编辑器用于多显示器和精细分区。简单展示可关闭后使用“快速创建工作台”。保存草稿不会更新运行版本。',
    savedHint: '“{0}”草稿已保存，运行版未更新。请预览并发布后使用。', publishNow: '预览并发布',
    previewDetails: '查看布局结构与预览', comparisonDetails: '查看详细变更',
    showAdvanced: '展开高级参数', hideAdvanced: '收起高级参数', basicEditHelp: '先选择区域并设置画面、标题；网格、区域编号和多屏管理按需展开。收起不会清除已有配置。', basicEditMobileHelp: '收起保留已有配置'
  },
  workbenchTips: {
    quickCreate: '从已发布画面快速创建工作台：选择展示方式、预览，再审阅发布；无需登记工位。', advancedCreate: '新建自定义布局草稿，可设置多个显示器和精细分区；不会自动发布。',
    configure: '进入工作台配置，管理布局草稿、发布版本和工作站分配。', openLayout: '运行此布局的已发布版本，不读取未发布草稿，也不修改本机工位分配。', unpublishedLayout: '此布局尚未发布。请先保存草稿并完成发布审阅后再运行。', editDraft: '编辑布局草稿，现有运行版本保持不变。',
    publishReview: '审阅已保存草稿的画面引用和工位影响，填写说明并确认后才会发布。', saveDraft: '仅保存草稿，不更新运行版；支持 Ctrl/⌘+S，未完成的区域可稍后修正。', saveReview: '先保存草稿，再打开发布审阅；取消审阅保留草稿，不会自动发布。',
    copyLayout: '复制为独立新草稿，保留分区和画面绑定；原布局及工位分配不变。', deleteLayout: '确认后删除布局并清除相关工位分配，不删除引用的画面。', history: '查看已发布版本、比较变更或审阅回退；查看历史本身不改变运行版。', more: '查看复制、发布历史和删除等操作。',
    runtimeLayout: '切换查看其他已发布工作台，不修改工作站分配。', targetRegion: '画面目录中的画面在所选区域打开；也可点击画面区域将其设为目标。', monitorOnly: '禁止此浏览器执行画面控制，不改变账号权限或工作站配置。', refreshRuntime: '重新读取画面目录和报警，并检查布局版本；新发布版仍需确认后应用。', openAll: '为其他逻辑显示器选择现场屏幕并打开窗口；浏览器可能要求权限或允许弹窗。', fullscreen: '进入或退出浏览器全屏，不改变布局和目标尺寸。', overview: '返回系统总览，不修改或发布当前工作台。', applyUpdate: '确认后应用新发布版，区域内画面导航将重置；不会自动应用。', openScreen: '直接运行单个已发布画面，无需创建工作台或登记工作站。',
    layoutName: '工作台草稿名称，最多 100 个字符；改名需发布后才更新运行名称。', screenBinding: '只能绑定已发布画面；绑定的是画面引用，不会替你发布画面。', template: '选择模板后点击套用，会确认替换全部显示器和分区；可撤销。', undo: '撤销上一次布局编辑，不撤销已发布版本；支持 Ctrl/⌘+Z。', redo: '重做已撤销的布局编辑；支持 Ctrl/⌘+Shift+Z 或 Ctrl/⌘+Y。', advanced: '按需显示网格、区域编号、目标尺寸和多屏管理；收起不会清除配置。',
    monitorName: '逻辑显示器名称，最多 40 个字符；不是现场物理屏幕编号。', gridColumns: '内容网格列数，范围 1～24；缩小导致越界时会先列出影响并确认处理方式。', gridRows: '内容网格行数，范围 1～24；固定报警横幅不占内容网格。', targetSize: '用于保存和预览的目标窗口尺寸，默认 1920×1080；不限制现场浏览器大小。',
    regionId: '当前显示器内唯一的区域编号，不能为空，最多 40 个字符；用于识别运行区域。', regionKind: '选择画面、画面目录或报警列表；报警横幅始终固定在顶部。', regionTitle: '区域显示标题，最多 60 个字符；不改变画面本身的名称。', regionRow: '区域起始行，从 1 开始；区域必须位于内容网格内。', regionColumn: '区域起始列，从 1 开始；区域不得与其他区域重叠。', regionRowSpan: '区域占用的连续行数，至少为 1；不能超过网格底边。', regionColumnSpan: '区域占用的连续列数，至少为 1；不能超过网格右边。',
    addRegion: '在当前显示器的空网格中添加区域，最多 16 个；没有空位时不可添加。', copyRegion: '复制当前区域的类型、画面绑定、标题和尺寸，可切换显示器后粘贴。', pasteRegion: '粘贴复制时的区域快照，自动使用唯一编号并寻找空位，不挤占已有区域。', duplicateRegion: '在当前显示器中复制此区域，保留尺寸并使用唯一编号；可撤销。', deleteRegion: '删除所选区域，可撤销；每个显示器至少保留一个区域。',
    addMonitor: '新增逻辑显示器，最多 8 个；无需立即连接或配置物理屏幕。', copyMonitor: '复制当前显示器的区域、画面绑定和目标尺寸；可撤销。', orderMonitor: '调整逻辑显示器顺序，决定运行页的显示器编号；保存并发布后生效。', deleteMonitor: '确认后移除当前显示器及全部区域，可撤销；不删除引用的画面。',
    stationName: '工位标识须为 1～60 个字母、数字、点、下划线或连字符。', stationDescription: '工位用途或位置说明，最多 200 个字符，不作为绑定标识。', stationLayout: '分配已发布工作台；清空可取消分配，草稿不可分配给运行工位。', stationControl: '关闭后服务端拒绝该工位的控制请求；开启仍受账号及区域权限限制。', localStation: '仅登记到本浏览器；之后进入运行工作台使用该工位的已发布布局，不修改工位分配。', clearLocal: '只清除此浏览器的工位登记，不删除服务器上的工位或分配。', enterLocal: '进入本浏览器登记工位所分配的已发布工作台；须先完成有效登记和分配。',
    search: '按草稿名称或运行名称搜索，可与发布状态及工位使用筛选组合。', statusFilter: '区分已发布、未发布和草稿有变更；草稿变更不会自动更新运行版。', usageFilter: '按是否分配工作站筛选；工位读取失败时不可判断使用情况。',
    publishNote: '记录本次发布或回退的原因，不能为空；说明将保存在版本历史中。', impactRefresh: '重新读取工位分配和控制权限，不会发布、回退或更改分配。', compare: '仅切换变更比较基准，不修改草稿或运行版。', rollback: '回退会生成新的发布版本，保留当前草稿；须核对引用和影响并确认。', acknowledge: '确认此条报警，不消除现场故障，也不确认其他报警。',
    detach: '将当前画面在新窗口运行，可选择物理显示器；不会删除原区域。', historyBack: '在此区域的画面历史中后退，不影响其他区域。', historyForward: '在此区域的画面历史中前进，不影响其他区域。', historyHome: '返回此区域最初绑定的画面，不清除其他区域的导航。',
    previewMonitor: '切换只读预览的逻辑显示器，不改变布局内容。', previewResolution: '仅改变当前预览视口，不改动已保存的目标尺寸，也不会执行控制。', alignRegion: '仅移动当前区域到对应的网格位置，保留尺寸及画面绑定；越界或重叠时不可用。', divideColumns: '按区域顺序左右均分当前网格，保留区域类型及绑定；可撤销。', divideRows: '按区域顺序上下均分当前网格，保留区域类型及绑定；可撤销。',
    issue: '点击切换到问题所在显示器，自动展开隐藏参数并定位相关字段。', restore: '恢复本浏览器暂存的编辑，不直接写服务器；服务器已变化时需确认恢复为新草稿。', discardLocal: '确认后删除本浏览器的这份暂存，不删除服务器布局或已发布版本。',
    mapMonitor: '选择逻辑显示器的物理屏幕，映射仅记在本浏览器；不可用时可选择普通窗口。', openMonitor: '为这个逻辑显示器打开运行窗口；需允许浏览器弹窗。', focusMonitor: '将已打开的运行窗口带到前台；窗口未打开时不可用。'
  },
  screenVersions: {
    title: '发布历史', versions: '历史发布版本', refresh: '刷新历史', retry: '重试', close: '关闭',
    historyHint: '按发布修订号从新到旧列出。回退只改变运行版本并生成新的发布修订，不替换当前草稿。',
    current: '当前运行版本', restoredFrom: '回退来源：r{0}', empty: '暂无发布历史', loadOlder: '加载更早版本',
    listFailed: '发布历史读取失败，请重试。', detailFailed: '版本详情读取失败，请重试。', rollbackFailed: '回退失败，请核对原因后重试。',
    preview: '只读预览此版本', hidePreview: '收起预览', previewHint: '使用该次发布冻结的画面与符号预览，不执行控制操作；实时数据仍为当前数据，跳转和弹窗读取目标当前发布版本并保持只读。',
    legacyBlocked: '该历史版本未固化发布时的模板或符号，无法可靠还原。禁止预览和直接回退，不会使用当前图库猜测历史内容；请在设计器核对草稿并重新发布。',
    dirtyBlocked: '当前设计器有未保存编辑，仍可查看历史，但不能回退。请先自行保存或处理编辑；不会自动保存或丢弃。',
    conflict: '版本冲突，未回退。请关闭历史并重新加载设计器，核对服务器草稿后再操作；不会自动重试或覆盖本地编辑。',
    rollbackEffect: '将运行画面回退到“{1}”(r{0})，并创建递增的新发布版本。草稿名称、内容及撤销记录保持不变；服务器会重新校验测点、导航和 SVG。',
    note: '回退说明', noteHint: '必填，去除首尾空白后 1–500 字；当前 {0} 字。', noteRequired: '请输入非空的回退说明。', noteTooLong: '回退说明不能超过 500 字。',
    acknowledge: '我确认切换运行版本并生成新发布记录，草稿不会随之更改。', confirmRollback: '确认回退运行版本',
    diffTitle: '与当前草稿的差异', diffNone: '与当前草稿没有差异', diffSummary: '{0} 个图元有差异', diffMore: '另有 {0} 个图元…',
    diffAdded: '仅当前草稿有', diffRemoved: '仅此版本有', diffGeometry: '位置/尺寸', diffText: '文字/字号', diffStyle: '颜色', diffData: '数据绑定', diffAction: '动作', diffStructure: '类型/结构',
    rollbackSuccess: '已回退运行版本，草稿未更改', kind_publish: '发布', kind_rollback: '回退', kind_import: '导入', kind_migration: '迁移'
  },
  hmiTemplateEditing: {
    description: '内置符号采用自研 ISA-101 风格；用户符号仅允许静态 SVG，导入时清洗并由服务端复核。模板通过占位符绑定实例测点，控制动作仍走控制闭环。发布时冻结所用模板和符号；图库修改仅在画面后续重新发布后影响运行画面。图库随工程包和配置快照保存。',
    bindingHint: '把图元、报警及命令绑定到占位符。图库修改仅影响后续重新发布的画面。',
    placeholderInUse: '占位符“{0}”仍被图元、报警、命令或其他测点配置使用，请先解除或更换绑定后再删除。',
    invalidBinding: '图元“{0}”的绑定“{1}”无效或不支持占位符，未保存。请重新选择有效占位符，或清除该测点配置后重试。',
    missingDocument: '模板缺少画面文档，无法编辑。',
    savedAffected: '模板已保存。以下画面需重新发布后才会更新运行版本：{0}'
  },
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
    setupScope: '按所选设备验证保存、连接、测点质量与报警规则；画面发布检查为当前账号可见的站点范围。状态来自实际数据，不使用人工勾选。',
    checkedAt: '检查于 {0}', done: '已验证', pending: '待完成', unknown: '待验证',
    deviceSaved: '设备配置已保存', deviceSavedHint: '保存配置不等于连接成功；请继续建立测点并验证采集。',
    tagsSaved: '测点配置已保存', tagsSavedHint: '所选设备已配置 {0} 个物理测点。',
    connected: '设备采集已连接', connectedHint: '启用设备并至少配置一个测点后，再验证采集连接；不会自动发送测试或重连指令。',
    values: '测点数据有效', valuesHint: 'Good 测点 {0} / 配置测点 {1}。全部测点必须有有效且未过期的数据。',
    alarmRulesConfigured: '报警规则已配置', alarmRulesHint: '所选设备的测点已有 {0} 条启用规则。',
    alarmRulesPendingHint: '为测点配置至少一条启用的报警规则；停用规则不计入完成。',
    alarmRulesFailed: '报警规则读取失败，请重新验证。',
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
    enableStates: '添加常用状态', feedback: '反馈', noCommands: '未配置操作', stateColor: '状态颜色',
    jog: '点动按钮', pulse: '瞬时按钮', selector: '多位置选择开关', knob: '旋钮', reset: '复位按钮', silence: '消音按钮',
    pressHold: '按住保持，松开释放', pulseHint: '点击后按脉冲宽度自动释放',
    confirm: '确定', cancel: '取消', ackDenied: '当前账号没有确认报警权限',
    resetMode: '复位方式', resetModeAlarms: '确认可见报警', resetModeTag: '复位输出测点',
    resetHint: '确认报警按当前账号可见范围批量确认；复位输出测点按固定值走控制闭环下发。',
    resetConfirm: '确认复位当前可见报警？所有未确认的可见报警都会被确认。', resetDone: '已确认 {0} 条报警（共 {1} 条）', resetFailed: '复位报警失败', resetNote: '画面复位按钮',
    silenceMinutes: '消音时长（分钟）', silenceHint: '对所有可见的已启用声光面板消音，到时自动恢复。',
    silenceConfirm: '确认对当前可见声光面板消音 {0} 分钟？', silenceDone: '已消音 {0} 个面板（共 {1} 个）', silenceFailed: '声光消音失败', silenceNote: '画面消音按钮',
    releaseValue: '释放值', maxHoldMs: '最长保持（毫秒）', pulseMs: '脉冲宽度（毫秒）',
    momentaryHint: '按下走完整控制检查；松开直接写入释放值并跳过联锁，服务端看门狗超时兜底释放。',
    jogReleaseFailed: '点动释放失败，服务端将在最长保持时间后自动释放',
    positionsHint: '每个位置对应一个控制值，运行画面按反馈值就近显示当前位置；支持 2 至 8 个位置。',
    recipe: '配方选择', recipeTemplate: '配方模板', recipePickTemplate: '请选择配方模板', recipeControlHint: '运行画面从该模板下的配方中选择后下发；需具备配方区域及全部参数测点的控制权限。',
    recipePick: '选择配方', recipeDownload: '下发', recipeEmpty: '该模板暂无配方', recipeUnavailable: '配方不可用', recipeDenied: '无配方下发权限', recipeDual: '双人复核',
    recipeDualHint: '该配方启用双人复核，提交后需他人审批。', recipeLoadFailed: '配方数据加载失败', recipeCheckFailed: '配方检查失败', recipeDownloadTitle: '配方下发',
    recipeParam: '参数', recipeTag: '控制测点', recipeChange: '当前 → 目标', recipeCheck: '检查结果', recipeReady: '可下发',
    recipePending: '配方下发待审批', recipeCompleted: '配方下发完成', recipeFailed: '配方下发失败', recipeUnfinished: '配方下发未完成：{0}',
    recipeAction: '配方下发', recipeActionHint: '点击后打开配方下发确认；下发走配方控制权限与双人复核。',
    sequence: '顺控流程', sequencePick: '请选择顺控流程', sequenceActionHint: '点击后确认启动顺控；启动走控制闭环审批与审计。',
    sequenceConfirm: '确认启动顺控“{0}”？共 {1} 步，启动后按流程逐步执行。', sequencePending: '顺控启动待审批', sequenceStarted: '顺控已启动（运行 #{0}）', sequenceFailed: '顺控启动失败',
    sequenceUnavailable: '顺控流程不可用', sequenceDenied: '无该顺控区域的控制权限', sequenceLoadFailed: '顺控流程加载失败',
    trend: '趋势', trendUnavailable: '历史趋势暂不可用', trendEmpty: '暂无历史数据', trendResetZoom: '重置缩放', trendHint: '滚轮缩放、拖动平移；十字线显示各序列同一时刻的值。',
    trendTag: '测点', trendLatest: '最新值', trendUnit: '单位', tableDrillHint: '双击查看该测点趋势',
    issueRecipeTemplate: '配方控件必须选择配方模板', issueRecipeTemplateMissing: '所选配方模板已不存在',
    issueActionSequence: '顺控动作必须选择顺控流程', issueActionSequenceMissing: '所选顺控流程已不存在',
    issueActionRecipe: '配方动作必须选择配方', issueActionRecipeMissing: '所选配方已不存在'
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
  },
  shortcuts: {
    title: '键盘快捷键',
    hint: '按 ? 可随时打开，Esc 关闭',
    global: '全局',
    alarms: '报警列表',
    cmdPalette: '打开命令面板',
    help: '打开快捷键一览',
    closeLayer: '关闭弹层 / 取消行聚焦',
    alarmDown: '聚焦下一条报警',
    alarmUp: '聚焦上一条报警',
    alarmTimeline: '查看该报警时间线',
    alarmAck: '确认该报警（实时页，弹出确认框）',
    alarmNote: '为该报警添加备注（实时页）',
    alarmSelect: '勾选 / 取消勾选（实时页，用于批量确认）',
    alarmSearch: '跳到搜索框',
    openHint: '键盘快捷键（?）',
    alarmsTip: '↑↓ 选择 · A 确认 · T 时间线 · ? 全部快捷键',
    historyTip: '↑↓ 选择 · T 时间线 · ? 全部快捷键'
  }
}

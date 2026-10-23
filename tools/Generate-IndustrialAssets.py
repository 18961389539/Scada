"""Generate icons and language dictionaries for the extended industrial component set.

Run after editing tools/Generate-IndustrialDemo.py style catalogs. The script is
idempotent: existing phrase values are preserved, missing entries are added.
"""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PLUGIN = ROOT / 'ScadaWeb/Mimics/PlgMimBasicComp'
IMAGES = PLUGIN / 'wwwroot/plugins/MimBasicComp/images'
LANG = PLUGIN / 'lang'
PREFIX = 'Scada.Web.Plugins.PlgMimBasicComp.Js.'

GROUPS = [
    ('Electrical', '电气单线图', 'Electrical single-line', 'Электрические схемы'),
    ('Shapes', '图形与管道', 'Shapes & pipes', 'Фигуры и трубопроводы'),
    ('Displays', '数据显示', 'Displays', 'Отображение данных'),
    ('Inputs', '操作输入', 'Inputs', 'Элементы управления'),
    ('Equipment', '工业设备', 'Equipment', 'Оборудование'),
    ('Alarms', '报警与状态', 'Alarms & status', 'Тревоги и состояния'),
    ('Layout', '布局与装饰', 'Layout & decoration', 'Компоновка и оформление'),
    ('Operations', '运行与操作流程', 'Runtime workflows', 'Рабочие процессы'),
]

PROPERTIES = [
    ('OpenValue', '分位反馈值', 'Open feedback value', 'Значение открытого положения'),
    ('ClosedValue', '合位反馈值', 'Closed feedback value', 'Значение закрытого положения'),
    ('TripValue', '跳闸反馈值', 'Trip feedback value', 'Значение отключения'),
    ('TripChannel', '跳闸反馈通道', 'Trip feedback channel', 'Канал отключения'),
    ('EnergizedChannel', '独立带电状态通道', 'Independent energization channel', 'Канал напряжения'),
    ('EnergizedValue', '带电反馈值', 'Energized feedback value', 'Значение под напряжением'),
    ('DeenergizedValue', '无电反馈值', 'Deenergized feedback value', 'Значение без напряжения'),
    ('PreviewEnergized', '编辑预览带电', 'Energized in editor preview', 'Предпросмотр напряжения'),
    ('ShowLabel', '显示标题', 'Show title', 'Показывать название'),
    ('ShowState', '显示状态文字', 'Show state text', 'Показывать состояние'),
    ('LabelPosition', '文字位置（right/left/top/bottom）', 'Label position (right/left/top/bottom)', 'Положение текста'),
    ('Route', '接线形状（straight/elbow/tee）', 'Line route (straight/elbow/tee)', 'Форма линии'),
    ('ShowJunction', '显示连接点', 'Show junction dot', 'Показывать соединение'),
    ('EnergizedColor', '带电颜色', 'Energized color', 'Цвет под напряжением'),
    ('DeenergizedColor', '无电颜色', 'Deenergized color', 'Цвет без напряжения'),
    ('UnknownColor', '未知颜色', 'Unknown color', 'Цвет неизвестного состояния'),
    ('OpenColor', '分闸颜色', 'Open color', 'Цвет открытого положения'),
    ('ClosedColor', '合闸颜色', 'Closed color', 'Цвет закрытого положения'),
    ('TripColor', '跳闸颜色', 'Trip color', 'Цвет отключения'),
    ('ExpectedValues', '条件期望值列表', 'Expected condition values', 'Ожидаемые значения'),
    ('PermitChannel', '控制器最终许可通道', 'Controller permit channel', 'Канал разрешения'),
    ('FaultChannel', '故障代码通道', 'Fault code channel', 'Канал кода ошибки'),
    ('FaultDefinitions', '故障说明（JSON）', 'Fault definitions (JSON)', 'Описания ошибок (JSON)'),
    ('PolicyId', '服务端复位策略编号', 'Server reset policy ID', 'Политика сброса'),
    ('Label', '标题', 'Label', 'Заголовок'),
    ('Value', '数值 / 预览值', 'Value / preview', 'Значение / предпросмотр'),
    ('Minimum', '最小值', 'Minimum', 'Минимум'),
    ('Maximum', '最大值', 'Maximum', 'Максимум'),
    ('Unit', '单位', 'Unit', 'Ед. изм.'),
    ('Decimals', '小数位数', 'Decimal places', 'Знаков после запятой'),
    ('ActiveColor', '运行颜色', 'Active color', 'Цвет активности'),
    ('IdleColor', '停止颜色', 'Idle color', 'Цвет простоя'),
    ('AlarmColor', '报警颜色', 'Alarm color', 'Цвет аварии'),
    ('Threshold', '运行判定阈值', 'Active threshold', 'Порог активности'),
    ('LineWidth', '线条宽度', 'Line width', 'Толщина линии'),
    ('FillColor', '填充颜色', 'Fill color', 'Цвет заливки'),
    ('Vertical', '垂直方向', 'Vertical', 'Вертикально'),
    ('Animate', '启用动画', 'Animation', 'Анимация'),
    ('Channels', '通道列表（逗号分隔）', 'Channels (comma separated)', 'Каналы (через запятую)'),
    ('Labels', '名称列表（逗号分隔）', 'Labels (comma separated)', 'Подписи (через запятую)'),
    ('HistoryMinutes', '历史时长（分钟）', 'History minutes', 'Глубина истории (мин)'),
    ('ArchiveBit', '归档位号', 'Archive bit', 'Бит архива'),
    ('MaxRows', '最大行数', 'Maximum rows', 'Макс. строк'),
    ('Options', '选项（名称=值;…）', 'Options (label=value;...)', 'Варианты (имя=значение;...)'),
    ('Step', '步长', 'Step', 'Шаг'),
    ('Target', '目标值', 'Target value', 'Целевое значение'),
    ('RoleFilter', '可见角色（all / admin）', 'Visible for role (all / admin)', 'Видимость по роли (all / admin)'),
    ('NumberFormat', '数字格式（plain / thousand / compact）', 'Number format (plain / thousand / compact)', 'Формат чисел (plain / thousand / compact)'),
    ('IntegratePower', '按功率归档积分', 'Integrate power archive', 'Интеграл мощности'),
    ('Units', '各曲线单位', 'Series units', 'Единицы рядов'),
    ('Minimums', '各曲线最小值', 'Series minima', 'Минимумы рядов'),
    ('Maximums', '各曲线最大值', 'Series maxima', 'Максимумы рядов'),
    ('ViewIds', '导航视图编号列表', 'View IDs', 'Номера представлений'),
    ('ViewId', '目标视图编号', 'Target view ID', 'Целевое представление'),
    ('FeedbackChannel', '反馈通道', 'Feedback channel', 'Канал обратной связи'),
    ('SetpointChannel', '设定输出通道', 'Setpoint output', 'Канал уставки'),
    ('ProcessChannel', '过程值通道', 'Process value channel', 'Канал процесса'),
    ('OutputChannel', '控制输出反馈通道', 'Control output feedback', 'Обратная связь выхода'),
    ('ModeChannel', '模式反馈通道', 'Mode feedback', 'Канал режима'),
    ('ModeOutputChannel', '模式输出通道', 'Mode output', 'Управление режимом'),
    ('StatusChannel', '设备在线状态通道', 'Device online channel', 'Канал связи'),
    ('StaleSeconds', '读取过期秒数', 'Read timeout seconds', 'Срок актуальности'),
    ('Tabs', '选项卡名称', 'Tab names', 'Названия вкладок'),
    ('Collapsed', '初始折叠', 'Initially collapsed', 'Свёрнуто изначально'),
]

TYPES = [
    ('CircuitBreaker', '断路器', 'Circuit breaker', 'Автоматический выключатель'),
    ('Disconnector', '隔离开关', 'Disconnector', 'Разъединитель'),
    ('GroundSwitch', '接地开关', 'Ground switch', 'Заземляющий выключатель'),
    ('Transformer', '变压器', 'Transformer', 'Трансформатор'),
    ('Busbar', '母线', 'Busbar', 'Шина'),
    ('ElectricalLine', '电气连接线', 'Electrical connection', 'Электрическая линия'),
    ('InterlockPanel', '联锁与操作许可', 'Interlock and operation permit', 'Блокировки и разрешения'),
    ('FaultDiagnostics', '故障诊断面板', 'Fault diagnostics', 'Диагностика неисправностей'),
    ('AlarmStatistics', '报警统计与排行', 'Alarm statistics', 'Статистика тревог'),
    # key, zh, en, ru
    ('Line', '直线', 'Line', 'Линия'),
    ('Rectangle', '矩形', 'Rectangle', 'Прямоугольник'),
    ('Ellipse', '圆形', 'Ellipse', 'Эллипс'),
    ('Arrow', '箭头', 'Arrow', 'Стрелка'),
    ('Pipe', '管道', 'Pipe', 'Трубопровод'),
    ('Tee', '三通', 'Tee', 'Тройник'),
    ('Elbow', '弯头', 'Elbow', 'Отвод'),
    ('DoubleArrow', '双向箭头', 'Double arrow', 'Двусторонняя стрелка'),
    ('Flange', '法兰', 'Flange', 'Фланец'),
    ('Gauge', '仪表盘', 'Gauge', 'Индикатор'),
    ('Progress', '进度条', 'Progress bar', 'Индикатор выполнения'),
    ('Level', '液位计', 'Level meter', 'Уровнемер'),
    ('Trend', '趋势图', 'Trend chart', 'График тренда'),
    ('DataTable', '数据表格', 'Data table', 'Таблица данных'),
    ('Thermometer', '温度计', 'Thermometer', 'Термометр'),
    ('PressureGauge', '压力表', 'Pressure gauge', 'Манометр'),
    ('FlowMeter', '流量计', 'Flow meter', 'Расходомер'),
    ('EnergyMeter', '电能表', 'Energy meter', 'Счётчик энергии'),
    ('BarChart', '柱状图', 'Bar chart', 'Столбчатая диаграмма'),
    ('PieChart', '饼图', 'Pie chart', 'Круговая диаграмма'),
    ('NumberInput', '数值输入框', 'Number input', 'Ввод числа'),
    ('Select', '下拉框', 'Dropdown', 'Раскрывающийся список'),
    ('Slider', '滑块', 'Slider', 'Ползунок'),
    ('Checkbox', '复选框', 'Checkbox', 'Флажок'),
    ('RadioGroup', '单选组', 'Radio group', 'Группа переключателей'),
    ('ButtonGroup', '按钮组', 'Button group', 'Группа кнопок'),
    ('Pump', '水泵', 'Pump', 'Насос'),
    ('Valve', '阀门', 'Valve', 'Клапан'),
    ('Fan', '风机', 'Fan', 'Вентилятор'),
    ('Motor', '电机', 'Motor', 'Двигатель'),
    ('Tank', '储罐', 'Tank', 'Резервуар'),
    ('HeatExchanger', '换热器', 'Heat exchanger', 'Теплообменник'),
    ('Compressor', '压缩机', 'Compressor', 'Компрессор'),
    ('Boiler', '锅炉', 'Boiler', 'Котёл'),
    ('Conveyor', '传送带', 'Conveyor', 'Конвейер'),
    ('Filter', '过滤器', 'Filter', 'Фильтр'),
    ('CoolingTower', '冷却塔', 'Cooling tower', 'Градирня'),
    ('AlarmList', '报警列表', 'Alarm list', 'Список тревог'),
    ('StatusGroup', '状态灯组', 'Status lamps', 'Группа индикаторов'),
    ('AlarmBanner', '报警横幅', 'Alarm banner', 'Лента тревог'),
    ('StatusBadge', '状态牌', 'Status badge', 'Индикатор состояния'),
    ('KpiCard', '指标卡', 'KPI card', 'Карточка KPI'),
    ('GroupBox', '分组框', 'Group box', 'Группа'),
    ('Divider', '分隔线', 'Divider', 'Разделитель'),
    ('EmergencyStop', '急停按钮', 'Emergency stop', 'Аварийный стоп'),
    ('KeySwitch', '钥匙开关', 'Key switch', 'Ключевой переключатель'),
    ('SelectorSwitch', '选择开关', 'Selector switch', 'Переключатель режимов'),
    ('AlarmAckButton', '报警确认', 'Alarm acknowledge', 'Квитирование тревог'),
    ('StackLight', '报警灯柱', 'Stack light', 'Световая колонна'),
    ('ControlValve', '调节阀', 'Control valve', 'Регулирующий клапан'),
    ('TrendMulti', '多通道趋势', 'Multi-channel trend', 'Многоканальный тренд'),
    ('NavBar', '页面导航栏', 'Page navigation', 'Навигация'),
    ('NavButton', '导航按钮', 'Navigation button', 'Переход'),
    ('BackButton', '返回按钮', 'Back button', 'Назад'),
    ('DevicePanel', '设备操作面板', 'Device operation panel', 'Панель устройства'),
    ('ActiveAlarmTable', '当前报警表', 'Active alarms', 'Активные тревоги'),
    ('HistoryQuery', '历史查询工具条', 'History query', 'Запрос истории'),
    ('CommStatus', '通信状态卡', 'Communication status', 'Состояние связи'),
    ('Tabs', '选项卡容器', 'Tabs container', 'Вкладки'),
    ('CollapsiblePanel', '折叠容器', 'Collapsible container', 'Сворачиваемая панель'),
    ('OperationLog', '操作记录表', 'Operation log', 'Журнал операций'),
    ('Recipe', '配方与参数组', 'Recipe parameters', 'Рецепты'),
    ('Schedule', '计划运行', 'Scheduled operation', 'Расписание'),
    ('PidPanel', 'PID 回路面板', 'PID loop panel', 'Панель ПИД'),
]

ICONS = {
    'CircuitBreaker': '<path d="M12 1 V7 M12 17 V23"/><rect x="7" y="7" width="10" height="10"/><path d="M12 7 V17"/>',
    'Disconnector': '<path d="M12 1 V7 M12 17 V23 M12 17 L18 8"/><circle cx="12" cy="7" r="1"/>',
    'GroundSwitch': '<path d="M12 1 V7 M12 17 L18 8 M12 17 V20 M5 20 H19 M8 23 H16"/>',
    'Transformer': '<path d="M12 1 V5 M12 19 V23"/><circle cx="12" cy="9" r="4"/><circle cx="12" cy="15" r="4"/>',
    'Busbar': '<path d="M2 10 H22 M2 14 H22 M7 12 V22 M17 12 V22"/>',
    'ElectricalLine': '<path d="M2 12 H22 M12 12 V22"/><circle cx="12" cy="12" r="2"/>',
    'InterlockPanel': '<rect x="3" y="4" width="18" height="16" rx="2"/><path d="M6 9 L8 11 L11 7 M6 15 H11 M15 8 V16 M13 12 H17"/>',
    'FaultDiagnostics': '<path d="M12 3 L22 20 H2 Z M12 9 V14 M12 17 V18"/>',
    'AlarmStatistics': '<path d="M3 3 V21 H22 M7 17 V12 H10 V17 M13 17 V8 H16 V17 M19 17 V5 H22 V17"/>',
    'NavBar': '<rect x="2" y="7" width="20" height="10" rx="2"/><path d="M8 7 V17 M15 7 V17"/>',
    'NavButton': '<rect x="2" y="6" width="20" height="12" rx="2"/><path d="M7 12 H17 M13 8 L17 12 L13 16"/>',
    'BackButton': '<path d="M10 6 L4 12 L10 18 M4 12 H19"/>',
    'DevicePanel': '<rect x="2" y="3" width="20" height="18" rx="2"/><circle cx="8" cy="10" r="3"/><path d="M14 7 H18 M14 11 H18 M6 17 H18"/>',
    'ActiveAlarmTable': '<path d="M5 4 H21 V21 H5 Z M5 10 H21 M12 10 V21 M1 4 V10 M1 14 V15"/>',
    'HistoryQuery': '<circle cx="10" cy="10" r="7"/><path d="M10 6 V10 H14 M15 15 L21 21"/>',
    'CommStatus': '<path d="M2 9 Q12 0 22 9 M6 13 Q12 7 18 13 M10 17 Q12 15 14 17"/><circle cx="12" cy="21" r="1"/>',
    'Tabs': '<path d="M2 8 H22 V21 H2 Z M2 8 V3 H9 V8 M9 4 H16 V8"/>',
    'CollapsiblePanel': '<rect x="2" y="3" width="20" height="18" rx="2"/><path d="M2 9 H22 M8 13 L12 17 L16 13"/>',
    'OperationLog': '<rect x="4" y="3" width="16" height="18" rx="2"/><path d="M8 8 H16 M8 12 H16 M8 16 H13"/>',
    'Recipe': '<path d="M5 3 H19 V21 H5 Z M8 7 H16 M8 12 H16 M8 17 H12"/><circle cx="15" cy="17" r="2"/>',
    'Schedule': '<rect x="2" y="5" width="20" height="17" rx="2"/><path d="M2 10 H22 M7 2 V7 M17 2 V7 M8 15 H12 V19"/>',
    'PidPanel': '<path d="M2 20 H22 M3 16 C7 16 5 5 10 8 S16 15 21 8"/><path d="M3 9 H21" stroke-dasharray="2 2"/>',
    'Tee': '<path d="M2 9 H22 M12 9 V22"/>',
    'Elbow': '<path d="M2 12 H12 V22"/>',
    'DoubleArrow': '<path d="M6 12 H18 M6 12 L9 9 M6 12 L9 15 M18 12 L15 9 M18 12 L15 15"/>',
    'Flange': '<path d="M2 12 H22 M8 6 V18 M16 6 V18"/>',
    'Thermometer': '<path d="M10 4 A2 2 0 0 1 14 4 V14 A4 4 0 1 1 10 14 Z M12 8 V16"/><path d="M16 6 H18 M16 10 H18 M16 14 H18"/>',
    'PressureGauge': '<circle cx="12" cy="12" r="8"/><path d="M12 12 L16 8 M12 4 V6 M20 12 H18 M12 20 V18 M4 12 H6 M6 6 L7.4 7.4"/>',
    'FlowMeter': '<circle cx="12" cy="12" r="8"/><path d="M12 12 C9 8 15 7 13 11 Z M12 12 C16 10 16 15 13 13 Z M12 12 C13 16 9 15 11 13 Z"/>',
    'EnergyMeter': '<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M7 10 H11 M13 10 H17 M7 14 H15"/>',
    'BarChart': '<path d="M3 20 H21"/><path d="M6 20 V12 M11 20 V7 M16 20 V10"/>',
    'PieChart': '<circle cx="12" cy="12" r="8"/><path d="M12 12 V4 A8 8 0 0 1 18.9 16 Z"/>',
    'Checkbox': '<rect x="3" y="3" width="18" height="18" rx="3"/><path d="M7 12 L10.5 15.5 L17 8"/>',
    'RadioGroup': '<circle cx="6" cy="8" r="2.5"/><circle cx="6" cy="16" r="2.5"/><circle cx="6" cy="16" r="1" fill="#36618c"/><path d="M11 8 H20 M11 16 H20"/>',
    'ButtonGroup': '<rect x="2" y="8" width="6" height="8" rx="2"/><rect x="9" y="8" width="6" height="8" rx="2"/><rect x="16" y="8" width="6" height="8" rx="2"/>',
    'AlarmBanner': '<path d="M12 3 L22 20 H2 Z"/><path d="M12 9 V14 M12 17 H12.01"/>',
    'StatusBadge': '<rect x="2" y="8" width="20" height="8" rx="4"/><circle cx="7" cy="12" r="2"/><path d="M11 12 H19"/>',
    'KpiCard': '<rect x="2" y="4" width="20" height="16" rx="2"/><path d="M6 9 H11 M6 13 H9 M6 17 H18"/>',
    'HeatExchanger': '<rect x="3" y="7" width="18" height="10" rx="2"/><path d="M6 9 V15 H9 V9 H12 V15 H15 V9"/>',
    'Compressor': '<rect x="3" y="6" width="13" height="12" rx="2"/><path d="M16 9 H21 V3"/><path d="M10 12 a3 3 0 1 1 3 3 a2 2 0 1 1 -2 -2"/>',
    'Boiler': '<rect x="5" y="4" width="14" height="17" rx="3"/><path d="M12 17 C9 14 11 13 12 10 C14 13 15 14 12 17 Z"/>',
    'Conveyor': '<path d="M2 12 H22"/><circle cx="6" cy="16" r="2"/><circle cx="12" cy="16" r="2"/><circle cx="18" cy="16" r="2"/><rect x="7" y="6" width="4" height="4"/><rect x="13" y="7" width="4" height="3"/>',
    'Filter': '<path d="M4 4 H20 L15 11 V18 H9 V11 Z"/><path d="M9 8 H15"/>',
    'CoolingTower': '<path d="M8 3 H16 L14 10 Q13 14 15 20 H9 Q11 14 10 10 Z"/><path d="M9 6 H15"/>',
    'GroupBox': '<rect x="3" y="7" width="18" height="13" rx="2"/><path d="M3 7 V4 H10 V7"/>',
    'Divider': '<path d="M2 12 H6 M9 12 H15 M18 12 H22"/>',
    'EmergencyStop': '<circle cx="12" cy="10" r="6"/><path d="M8 17 H16 M6 20 H18"/>',
    'KeySwitch': '<rect x="4" y="9" width="16" height="11" rx="2"/><circle cx="12" cy="14.5" r="3"/><path d="M12 6 V9"/>',
    'SelectorSwitch': '<circle cx="12" cy="13" r="7"/><path d="M12 13 V7 M6 22 H18"/>',
    'AlarmAckButton': '<path d="M12 3 A5 5 0 0 0 7 8 V13 L5 16 H19 L17 13 V8 A5 5 0 0 0 12 3 Z"/><path d="M9 19 H15"/>',
    'StackLight': '<rect x="8" y="3" width="8" height="5" rx="1.5"/><rect x="8" y="9" width="8" height="5" rx="1.5"/><rect x="8" y="15" width="8" height="5" rx="1.5"/><path d="M12 20 V22"/>',
    'ControlValve': '<path d="M2 12 H9 M15 12 H22"/><path d="M9 6 L15 18 V6 L9 18 Z"/><path d="M12 6 V2"/>',
    'TrendMulti': '<path d="M3 20 H21 V4"/><path d="M5 16 L10 10 L14 13 L19 7"/><path d="M5 19 L10 14 L14 17 L19 11"/>',
}

SVG_TEMPLATE = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
                'stroke="#36618c" stroke-width="1.6" stroke-linecap="round" '
                'stroke-linejoin="round">{}</svg>')


def write_icons():
    IMAGES.mkdir(parents=True, exist_ok=True)
    for name, body in ICONS.items():
        path = IMAGES / f'industrial-{name}.svg'
        path.write_text(SVG_TEMPLATE.format(body), encoding='utf-8')
    print(f'icons written: {len(ICONS)}')


def patch_lang(culture, index):
    path = LANG / f'PlgMimBasicComp.{culture}.xml'
    tree = ET.parse(path)
    root = tree.getroot()
    key_of = lambda element: element.get('key')
    group_key = PREFIX + 'IndustrialGroups'
    group_dict = next((d for d in root if key_of(d) == group_key), None)
    if group_dict is None:
        # Repair a dictionary whose key attribute was lost by an earlier run.
        group_dict = next((d for d in root if any(p.get('key') == 'Shapes' for p in d)), None)
    if group_dict is None:
        raise SystemExit(f'{path.name}: IndustrialGroups dictionary not found')

    # Rebuild the palette dictionary: group titles first, then component titles.
    group_dict.clear()
    group_dict.set('key', group_key)
    for entry in GROUPS:
        ET.SubElement(group_dict, 'Phrase', key=entry[0]).text = entry[index]
    for entry in TYPES:
        ET.SubElement(group_dict, 'Phrase', key=entry[0]).text = entry[index]

    # Per-component property dictionaries.
    existing = {key_of(d): d for d in root}
    for type_name, *_ in TYPES:
        dict_key = PREFIX + 'Industrial' + type_name
        node = existing.get(dict_key)
        if node is None:
            node = ET.SubElement(root, 'Dictionary', key=dict_key)
        known = {key_of(p) for p in node}
        for prop in PROPERTIES:
            if prop[0] not in known:
                ET.SubElement(node, 'Phrase', key=prop[0]).text = prop[index]

    ET.indent(tree, space='  ')
    tree.write(path, encoding='utf-8', xml_declaration=True)
    if culture == 'zh-Hans':
        language_pack = ROOT / 'LangPack/Chinese-Simplified/ScadaWeb/lang' / path.name
        language_pack.write_bytes(path.read_bytes())
    print(f'{path.name}: {len(TYPES)} component titles, {len(GROUPS)} groups, '
          f'{len(PROPERTIES)} properties each')


if __name__ == '__main__':
    write_icons()
    for culture, index in (('zh-Hans', 1), ('en-GB', 2), ('ru-RU', 3)):
        patch_lang(culture, index)

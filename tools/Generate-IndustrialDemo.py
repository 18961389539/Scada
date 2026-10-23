"""Generate a separate industrial component showcase; never modify Process.mim.

Normal demo startup uses the checked-in output. Run this script after changing
the showcase layout, then redeploy it using the same procedure as other views.
"""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Projects/HelloWorld'

def add(parent, key, value=None):
    node = ET.SubElement(parent, key)
    if isinstance(value, dict):
        for k, v in value.items(): add(node, k, v)
    elif value is not None:
        node.text = str(value).lower() if isinstance(value, bool) else str(value)
    return node

def save(root, path):
    ET.indent(root, space='  ')
    ET.ElementTree(root).write(path, encoding='utf-8', xml_declaration=True)

mimic = ET.Element('Mimic', editorVersion='6.0.2.2')
add(mimic, 'Dependencies')
add(mimic, 'Document', dict(BackColor='#f3f6fa', Font=dict(Name='Microsoft YaHei', Size=14), ForeColor='#213047', Size=dict(Width=1440, Height=2280)))
components = add(mimic, 'Components')
counter = 0

def component(kind, x, y, w, h, **props):
    global counter
    counter += 1
    parent_id = props.pop('ParentID', None)
    parent = components
    if parent_id:
        container = next(c for c in components.iter() if c.findtext('ID') == str(parent_id))
        parent = container.find('Components')
        if parent is None:
            parent = add(container, 'Components')
    comp = add(parent, kind)
    for key, value in dict(ID=counter, Name='Showcase' + str(counter), Enabled=True, Visible=True,
                           Location=dict(X=x, Y=y), Size=dict(Width=w, Height=h), **props).items():
        add(comp, key, value)
    return comp

def text(value, y, size=20):
    component('Text', 30, y, 1370, 40, Text=value, AutoSize=False,
              Font=dict(Name='Microsoft YaHei', Size=size), ForeColor='#213047')

text('工业组件实验室 / 组件示例（调色板共 79 种）', 20, 30)
text('真实数据绑定 · 管道和设备状态联动 · 输入控件遵循用户控制权限 · 悬停查看组件说明（触屏长按）', 70, 15)
text('01 / 工艺设备与液位', 125)
component('IndustrialTank', 45, 185, 170, 225, Label='储罐液位', InCnlNum=201, Minimum=0, Maximum=100, Unit='%',
          Tooltip='储罐液位\n通道 201，量程 0–100%\n液位低于低限时颜色转报警色')
component('IndustrialPipe', 225, 295, 180, 40, InCnlNum=204, ActiveColor='#1689d8', Threshold=0, LineWidth=14,
          Tooltip='工艺管道\n通道 204，数值大于阈值 0 时流动\n颜色跟随泵的运行状态')
component('IndustrialPump', 410, 205, 150, 155, Label='水泵', InCnlNum=204, OutCnlNum=204,
          ClickAction=dict(ActionType='SendCommand', CommandArgs=dict(ShowDialog=True)),
          Tooltip='离心泵\n通道 204，单击弹出命令窗口下发启停\n运行/停止由数值与阈值判定')
component('IndustrialValve', 600, 225, 145, 125, Label='阀门 / 跟随泵状态', InCnlNum=204,
          Tooltip='调节阀\n跟随泵状态开闭，通道 204\n绿色为开启，灰色为关闭')
component('IndustrialFan', 790, 195, 140, 165, Label='送风机', InCnlNum=304, OutCnlNum=304,
          ClickAction=dict(ActionType='SendCommand', CommandArgs=dict(ShowDialog=True)),
          Tooltip='送风机\n通道 304，单击下发命令\n状态码无效时显示“无数据”')
component('IndustrialMotor', 975, 215, 165, 140, Label='电机 / 跟随风机', InCnlNum=304,
          Tooltip='驱动电机\n跟随风机运行，通道 304\n运行中散热片与机座同步高亮')
component('IndustrialLevel', 1220, 180, 150, 225, Label='液位计', InCnlNum=201, Minimum=0, Maximum=100, Unit='%',
          Tooltip='管式液位计\n通道 201，底部为 0%\n量程与单位可单独配置')
text('02 / 数据显示与操作输入', 450)
component('IndustrialGauge', 35, 510, 235, 170, Label='送风温度', InCnlNum=301, Value=23, Minimum=0, Maximum=50, Unit='°C',
          Tooltip='指针式仪表盘\n通道 301，量程 0–50 °C\n指针与圆弧同步指示当前值')
component('IndustrialProgress', 300, 510, 300, 110, Label='储罐液位', InCnlNum=201, Minimum=0, Maximum=100, Unit='%',
          Tooltip='横向进度条\n通道 201，按 0–100% 填充\n超出量程自动截断')
component('IndustrialStatusGroup', 645, 510, 370, 110, Label='设备状态', Channels='204,304', Labels='水泵,风机',
          Tooltip='状态灯组\n通道 204、304，每 5 秒刷新\n绿色为开启，灰色为关闭')
component('IndustrialNumberInput', 35, 715, 355, 125, Label='液位设定', InCnlNum=205, OutCnlNum=205, Minimum=0, Maximum=100, Step=1, Unit='%',
          Tooltip='数值输入框\n读通道 205，写通道 205\n输入后点“发送”，仅在运行画面可用')
component('IndustrialSlider', 425, 715, 460, 125, Label='温度设定', InCnlNum=305, OutCnlNum=305, Value=23, Minimum=10, Maximum=45, Step=1, Unit='°C',
          Tooltip='滑块设定\n读通道 305，写通道 305\n拖动后点“发送”下发')
component('IndustrialSelect', 925, 715, 450, 125, Label='水泵命令', InCnlNum=204, OutCnlNum=204,
          Minimum=0, Maximum=1, Options='停止=0;启动=1', Unit='', Decimals=0,
          Tooltip='下拉选择\n选项“停止=0 / 启动=1”\n写通道 204，需操作权限')
text('03 / 真实历史与事件', 885)
component('IndustrialTrend', 35, 945, 520, 255, Label='液位历史趋势', InCnlNum=201, Minimum=0, Maximum=100, Unit='%', HistoryMinutes=60, ArchiveBit=1,
          Tooltip='历史趋势图\n通道 201，读取分钟归档最近 60 分钟\n每 30 秒刷新，无效数据断线')
component('IndustrialDataTable', 590, 945, 360, 255, Label='实时数据表', Channels='201,202,301,403', Labels='液位,流量,温度,总功率', Decimals=2, MaxRows=4,
          Tooltip='实时数据表\n通道 201、202、301、403\n每 5 秒刷新，最多显示 4 行')
component('IndustrialAlarmList', 985, 945, 420, 255, Label='最近两天事件', ArchiveBit=4, MaxRows=6,
          Tooltip='事件列表\n读取事件归档最近两天\n每 5 秒刷新，最多 6 行')
text('04 / 基础图形（可缩放、调色及绑定属性）', 1250)
component('IndustrialLine', 45, 1340, 200, 35, ActiveColor='#526b86', LineWidth=3,
          Tooltip='直线图元\n可改颜色、线宽与方向\n不绑定数据')
component('IndustrialArrow', 300, 1325, 210, 60, ActiveColor='#1689d8', LineWidth=4,
          Tooltip='箭头图元\n用于标注介质流向\n可改颜色与线宽')
component('IndustrialRectangle', 600, 1310, 230, 110, ActiveColor='#5281ae', FillColor='#e4edf7', LineWidth=2,
          Tooltip='矩形图元\n可设填充色与描边\n常用作设备底座或区域')
component('IndustrialEllipse', 930, 1300, 150, 130, ActiveColor='#159d83', FillColor='#d9f2ea', LineWidth=3,
          Tooltip='圆形图元\n可设填充色与描边\n常用于罐体或标识')
text('操作提示：改变液位/温度后点击“发送”；仅在运行画面发送命令。编辑器中的曲线和事件标注为“编辑预览”。', 1490, 15)
text('趋势读取分钟归档；数据表和灯组每 5 秒刷新；报警列表显示事件记录。viewer 只能查看。', 1535, 15)
text('05 / 安全与操作台（新增组件）', 1600)
component('IndustrialEmergencyStop', 45, 1690, 150, 165, Label='急停', ActiveColor='#dc2626',
          Tooltip='急停按钮\n长按 0.8 秒才会下发，避免误触\n演示画面未绑定输出通道，绑定后可用')
component('IndustrialSelectorSwitch', 230, 1700, 180, 155, Label='选择开关（未绑定）',
          Options='手动=1;停止=0;自动=2', Minimum=0, Maximum=2, Unit='', Decimals=0,
          Tooltip='三位置选择开关示例\n连接实际运行方式通道后可用')
component('IndustrialKeySwitch', 450, 1705, 150, 145, Label='钥匙开关', InCnlNum=204, OutCnlNum=204,
          Tooltip='带保护盖钥匙开关\n第一次点击打开保护盖，再次点击切换\n写通道 204（0/1）')
component('IndustrialAlarmAckButton', 640, 1680, 220, 180, Label='报警确认', ArchiveBit=4, Channels='201,301',
          Tooltip='选择真实的待确认事件\n直接打开原生确认窗口，记录确认人和时间')
component('IndustrialStackLight', 870, 1665, 110, 235, Channels='304,301,204', Labels='风机,温度,水泵',
          Tooltip='三层报警灯柱\n通道顺序：红(报警) / 黄 / 绿\n红灯由状态码 11/12/14/15 点亮')
component('IndustrialControlValve', 1020, 1700, 165, 155, Label='调节阀（待绑定）',
          Minimum=0, Maximum=100, Unit='%', Decimals=0,
          Tooltip='调节阀图形示例\n连接实际阀门开度与输出通道后可操作')
text('06 / 多通道对比趋势', 1890)
component('IndustrialTrendMulti', 35, 1950, 640, 260, Label='液位/流量/温度对比', Channels='201,202,301',
          Labels='液位,流量,温度', Minimum=0, Maximum=100, Unit='', Units='%,m³/h,°C', Minimums='0,0,0', Maximums='100,25,50', HistoryMinutes=60, ArchiveBit=1,
          Tooltip='多通道趋势\n最多 4 条曲线，读取分钟归档 60 分钟\n每 30 秒刷新，无效数据断线')
text('提示：急停长按 0.8 秒；钥匙开关先开保护盖；选择开关点击循环；未绑定输出通道的控件为锁定态。', 2230, 15)
text('07 / 页面导航与运行流程', 2310)
component('IndustrialNavBar', 35, 2370, 1370, 75, Label='页面导航', ViewIds='10,21,22,23,2,24')
component('IndustrialNavButton', 35, 2470, 220, 70, Label='前往储罐泵站', ViewId=21)
component('IndustrialBackButton', 280, 2470, 220, 70, Label='返回上一画面', ViewId=10)
component('IndustrialCommStatus', 535, 2470, 400, 210, Label='采集数据状态', Channels='201,202,203,301', StatusChannel=0, StaleSeconds=15)
component('IndustrialDevicePanel', 970, 2470, 435, 320, Label='水泵操作', Channels='204,202,203', Labels='运行命令,流量 (m³/h),压力 (bar)', InCnlNum=204, OutCnlNum=204, FeedbackChannel=204, Minimum=0, Maximum=1, Step=1)
component('IndustrialActiveAlarmTable', 35, 2830, 670, 350, Label='当前未恢复报警', Channels='201,301', ArchiveBit=4, MaxRows=10)
component('IndustrialHistoryQuery', 735, 2830, 670, 440, Label='历史查询与导出', Channels='201,301,403', ArchiveBit=1, HistoryMinutes=60, MaxRows=100)
text('08 / 参数组与计划（服务端保存，计划默认停用）', 3310)
component('IndustrialRecipe', 35, 3370, 1370, 470, Label='配方与参数组', Channels='204,205,304,305')
component('IndustrialSchedule', 35, 3880, 1370, 490, Label='计划运行', Channels='204,304')
component('IndustrialOperationLog', 35, 4410, 1370, 350, Label='操作与执行记录', MaxRows=50)
text('09 / 容器与控制回路', 4800)
tabs = component('IndustrialTabs', 35, 4860, 670, 330, Label='选项卡示例', Tabs='液位,温度')
parent_id = tabs.findtext('ID')
component('IndustrialGauge', 20, 15, 270, 190, ParentID=parent_id, CssClass='tab-1', Label='储罐液位', InCnlNum=201, Minimum=0, Maximum=100, Unit='%')
component('IndustrialThermometer', 20, 15, 150, 220, ParentID=parent_id, CssClass='tab-2', Label='送风温度', InCnlNum=301, Minimum=0, Maximum=50, Unit='°C')
collapse = component('IndustrialCollapsiblePanel', 735, 4860, 670, 330, Label='展开／收起设备读数', Collapsed=False)
component('IndustrialDataTable', 15, 10, 600, 200, ParentID=collapse.findtext('ID'), Label='实时读数', Channels='201,202,301,403', Labels='液位 (%),流量 (m³/h),温度 (°C),总功率 (kW)', MaxRows=4)
component('IndustrialPidPanel', 35, 5230, 670, 340, Label='PID 回路面板（实际回路待绑定）', ProcessChannel=301, FeedbackChannel=305, SetpointChannel=0, OutputChannel=0, ModeChannel=0, ModeOutputChannel=0, Minimum=10, Maximum=45, Step=1, Unit='°C')
component('IndustrialEnergyMeter', 735, 5230, 670, 220, Label='最近 24 小时积分电量', InCnlNum=403, Minimum=0, Maximum=10000, Unit='kWh', IntegratePower=True, HistoryMinutes=1440, ArchiveBit=1, Decimals=3)
text('10 / 联锁、故障诊断与报警分析', 5630)
component('IndustrialInterlockPanel', 35, 5690, 670, 340, Label='条件信号接入示例（运行反馈）', Channels='204,304', Labels='水泵运行反馈（示例）,风机运行反馈（示例）', ExpectedValues='1,1', PermitChannel=0,
          Tooltip='读取真实运行反馈演示条件匹配；控制器最终许可未绑定，不据此生成操作许可。')
component('IndustrialFaultDiagnostics', 735, 5690, 670, 340, Label='设备故障诊断（待接入）', FaultChannel=0, FaultDefinitions='{}', PolicyId='',
          Tooltip='绑定设备故障代码通道，并按设备手册配置故障说明；服务端配置复位策略后方可申请复位。')
component('IndustrialAlarmStatistics', 35, 6070, 1370, 470, Label='液位与温度报警统计', Channels='201,301', ArchiveBit=4, HistoryMinutes=1440, MaxRows=20)
text('11 / 电气单线图（独立可编辑组件）', 6610)
text('接线示意 · 电气测点待接入 · 灰色虚线表示状态未知 · 开关位置和带电状态分别绑定', 6670, 15)
component('Text', 190, 6730, 340, 35, Text='10 kV 进线（示意）', AutoSize=False, Font=dict(Name='Microsoft YaHei', Size=18), ForeColor='#213047')
component('IndustrialElectricalLine', 338, 6770, 24, 55, Label='进线', Vertical=True, PreviewEnergized=True)
component('IndustrialTransformer', 280, 6825, 140, 140, Label='T1 变压器 / 10 / 0.4 kV', Value=1, PreviewEnergized=True,
          Tooltip='变压器符号；输入通道绑定实际带电状态，0=无电反馈、1=带电；当前未接入。')
component('IndustrialCircuitBreaker', 300, 6965, 100, 140, Label='QF0 进线断路器', Value=1, PreviewEnergized=True,
          Tooltip='位置反馈与带电反馈独立；0=分闸、1=合闸、2=跳闸。默认不绑定输出或发送命令。')
component('IndustrialElectricalLine', 338, 7105, 24, 27, Label='进线至母线', Vertical=True, PreviewEnergized=True)
component('IndustrialBusbar', 130, 7120, 1180, 24, Label='0.4 kV 母线（示意）', Value=1, PreviewEnergized=True)
component('IndustrialElectricalLine', 228, 7132, 24, 68, Label='馈线一上段', Vertical=True, PreviewEnergized=True)
component('IndustrialElectricalLine', 708, 7132, 24, 68, Label='馈线二上段', Vertical=True, PreviewEnergized=True)
component('IndustrialCircuitBreaker', 190, 7200, 100, 140, Label='QF1 馈线断路器', Value=1, PreviewEnergized=True)
component('IndustrialDisconnector', 670, 7200, 100, 140, Label='QS1 隔离开关', Value=0)
component('IndustrialElectricalLine', 228, 7340, 24, 140, Label='馈线一出线', Vertical=True, PreviewEnergized=True)
component('IndustrialElectricalLine', 708, 7340, 24, 140, Label='馈线二与接地支路', Vertical=True, ShowJunction=True)
component('IndustrialElectricalLine', 720, 7398, 340, 24, Label='接地支路线', Vertical=False)
component('IndustrialGroundSwitch', 1010, 7410, 100, 140, Label='QE1 接地开关', Value=0)
component('Text', 155, 7510, 280, 35, Text='馈线一 / 负载（示意）', AutoSize=False, Font=dict(Name='Microsoft YaHei', Size=16), ForeColor='#213047')
component('Text', 635, 7510, 280, 35, Text='馈线二 / 检修支路（示意）', AutoSize=False, Font=dict(Name='Microsoft YaHei', Size=16), ForeColor='#213047')
text('位置：红=合闸，绿=分闸，黄=跳闸；导线带电状态独立读取，不从开关位置推断供电状态。', 7610, 14)
text('编辑提示：开关与导线可设为横向；连接线支持 straight / elbow / tee，连接点需明确标注。', 7660, 14)
mimic.find('Document/Size/Height').text = '7750'
add(mimic, 'Images')
save(mimic, PROJECT / 'Views/HelloWorld/Components.mim')

views_path = PROJECT / 'BaseXML/View.xml'
views = ET.parse(views_path).getroot()
for existing in list(views):
    if existing.findtext('ViewID') == '24': views.remove(existing)
view = add(views, 'View')
for key, value in dict(ViewID=24, Path='HelloWorld\\Components.mim', ViewTypeID=7,
                       Title='07 工业组件实验室', Ord=7, ObjNum=1).items(): add(view, key, value)
save(views, views_path)
type_count = len({c.tag for c in components.iter() if c.tag.startswith('Industrial')})
print(f'Generated Components.mim: {counter} instances, {type_count} industrial component types; ViewID=24')

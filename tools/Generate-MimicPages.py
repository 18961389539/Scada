"""Generate the HelloWorld demo pages as mimics and switch every leaf view to the Mimic type.

All pages are built from the same component library as the online editor, so the
result can be opened and edited in the mimic editor. Run from the repository root:

    python tools/Generate-MimicPages.py
"""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Projects/HelloWorld'
VIEWS = PROJECT / 'Views/HelloWorld'
WIDTH = 1400

PAGES = {
    'Overview': ('01 运行总览', '由一个信号，到完整的监控与控制系统。', 980),
    'Tank': ('03 储罐泵站', '储罐、泵、阀门与管道联动，读写通道 201–205。', 820),
    'HVAC': ('04 空调机组', '送风机、温度与湿度，读写通道 301–305。', 820),
    'Energy': ('05 能耗与公式', '功率、电量与费用由服务端公式计算后回读。', 820),
    'Simulator': ('06 基础信号实验', '模拟器通道 101–106，用于验证各类组件。', 820),
}


def add(parent, key, value=None):
    node = ET.SubElement(parent, key)
    if isinstance(value, dict):
        for k, v in value.items():
            add(node, k, v)
    elif isinstance(value, bool):
        node.text = str(value).lower()
    elif value is not None:
        node.text = str(value)
    return node


class Page:
    def __init__(self, name):
        self.counter = 0
        self.mimic = ET.Element('Mimic', editorVersion='6.0.2.2')
        add(self.mimic, 'Dependencies')
        title, subtitle, height = PAGES[name]
        add(self.mimic, 'Document', dict(
            BackColor='#eef2f7', Font=dict(Name='Microsoft YaHei', Size=14),
            ForeColor='#213047', Size=dict(Width=WIDTH, Height=height)))
        add(self.mimic, 'Components')
        self.components = self.mimic.find('Components')
        self.text(title, 30, 26, 34, bold=True)
        self.text(subtitle, 34, 76, 15, color='#5b6b80')

    def component(self, kind, x, y, w, h, **props):
        self.counter += 1
        comp = add(self.components, kind)
        for key, value in dict(ID=self.counter, Name=f'{kind}{self.counter}', Enabled=True, Visible=True,
                               Location=dict(X=x, Y=y), Size=dict(Width=w, Height=h), **props).items():
            add(comp, key, value)
        return comp

    def text(self, value, x, y, size=16, w=None, color='#213047', bold=False):
        return self.component('Text', x, y, w or (WIDTH - 2 * x), 40, Text=value, AutoSize=False,
                              Font=dict(Name='Microsoft YaHei', Size=size), ForeColor=color)

    def section(self, value, x, y, w=None):
        return self.component('IndustrialDivider', x, y, w or (WIDTH - 2 * x), 34,
                              Label=value, ActiveColor='#b8c7d8', LineWidth=1)

    def save(self, name):
        # Keep every component inside the scrollable canvas, including the bottom row.
        bottom = max(int(c.findtext('Location/Y')) + int(c.findtext('Size/Height'))
                     for c in self.components)
        self.mimic.find('Document/Size/Height').text = str(bottom + 30)
        add(self.mimic, 'Images')
        VIEWS.mkdir(parents=True, exist_ok=True)
        path = VIEWS / f'{name}.mim'
        ET.indent(self.mimic, space='  ')
        ET.ElementTree(self.mimic).write(path, encoding='utf-8', xml_declaration=True)
        return path


def build_overview():
    p = Page('Overview')
    # Cross-view navigation so operators do not have to use the left tree.
    for index, (label, view_id) in enumerate([
        ('总览', 10), ('工艺组态', 20), ('储罐泵站', 21), ('空调机组', 22),
        ('能耗与公式', 23), ('基础信号', 2), ('组件实验室', 24)
    ]):
        p.component('BasicButton', 700 + index * 96, 26, 92, 34, Text=label,
                    ClickAction=dict(ActionType='OpenLink', LinkArgs=dict(ViewID=view_id)),
                    Tooltip='切换到视图 ' + str(view_id))

    p.section('关键指标', 30, 120)
    p.component('IndustrialKpiCard', 30, 170, 320, 165, Label='储罐液位', InCnlNum=201,
                Minimum=0, Maximum=100, Target=60, Unit='%', Value=44,
                Tooltip='储罐液位\n通道 201，量程 0–100%\n红色刻度为目标液位 60%')
    p.component('IndustrialGauge', 370, 170, 320, 165, Label='送风温度', InCnlNum=301,
                Minimum=0, Maximum=50, Unit='°C', Value=24,
                Tooltip='送风温度\n通道 301，量程 0–50 °C')
    p.component('IndustrialKpiCard', 710, 170, 320, 165, Label='设备总功率', InCnlNum=403,
                Minimum=0, Maximum=7, Target=5, Unit='kW', Decimals=2, Value=0.08,
                Tooltip='设备总功率\n通道 403，由服务端公式汇总\n量程 0–7 kW，参考值 5 kW')
    p.component('IndustrialKpiCard', 1050, 170, 320, 165, Label='当前报警点数', InCnlNum=405,
                Minimum=0, Maximum=10, Target=0, Unit='点', Decimals=0, Value=0,
                Tooltip='当前报警点数\n通道 405\n目标值 0 点表示无越限')

    p.section('工艺总览', 30, 370)
    p.component('IndustrialTank', 60, 430, 190, 250, Label='储罐', InCnlNum=201,
                Minimum=0, Maximum=100, Unit='%', ActiveColor='#1689d8',
                Tooltip='储罐\n通道 201，量程 0–100%\n液位低于低限时转为报警色')
    p.component('IndustrialPipe', 260, 530, 210, 46, InCnlNum=204, Threshold=0, LineWidth=16,
                Tooltip='出水管道\n通道 204，泵运行时显示流动')
    p.component('IndustrialPump', 480, 440, 160, 165, Label='水泵', InCnlNum=204, OutCnlNum=204,
                ClickAction=dict(ActionType='SendCommand', CommandArgs=dict(ShowDialog=True)),
                Tooltip='水泵\n通道 204，单击下发启停命令')
    p.component('IndustrialValve', 660, 460, 150, 135, Label='出水阀', InCnlNum=204,
                Tooltip='出水阀\n跟随水泵状态开闭')
    p.component('IndustrialFlowMeter', 830, 445, 180, 180, Label='出水流量', InCnlNum=202,
                Minimum=0, Maximum=25, Unit='m³/h', Value=0,
                Tooltip='流量计\n通道 202，量程 0–25 m³/h')
    p.component('IndustrialStackLight', 1040, 420, 110, 235, Channels='201,301,204',
                Labels='液位,温度,水泵',
                Tooltip='三层报警灯柱\n红灯=通道 201 越限\n黄灯=温度越限，绿灯=水泵运行')

    p.section('趋势与事件', 30, 720)
    p.component('IndustrialTrend', 30, 770, 660, 210, Label='液位趋势', InCnlNum=201,
                Minimum=0, Maximum=100, Unit='%', HistoryMinutes=60, ArchiveBit=1)
    p.component('IndustrialTrend', 30, 1000, 660, 210, Label='流量趋势', InCnlNum=202,
                Minimum=0, Maximum=25, Unit='m³/h', HistoryMinutes=60, ArchiveBit=1)
    p.component('IndustrialAlarmList', 710, 770, 660, 440, Label='最近事件与报警确认', ArchiveBit=4, MaxRows=8,
                Tooltip='事件列表\n读取最近两天的事件归档')
    p.section('当前报警与历史查询', 30, 1240)
    p.component('IndustrialActiveAlarmTable', 30, 1290, 660, 340, Label='当前未恢复报警', Channels='201,301', ArchiveBit=4, MaxRows=10)
    p.component('IndustrialHistoryQuery', 710, 1290, 660, 420, Label='历史查询与 CSV 导出', Channels='201,301,403', ArchiveBit=1, HistoryMinutes=60, MaxRows=100)
    return p.save('Overview')


def build_tank():
    p = Page('Tank')
    p.section('储罐与泵组', 30, 120)
    p.component('IndustrialTank', 60, 180, 230, 330, Label='1# 储罐', InCnlNum=201,
                Minimum=0, Maximum=100, Unit='%', ActiveColor='#1689d8',
                Tooltip='1# 储罐\n通道 201，量程 0–100%')
    p.component('IndustrialPipe', 300, 330, 240, 46, InCnlNum=204, Threshold=0, LineWidth=16,
                Tooltip='主管道\n泵运行时流动')
    p.component('IndustrialPump', 550, 240, 170, 175, Label='进料泵', InCnlNum=204, OutCnlNum=204,
                ClickAction=dict(ActionType='SendCommand', CommandArgs=dict(ShowDialog=True)),
                Tooltip='进料泵\n通道 204，单击下发启停')
    p.component('IndustrialMotor', 740, 250, 180, 155, Label='泵电机', InCnlNum=204,
                Tooltip='泵电机\n跟随水泵运行')
    p.component('IndustrialGauge', 940, 240, 185, 175, Label='液位反馈', InCnlNum=201,
                Minimum=0, Maximum=100, Unit='%', Decimals=0,
                Tooltip='液位实际采集反馈\n通道 201；下方输入框用于液位模拟注入')
    p.component('IndustrialLevel', 1150, 180, 150, 330, Label='液位计', InCnlNum=201,
                Minimum=0, Maximum=100, Unit='%',
                Tooltip='液位计\n通道 201')

    p.section('操作与读数', 30, 540)
    p.component('IndustrialNumberInput', 30, 600, 340, 150, Label='液位模拟设定', InCnlNum=201, OutCnlNum=205,
                Minimum=0, Maximum=100, Step=1, Unit='%',
                Tooltip='液位模拟注入（非闭环控制）\n读实际液位 201、写设定 205，输入后点发送')
    p.component('IndustrialGauge', 390, 590, 240, 170, Label='管道压力', InCnlNum=203,
                Minimum=0, Maximum=4, Unit='bar', Decimals=2, Value=0.2,
                Tooltip='管道压力\n通道 203，量程 0–4 bar')
    p.component('IndustrialStatusGroup', 660, 600, 330, 120, Label='设备状态', Channels='204',
                Labels='水泵',
                Tooltip='状态灯组\n通道 204，每 5 秒刷新')
    p.component('IndustrialButtonGroup', 1010, 600, 340, 150, Label='水泵启停', InCnlNum=204, OutCnlNum=204,
                Options='启动=1;停止=0', Minimum=0, Maximum=1, Unit='', Decimals=0,
                Tooltip='水泵启停命令\n通道 204；运行状态来自命令通道回读')

    p.component('IndustrialDataTable', 30, 780, 670, 240, Label='实时读数',
                Channels='201,202,203,401', Labels='液位 (%),流量 (m³/h),压力 (bar),存水量 (m³)', Decimals=2, MaxRows=4,
                Tooltip='实时数据表\n每 5 秒刷新')
    p.component('IndustrialTrend', 730, 780, 640, 240, Label='液位趋势', InCnlNum=201,
                Minimum=0, Maximum=100, Unit='%', HistoryMinutes=60, ArchiveBit=1,
                Tooltip='趋势图\n读取分钟归档最近 60 分钟')
    p.section('设备操作与通信', 30, 1060)
    p.component('IndustrialDevicePanel', 30, 1110, 670, 310, Label='水泵操作面板', Channels='204,202,203', Labels='运行命令,流量 (m³/h),压力 (bar)', InCnlNum=204, OutCnlNum=204, FeedbackChannel=204, Minimum=0, Maximum=1, Step=1)
    p.component('IndustrialCommStatus', 730, 1110, 640, 210, Label='泵站数据通信', Channels='201,202,203,204', StatusChannel=0, StaleSeconds=15)
    p.component('IndustrialOperationLog', 30, 1450, 1340, 320, Label='控制与参数修改记录', MaxRows=50)
    p.section('联锁与故障诊断', 30, 1810)
    p.component('IndustrialInterlockPanel', 30, 1870, 650, 330, Label='条件信号接入示例（运行反馈）', Channels='204,304', Labels='水泵运行反馈（示例）,风机运行反馈（示例）', ExpectedValues='1,1', PermitChannel=0)
    p.component('IndustrialFaultDiagnostics', 710, 1870, 660, 330, Label='设备故障诊断（待接入）', FaultChannel=0, FaultDefinitions='{}', PolicyId='')
    p.component('IndustrialAlarmStatistics', 30, 2240, 1340, 470, Label='液位与温度报警统计', Channels='201,301', ArchiveBit=4, HistoryMinutes=1440, MaxRows=20)
    return p.save('Tank')


def build_hvac():
    p = Page('HVAC')
    p.section('机组状态', 30, 120)
    p.component('IndustrialFan', 70, 190, 210, 220, Label='送风机', InCnlNum=304, OutCnlNum=304,
                ClickAction=dict(ActionType='SendCommand', CommandArgs=dict(ShowDialog=True)),
                Tooltip='送风机\n通道 304，单击下发启停')
    p.component('IndustrialMotor', 320, 210, 200, 175, Label='风机电机', InCnlNum=304,
                Tooltip='风机电机\n跟随风机运行')
    p.component('IndustrialThermometer', 560, 175, 110, 245, Label='送风温度', InCnlNum=301,
                Minimum=0, Maximum=50, Unit='°C',
                Tooltip='温度计\n通道 301，量程 0–50 °C')
    p.component('IndustrialGauge', 700, 200, 250, 180, Label='环境湿度', InCnlNum=302,
                Minimum=0, Maximum=100, Unit='%',
                Tooltip='湿度仪表\n通道 302，量程 0–100%')
    p.component('IndustrialStackLight', 990, 180, 110, 235, Channels='301,302,304',
                Labels='温度,湿度,风机',
                Tooltip='报警灯柱\n红=温度越限，黄=湿度越限，绿=风机运行')

    p.section('参数设定', 30, 450)
    p.component('IndustrialSlider', 30, 510, 480, 150, Label='温度模拟设定', InCnlNum=301, OutCnlNum=305,
                Minimum=10, Maximum=45, Step=1, Unit='°C', Value=23,
                Tooltip='温度模拟注入（非闭环控制）\n读温度 301、写设定 305，拖动后点发送')
    p.component('IndustrialNumberInput', 540, 510, 380, 150, Label='温度模拟微调', InCnlNum=301, OutCnlNum=305,
                Minimum=10, Maximum=45, Step=1, Unit='°C',
                Tooltip='数值输入\n与滑块共用通道 305')
    p.component('IndustrialButtonGroup', 950, 510, 420, 150, Label='风机命令', InCnlNum=304, OutCnlNum=304,
                Options='启动=1;停止=0', Minimum=0, Maximum=1, Unit='', Decimals=0,
                Tooltip='按钮组\n点击直接下发通道 304')

    p.component('IndustrialTrend', 30, 690, 660, 210, Label='送风温度趋势', InCnlNum=301,
                Minimum=0, Maximum=50, Unit='°C', HistoryMinutes=60, ArchiveBit=1)
    p.component('IndustrialTrend', 30, 920, 660, 210, Label='环境湿度趋势', InCnlNum=302,
                Minimum=0, Maximum=100, Unit='%', HistoryMinutes=60, ArchiveBit=1)
    p.component('IndustrialDataTable', 710, 690, 660, 260, Label='机组读数',
                Channels='301,302,303,304', Labels='温度 (°C),湿度 (%),风机功率 (kW),风机命令', Decimals=2, MaxRows=4,
                Tooltip='实时数据表\n每 5 秒刷新')
    return p.save('HVAC')


def build_energy():
    p = Page('Energy')
    p.section('能耗指标', 30, 120)
    p.component('IndustrialKpiCard', 30, 170, 330, 165, Label='储罐存水量', InCnlNum=401,
                Minimum=0, Maximum=100, Target=60, Unit='m³', Value=35.8,
                Tooltip='估算存水量\n通道 401，由液位换算')
    p.component('IndustrialKpiCard', 380, 170, 330, 165, Label='水泵功率', InCnlNum=402,
                Minimum=0, Maximum=3, Target=2.2, Unit='kW', Decimals=2, Value=0,
                Tooltip='水泵估算功率\n通道 402')
    p.component('IndustrialKpiCard', 730, 170, 330, 165, Label='设备总功率', InCnlNum=403,
                Minimum=0, Maximum=7, Target=5, Unit='kW', Decimals=2, Value=0.08,
                Tooltip='设备总功率\n通道 403')
    p.component('IndustrialKpiCard', 1080, 170, 290, 165, Label='每小时电费', InCnlNum=404,
                Minimum=0, Maximum=6, Target=4.25, Unit='元/h', Decimals=2, Value=0.07,
                Tooltip='每小时估算电费\n通道 404')

    p.section('功率构成与趋势', 30, 370)
    p.component('IndustrialBarChart', 30, 430, 620, 260, Label='设备功率', Channels='402,303',
                Labels='水泵,风机', Unit='kW', Decimals=2, ActiveColor='#1689d8',
                Tooltip='柱状图\n按通道当前值归一显示')
    p.component('IndustrialTrend', 680, 430, 690, 260, Label='总功率趋势', InCnlNum=403,
                Minimum=0, Maximum=7, Unit='kW', HistoryMinutes=120, ArchiveBit=1)
    p.component('IndustrialTrend', 30, 720, 620, 240, Label='每小时估算电费', InCnlNum=404,
                Minimum=0, Maximum=6, Unit='元/h', HistoryMinutes=120, ArchiveBit=1)

    p.component('IndustrialDataTable', 680, 990, 690, 220, Label='公式计算结果',
                Channels='401,402,403,404', Labels='存水量 (m³),泵功率 (kW),总功率 (kW),电费 (元/h)', Decimals=2, MaxRows=4,
                Tooltip='服务端公式计算结果\n每 5 秒刷新')
    p.component('IndustrialPieChart', 680, 720, 690, 240, Label='功率占比', Channels='402,303',
                Labels='水泵,风机', Unit='kW', Decimals=2,
                Tooltip='饼图\n按当前值计算占比')
    p.component('IndustrialEnergyMeter', 30, 990, 620, 220, Label='最近 24 小时积分电量', InCnlNum=403,
                Minimum=0, Maximum=10000, Unit='kWh', Decimals=3, IntegratePower=True, HistoryMinutes=1440, ArchiveBit=1,
                Tooltip='按功率归档积分的估算电量\n断点和超过 3 分钟的缺口不积分\n显示实际有效覆盖时长')
    return p.save('Energy')


def build_simulator():
    p = Page('Simulator')
    p.section('模拟信号 101–106', 30, 120)
    p.component('IndustrialGauge', 30, 180, 250, 190, Label='正弦 101', InCnlNum=101,
                Minimum=0, Maximum=100, Unit='', Tooltip='正弦模拟通道 101')
    p.component('IndustrialProgress', 300, 190, 330, 120, Label='方波 102', InCnlNum=102,
                Minimum=0, Maximum=100, Unit='', Tooltip='方波模拟通道 102')
    p.component('IndustrialLevel', 650, 175, 150, 250, Label='三角 103', InCnlNum=103,
                Minimum=0, Maximum=100, Unit='', Tooltip='三角模拟通道 103')
    p.component('IndustrialStatusBadge', 830, 190, 210, 110, Label='继电器 104', InCnlNum=104,
                Threshold=0.5, Unit='',
                Tooltip='继电器状态\n通道 104，大于 0.5 视为吸合')
    p.component('IndustrialThermometer', 1070, 175, 100, 250, Label='模拟量 105', InCnlNum=105,
                Minimum=0, Maximum=100, Unit='%', Tooltip='模拟输出通道 105')
    p.component('IndustrialKpiCard', 1200, 190, 170, 165, Label='数组 106', InCnlNum=106,
                Minimum=0, Maximum=1000, Target=500, Unit='', Decimals=0,
                Tooltip='数组模拟通道 106')

    p.section('交互与命令', 30, 450)
    p.component('IndustrialNumberInput', 30, 510, 340, 135, Label='写入 105', InCnlNum=105, OutCnlNum=105,
                Minimum=0, Maximum=100, Step=1, Unit='%',
                Tooltip='模拟量写入\n读 105、写 105')
    p.component('IndustrialSlider', 390, 510, 420, 135, Label='滑块 105', InCnlNum=105, OutCnlNum=105,
                Minimum=0, Maximum=100, Step=1, Unit='%',
                Tooltip='滑块写入通道 105')
    p.component('IndustrialSelect', 830, 510, 330, 135, Label='继电器控制', InCnlNum=104, OutCnlNum=104,
                Options='断开=0;吸合=1', Minimum=0, Maximum=1, Unit='', Decimals=0,
                Tooltip='下拉框写入通道 104')
    p.component('IndustrialCheckbox', 1180, 510, 190, 135, Label='使能', InCnlNum=105, OutCnlNum=105,
                Tooltip='复选框写入通道 105（0/1）')

    p.section('读数与趋势', 30, 680)
    p.component('IndustrialDataTable', 30, 740, 660, 230, Label='模拟器读数',
                Channels='101,102,103,104,105,106', Labels='正弦,方波,三角,继电器,模拟量,数组',
                Decimals=1, MaxRows=6, Tooltip='模拟器全部通道，每 5 秒刷新')
    p.component('IndustrialTrendMulti', 710, 740, 660, 230, Label='波形对比', Channels='101,102,103',
                Labels='正弦,方波,三角', Minimum=0, Maximum=100, Unit='', HistoryMinutes=60, ArchiveBit=1,
                Tooltip='多通道趋势\n读取分钟归档')
    return p.save('Simulator')


def patch_views():
    path = PROJECT / 'BaseXML/View.xml'
    tree = ET.parse(path)
    root = tree.getroot()
    mapping = {
        '10': ('HelloWorld\\Overview.mim', '01 运行总览'),
        '21': ('HelloWorld\\Tank.mim', '03 储罐泵站'),
        '22': ('HelloWorld\\HVAC.mim', '04 空调机组'),
        '23': ('HelloWorld\\Energy.mim', '05 能耗与公式'),
        '2': ('HelloWorld\\Simulator.mim', '06 基础信号实验'),
    }
    for view in root:
        view_id = view.findtext('ViewID')
        if view_id not in mapping:
            continue
        new_path, _ = mapping[view_id]
        view.find('Path').text = new_path
        view_type = view.find('ViewTypeID')
        if view_type is None:
            view_type = ET.SubElement(view, 'ViewTypeID')
        view_type.text = '7'  # Mimic view
    ET.indent(tree, space='  ')
    tree.write(path, encoding='utf-8', xml_declaration=True)
    print('View.xml: 5 views switched to Mimic')


if __name__ == '__main__':
    for builder in (build_overview, build_tank, build_hvac, build_energy, build_simulator):
        print('generated', builder().name)
    patch_views()

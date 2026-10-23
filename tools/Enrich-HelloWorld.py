"""Generate the checked-in learning project using Rapid SCADA's native XML formats."""
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / 'Projects/HelloWorld'
BASE = PROJECT / 'BaseXML'
NS = 'http://www.w3.org/2001/XMLSchema-instance'
ET.register_namespace('xsi', NS)

def node(parent, name, value=None, **attrs):
    e = ET.SubElement(parent, name, attrs)
    if value is not None:
        e.text = str(value).lower() if isinstance(value, bool) else str(value)
    return e

def save(root, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(root, space='  ')
    ET.ElementTree(root).write(path, encoding='utf-8', xml_declaration=True)

def table(name, rows):
    root = ET.Element('ArrayOf' + name)
    for row in rows:
        entry = node(root, name)
        for key, val in row.items():
            node(entry, key, val)
    save(root, BASE / (name + '.xml'))

table('Obj', [dict(ObjNum=1, Name='HelloWorld 智慧工厂', Code='DemoFactory'),
              dict(ObjNum=2, Name='储罐泵站', Code='TankStation', ParentObjNum=1),
              dict(ObjNum=3, Name='空调机组', Code='AirHandling', ParentObjNum=1),
              dict(ObjNum=4, Name='能耗与计算', Code='Energy', ParentObjNum=1)])
table('Device', [dict(DeviceNum=n, Name=name, Code=code, DevTypeID=230, CommLineNum=1)
                 for n, name, code in [(1, '基础信号模拟器', 'Sim'), (2, '储罐与泵模拟器', 'Tank'), (3, '空调机组模拟器', 'AHU')]])

# Preserve the original waveform channels as a separate learning view.
cnls = ET.parse(BASE / 'Cnl.xml').getroot()
for child in list(cnls):
    if int(child.findtext('CnlNum')) >= 200:
        cnls.remove(child)

def channel(n, name, dev, tag, formula=None, obj=None, kind=1, unit=None, fmt=4, limit=None, events=None, output=None):
    c = node(cnls, 'Cnl')
    values = dict(CnlNum=n, Active=True, Name=name, Code='HelloWorld.' + str(n), CnlTypeID=kind,
                  ObjNum=obj or dev, DeviceNum=dev, TagCode=tag, FormulaEnabled=bool(formula or output),
                  InFormula=formula, OutFormula=output, FormatID=fmt, UnitID=unit, LimID=limit, EventMask=events)
    for k, v in values.items():
        if v is not None:
            node(c, k, v)

# Unit IDs below are demo-specific and added without replacing built-in units.
channel(201, '储罐液位', 2, 'Sin', 'DemoLevel()', unit=201, limit=1, events=49)
channel(202, '出水流量', 2, 'Tri', 'Val(204) > 0 ? 18 + 2 * Math.Sin(Timestamp.TimeOfDay.TotalSeconds / 8) : 0', unit=202)
channel(203, '管道压力', 2, 'Sqr', 'Val(204) > 0 ? 2.8 + 0.2 * Math.Sin(Timestamp.TimeOfDay.TotalSeconds / 9) : 0.2', unit=203)
channel(204, '水泵运行命令', 2, 'DO', kind=2, fmt=21, events=121)
channel(205, '液位模拟设定', 2, 'AO', kind=2, unit=201, events=113,
        output='Math.Min(100, Math.Max(0, CmdVal))')
channel(206, '储罐传感器数组', 2, 'RA', unit=201)
node(cnls[-1], 'DataLen', 3)
channel(301, '送风温度', 3, 'Sin', 'DemoTemperature()', unit=204, limit=2, events=49)
channel(302, '环境湿度', 3, 'Tri', '48 + 12 * Math.Sin(Timestamp.TimeOfDay.TotalSeconds / 18)', unit=201)
channel(303, '风机功率', 3, 'Sqr', 'Val(304) > 0 ? 3.2 + 0.4 * Math.Sin(Timestamp.TimeOfDay.TotalSeconds / 11) : 0.08', unit=205)
channel(304, '风机运行命令', 3, 'DO', kind=2, fmt=21, events=121)
channel(305, '温度模拟设定', 3, 'AO', kind=2, unit=204, events=113,
        output='Math.Min(60, Math.Max(0, CmdVal))')
channel(306, '环境传感器', 3, 'RA')
node(cnls[-1], 'DataLen', 3)
channel(401, '储罐估算存水量', 2, '', 'Val(201) * 0.8', obj=4, kind=3, unit=206)
channel(402, '水泵估算功率', 2, '', 'Val(204) > 0 ? 2.2 : 0', obj=4, kind=3, unit=205)
channel(403, '设备总功率', 3, '', 'Val(303) + Val(402)', obj=4, kind=3, unit=205)
channel(404, '每小时估算电费', 3, '', 'Val(403) * 0.85', obj=4, kind=3, unit=207)
channel(405, '当前报警点数', 2, '', '((Stat(201) == 11 || Stat(201) == 12 || Stat(201) == 14 || Stat(201) == 15) ? 1 : 0) + ((Stat(301) == 11 || Stat(301) == 12 || Stat(301) == 14 || Stat(301) == 15) ? 1 : 0)', obj=4, kind=3, fmt=1)
channel(406, '水泵联动流量校验', 2, '', 'Val(204) > 0 && Val(202) > 0 ? 1 : 0', obj=4, kind=3, fmt=21)
save(cnls, BASE / 'Cnl.xml')

table('Lim', [dict(LimID=1, Name='液位四级越限（10 / 20 / 80 / 90）', IsBoundToCnl=False, IsShared=False,
                   LoLo=10, Low=20, High=80, HiHi=90, Deadband=1),
              dict(LimID=2, Name='温度四级越限（10 / 16 / 30 / 36）', IsBoundToCnl=False, IsShared=False,
                   LoLo=10, Low=16, High=30, HiHi=36, Deadband=0.5)])
units = ET.parse(BASE / 'Unit.xml').getroot()
for e in list(units):
    if int(e.findtext('UnitID')) >= 201:
        units.remove(e)
for i, name in [(201, '%'), (202, 'm³/h'), (203, 'bar'), (204, '°C'), (205, 'kW'), (206, 'm³'), (207, '元/h')]:
    e = node(units, 'Unit'); node(e, 'UnitID', i); node(e, 'Name', name)
save(units, BASE / 'Unit.xml')

scripts = ET.parse(BASE / 'Script.xml').getroot()
for e in list(scripts):
    if e.findtext('ScriptID') == '101': scripts.remove(e)
e = node(scripts, 'Script'); node(e, 'ScriptID', 101); node(e, 'Name', 'HelloWorld 工艺模拟')
node(e, 'Source', '''// Called by input formulas after each simulator acquisition.
// Setpoint commands arrive through Communicator's native DO/AO tags.
public double DemoLevel()
{
    return Stat(205) > 0 ? Val(205) : 56 + 12 * Math.Sin(Timestamp.TimeOfDay.TotalSeconds / 20);
}
public double DemoTemperature()
{
    return Stat(305) > 0 ? Val(305) : 23 + 3 * Math.Sin(Timestamp.TimeOfDay.TotalSeconds / 15);
}
''')
save(scripts, BASE / 'Script.xml')

comm_path = PROJECT / 'Instances/Default/ScadaComm/Config/ScadaCommConfig.xml'
comm = ET.parse(comm_path).getroot()
comm.find('GeneralOptions/SendModifiedData').text = 'false'
polling = comm.find('Lines/Line/DevicePolling')
for e in list(polling):
    if e.get('number') != '1': polling.remove(e)
for n, name in [(2, '储罐与泵模拟器'), (3, '空调机组模拟器')]:
    ET.SubElement(polling, 'Device', dict(active='true', isBound='true', number=str(n), name=name,
        driver='DrvSimulator', numAddress='0', strAddress='', pollOnCmd='false', timeout='0', delay='0',
        time='00:00:00', period='00:00:00', cmdLine=''))
save(comm, comm_path)

view_types = ET.parse(BASE / 'ViewType.xml').getroot()
for vt in list(view_types):
    if vt.findtext('ViewTypeID') == '101': view_types.remove(vt)
vt = node(view_types, 'ViewType')
for key, value in dict(ViewTypeID=101, Name='HelloWorld 总览', Code='HelloWorldView', Descr='Requires PlgHelloWorld').items(): node(vt,key,value)
save(view_types, BASE / 'ViewType.xml')
view_rows = [dict(ViewID=1, Path='HelloWorld\\', Title='HelloWorld · 智慧工厂', ObjNum=1),
    dict(ViewID=10, Path='HelloWorld\\Overview', ViewTypeID=101, Title='01 运行总览', Ord=1, ObjNum=1),
    dict(ViewID=20, Path='HelloWorld\\Process.mim', ViewTypeID=7, Title='02 原生工艺组态', Ord=2, ObjNum=1),
    dict(ViewID=21, Path='HelloWorld\\Tank.tbl', ViewTypeID=1, Title='03 储罐泵站', Ord=3, ObjNum=2),
    dict(ViewID=22, Path='HelloWorld\\HVAC.tbl', ViewTypeID=1, Title='04 空调机组', Ord=4, ObjNum=3),
    dict(ViewID=23, Path='HelloWorld\\Energy.tbl', ViewTypeID=1, Title='05 能耗与公式', Ord=5, ObjNum=4),
    dict(ViewID=2, Path='HelloWorld\\Simulator.tbl', ViewTypeID=1, Title='06 基础信号实验', Ord=6, ObjNum=1)]
table('View', view_rows)
for filename, devices in [('Tank', [2]), ('HVAC', [3])]:
    root = ET.Element('TableView'); node(node(root, 'TableOptions'), 'UseDefault', True)
    items = node(root, 'TableItems')
    for dev in devices: ET.SubElement(items, 'Item', deviceNum=str(dev), autoText='true')
    save(root, PROJECT / f'Views/HelloWorld/{filename}.tbl')
root = ET.Element('TableView'); node(node(root, 'TableOptions'), 'UseDefault', True)
items = node(root, 'TableItems')
for n in range(401, 407): ET.SubElement(items, 'Item', cnlNum=str(n), autoText='true')
save(root, PROJECT / 'Views/HelloWorld/Energy.tbl')

# Native, editable mimic. Text data bindings and command actions are handled by PlgMimic.
mimic = ET.Element('Mimic', editorVersion='6.0.2.2')
doc = node(mimic, 'Document'); size = node(doc, 'Size'); node(size, 'Width', 1160); node(size, 'Height', 680)
node(doc, 'BackColor', '#f3f6fa'); node(doc, 'ForeColor', '#213047')
font = node(doc, 'Font'); node(font, 'Name', 'Microsoft YaHei'); node(font, 'Size', 16)
components = node(mimic, 'Components'); seq = 0
def comp(text, x, y, w, h, cnl=0, out=0, bg='', color='#213047', font_size=18):
    global seq
    seq += 1; e = node(components, 'Text'); node(e, 'ID', seq); node(e, 'Name', 'Demo' + str(seq))
    node(e, 'Enabled', True); node(e, 'Visible', True)
    p = node(e, 'Location'); node(p, 'X', x); node(p, 'Y', y)
    p = node(e, 'Size'); node(p, 'Width', w); node(p, 'Height', h)
    node(e, 'AutoSize', False); node(e, 'Text', text); node(e, 'TextAlign', 'MiddleLeft')
    node(e, 'BackColor', bg); node(e, 'ForeColor', '' if cnl else color)
    p = node(e, 'Font'); node(p, 'Name', 'Microsoft YaHei'); node(p, 'Size', font_size)
    if cnl: node(e, 'InCnlNum', cnl)
    if out: node(e, 'OutCnlNum', out)
    if cnl or out:
        a = node(e, 'ClickAction'); node(a, 'ActionType', 'SendCommand' if out else 'DrawChart')
    return e
comp('智慧工厂 / 工艺组态', 36, 22, 1000, 55, font_size=30)
comp('原生 Mimic 画面 · 点击数值查看趋势，点击控制按钮发送命令', 36, 80, 1060, 36, color='#61728a', font_size=16)
comp('储罐泵站', 36, 140, 510, 50, bg='#e1eaf7', font_size=24)
comp('空调机组', 606, 140, 510, 50, bg='#e1eaf7', font_size=24)
for x, title, cnl, y in [(56,'储罐液位',201,210),(56,'出水流量',202,270),(56,'管道压力',203,330),
                         (626,'送风温度',301,210),(626,'环境湿度',302,270),(626,'风机功率',303,330)]:
    comp(title, x, y, 220, 40)
    comp('---', x+240, y, 220, 40, cnl=cnl, font_size=24)
comp('水泵启停', 56, 400, 210, 52, out=204, bg='#d9f2ea')
comp('液位设定 / 报警实验', 286, 400, 250, 52, out=205, bg='#e1eaf7')
comp('风机启停', 626, 400, 210, 52, out=304, bg='#d9f2ea')
comp('温度设定 / 报警实验', 856, 400, 250, 52, out=305, bg='#e1eaf7')
comp('总功率', 56, 500, 180, 40); comp('---', 236, 500, 240, 40, cnl=403, font_size=26)
comp('当前报警点数', 626, 500, 230, 40); comp('---', 856, 500, 200, 40, cnl=405, font_size=26)
comp('实验：液位设为 95 触发高高限，设为 55 恢复；温度设为 40 触发高高限，设为 23 恢复。', 36, 590, 1080, 50, color='#61728a', font_size=15)
save(mimic, PROJECT / 'Views/HelloWorld/Process.mim')

web_path = PROJECT / 'Instances/Default/ScadaWeb/config/ScadaWebConfig.xml'
web = ET.parse(web_path).getroot()
web.find('GeneralOptions/DefaultStartPage').text = '~/View/10'
web.find('GeneralOptions/DefaultCulture').text = 'zh-Hans'
web.find('GeneralOptions/ShareStats').text = 'false'
plugins = web.find('Plugins'); plugins.clear()
for code in ['PlgChart','PlgMain','PlgMimic','PlgMimicEditor','PlgMimBasicComp','PlgWebPage','PlgHelloWorld']:
    ET.SubElement(plugins, 'Plugin', code=code)
for group in web.findall('CustomOptions/OptionGroup'):
    if group.get('name') == 'Main':
        for opt in group.findall('Option'):
            if opt.get('name') == 'TableArchiveCode': opt.set('value','Min')
            if opt.get('name') == 'TablePeriod': opt.set('value','1')
save(web, web_path)

# Public learning credentials, isolated to this sample project.
import hashlib, struct
def password_hash(key, password):
    md5 = lambda b: hashlib.md5(b).hexdigest().upper()
    return md5((md5(password.encode()) + md5(struct.pack('<i', key)) + 'aEGnwn3CCSFdth7kNXc3').encode())
users = ET.parse(BASE / 'User.xml').getroot()
for user in list(users):
    if user.findtext('Name') in ('operator', 'viewer'): users.remove(user)
for key, name, role in [(13, 'operator', 2), (14, 'viewer', 3)]:
    user = node(users, 'User')
    for field, val in dict(UserID=key, Enabled=True, Name=name, Password=password_hash(key,'demo123'), RoleID=role).items():
        node(user, field, val)
save(users, BASE / 'User.xml')
import runpy
runpy.run_path(str(ROOT / 'tools/Generate-IndustrialDemo.py'), run_name='__main__')
print('HelloWorld enriched: 3 devices, 24 channel definitions / 30 points, 8 views, native mimic and alarm limits.')

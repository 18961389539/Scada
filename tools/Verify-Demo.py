"""Integration smoke test against the running, local HelloWorld sample.

Exercises real authentication, native command forms, limit processing, event
acknowledgement, historian, reports, and read-only role enforcement.
Leaves the demo in its normal state with both simulated devices running.
"""
import http.cookiejar
import json
import re
import time
import urllib.parse
import urllib.request
import urllib.error
import xml.etree.ElementTree as ET
from datetime import datetime, timedelta, timezone
from html import unescape
from pathlib import Path

ROOT = 'http://127.0.0.1:5000'
results = []

def check(condition, message):
    if not condition: raise AssertionError(message)
    results.append(message)
    print('PASS ' + message)

class Session:
    def __init__(self):
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def request(self, path, fields=None, json_body=None):
        headers = {}
        payload = None
        if fields is not None: payload = urllib.parse.urlencode(fields).encode()
        if json_body is not None:
            payload = json.dumps(json_body).encode()
            headers['Content-Type'] = 'application/json'
        req = urllib.request.Request(ROOT + path, data=payload, headers=headers)
        with self.client.open(req, timeout=20) as response:
            return response.read(), response.headers, response.url
    def text(self, path, fields=None): return self.request(path, fields)[0].decode('utf-8')
    def api(self, path, **params):
        data = json.loads(self.request('/Api/Main/' + path + '?' + urllib.parse.urlencode(params))[0])
        if not data['ok']: raise AssertionError(data.get('msg'))
        return data['data']
    def login(self, name, password):
        html = self.text('/Login')
        self.text('/Login', dict(Username=name, Password=password, __RequestVerificationToken=token(html)))
        check(name in self.text('/Demo'), name + ' login succeeds')
    def command(self, cnl, value):
        path = '/Main/Command?cnlNum=' + str(cnl)
        html = self.text(path)
        html = self.text(path, dict(CmdVal=value, __RequestVerificationToken=token(html)))
        check('alert-success' in html, f'Native command accepted: {cnl} = {value}')

def token(html):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html)
    if not match: raise AssertionError('Antiforgery token not found')
    return unescape(match.group(1))

def eventually(predicate, description, timeout=18):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate(): check(True, description); return
        time.sleep(1)
    raise AssertionError(description)

admin = Session(); admin.login('admin', '12345')
current = lambda: {p['cnlNum']: p for p in admin.api('GetCurData', cnlNums='201-205,301-305,401-406')}
check(all(current()[n]['stat'] > 0 for n in [201,202,203,301,302,303,401,402,403,404,405,406]), 'New acquisition and calculated channels have valid values')
check(all(p['stat'] > 0 for p in admin.api('GetCurData', cnlNums='206-208,306-308')), 'Both native three-element array channels are acquired')
check(admin.api('LoadView', viewID=10) == 'MimicView', 'Overview uses the native editable mimic view')
overview_channels = {p['d']['cnlNum'] for p in admin.api('GetCurDataByView', viewID=10, cnlListID=0, appendUnit='false')['records']}
check({201,202,204,301,403,405}.issubset(overview_channels), 'Overview binds its displayed process and alarm channels')
for path in ['/Mimic/MimicView/20', '/Mimic/MimicView/24', '/Mimic/MimicView/21', '/Mimic/MimicView/22', '/Mimic/MimicView/23', '/Reports', '/Main/HistDataReport', '/Main/EventReport']:
    check('<html' in admin.text(path).lower(), 'Native page loads: ' + path)
runtime = Path(__file__).resolve().parents[1] / '.local/HelloWorld'
editor_path = '/MimicEditor/MimicOpen?' + urllib.parse.urlencode(dict(fileName=str(runtime / 'Views/HelloWorld/Process.mim')))
editor, _, editor_url = admin.request(editor_path)
check('/MimicEditor/MimicEdit/' in editor_url, 'Native online editor opens the deployed project mimic')
editor_html = editor.decode('utf-8')
check('editorChannelCatalog' in editor_html and 'componentToolsTemplate' in editor_html, 'Editor includes searchable components and a real channel catalog')
check('channelPicker' in editor_html and 'usability.js' in editor_html, 'Channel binding and save feedback load in the editor')

# Check physical semantics against the channel database, not only against rendered labels.
base = Path(__file__).resolve().parents[1] / 'Projects/HelloWorld'
units = {u.findtext('UnitID'): u.findtext('Name') for u in ET.parse(base / 'BaseXML/Unit.xml').getroot()}
channel_units = {int(c.findtext('CnlNum')): units.get(c.findtext('UnitID'), '') for c in ET.parse(base / 'BaseXML/Cnl.xml').getroot()}
for name in ['Overview', 'Tank', 'HVAC', 'Energy', 'Simulator']:
    page = ET.parse(runtime / f'Views/HelloWorld/{name}.mim').getroot()
    width, height = int(page.findtext('Document/Size/Width')), int(page.findtext('Document/Size/Height'))
    bounds_ok, units_ok, energy_ok = True, True, True
    for c in page.find('Components'):
        bounds_ok &= (int(c.findtext('Location/X')) + int(c.findtext('Size/Width')) <= width and
                      int(c.findtext('Location/Y')) + int(c.findtext('Size/Height')) <= height)
        cnl = int(c.findtext('InCnlNum') or 0)
        if cnl >= 201 and channel_units.get(cnl) and c.find('Unit') is not None and c.findtext('IntegratePower') != 'true':
            units_ok &= c.findtext('Unit') == channel_units[cnl]
        energy_ok &= not (c.tag == 'IndustrialEnergyMeter' and cnl == 403 and c.findtext('IntegratePower') != 'true')
    check(bounds_ok, f'{name}: all components fit the scrollable canvas')
    check(units_ok, f'{name}: displayed physical units match the channel database')
    check(energy_ok, f'{name}: instantaneous power is not mislabeled cumulative energy')
    if name == 'Energy':
        cards = [c for c in page.find('Components') if c.tag == 'IndustrialKpiCard' and c.findtext('InCnlNum') == '403']
        check(cards and all(float(c.findtext('Maximum')) >= 5.8 for c in cards), 'Total power range contains simultaneous pump and fan operation')

operator = Session(); operator.login('operator', 'demo123')
check('data-control="true"' in operator.text('/Demo'), 'Operator can control')
viewer = Session(); viewer.login('viewer', 'demo123')
check('data-control="false"' in viewer.text('/Demo'), 'Viewer dashboard is read-only')
denied = json.loads(viewer.request('/Api/Main/SendCommand', json_body=dict(CnlNum=204, CmdVal=1))[0])
check(not denied['ok'], 'Server rejects a forged viewer command, independent of disabled UI')

try:
    admin.command(205, 55); admin.command(305, 23)
    eventually(lambda: current()[201]['stat'] == 13 and current()[301]['stat'] == 13, 'Alarm exercise starts from normal conditions')
    operator.command(204, 1); operator.command(304, 1)
    eventually(lambda: current()[202]['val'] > 0 and current()[303]['val'] > 2.7, 'Pump and fan commands change acquired flow and power')
    d = current()
    check(abs(d[403]['val'] - d[402]['val'] - d[303]['val']) < 0.2, 'Server-side total power formula is consistent')
    check(abs(d[404]['val'] - d[403]['val'] * 0.85) < 0.2, 'Cost estimate is computed on server')

    before_ids = {e['id'] for e in admin.api('GetLastAvailableEvents', archiveBit=4, period=2, limit=100, filterID=0)['records']}
    admin.command(205, 95); admin.command(305, 40)
    eventually(lambda: current()[201]['stat'] == 15 and current()[301]['stat'] == 15 and current()[405]['val'] == 2, 'Both high-high alarms and the active alarm count are generated')
    def find_alarm():
        events = admin.api('GetLastAvailableEvents', archiveBit=4, period=2, limit=100, filterID=0)['records']
        return next((e for e in events if e['id'] not in before_ids and e['e']['cnlNum'] == 201 and e['e']['cnlStat'] == 15 and not e['e']['ack']), None)
    eventually(lambda: find_alarm() is not None, 'New high-high event persists in native event archive')
    alarm = find_alarm()
    ack_path = '/Main/EventAck?' + urllib.parse.urlencode(dict(eventID=alarm['id'], archiveBit=4))
    ack_html = admin.text(ack_path)
    result = admin.text(ack_path, dict(__RequestVerificationToken=token(ack_html)))
    check('alert-success' in result, 'Native alarm acknowledgement succeeds')
    events = admin.api('GetLastAvailableEvents', archiveBit=4, period=2, limit=100, filterID=0)['records']
    eventually(lambda: any(e['id'] == alarm['id'] and e['e'].get('ack') for e in admin.api('GetLastAvailableEvents', archiveBit=4, period=2, limit=100, filterID=0)['records']), 'Acknowledgement is persisted')
finally:
    admin.command(205, 55); admin.command(305, 23)
eventually(lambda: current()[201]['stat'] == 13 and current()[301]['stat'] == 13 and current()[405]['val'] == 0, 'Normal setpoints clear both active alarms')

now = datetime.now(timezone.utc)
hist = admin.api('GetHistData', archiveBit=1, cnlNums='201,301,403', startTime=(now-timedelta(hours=1)).isoformat(), endTime=now.isoformat(), endInclusive='true')
check(len(hist['timestamps']) > 0 and any(p['d']['stat'] > 0 for p in hist['trends'][0]), 'Native minute archive contains real acquired data')
args = urllib.parse.urlencode(dict(startTime=datetime.now().replace(hour=0,minute=0,second=0,microsecond=0).isoformat(), endTime=datetime.now().isoformat(), archive='Min', cnlNums='201,301,403'))
report, headers, url = admin.request('/Main/Print/PrintHistDataReport?' + args)
check(len(report) > 500 and 'Login' not in url and (b'Workbook' in report or report.startswith(b'PK')), 'Native historical report generates an Excel-compatible workbook')
out = Path(__file__).resolve().parents[1] / '.local/HelloWorld/verification'
out.mkdir(exist_ok=True)
(out / 'historical-report.xml').write_bytes(report)
csv_args = urllib.parse.urlencode(dict(handler='Export', cnlNum=201, startTime=(now-timedelta(hours=1)).isoformat(), endTime=now.isoformat()))
csv_data, csv_headers, _ = viewer.request('/Demo?' + csv_args)
check(csv_headers.get('Content-Type','').startswith('text/csv') and len(csv_data.decode('utf-8-sig').splitlines()) > 1, 'Read-only user can download a real historical CSV file')
(out / 'tank-history.csv').write_bytes(csv_data)
try:
    viewer.request('/Demo?' + csv_args.replace('cnlNum=201', 'cnlNum=999999'))
    raise AssertionError('Invalid CSV channel accepted')
except urllib.error.HTTPError as error:
    check(error.code == 400, 'CSV export rejects channels outside the learning example')
(out / 'results.json').write_text(json.dumps(dict(passed=len(results), checks=results), ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Completed: {len(results)} checks passed. Artifacts: {out}')

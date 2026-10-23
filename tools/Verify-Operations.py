"""Exercise real workflow APIs against the local simulator, with disposable test recipes/plans."""
import http.cookiejar
import json
import re
import time
import uuid
import urllib.request
import urllib.parse
import urllib.error
from datetime import datetime, timedelta, timezone
from html import unescape
from pathlib import Path

ROOT = 'http://127.0.0.1:5000'
checks = []
def check(condition, message):
    if not condition: raise AssertionError(message)
    checks.append(message); print('PASS ' + message, flush=True)
def eventually(predicate, message, timeout=25):
    end = time.monotonic() + timeout
    while time.monotonic() < end:
        if predicate(): check(True, message); return
        time.sleep(.5)
    raise AssertionError(message)
class Session:
    def __init__(self, name, password):
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        html = self.request('/Login').decode()
        self.request('/Login', form={'Username': name, 'Password': password, '__RequestVerificationToken': self.token(html)})
        html = self.request('/Mimic/MimicView/10').decode()
        self.csrf = self.token(html)
        check('runtimeNavigation' in html, name + ' receives persistent runtime navigation')
    @staticmethod
    def token(html): return unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', html).group(1))
    def request(self, path, form=None, body=None, csrf=True):
        headers = {}; data = None
        if form is not None: data = urllib.parse.urlencode(form).encode()
        if body is not None:
            data = json.dumps(body).encode(); headers['Content-Type'] = 'application/json'
            if csrf: headers['RequestVerificationToken'] = self.csrf
        return self.client.open(urllib.request.Request(ROOT + path, data=data, headers=headers), timeout=90).read()
    def api(self, action, body=None, **params):
        path = '/Api/Operations/' + action + ('?' + urllib.parse.urlencode(params) if params else '')
        dto = json.loads(self.request(path, body=body))
        check(dto['ok'], 'API succeeds: ' + action)
        return dto.get('data')
    def denied(self, action, body, status=403, csrf=True):
        try: self.request('/Api/Operations/' + action, body=body, csrf=csrf)
        except urllib.error.HTTPError as error: check(error.code == status, action + ' rejects unauthorized/invalid request'); return
        raise AssertionError(action + ' unexpectedly accepted')
    def current(self):
        dto = json.loads(self.request('/Api/Main/GetCurData?cnlNums=201,205,301,305'))
        return {p['cnlNum']: p for p in dto['data']}
    def command(self, channel, value):
        dto = json.loads(self.request('/Api/Main/SendCommand', body={'CnlNum': channel, 'CmdVal': value}))
        check(dto['ok'], f'Native simulator command {channel}={value}')

admin = Session('admin', '12345')
operator = Session('operator', 'demo123')
viewer = Session('viewer', 'demo123')
views = admin.api('Views')
check({10,20,21,22,23,2,24}.issubset(v['id'] for v in views), 'Navigation includes every configured demo page')
check(all(v['url'].startswith('/View/') for v in views), 'Navigation targets complete runtime pages')
check(not viewer.api('Channels')['admin'], 'Viewer catalog cannot grant administrator actions')
check(not any(c['writable'] for c in viewer.api('Channels')['channels']), 'Viewer has no writable channel choices')

recipe_id = schedule_id = ''
name = 'workflow-test-' + uuid.uuid4().hex[:8]
parameter = lambda channel, feedback, value, maximum=100: dict(channel=channel, feedbackChannel=feedback, value=value, minimum=0, maximum=maximum, step=1, tolerance=.01)
try:
    payload = dict(name=name, parameters=[parameter(205,201,55), parameter(305,301,23)])
    viewer.denied('SaveRecipe', payload)
    operator.denied('SaveRecipe', payload)
    admin.denied('SaveRecipe', payload, 400, csrf=False)
    invalid = dict(name=name, parameters=[parameter(205,201,55), parameter(205,201,56)])
    admin.denied('SaveRecipe', invalid, 400)
    admin.denied('SaveRecipe', dict(name=name, parameters=[parameter(201,201,55)]), 403)
    admin.denied('SaveRecipe', dict(name=name, parameters=[parameter(205,201,101)]), 400)
    recipe = admin.api('SaveRecipe', body=payload); recipe_id = recipe['id']
    check(any(r['id'] == recipe_id for r in operator.api('Recipes')), 'Saved recipe is visible across independent sessions')
    viewer.denied('RunRecipe', {'id': recipe_id})
    result = operator.api('RunRecipe', body={'id': recipe_id, 'parameters': [parameter(205,201,95)]})
    check(result['completed'] and len(result['rows']) == 2 and all(r['result'] == '回读已确认' for r in result['rows']), 'Operator runs the saved recipe with acquired feedback for every value')
    check(admin.current()[201]['val'] == 55, 'Forged run-time recipe values cannot override the saved recipe')

    admin.command(205,95); admin.command(305,40)
    eventually(lambda: {r['channel'] for r in json.loads(admin.request('/Api/Operations/ActiveAlarms?channels=201,301'))['data']['rows']} == {201,301}, 'Active alarm table reflects current high-high measurements')
    rows = admin.api('ActiveAlarms', channels='201,301')['rows']
    check(all(r['status'] == 15 and r['eventId'] and r['canAck'] for r in rows), 'Active alarms locate the current native event without truncating its ID')
    check(all(not r['canAck'] for r in viewer.api('ActiveAlarms', channels='201,301')['rows']), 'Viewer cannot acknowledge active alarms')
    admin.command(205,55); admin.command(305,23)
    eventually(lambda: not json.loads(admin.request('/Api/Operations/ActiveAlarms?channels=201,301'))['data']['rows'], 'Recovered measurements disappear from the active alarm table even when events remain unacknowledged')

    energy = viewer.api('Energy', channel=403, archiveBit=1, minutes=120)
    check(energy['kwh'] is not None and energy['kwh'] >= 0 and energy['coveredMinutes'] > 0, 'Power integration uses real archive intervals with measured coverage')
    try: viewer.api('Energy', channel=201)
    except urllib.error.HTTPError as error: check(error.code == 400, 'Power integration rejects non-power units')
    else: raise AssertionError('Non-power input accepted')

    schedule = dict(name=name, command=parameter(205,201,55), nextRunUtc=(datetime.now(timezone.utc)+timedelta(seconds=8)).isoformat(), enabled=True, repeatDays='')
    viewer.denied('SaveSchedule', schedule); operator.denied('SaveSchedule', schedule)
    saved = admin.api('SaveSchedule', body=schedule); schedule_id = saved['id']
    eventually(lambda: any(s['id']==schedule_id and not s['enabled'] and s['lastResult']=='回读已确认' for s in json.loads(admin.request('/Api/Operations/Schedules'))['data']), 'Server executes and confirms a one-shot schedule without a browser timer')
    log = admin.api('Log', query=name)
    check(sum(r['action'] == '计划：'+name and r['result']=='回读已确认' for r in log) == 1, 'Scheduled occurrence executes exactly once')
    time.sleep(3)
    log = admin.api('Log', query=name)
    check(sum(r['action'] == '计划：'+name and r['result']=='回读已确认' for r in log) == 1, 'Completed schedule is not automatically resent on later worker ticks')
    check(any(r['action']=='配方：'+name and r['user']=='operator' and r['before'] is not None for r in log), 'Operation history contains actor, previous value, target and confirmed outcome')
    native = admin.api('Log', channel=205)
    check(any(r['action']=='原生命令' and r['value']==95 for r in native), 'Native API commands also appear in the operation log')
    store = Path(__file__).resolve().parents[1] / '.local/HelloWorld/ScadaWeb/storage/industrial-operations.json'
    check(store.exists() and recipe_id in store.read_text(encoding='utf-8'), 'Recipes, schedules and results are durably stored on disk')
finally:
    if schedule_id: admin.api('DeleteSchedule', body={'id': schedule_id})
    if recipe_id: admin.api('DeleteRecipe', body={'id': recipe_id})
    admin.command(205,55); admin.command(305,23)

out = Path(__file__).resolve().parents[1] / '.local/HelloWorld/verification/operations-results.json'
out.write_text(json.dumps({'passed':len(checks),'checks':checks}, ensure_ascii=False, indent=2), encoding='utf-8')
print(f'Completed: {len(checks)} workflow checks passed.', flush=True)

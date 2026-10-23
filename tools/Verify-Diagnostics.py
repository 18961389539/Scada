"""Integration checks for read-only interlocks, event statistics and server-authorized reset.
Uses only the local simulator, restores changed values and removes its temporary policy.
"""
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
class Session:
    def __init__(self, name, password):
        self.client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        html = self.request('/Login').decode()
        token = lambda text: unescape(re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', text).group(1))
        self.request('/Login', form={'Username':name,'Password':password,'__RequestVerificationToken':token(html)})
        self.csrf = token(self.request('/Mimic/MimicView/21').decode())
    def request(self, path, form=None, body=None, csrf=True):
        data = None; headers = {}
        if form is not None: data = urllib.parse.urlencode(form).encode()
        if body is not None:
            data = json.dumps(body).encode(); headers['Content-Type'] = 'application/json'
            if csrf: headers['RequestVerificationToken'] = self.csrf
        return self.client.open(urllib.request.Request(ROOT+path, data=data, headers=headers), timeout=90).read()
    def api(self, action, body=None, **params):
        dto = json.loads(self.request('/Api/Operations/'+action+'?'+urllib.parse.urlencode(params), body=body))
        check(dto['ok'], 'API succeeds: '+action); return dto['data']
    def rejected(self, action, status=400, body=None, csrf=True, **params):
        try: self.request('/Api/Operations/'+action+'?'+urllib.parse.urlencode(params),body=body,csrf=csrf)
        except urllib.error.HTTPError as ex: check(ex.code==status, action+' rejects invalid or unauthorized request'); return
        raise AssertionError(action+' unexpectedly accepted')
    def current(self, channels):
        return {d['cnlNum']:d for d in json.loads(self.request('/Api/Main/GetCurData?cnlNums='+channels))['data']}
    def command(self, channel, value):
        dto = json.loads(self.request('/Api/Main/SendCommand',body={'CnlNum':channel,'CmdVal':value}))
        check(dto['ok'], f'Simulator command {channel}={value}')
def eventually(predicate, text):
    deadline=time.monotonic()+20
    while time.monotonic()<deadline:
        if predicate(): check(True,text); return
        time.sleep(.4)
    raise AssertionError(text)

admin=Session('admin','12345'); viewer=Session('viewer','demo123'); operator=Session('operator','demo123')
unknown=viewer.api('Interlocks',channels='204,304',expectedValues='1,1')
check(unknown['permitted'] is None and len(unknown['rows'])==2, 'Matched component conditions cannot invent final controller permission')
actual=admin.current('204,205,201')
permit=viewer.api('Interlocks',channels='204',expectedValues=str(actual[204]['val']),permitChannel=204)
check(permit['rows'][0]['matched'] and permit['permitted']==(actual[204]['val']==1), 'Final permission reads the selected server channel independently')
viewer.rejected('Interlocks',channels='204,304',expectedValues='1')
viewer.rejected('Interlocks',channels='204',expectedValues='NaN')
viewer.rejected('Interlocks',status=403,channels='999999')
check(viewer.api('Diagnostics')['bound'] is False, 'Unbound diagnostics never display a normal device state')
viewer.rejected('Diagnostics',status=403,channel=999999)
viewer.rejected('ResetFault',body={'id':'nonexistent'})

now=datetime.now(timezone.utc)
stats=viewer.api('AlarmStats',channels='201,301',start=(now-timedelta(days=1)).isoformat(),end=now.isoformat())
check(len(stats['rows'])==2 and all(0 <= r['knownSeconds'] <= 86400.01 and r['alarmSeconds'] <= r['knownSeconds'] for r in stats['rows']), 'Alarm durations never exceed valid state coverage')
check(all(isinstance(r['occurrences'],int) and isinstance(r['chatter'],int) for r in stats['rows']), 'Alarm rankings expose numeric occurrence and chatter counts')
viewer.rejected('AlarmStats',channels='201',start=now.isoformat(),end=(now-timedelta(minutes=1)).isoformat())
viewer.rejected('AlarmStats',channels='201',start=(now-timedelta(days=8)).isoformat(),end=now.isoformat())
viewer.rejected('AlarmStats',status=403,channels='999999')

policy_path=Path(__file__).resolve().parents[1]/'.local/HelloWorld/ScadaWeb/storage/industrial-diagnostics.json'
policy_id='diagnostics-test-'+uuid.uuid4().hex
policy={'id':policy_id,'faultChannel':204,'resetAllowedCodes':[1],
        'reset':{'channel':204,'feedbackChannel':204,'value':0,'minimum':0,'maximum':1,'step':1,'tolerance':.01},
        'conditions':[{'channel':205,'expected':actual[205]['val']}]}
def load_policies(): return json.loads(policy_path.read_text(encoding='utf-8-sig')) if policy_path.exists() else []
def write_policy(p):
    others=[v for v in load_policies() if v.get('id',v.get('Id'))!=policy_id]
    policy_path.write_text(json.dumps(others+[p],ensure_ascii=False),encoding='utf-8')
try:
    range_start=datetime.now(timezone.utc)-timedelta(seconds=1)
    admin.command(205,95)
    eventually(lambda: admin.current('201')[201]['val']==95,'Simulator acquires a real high-high alarm for statistics')
    time.sleep(1)
    admin.command(205,actual[205]['val'])
    eventually(lambda: admin.current('201')[201]['val']==actual[205]['val'],'Simulator acquires recovery for the statistics interval')
    def archive_recovered():
        params=urllib.parse.urlencode(dict(channels='201',start=range_start.isoformat(),end=datetime.now(timezone.utc).isoformat()))
        current_stats=json.loads(viewer.request('/Api/Operations/AlarmStats?'+params))['data']['rows'][0]
        return current_stats['occurrences']==1 and current_stats['recoveries']==1 and current_stats['alarmSeconds']>0 and current_stats['activeAtEnd'] is False
    eventually(archive_recovered,'Real archive onset and recovery produce one occurrence and measured duration')
    write_policy(policy)
    admin.command(204,1)
    eventually(lambda: admin.current('204')[204]['val']==1,'Simulator has an actual nonzero test fault signal')
    data=operator.api('Diagnostics',channel=204,policyId=policy_id)
    check(data['code']==1 and data['canReset'], 'Operator can request a server-configured reset with valid conditions')
    check(not viewer.api('Diagnostics',channel=204,policyId=policy_id)['canReset'],'Viewer diagnostics never enable reset')
    viewer.rejected('ResetFault',status=403,body={'id':policy_id})
    admin.rejected('ResetFault',body={'id':policy_id},csrf=False)
    admin.rejected('Diagnostics',channel=201,policyId=policy_id)
    policy['conditions'][0]['expected']=999; write_policy(policy)
    check(not operator.api('Diagnostics',channel=204,policyId=policy_id)['canReset'],'Changed server permissive blocks the reset action')
    operator.rejected('ResetFault',body={'id':policy_id})
    check(admin.current('204')[204]['val']==1,'Blocked reset does not send an output command')
    policy['conditions'][0]['expected']=actual[205]['val']; write_policy(policy)
    outcome=operator.api('ResetFault',body={'id':policy_id,'channel':205,'value':95})
    check(outcome['confirmed'] and outcome['rows'][0]['channel']==204,'Server uses its saved output binding and ignores forged command fields')
    check(admin.current('205')[205]['val']==actual[205]['val'],'Forged output binding leaves the unrelated channel untouched')
    check(operator.api('Diagnostics',channel=204,policyId=policy_id)['code']==0,'Diagnostics rereads the actual post-reset fault state')
    operator.rejected('ResetFault',body={'id':policy_id})
    records=admin.api('Log',query=policy_id)
    check(any(r['action']=='故障复位：'+policy_id and r['user']=='operator' and r['result']=='回读已确认' for r in records),'Reset audit records the actor, server policy and verified command result')
finally:
    admin.command(204,actual[204]['val'])
    admin.command(205,actual[205]['val'])
    remaining=[v for v in load_policies() if v.get('id',v.get('Id'))!=policy_id]
    if remaining: policy_path.write_text(json.dumps(remaining,ensure_ascii=False),encoding='utf-8')
    elif policy_path.exists(): policy_path.unlink()

out=Path(__file__).resolve().parents[1]/'.local/HelloWorld/verification/diagnostics-results.json'
out.write_text(json.dumps({'passed':len(checks),'checks':checks},ensure_ascii=False,indent=2),encoding='utf-8')
print(f'Completed: {len(checks)} diagnostics checks passed.',flush=True)

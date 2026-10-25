#!/usr/bin/env python3
"""抽取后端返回给用户的中文提示（BadRequest/Forbid/message= 等），转为 {0} 占位模式，输出待翻译清单。"""
import re, glob, json, sys
DIRS = ['src/Scada.Server/Endpoints', 'src/Scada.Server/Auth', 'src/Scada.Engine/Control', 'src/Scada.Core/Models', 'src/Scada.Server/Projects', 'src/Scada.Server/Program.cs']
PAT = re.compile(r'(BadRequest|Conflict|NotFound|Problem|ForbidAsync|Fail|Error|Issue|Add|Reject|Deny|message\s*[=:]|Message\s*=|throw new \w+Exception)\s*\(?[^;]*?(\$?)"((?:[^"\\\n]|\\.)*[\u4e00-\u9fff](?:[^"\\\n]|\\.)*)"')
def holes(s):
    out = ''; i = 0; n = 0
    while i < len(s):
        if s.startswith('{{', i): out += '{'; i += 2; continue
        if s.startswith('}}', i): out += '}'; i += 2; continue
        if s[i] == '{':
            depth = 1; j = i + 1
            while j < len(s) and depth:
                depth += {'{': 1, '}': -1}.get(s[j], 0); j += 1
            out += '{' + str(n) + '}'; n += 1; i = j; continue
        out += s[i]; i += 1
    return out
msgs = set()
for d in DIRS:
    for f in ([d] if d.endswith('.cs') else glob.glob(d + '/**/*.cs', recursive=True)):
        s = re.sub(r'//.*', '', open(f, encoding='utf-8').read())
        for m in PAT.finditer(s):
            text = m.group(3).replace('\\"', '"').replace('\\n', '\n')
            msgs.add(holes(text) if m.group(2) else text)
json.dump(sorted(msgs), sys.stdout, ensure_ascii=False, indent=0)

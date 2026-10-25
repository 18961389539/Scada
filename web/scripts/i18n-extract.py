#!/usr/bin/env python3
"""一次性/增量抽取：把 src 下 .vue/.ts 中的中文字面量替换为 t()/$t() 调用，并把文案写入 src/i18n/locales/zh-CN.json。
带 `i18n-ignore` 注释的行不处理；对象键位置的中文字符串不处理（由 i18n-check 报告）。"""
import hashlib, json, os, re, sys

ROOT = os.path.join(os.path.dirname(__file__), '..', 'src')
ZH = os.path.join(ROOT, 'i18n', 'locales', 'zh-CN.json')
CJK = re.compile(r'[\u4e00-\u9fff\u3000-\u303f\uff00-\uffef]')
HAN = re.compile(r'[\u4e00-\u9fff]')
messages = json.load(open(ZH, encoding='utf-8')) if os.path.exists(ZH) else {}

def ns_of(path):
    base = os.path.splitext(os.path.basename(path))[0]
    return base[0].lower() + base[1:]

def escape_msg(text):
    out = []
    for ch in text:
        out.append("{'" + ch + "'}" if ch in '{}@$|' else ch)
    return ''.join(out)

def key_for(ns, msg):
    h = hashlib.sha1(msg.encode('utf-8')).hexdigest()[:6]
    messages.setdefault(ns, {})
    existing = messages[ns].get('k' + h)
    if existing is not None and existing != msg:
        h = hashlib.sha1(('x' + msg).encode('utf-8')).hexdigest()[:7]
    messages[ns]['k' + h] = msg
    return f'{ns}.k{h}'

def js_quote(s):
    return "'" + s.replace('\\', '\\\\').replace("'", "\\'") + "'"

# ---------- JS ----------
def split_template_literal(src, i):
    """src[i] == '`'. returns (end_index_after, parts[list of (str|('expr',code))])"""
    j = i + 1; parts = []; buf = ''
    while j < len(src):
        c = src[j]
        if c == '\\': buf += src[j:j+2]; j += 2; continue
        if c == '`': parts.append(buf); return j + 1, parts
        if c == '$' and src[j+1:j+2] == '{':
            parts.append(buf); buf = ''
            depth = 1; k = j + 2
            while k < len(src) and depth:
                ch = src[k]
                if ch == '/' and src[:k].rstrip()[-1:] in ('(', ','):
                    k = src.index('/', k + 1) + 1; continue
                if ch in '\'"`':
                    k = skip_string(src, k); continue
                if ch == '{': depth += 1
                elif ch == '}': depth -= 1
                k += 1
            parts.append(('expr', src[j+2:k-1])); j = k; continue
        buf += c; j += 1
    raise ValueError('unterminated template literal')

def skip_string(src, i):
    q = src[i]
    if q == '`': return split_template_literal(src, i)[0]
    j = i + 1
    while j < len(src):
        if src[j] == '\\': j += 2; continue
        if src[j] == q: return j + 1
        if src[j] == '\n': return j
        j += 1
    return j

def unescape_js(s):
    return re.sub(r"\\(.)", lambda m: {'n': '\n', 't': '\t'}.get(m.group(1), m.group(1)), s)

def transform_js(src, ns, fn):
    out = []; i = 0; n = len(src); changed = False
    line_ignored = lambda pos: 'i18n-ignore' in src[src.rfind('\n', 0, pos) + 1: (src.find('\n', pos) if src.find('\n', pos) >= 0 else n)]
    while i < n:
        c = src[i]
        if src.startswith('//', i):
            j = src.find('\n', i); j = n if j < 0 else j; out.append(src[i:j]); i = j; continue
        if src.startswith('/*', i):
            j = src.find('*/', i + 2); j = n if j < 0 else j + 2; out.append(src[i:j]); i = j; continue
        if c == '/' and (src[:i].rstrip()[-1:] in tuple('(,=:[!&|?{};') or src[:i].rstrip().endswith('return')):
            j = i + 1; in_class = False
            while j < n and src[j] != '\n':
                ch = src[j]
                if ch == '\\': j += 2; continue
                if ch == '[': in_class = True
                elif ch == ']': in_class = False
                elif ch == '/' and not in_class: break
                j += 1
            out.append(src[i:j + 1]); i = j + 1; continue
        if c in '\'"`':
            j = skip_string(src, i); lit = src[i:j]
            if not HAN.search(lit) or line_ignored(i):
                if c == '`' and '${' in lit:
                    # 递归处理插值表达式里的字符串
                    _, parts = split_template_literal(src, i)
                    rebuilt = '`'
                    for p in parts:
                        rebuilt += ('${' + transform_js(p[1], ns, fn)[0] + '}') if isinstance(p, tuple) else p
                    rebuilt += '`'; out.append(rebuilt); changed |= rebuilt != lit
                else: out.append(lit)
                i = j; continue
            prev = src[:i].rstrip()[-1:] ; nxt = src[j:].lstrip()[:1]
            before = src[:i].rstrip()
            if nxt == ':' and prev in '{,' or before.endswith('import') or before.endswith('from') or prev == '[' and nxt == ']' and False:
                out.append(lit); i = j; continue
            if c == '`':
                _, parts = split_template_literal(src, i)
                msg = ''; args = []
                for p in parts:
                    if isinstance(p, tuple): msg += '{' + str(len(args)) + '}'; args.append(transform_js(p[1], ns, fn)[0].strip())
                    else: msg += escape_msg(unescape_js(p))
                k = key_for(ns, msg)
                out.append(f"{fn}('{k}', [{', '.join(args)}])" if args else f"{fn}('{k}')")
            else:
                k = key_for(ns, escape_msg(unescape_js(lit[1:-1])))
                out.append(f"{fn}('{k}')")
            changed = True; i = j; continue
        out.append(c); i += 1
    return ''.join(out), changed

# ---------- template ----------
def transform_template(tpl, ns):
    out = []; i = 0; n = len(tpl)
    while i < n:
        if tpl.startswith('<!--', i):
            j = tpl.find('-->', i) + 3; out.append(tpl[i:j]); i = j; continue
        if tpl[i] == '<':
            # tag
            j = i + 1; quote = None
            while j < n:
                ch = tpl[j]
                if quote:
                    if ch == quote: quote = None
                elif ch in '"\'': quote = ch
                elif ch == '>': break
                j += 1
            out.append(transform_tag(tpl[i:j+1], ns)); i = j + 1; continue
        # text node
        j = i
        while j < n and tpl[j] != '<':
            if tpl.startswith('{{', j):
                k = tpl.find('}}', j); j = k + 2
            else: j += 1
        out.append(transform_text(tpl[i:j], ns)); i = j
    return ''.join(out)

ATTR = re.compile(r'(\s)([:@#]?[\w.\-:]+)(?:="([^"]*)")?', re.S)

def transform_tag(tag, ns):
    if tag.startswith('</') or not HAN.search(tag): return tag
    def rep(m):
        sp, name, val = m.group(1), m.group(2), m.group(3)
        if val is None or not HAN.search(val): return m.group(0)
        if name.startswith((':', '@', 'v-', '#')):
            code, _ = transform_js(val.replace('&quot;', '"'), ns, '$t')
            return f'{sp}{name}="{code}"'
        k = key_for(ns, escape_msg(val))
        return f'{sp}:{name}="$t(\'{k}\')"'
    m = re.match(r'(<[\w\-]+)(.*?)(/?>)$', tag, re.S)
    if not m: return tag
    return m.group(1) + ATTR.sub(rep, m.group(2)) + m.group(3)

def transform_text(text, ns):
    if not HAN.search(text): return text
    lead = re.match(r'\s*', text).group(0); trail = re.search(r'\s*$', text).group(0)
    body = text[len(lead):len(text) - len(trail)]
    pieces = re.split(r'(\{\{.*?\}\})', body, flags=re.S)
    if not any(HAN.search(p) for p in pieces if not p.startswith('{{')):
        # 只有插值里有中文
        return lead + ''.join(('{{ ' + transform_js(p[2:-2].strip(), ns, '$t')[0] + ' }}') if p.startswith('{{') else p for p in pieces) + trail
    msg = ''; args = []
    for p in pieces:
        if p.startswith('{{'):
            msg += '{' + str(len(args)) + '}'; args.append(transform_js(p[2:-2].strip(), ns, '$t')[0])
        else:
            msg += escape_msg(re.sub(r'\s+', ' ', p.replace('&nbsp;', ' ')))
    k = key_for(ns, msg)
    return lead + ("{{ $t('" + k + "', [" + ', '.join(args) + "]) }}" if args else "{{ $t('" + k + "') }}") + trail

def rel_import(path):
    rel = os.path.relpath(os.path.join(ROOT, 'i18n'), os.path.dirname(path)).replace(os.sep, '/')
    return rel if rel.startswith('.') else './' + rel

def ensure_import(script, path):
    if re.search(r"import \{[^}]*\btr\b[^}]*\} from '[./]*i18n'", script): return script
    line = f"import {{ tr }} from '{rel_import(path)}'\n"
    # 放在最后一个 import 之后
    ms = list(re.finditer(r'^import .*?(?:from )?[\'"][^\'"]+[\'"]\n', script, re.M | re.S))
    pos = ms[-1].end() if ms else 0
    return script[:pos] + line + script[pos:]

def process(path):
    src = open(path, encoding='utf-8').read(); ns = ns_of(path)
    if path.endswith('.ts'):
        new, changed = transform_js(src, ns, 'tr')
        if changed: new = ensure_import(new, path)
    else:
        new = src
        tm = re.search(r'<template>(.*)</template>', src, re.S)
        sm = re.search(r'(<script[^>]*>)(.*?)(</script>)', src, re.S)
        if sm:
            code, changed = transform_js(sm.group(2), ns, 'tr')
            if changed: code = ensure_import(code, path)
            new = new[:sm.start(2)] + code + new[sm.end(2):]
        if tm:
            tm = re.search(r'<template>(.*)</template>', new, re.S)
            new = new[:tm.start(1)] + transform_template(tm.group(1), ns) + new[tm.end(1):]
    if new != src: open(path, 'w', encoding='utf-8').write(new); print('updated', path)

targets = sys.argv[1:] or [os.path.join(dp, f) for dp, _, fs in os.walk(ROOT) for f in fs if f.endswith(('.vue', '.ts')) and '/i18n' not in dp and not f.endswith('.d.ts')]
for p in targets:
    try: process(p)
    except Exception as e: print('FAILED', p, e)
json.dump(messages, open(ZH, 'w', encoding='utf-8'), ensure_ascii=False, indent=1, sort_keys=True)

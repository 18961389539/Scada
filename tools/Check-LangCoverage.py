"""Report missing language phrases used by Razor pages of the mimic plugins.

Scans every .cshtml for Locale.GetDictionary(<key>) and the dynamic phrases it uses
(dict.Name, plus a local alias such as searchDict.Name), then checks the matching
dictionary in the plugin language files. Exits with code 1 when gaps are found so the
check can be wired into a build script.

    python tools/Check-LangCoverage.py
"""
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PLUGIN_DIRS = [
    ROOT / 'ScadaWeb/Mimics/PlgMimic',
    ROOT / 'ScadaWeb/Mimics/PlgMimicEditor',
    ROOT / 'ScadaWeb/Mimics/PlgMimBasicComp',
]
CULTURES = ['zh-Hans', 'en-GB', 'ru-RU']


def load_dictionaries(plugin_dir):
    """Returns {culture: {dictionary_key: {phrase_key, ...}}}."""
    result = {culture: {} for culture in CULTURES}
    lang_dir = plugin_dir / 'lang'

    if not lang_dir.exists():
        return result

    for culture in CULTURES:
        path = lang_dir / f'{plugin_dir.name}.{culture}.xml'
        if not path.exists():
            continue
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as error:
            print(f'  ! {path.name}: {error}')
            continue
        for dictionary in root:
            key = dictionary.get('key')
            if not key:
                continue
            result[culture][key] = {phrase.get('key') for phrase in dictionary}
    return result


def iter_pages(plugin_dir):
    for path in plugin_dir.rglob('*.cshtml'):
        if 'obj' in path.parts or 'bin' in path.parts:
            continue
        yield path


def scan_page(path):
    """Returns [(dictionary_key, {phrase, ...}), ...] found in the page."""
    text = path.read_text(encoding='utf-8')
    found = []

    # dynamic dict = Locale.GetDictionary("...")
    for match in re.finditer(r'(\w+)\s*=\s*Locale\.GetDictionary\(\s*"([^"]+)"\s*\)', text):
        alias, key = match.group(1), match.group(2)
        phrases = set(re.findall(rf'\b{alias}\.(\w+)', text))
        found.append((key, phrases))

    # partial pages receive the parent dictionary through typeof(...).FullName
    for match in re.finditer(r'Locale\.GetDictionary\(typeof\((\w+)\)\.FullName\s*\+\s*"([^"]+)"\)', text):
        alias, suffix = match.group(1), match.group(2)
        phrases = set(re.findall(r'\bdict\.(\w+)', text))
        found.append((f'<typeof {alias}>{suffix}', phrases))

    return found


def main():
    gaps = 0

    for plugin_dir in PLUGIN_DIRS:
        if not plugin_dir.exists():
            continue

        dictionaries = load_dictionaries(plugin_dir)
        pages = list(iter_pages(plugin_dir))
        if not pages:
            continue

        print(f'\n{plugin_dir.name}: {len(pages)} pages, '
              f'{len(dictionaries["zh-Hans"])} zh-Hans dictionaries')

        for page in pages:
            for dict_key, phrases in scan_page(page):
                if not phrases:
                    continue
                if dict_key.startswith('<typeof'):
                    # Resolve "<typeof Model>suffix" to the real dictionary by its tail.
                    tail = dict_key[len('<typeof '):].replace('>.', '.')
                    candidates = [key for key in dictionaries['zh-Hans']
                                  if key.endswith(tail)]
                    if len(candidates) != 1:
                        print(f'  [zh-Hans] {page.name}: cannot resolve {dict_key} '
                              f'({len(candidates)} candidates)')
                        gaps += 1
                        continue
                    dict_key = candidates[0]
                for culture in CULTURES:
                    known = dictionaries[culture].get(dict_key)
                    if known is None:
                        print(f'  [{culture}] {page.name}: dictionary not found: {dict_key}')
                        gaps += 1
                        continue
                    missing = sorted(phrases - known)
                    if missing:
                        print(f'  [{culture}] {page.name}: {dict_key} -> missing {missing}')
                        gaps += 1

    print(f'\n{"FAIL" if gaps else "PASS"}: {gaps} gap(s)')
    return 1 if gaps else 0


if __name__ == '__main__':
    sys.exit(main())

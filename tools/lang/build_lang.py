"""Builds app/SanctumHelper/Assets/lang/<code>.json from the translation tables in this folder,
after checking that every string the app uses is covered and that placeholders ({0}, {1}) match.

    python tools/lang/build_lang.py

The strings the app uses are collected from its source (Lang.T / Lang.F calls, {l:T '...'} in
XAML, and the lists that are translated at use: pages, marker layers, map styles, directions).
"""
import json, os, re, sys, glob

HERE = os.path.dirname(os.path.abspath(__file__))
APP = os.path.join(HERE, "..", "..", "app", "SanctumHelper")
sys.path.insert(0, HERE)
import tr_eu, tr_asia, tr_other  # noqa: E402

TABLES = {
    "de_DE": tr_eu.de_DE, "fr_FR": tr_eu.fr_FR, "es_ES": tr_eu.es_ES, "pt_PT": tr_eu.pt_PT,
    "zh_CN": tr_asia.zh_CN, "zh_TW": tr_asia.zh_TW, "ja_JP": tr_asia.ja_JP, "ko_KR": tr_asia.ko_KR,
    "ru_RU": tr_other.ru_RU, "vi_VN": tr_other.vi_VN, "id_ID": tr_other.id_ID, "th_TH": tr_other.th_TH,
}
# Shown as they are in every language (controller button letters, the app's name).
UNTRANSLATED = {"A", "B", "X", "Y", "LB", "RB", "LT", "RT", "Sanctum Helper"}

STR = r'"((?:[^"\\]|\\.)*)"'


def collect():
    keys = []

    def add(k):
        if k and k not in keys:
            keys.append(k)
    for f in sorted(glob.glob(os.path.join(APP, "*.cs"))):
        s = open(f, encoding="utf8").read()
        for m in re.finditer(r'Lang\.(?:T|F)\(\s*' + STR, s):
            add(m.group(1).replace('\\"', '"'))
        # Count(n, "1 thing", "{0} things")
        for m in re.finditer(r'Count\([^,]+, ' + STR + r', ' + STR + r'\)', s):
            add(m.group(1)); add(m.group(2))
        for m in re.finditer(r'Lang\.T\(([^;\n]*?\?[^;\n]*?)\)', s):
            for q in re.findall(STR, m.group(1)):
                add(q)
    x = open(os.path.join(APP, "SettingsWindow.xaml"), encoding="utf8").read()
    for m in re.finditer(r"\{l:T '((?:[^'\\]|\\.)*)'\}", x):
        add(m.group(1).replace("\\'", "'").replace("\\\\", "\\"))
    sw = open(os.path.join(APP, "SettingsWindow.xaml.cs"), encoding="utf8").read()
    for t, st in re.findall(r'\(' + STR + r', ' + STR + r'\)', re.search(r'Pages = new\(\)\s*\{(.*?)\};', sw, re.S).group(1)):
        add(t); add(st)
    for lab in re.findall(r'\("[^"]*", "[^"]*", "([^"]*)", "[^"]*"\)', re.search(r'LayerList =\s*\[(.*?)\];', sw, re.S).group(1)):
        add(lab)
    for t in re.findall(r'\("(?:TopRight|TopCenter|Below|Above|Off)", "([^"]*)"\)', sw):
        add(t)
    ov = open(os.path.join(APP, "OverlayWindow.cs"), encoding="utf8").read()
    for n, d in re.findall(r'\("[^"]*", ' + STR + r', ' + STR + r'\)', re.search(r'MiniMapStyles =\s*\[(.*?)\];', ov, re.S).group(1)):
        add(n); add(d)
    for c in ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west", "saved run", "minimap", "map", "None"]:
        add(c)
    gp = open(os.path.join(APP, "Gamepad.cs"), encoding="utf8").read()
    for n in re.findall(r', "([^"]+)"\)', re.search(r'Names =\s*\[(.*?)\];', gp, re.S).group(1)):
        add(n)
    return [k for k in keys if re.search(r"[A-Za-z]", k) and k not in UNTRANSLATED]


def main():
    keys = collect()
    out_dir = os.path.join(APP, "Assets", "lang")
    os.makedirs(out_dir, exist_ok=True)
    ok = True
    for code, table in TABLES.items():
        missing = [k for k in keys if k not in table]
        bad = [k for k in keys if k in table and sorted(re.findall(r"\{\d\}", k)) != sorted(re.findall(r"\{\d\}", table[k]))]
        if missing or bad:
            ok = False
            print(f"{code}: {len(missing)} missing, {len(bad)} placeholder mismatches")
            for k in missing[:20]: print("   missing:", k)
            for k in bad: print("   placeholders:", k, "->", table[k])
        data = {k: table[k] for k in keys if k in table}
        with open(os.path.join(out_dir, code + ".json"), "w", encoding="utf8") as f:
            json.dump(data, f, ensure_ascii=False, indent=1)
    print(f"{len(keys)} strings, {len(TABLES)} languages -> {out_dir}" + ("" if ok else "  (see problems above)"))


if __name__ == "__main__":
    main()

"""The game's own wording for the terms the app shows or reads, in every language the game ships,
into app/SanctumHelper/Assets/lang/game_terms.json. Read-only on the install.

    python tools/extract_text.py [--game D:/PawPrint/Aniimo]

Each English term appears under several text ids (one per screen that uses it), and their
translations can differ slightly, so every variant is kept, most used first: the first is what
the app shows, all of them are what the party-screen reader accepts.
"""
import argparse, collections, json, os, sys, zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from build_data import find_xdf  # noqa: E402

# The game's text files per language; index = the game's language setting
# (system_language_map_data.gameLanguage), stored in the registry as CBT2_language_h...
LANGS = ["zh_CN", "zh_TW", "en", "ko_KR", "ja_JP", "vi_VN", "ru_RU", "de_DE", "fr_FR", "es_ES", "pt_PT", "id_ID", "th_TH"]

TERMS = {
    "Normal": "Normal", "Hard": "Hard", "Nightmare": "Nightmare", "Chaos": "Chaos",
    "EggHeistTeamMode": "Operation: Egg Heist Team Mode",
    "EggHeist": "Egg Heist",
    "EggBoat": "Egg Boat",
    "EggNest": "Egg Nest",
    "Challenge": "Limited-Time Challenge",
}


def load(zf, lang):
    base = "xfs/luascripts/Data/I18N/"
    m = json.loads(zf.read(base + f"NewTextMap_{lang}.json"))
    blob = zf.read(base + f"Compress_{lang}.bin")
    return {k: blob[v[0]:v[0] + v[1]].decode("utf8", "replace") for k, v in m.items() if isinstance(v, list)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=r"D:\PawPrint\Aniimo")
    ap.add_argument("--out", default=os.path.join(ROOT, "app", "SanctumHelper", "Assets", "lang", "game_terms.json"))
    a = ap.parse_args()
    zf = zipfile.ZipFile(find_xdf(a.game))
    en = load(zf, "en")
    ids = {key: [k for k, s in en.items() if s.strip() == text] for key, text in TERMS.items()}
    out = {"languages": LANGS, "terms": {}}
    for lang in LANGS:
        texts = en if lang == "en" else load(zf, lang)
        terms = {}
        for key, idl in ids.items():
            c = collections.Counter(texts[k].strip() for k in idl if texts.get(k, "").strip())
            terms[key] = [t for t, _ in c.most_common()]
        out["terms"][lang] = terms
        print(lang, {k: v[:2] for k, v in terms.items()})
    os.makedirs(os.path.dirname(a.out), exist_ok=True)
    with open(a.out, "w", encoding="utf8") as f:
        json.dump(out, f, ensure_ascii=False, indent=1)
    print("->", a.out)


if __name__ == "__main__":
    main()

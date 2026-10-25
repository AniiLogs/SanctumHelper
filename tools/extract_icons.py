"""Copy the game's own map-mark icons used by the overlay into app/SanctumHelper/Assets/icons.

Read-only on the install.

    python tools/extract_icons.py [--game D:/PawPrint/Aniimo] [--index cached_bundle_index.json]
"""
import argparse, glob, json, os
import UnityPy

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

# overlay name -> game texture (Assets/Res/XGUI/Texture/Icon/... in the UI bundles)
ICONS = {
    "door_orange": "Img_Map_Mark_Uderground_Front_Door",  # the team's start door (orange, crowned)
    "door_blue": "Img_Map_Mark_Uderground_Begin",         # the blue door
    "key": "Img_Map_Mark_Uderground_Key_Orange",          # gold key room
    "nest": "UI_Icon_MarkPoint_EggNestChest",
    "challenge": "UI_Icon_MarkPoint_Challenge_TimeLimited",
    "chest": "UI_Icon_MarkPoint_Chest",                    # what the game marks every loot box with
    "treasure": "UI_Icon_MarkPoint_TreasureChest",
    "boat": "Img_Map_Mark_GrabEgg_EggBoat",
    "chest_white": "ui_item_GrabEgg_TreasureChest_White",
    "chest_green": "ui_item_GrabEgg_TreasureChest_Green",
    "chest_blue": "ui_item_GrabEgg_TreasureChest_Blue",
    "chest_purple": "ui_item_GrabEgg_TreasureChest_Purple",
    "chest_gold": "ui_item_GrabEgg_TreasureChest_Gold",
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=r"D:\PawPrint\Aniimo")
    ap.add_argument("--index", help="optional cached bundle index json (path -> {ui:[...]}) to skip unrelated bundles")
    ap.add_argument("--out", default=os.path.join(ROOT, "app", "SanctumHelper", "Assets", "icons"))
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    root = os.path.join(a.game, "game", "Aniimo_Data", "cvs", "res", "uab", "win", "DefaultPackage", "CacheBundleFiles")
    bundles = glob.glob(os.path.join(root, "*", "*", "*.uab"))
    if a.index:
        idx = json.load(open(a.index, encoding="utf8"))
        keep = [b for b in bundles if any(os.path.basename(p)[:-4] in ICONS.values() for p in idx.get(b, {}).get("ui", []))]
        bundles = keep or bundles
    wanted = {v: k for k, v in ICONS.items()}
    found = set()
    for b in bundles:
        try:
            env = UnityPy.load(b)
        except Exception:
            continue
        for path, obj in env.container.items():
            stem = os.path.basename(path)[:-4]
            if stem in wanted and obj.type.name == "Texture2D" and stem not in found:
                obj.read().image.save(os.path.join(a.out, wanted[stem] + ".png"))
                found.add(stem)
        if len(found) == len(wanted):
            break
    missing = set(wanted) - found
    print(f"{len(found)} icons -> {a.out}" + (f"; missing: {sorted(missing)}" if missing else ""))


if __name__ == "__main__":
    main()

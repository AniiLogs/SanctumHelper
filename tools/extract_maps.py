"""Pull the Lost Sanctum map tiles out of the game's Unity bundles and stitch them.

Read-only on the install. Writes build/maps/<layer key>.png (e.g. 20032.png,
20034_1_2.png for the extra layers some layouts have).

    python tools/extract_maps.py [--game D:/PawPrint/Aniimo]
"""
import argparse, collections, glob, json, os, re, sys
import UnityPy
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
# Lost Sanctum layouts, plus 3005 = the Egg Heist team-mode island around them.
SCENES = list(range(20031, 20061)) + [3005]


def bundles(game):
    root = os.path.join(game, "game", "Aniimo_Data", "cvs", "res", "uab", "win", "DefaultPackage", "CacheBundleFiles")
    return glob.glob(os.path.join(root, "*", "*", "*.uab"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=r"D:\PawPrint\Aniimo")
    ap.add_argument("--index", help="optional cached bundle index json (path -> {ui:[...]})")
    ap.add_argument("--out", default=os.path.join(ROOT, "build", "maps"))
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    cands = bundles(a.game)
    if a.index:
        idx = json.load(open(a.index, encoding="utf8"))
        cands = [b for b in cands if any(("MapRes/Texture/200" in p or "MapRes/Texture/3005" in p) for p in idx.get(b, {}).get("ui", []))] or cands
    tiles = collections.defaultdict(dict)  # key -> {(col,row): Image}
    for n, b in enumerate(cands):
        try:
            env = UnityPy.load(b)
        except Exception:
            continue
        for path, obj in env.container.items():
            if "MapRes/Texture/" not in path or obj.type.name != "Texture2D" or "/Mini/" in path:
                continue
            m = re.search(r"MapRes/Texture/(\d+)(?:_(\d+)/(\d+))?/(UI_Img_Map\w*)\.png$", path)
            if not m or int(m.group(1)) not in SCENES:
                continue
            scene, la, lb, stem = m.groups()
            nums = stem.split("_")
            col, row = int(nums[-2]), int(nums[-1])
            key = scene if not la else f"{scene}_{la}_{lb}_{nums[-3]}"
            try:
                tiles[key][(col, row)] = obj.read().image.convert("RGBA")
            except Exception as e:
                print("skip", path, e)
        if n % 100 == 0:
            print(f"{n}/{len(cands)} bundles, {sum(len(v) for v in tiles.values())} tiles", flush=True)
    meta = {}
    for key, t in sorted(tiles.items()):
        tw, th = next(iter(t.values())).size
        cols = max(c for c, _ in t) + 1
        rows = max(r for _, r in t) + 1
        img = Image.new("RGBA", (cols * tw, rows * th))
        for (c, r), im in t.items():
            # tile names are <col>_<row>, row 0 at the top (checked by seam matching)
            img.paste(im, (c * tw, r * th))
        img.save(os.path.join(a.out, key + ".png"))
        meta[key] = {"cols": cols, "rows": rows, "tile": [tw, th], "count": len(t)}
        print(key, meta[key])
    json.dump(meta, open(os.path.join(a.out, "tiles.json"), "w"), indent=1)


if __name__ == "__main__":
    main()

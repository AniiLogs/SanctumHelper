"""Build the app's layout database from the Aniimo install (read-only).

Writes app/SanctumHelper/Assets/layouts.json and copies the stitched map images
(build/maps, made by extract_maps.py) next to it. A layout whose art isn't in build/maps (the
game downloads Sanctum map art in-game, so a fresh install may not have it) keeps the art
already in Assets/maps if its map transform hasn't changed; otherwise it is left out and listed
under "missingArt" until its art can be extracted.

    python tools/extract_maps.py
    python tools/build_data.py [--game D:/PawPrint/Aniimo]

Facts this relies on (checked against the game, 2026-10-29, data patch 3846247):
- Team-mode Egg Heist is rob_egg_born_data[3005]. DungeonList[2..5] are the
  layout pools for Normal, Hard, Nightmare, Chaos ("Layout NN" = scene 200NN,
  20031..20062 as of 3846247).
- Scene.<id>.scene_room_data lists the rooms: roomType 3 = orange door (start),
  4 = blue door, 2 = corridor, 6 = Limited-Time Challenge (octagonal room),
  prefabs ending in _Key = gold key rooms, Level_Room_30_BOSS = boss room
  (also roomType 3, new in 3846247).
- Room contents live in the room-template scene 10000: sandbox -> spawner ->
  entity, with positions local to the room prefab (rotated by the room's yaw).
- scene_data[<id>] maps world (x, z) to the 2048x2048 map image with two
  reference points (PointA/B world <-> MapA/B image, image y is negative).
"""
import argparse, glob, json, math, os, re, sys, zipfile
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, HERE)
from bdd import Bdd, Table  # noqa: E402

DIFFICULTIES = {2: "Normal", 3: "Hard", 4: "Nightmare", 5: "Chaos"}  # 2 is "Normal" in the game's UI
MODE = "3005"
SANCTUMS = range(20031, 20100)  # team-mode Lost Sanctum scenes (20100 and up are other modes)

# chest id (rob_egg_chest_data) -> marker kind shown in the app
KIND_BY_CHEST = {
    10: "nest", 11: "mound", 12: "nest",
    2011: "legendary",
    2010: "luxurious", 2007: "luxurious", 2004: "luxurious",
    2009: "exquisite", 2006: "exquisite", 2003: "exquisite", 2002: "exquisite", 2005: "exquisite",
    2008: "simple", 2001: "simple",
    1000: "pot", 1001: "pot", 1002: "pot",
    3001: "pile", 3003: "pile", 3004: "pile", 3005: "pile",
}


def data_dir(game):
    """Aniimo_Data of an install: the launcher's <game>/game/Aniimo_Data or Steam's <game>/Aniimo_Data."""
    for rel in ("game/Aniimo_Data", "Aniimo_Data", "."):
        p = os.path.join(game, rel)
        if os.path.isdir(os.path.join(p, "cvs")) or os.path.isdir(os.path.join(p, "StreamingAssets")):
            return os.path.normpath(p)
    sys.exit("Aniimo_Data not found under " + game)


def bundle_files(game, ui=False):
    """The install's asset bundles: the ones shipped in StreamingAssets, then the ones the game
    downloaded (CacheBundleFiles, hashed names), so a downloaded copy of an asset wins.
    ui=True keeps only the shipped UI bundles: ar_xgui_* (one folder each, e.g. the island's tiles) and
    mres_xgui_* (folders grouped together, e.g. the Lost Sanctum map tiles as of 3846247)."""
    d = data_dir(game)
    base = os.path.join(d, "StreamingAssets", "cvs", "res", "uab", "win", "DefaultPackage")
    pats = ("ar_xgui_*.uab", "mres_xgui_*.uab") if ui else ("*.uab",)
    shipped = [p for pat in pats for p in glob.glob(os.path.join(base, pat))]
    cached = glob.glob(os.path.join(d, "cvs", "res", "uab", "win", "DefaultPackage", "CacheBundleFiles", "*", "*", "*.uab"))
    return sorted(shipped) + sorted(cached)


def find_xdf(game):
    d = data_dir(game)
    for rel in ("cvs/res/lua", "StreamingAssets/cvs/res/lua"):
        p = os.path.join(d, rel, "LuaScripts.xdf")
        if os.path.exists(p):
            return p
    sys.exit("LuaScripts.xdf not found under " + game)


def game_build(xdf):
    """The data patch number, from LuaCacheVer.txt next to the xdf ("1.1.3846247,...")."""
    try:
        v = open(os.path.join(os.path.dirname(xdf), "LuaCacheVer.txt"), encoding="utf8").read().split(",")[0]
        return v.rsplit(".", 1)[-1]
    except (OSError, IndexError):
        return os.path.basename(os.path.dirname(xdf))


class Text:
    def __init__(self, zf):
        base = "xfs/luascripts/Data/I18N/"
        self.map = json.loads(zf.read(base + "NewTextMap_en.json"))
        self.blob = zf.read(base + "Compress_en.bin")

    def __call__(self, h):
        if not isinstance(h, int):
            return None
        v = self.map.get(str(h)) or self.map.get(str(h & 0xFFFFFFFF))
        return self.blob[v[0]:v[0] + v[1]].decode("utf8", "replace") if v else None


def py(v):
    return v.to_py() if isinstance(v, Table) else v


def rows(v):
    v = py(v)
    return list(v.values()) if isinstance(v, dict) else v


def rotate(local, yaw):
    """Unity yaw (degrees, clockwise seen from above) applied to a local (x, z)."""
    a = math.radians(yaw)
    x, z = local
    return x * math.cos(a) + z * math.sin(a), -x * math.sin(a) + z * math.cos(a)


ISLAND = 3005
BOAT_PUPPETS = {999002, 5000018}  # puppet_data rows named "Egg Boat"
# map_mark_resource_data config id -> marker kind on the island
ISLAND_MARKS = {1009: "entrance_main", 1008: "entrance_side", 1007: "boat", 1013: "rare"}


def build_island(r, T, maps_dir):
    """The team-mode island around the Lost Sanctum (scene 3005): map art + its map marks."""
    sd = py(r["scene_data"][ISLAND])
    (wax, waz), (wbx, wbz) = sd["PointAPositon"], sd["PointBPosition"]
    (max_, may), (mbx, mby) = sd["MapAPosition"], sd["MapBPosition"]
    sx = (mbx - max_) / (wbx - wax)
    sz = (mby - may) / (wbz - waz)
    ox, oy = max_ - wax * sx, -(may - waz * sz)
    markers = []
    # The Egg Boat comes in at one of five spawn areas ("区域N-出生点"), three boats a few metres
    # apart in each; the single map mark is just one of them. One marker per area, at its middle.
    ent = r[f"Scene.{ISLAND}.scene_entity_data"]
    boats = {k: py(ent[k]) for k in ent.keys()}
    boats = {k: e for k, e in boats.items() if e.get("subType") == "Puppet" and int(e.get("idInType") or 0) in BOAT_PUPPETS}
    owner = {}
    for k in r[f"Scene.{ISLAND}.scene_spawner_data"].keys():
        s = py(r[f"Scene.{ISLAND}.scene_spawner_data"][k])
        for g in rows(s.get("spawnGroups")) or []:
            for eid in rows(g.get("spawnIds")) or []:
                owner[eid] = k
    area_of = {}
    for k in r[f"Scene.{ISLAND}.scene_sandbox_data"].keys():
        v = py(r[f"Scene.{ISLAND}.scene_sandbox_data"][k])
        for s in v.get("spawners") or []:
            area_of[s] = k
    groups = {}
    for k, e in boats.items():
        groups.setdefault(area_of.get(owner.get(k)), []).append(e["position"])
    for area, ps in sorted(groups.items(), key=lambda kv: str(kv[0])):
        x = sum(p[0] for p in ps) / len(ps)
        z = sum(p[2] for p in ps) / len(ps)
        markers.append({"kind": "boat", "name": "Egg Boat", "room": 0, "world": [round(x, 2), round(z, 2)],
                        "img": [round(ox + x * sx, 1), round(oy - z * sz, 1)], "group": ""})
    for m in rows(r[f"Scene.{ISLAND}.scene_mark_point_data"]):
        cid, (x, _, z) = m.get("markConfigId"), m["markPosition"]
        kind = ISLAND_MARKS.get(cid)
        if kind == "boat" and groups:
            continue  # covered by the spawn areas above
        name = T(int(m["infoTitle"])) if str(m.get("infoTitle", "")).lstrip("-").isdigit() else None
        if kind is None and m.get("markType") == 13 and name:
            kind = "area"  # named places ("Ancient Altar", "Forbidden Peak", ...)
        if kind is None:
            continue
        markers.append({"kind": kind, "name": name or kind, "room": 0, "world": [round(x, 2), round(z, 2)],
                        "img": [round(ox + x * sx, 1), round(oy - z * sz, 1)], "group": ""})
    return {"scene": ISLAND, "layout": ISLAND, "kind": "island", "difficulties": [],
            "image": f"maps/{ISLAND}.png", "layers": [], "imageSize": sd.get("mapUISize", [6144, 4096]),
            "worldToImage": {"ox": ox, "oy": oy, "sx": sx, "sz": sz}, "doorVector": {"angle": 0, "distance": 0},
            "rooms": [], "markers": markers}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=r"D:\PawPrint\Aniimo")
    ap.add_argument("--maps", default=os.path.join(ROOT, "build", "maps"))
    ap.add_argument("--out", default=os.path.join(ROOT, "app", "SanctumHelper", "Assets"))
    a = ap.parse_args()

    xdf = find_xdf(a.game)
    zf = zipfile.ZipFile(xdf)
    b = Bdd(zf.read("xfs/luascripts/Common/Data/pmdata.bin"))
    r = b.root()
    T = Text(zf)
    version = game_build(xdf)

    born = py(r["rob_egg_born_data"])[int(MODE)] if int(MODE) in r["rob_egg_born_data"] else py(r["rob_egg_born_data"])[MODE]
    pools = born["DungeonList"]
    diff_of = {}
    for i, name in DIFFICULTIES.items():
        for sc in pools[i]:
            diff_of.setdefault(sc, [])
            if name not in diff_of[sc]:
                diff_of[sc].append(name)

    scene_data = r["scene_data"]
    chests = r["rob_egg_chest_data"]
    sandboxes = r["Scene.10000.scene_sandbox_data"]
    spawners = r["Scene.10000.scene_spawner_data"]
    entities = r["Scene.10000.scene_entity_data"]
    chest_name = {}

    def chest_info(cid):
        if cid not in chest_name:
            c = py(chests[cid]) if cid in chests.keys() else {}
            chest_name[cid] = T(c.get("name")) or c.get("editorName") or str(cid)
        return chest_name[cid]

    def room_contents(sandbox_ids):
        """[(kind, name, local x, local z, sandbox name)] for one room prefab."""
        out = []
        for sid in sandbox_ids:
            if sid not in sandboxes.keys():
                continue
            sb = py(sandboxes[sid])
            for spid in sb.get("spawners") or []:
                if spid not in spawners.keys():
                    continue
                for g in rows(spawners[spid]["spawnGroups"]) or []:
                    for eid in rows(g.get("spawnIds")) or []:
                        if eid not in entities.keys():
                            continue
                        e = py(entities[eid])
                        p = e.get("position") or [0, 0, 0]
                        if e.get("subType") == "ResourceBox":
                            cid = int(e.get("idInType") or 0)
                            kind = KIND_BY_CHEST.get(cid)
                            if kind:
                                out.append((kind, chest_info(cid), p[0], p[2], sb.get("name", "")))
                        elif e.get("subType") == "Puppet" and int(e.get("idInType") or 0) == 881085:
                            out.append(("exit", "Lost Sanctum Exit", p[0], p[2], sb.get("name", "")))
        return out

    layouts = []
    for sc in SANCTUMS:
        name = f"Scene.{sc}.scene_room_data"
        if name not in r.keys():
            continue
        sd = py(scene_data[sc])
        (wax, waz), (wbx, wbz) = sd["PointAPositon"], sd["PointBPosition"]
        (max_, may), (mbx, mby) = sd["MapAPosition"], sd["MapBPosition"]
        sx = (mbx - max_) / (wbx - wax)
        sz = (mby - may) / (wbz - waz)
        # image pixel = (ox + x * sx, oy - z * sz)   (image y grows downwards)
        ox, oy = max_ - wax * sx, -(may - waz * sz)

        def to_img(x, z):
            return round(ox + x * sx, 1), round(oy - z * sz, 1)

        rooms, markers = [], []
        for rm in rows(r[name]):
            x, _, z = rm["position"]
            kind = {3: "orange", 4: "blue", 2: "corridor", 6: "challenge"}.get(rm["roomType"], "room")
            if "_Key" in rm["resId"]:
                kind = "key"
            elif "_BOSS" in rm["resId"]:
                kind = "boss"  # 3846247+: boss rooms reuse roomType 3, they aren't a second start door
            ix, iy = to_img(x, z)
            rooms.append({"id": rm["id"], "type": rm["roomType"], "kind": kind, "depth": rm.get("depth"),
                          "prefab": rm["resId"].strip("$").replace(".prefab", ""), "yaw": rm.get("yaw", 0),
                          "world": [x, z], "img": [ix, iy], "links": rm.get("connectedCount")})
            if kind == "key":
                # Rooms are squares centred on their position; the prefab name gives the side in
                # metres (Level_Room_20_005_Key = 20 m). The locked door is a level item in the
                # room's sandbox, its variant names the key that opens it.
                m = re.match(r"\$?Level_Room_(\d+)_", rm["resId"])
                rooms[-1]["size"] = int(m.group(1)) if m else None
                if rooms[-1]["size"]:
                    h = rooms[-1]["size"] / 2
                    rooms[-1]["imgRect"] = [*to_img(x - h, z + h), *to_img(x + h, z - h)]
                for sid in rm.get("sandboxIds") or []:
                    if sid not in sandboxes.keys():
                        continue
                    for li in (py(sandboxes[sid]).get("levelItems") or {}).values():
                        en = li.get("EditorName") or ""
                        if "LockedDoor" not in en:
                            continue
                        lp = li.get("position") or [0, 0, 0]
                        dx, dz = rotate((lp[0], lp[2]), rm.get("yaw", 0))
                        rooms[-1]["door"] = {"world": [round(x + dx, 2), round(z + dz, 2)], "img": list(to_img(x + dx, z + dz)),
                                             "color": en.rsplit("_", 1)[-1].lower()}
            if kind == "corridor":
                continue
            seen = []
            for mk, mname, lx, lz, sbname in room_contents(rm.get("sandboxIds") or []):
                dx, dz = rotate((lx, lz), rm.get("yaw", 0))
                wx, wz = x + dx, z + dz
                if any(s[0] == mk and abs(s[1] - wx) < 0.6 and abs(s[2] - wz) < 0.6 for s in seen):
                    continue
                seen.append((mk, wx, wz))
                if mk == "exit":
                    mk = kind if kind in ("orange", "blue") else "exit"
                px, py_ = to_img(wx, wz)
                markers.append({"kind": mk, "name": mname, "room": rm["id"], "world": [round(wx, 2), round(wz, 2)],
                                "img": [px, py_], "group": sbname.split("_")[-1]})

        orange = next(x for x in rooms if x["kind"] == "orange")
        blue = next(x for x in rooms if x["kind"] == "blue")
        dx, dz = blue["world"][0] - orange["world"][0], blue["world"][1] - orange["world"][1]
        layers = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(a.maps, f"{sc}_*.png")))
        if not os.path.exists(os.path.join(a.maps, f"{sc}.png")):
            layers = sorted(os.path.basename(p)[:-4] for p in glob.glob(os.path.join(a.out, "maps", f"{sc}_*.png")))
        layouts.append({
            "scene": sc, "layout": sc - 20000, "difficulties": diff_of.get(sc, []),
            "image": f"maps/{sc}.png", "layers": [f"maps/{n}.png" for n in layers],
            "imageSize": sd.get("mapUISize", [2048, 2048]),
            "worldToImage": {"ox": ox, "oy": oy, "sx": sx, "sz": sz},
            "doorVector": {"angle": round(math.degrees(math.atan2(dx, dz)) % 360, 1), "distance": round(math.hypot(dx, dz), 1)},
            "miniMapImageScale": sd.get("miniMapImageScale"), "mapImageScale": sd.get("mapImageScale"),
            "rooms": rooms, "markers": markers,
        })

    layouts.append(build_island(r, T, a.maps))

    # Art not freshly extracted: keep what Assets/maps has only while it still lines up.
    try:
        old = {L["scene"]: L for L in json.load(open(os.path.join(a.out, "layouts.json"), encoding="utf8"))["layouts"]}
    except (OSError, ValueError, KeyError):
        old = {}
    missing = []
    for L in list(layouts):
        if os.path.exists(os.path.join(a.maps, os.path.basename(L["image"]))):
            continue
        prev = old.get(L["scene"])
        if prev and prev["worldToImage"] == L["worldToImage"] and os.path.exists(os.path.join(a.out, L["image"])):
            continue
        layouts.remove(L)
        missing.append(L["scene"])
        print(f"{L['scene']}: no map art" + (" (its map changed)" if prev else "") + f", left out {L['difficulties']}")

    os.makedirs(os.path.join(a.out, "maps"), exist_ok=True)
    used = set()
    for L in layouts:
        for p in [L["image"]] + L["layers"]:
            src = os.path.join(a.maps, os.path.basename(p))
            if os.path.exists(src):
                # 256-colour palette with alpha: the art is flat-shaded, so this is lossless
                # to the eye and ~7x smaller than RGBA.
                Image.open(src).convert("RGBA").quantize(colors=256, method=Image.Quantize.FASTOCTREE)                     .save(os.path.join(a.out, "maps", os.path.basename(p)), optimize=True)
                used.add(p)
    db = {"gameDataVersion": version, "mode": int(MODE), "difficulties": list(DIFFICULTIES.values()),
          "pools": {name: pools[i] for i, name in DIFFICULTIES.items()}, "missingArt": missing, "layouts": layouts}
    json.dump(db, open(os.path.join(a.out, "layouts.json"), "w", encoding="utf8"), ensure_ascii=False, indent=1)
    for L in layouts:
        kinds = {}
        for m in L["markers"]:
            kinds[m["kind"]] = kinds.get(m["kind"], 0) + 1
        print(L["layout"], L["difficulties"], L["doorVector"], kinds)
    print(f"{len(layouts)} layouts, {len(used)} new images -> {a.out}" + (f"; no art yet: {missing}" if missing else ""))


if __name__ == "__main__":
    main()

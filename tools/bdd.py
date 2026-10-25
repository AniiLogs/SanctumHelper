"""Reader for Aniimo's "BDD" binary design-data file (pmdata.bin).

pmdata.bin ships inside LuaScripts.xdf (a plain zip) at
xfs/luascripts/Common/Data/pmdata.bin. The client's native reader is the
`bdd` Lua module in xlua.dll (luaopen_bdd). This module re-implements it from
static analysis of that DLL; see research/gamedata/GAMEDATA.md for the layout.

Usage:
    from bdd import Bdd
    b = Bdd(open("pmdata.bin", "rb").read())
    root = b.root()               # lazy Table
    skills = root["pet_skill_data"].to_py()
"""
import struct

# Numeric encodings (shared by values, numeric keys and offset tables).
NUM_FMT = {0: "<d", 1: "<B", 2: "<b", 3: "<H", 4: "<h", 5: "<I", 6: "<i"}
NUM_WIDTH = {0: 8, 1: 1, 2: 1, 3: 2, 4: 2, 5: 4, 6: 4}
T_STRING, T_TABLE, T_BOOL = 7, 8, 9
REL = 100  # type >= 100: value stored relative to the offset-table entry


class Bdd:
    def __init__(self, data: bytes):
        self.d = data
        # Trailer: 6 sections of (start, end_inclusive, count) + root handle.
        t = struct.unpack_from("<19I", data, len(data) - 76)
        self.sections = [(t[i], t[i + 1], t[i + 2]) for i in range(0, 18, 3)]
        self.root_handle = t[18]
        self.string_pool_end = self.sections[0][1]
        self._shape_cache = {}

    def num(self, enc, off):
        return struct.unpack_from(NUM_FMT[enc], self.d, off)[0]

    def string(self, off):
        e = self.d.index(b"\0", off)
        return self.d[off:e].decode("utf-8", "replace")

    def shape(self, h):
        sp = struct.unpack_from("<I", self.d, h)[0]
        s = self._shape_cache.get(sp)
        if s is None:
            ks, ts, os_ = struct.unpack_from("<3I", self.d, sp)
            d = self.d
            kenc = d[ks + 4]
            n, m = struct.unpack_from("<II", d, ks + 5)
            kw = NUM_WIDTH[kenc]
            keys = [self.num(kenc, ks + 13 + kw * i) for i in range(m)]
            sbase = ks + 13 + kw * m
            for i in range(n - m):
                keys.append(self.string(struct.unpack_from("<I", d, sbase + 4 * i)[0]))
            keys = [int(k) if isinstance(k, float) and k.is_integer() else k for k in keys]
            types = d[ts:ts + n]
            oenc = d[os_]
            ow = NUM_WIDTH[oenc]
            slots = [(os_ + 1 + ow * i, self.num(oenc, os_ + 1 + ow * i)) for i in range(n)]
            s = (keys, types, slots)
            self._shape_cache[sp] = s
        return s

    def value(self, h, t, slot):
        entry, rel = slot
        if t >= REL:
            t -= REL
            pos = entry - rel
        else:
            pos = h + rel
        if t <= 6:
            v = self.num(t, pos)
            return int(v) if isinstance(v, float) and v.is_integer() and t != 0 else v
        if t == T_STRING:
            return self.string(struct.unpack_from("<I", self.d, pos)[0])
        if t == T_TABLE:
            return Table(self, struct.unpack_from("<I", self.d, pos)[0])
        if t == T_BOOL:
            return self.d[pos] == 1
        raise ValueError(f"unknown bdd type {t} at handle {h:#x}")

    def root(self):
        return Table(self, self.root_handle)


class Table:
    __slots__ = ("b", "h")

    def __init__(self, b, h):
        self.b, self.h = b, h

    def items(self):
        keys, types, slots = self.b.shape(self.h)
        for k, t, s in zip(keys, types, slots):
            yield k, self.b.value(self.h, t, s)

    def keys(self):
        return list(self.b.shape(self.h)[0])

    def __getitem__(self, key):
        keys, types, slots = self.b.shape(self.h)
        i = keys.index(key)
        return self.b.value(self.h, types[i], slots[i])

    def get(self, key, default=None):
        try:
            return self[key]
        except ValueError:
            return default

    def __contains__(self, key):
        return key in self.b.shape(self.h)[0]

    def __len__(self):
        return len(self.b.shape(self.h)[0])

    def to_py(self, depth=64):
        """Convert to dict (or list when keys are exactly 1..n)."""
        if depth <= 0:
            return "<depth>"
        out = {}
        for k, v in self.items():
            out[k] = v.to_py(depth - 1) if isinstance(v, Table) else v
        ks = list(out.keys())
        if ks and all(isinstance(k, int) for k in ks) and ks == list(range(1, len(ks) + 1)):
            return [out[k] for k in ks]
        return out

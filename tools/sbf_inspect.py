#!/usr/bin/env python3
"""SBF / VBF archive inspector and format validator for SEGA Rally Revo (PC / PS3).

Implements the container format described in Document/SBF_format.md and checks
every structural rule against real files. Standard library only.

Usage:
    python sbf_inspect.py <file.sbf|file.vbf>            # dump one archive
    python sbf_inspect.py --validate <dir> [<dir> ...]   # validate every archive under the dirs

Notes:
    * SBZ1-compressed files are decompressed transparently.
    * Endianness is detected automatically (PC = little, PS3 = big). The extension
      is NOT a reliable hint: PS3 ".sbf" files are big-endian too.
"""
import collections
import os
import struct
import sys
import zlib

TYPE_NAMES = {
    1: "Model",            # imports type 2 (materials)
    2: "Material",         # imports type 3 (shader) and type 4 (textures)
    3: "Shader/Effect",    # contains the .fx name
    4: "Texture",          # 0x2C descriptor + DDS
    5: "DataTable",        # named-field parameter block (+ optional symbol table)
    6: "ParticleInfo",     # imports type 1
    7: "RawFile",          # XML / Granny .gr2 / string tables, stored verbatim
    11: "SceneGraph",      # *_master_gfx: imports type 1
    12: "TypedObject",     # +0x0C = class hash
    13: "TextureList",     # texture pack: list of type 4 hashes
}

NULL_PTR = 0xFFFFFFFF
TEX_DESC_SIZE = 0x2C
DDS_HEADER_SIZE = 0x80
VBF_BLOCK = 0x18 + TEX_DESC_SIZE  # 0x44: block header + texture descriptor


def ru(x, a):
    return (x + a - 1) // a * a


class FormatError(Exception):
    pass


def load(path):
    """Return (raw_bytes, compressed: bool). Decompresses SBZ1."""
    with open(path, "rb") as fh:
        data = fh.read()
    if data[:4] != b"SBZ1":
        return data, False
    dec = zlib.decompress(data[8:])
    le, be = struct.unpack_from("<I", data, 4)[0], struct.unpack_from(">I", data, 4)[0]
    if len(dec) not in (le, be):
        raise FormatError("SBZ1 size field mismatch")
    return dec, True


def detect_endian(d):
    """Pick the byte order for which entry table and block types are plausible."""
    if len(d) < 0x18:
        raise FormatError("file shorter than global header")
    ok = []
    for e in "<>":
        ver, cnt = struct.unpack_from(e + "I", d, 0)[0], struct.unpack_from(e + "I", d, 0x14)[0]
        if ver not in (3, 4) or 0x18 + cnt * 8 > len(d):
            continue
        good = True
        for i in range(cnt):
            _, off = struct.unpack_from(e + "II", d, 0x18 + i * 8)
            if off + 0x18 > len(d) or struct.unpack_from(e + "I", d, off)[0] not in TYPE_NAMES:
                good = False
                break
        if good:
            ok.append(e)
    if len(ok) != 1:
        raise FormatError("cannot determine endianness (%r)" % ok)
    return ok[0]


class Block:
    __slots__ = ("index", "offset", "type", "hash", "size", "sub", "relocs", "imports",
                 "data_offset", "end", "symbols", "tex")

    def __repr__(self):
        return "<Block #%d %s hash=%08X off=%X size=%X>" % (
            self.index, TYPE_NAMES.get(self.type, self.type), self.hash, self.offset, self.size)


class Archive:
    def __init__(self, path):
        self.path = path
        self.data, self.compressed = load(path)
        d = self.data
        self.e = e = detect_endian(d)
        (self.version, self.flag, self.main_size, self.unk0c, self.unk10,
         count) = struct.unpack_from(e + "6I", d, 0)
        self.entries = [struct.unpack_from(e + "II", d, 0x18 + i * 8) for i in range(count)]
        # VBF = texture-only archive whose block headers are packed together.
        # (+0x08 != 0 marks it; an empty VBF has +0x08 == 0x18.)
        self.is_vbf = self.main_size != 0
        self.blocks = [self._parse_block(i, h, o) for i, (h, o) in enumerate(self.entries)]

    def u32(self, off):
        return struct.unpack_from(self.e + "I", self.data, off)[0]

    def _parse_block(self, index, entry_hash, off):
        d, e = self.data, self.e
        b = Block()
        b.index, b.offset = index, off
        b.type, b.hash, b.size, b.sub, nrel = struct.unpack_from(e + "5I", d, off)
        if b.hash != entry_hash:
            raise FormatError("entry hash != block hash at #%d" % index)
        p = off + 0x14
        b.relocs = list(struct.unpack_from(e + "%dI" % nrel, d, p))
        p += 4 * nrel
        nimp = self.u32(p)
        b.imports = list(struct.unpack_from(e + "%dI" % nimp, d, p + 4))
        b.data_offset = p + 4 + 4 * nimp
        b.symbols = None
        b.tex = None
        if self.is_vbf:
            # Only the 0x2C texture descriptor is stored inline; DDS lives in the data area.
            b.end = b.data_offset + TEX_DESC_SIZE
        else:
            b.end = b.data_offset + b.size
            if b.type == 5:
                b.end = self._parse_symbols(b)
        if b.type == 4:
            b.tex = self._parse_tex(b)
        return b

    def _parse_symbols(self, b):
        """Type 5 trailer: u32 0, u32 n, {u32 nameOff, u32 valueOff}[n], names (offsets from data start)."""
        p = b.data_offset + b.size
        zero, n = struct.unpack_from(self.e + "II", self.data, p)
        if zero != 0:
            raise FormatError("type5 trailer word0 != 0 in #%d" % b.index)
        if (n != 0) != (b.sub != 0):
            raise FormatError("type5 symbol presence disagrees with +0x0C in #%d" % b.index)
        if n == 0:
            return p + 8
        if b.sub != b.size:
            raise FormatError("type5 +0x0C != size in #%d" % b.index)
        syms = []
        end = p + 8 + 8 * n
        for k in range(n):
            name_off, val_off = struct.unpack_from(self.e + "II", self.data, p + 8 + 8 * k)
            q = b.data_offset + name_off
            z = self.data.index(b"\0", q)
            syms.append((self.data[q:z].decode("latin1"), val_off))
            end = max(end, z + 1)
        b.symbols = syms
        return end

    def _parse_tex(self, b):
        f = struct.unpack_from(self.e + "11I", self.data, b.data_offset)
        tex = {
            "kind": {1: "2D", 2: "Cube"}.get(f[0], f[0]),
            "data_ptr": f[1], "width": f[2], "height": f[3], "unk10": f[4],
            "addressU": f[5], "addressV": f[6], "addressW": f[7], "filter": f[8],
            "lod_bias": struct.unpack("<f", struct.pack("<I", f[9]))[0], "max_aniso": f[10],
        }
        if not self.is_vbf:
            tex["dds_offset"] = b.data_offset + TEX_DESC_SIZE
        return tex

    # ---- VBF physical layout -------------------------------------------------
    def vbf_dds_offsets(self):
        """Physical offsets of each texture's (dword-swapped) DDS header in a VBF."""
        return [b.tex["data_ptr"] + 0x1018 + VBF_BLOCK * k for k, b in enumerate(self.blocks)]


# ---- validation ---------------------------------------------------------------
TYPE_ALIGN = {5: 16, 7: 8, 11: 16}


def validate(arc, report):
    """Check every documented rule; record outcomes in report (Counter)."""
    d, e = arc.data, arc.e
    plat = "PS3" if e == ">" else "PC"
    kind = "VBF" if arc.is_vbf else "SBF"
    tag = (plat, kind)

    def check(name, cond):
        report[tag + (name, bool(cond))] += 1
        return cond

    check("version in {3,4}", arc.version in (3, 4))
    check("+0x0C/+0x10 zero", arc.unk0c == 0 and arc.unk10 == 0)
    offs = [o for _, o in arc.entries]
    check("entries sorted by offset", offs == sorted(offs))
    if arc.is_vbf:
        n = len(arc.blocks)
        check("VBF +0x08 == end of packed headers", arc.main_size == (0x1000 + VBF_BLOCK * n if n else 0x18))
        if n:
            check("VBF block k at 0x1000+0x44k", all(o == 0x1000 + VBF_BLOCK * k for k, o in enumerate(offs)))
            phys = arc.vbf_dds_offsets()
            check("VBF first DDS payload at roundUp(+0x08,0x1000)+0x1000",
                  phys[0] + DDS_HEADER_SIZE == ru(arc.main_size, 0x1000) + 0x1000)
            for k, (b, h) in enumerate(zip(arc.blocks, phys)):
                check("VBF type == 4 and no reloc/import", b.type == 4 and not b.relocs and not b.imports)
                check("VBF DDS header (dword-swapped) at data_ptr+0x1018+0x44k", d[h:h + 4] == b" SDD")
                check("VBF payload 0x1000 aligned", (h + DDS_HEADER_SIZE) % 0x1000 == 0)
                pay_end = h + DDS_HEADER_SIZE + b.size - 0xAC
                if k + 1 < n:
                    check("VBF 0x44 zero gap after payload", d[pay_end:pay_end + VBF_BLOCK] == bytes(VBF_BLOCK))
                else:
                    check("VBF EOF == last payload end", len(d) == pay_end)
        else:
            check("empty VBF is 24 bytes", len(d) == 0x18)
        return

    prev_end = ru(0x18 + 8 * len(arc.blocks), 0x1000)
    for k, b in enumerate(arc.blocks):
        nxt = offs[k + 1] if k + 1 < len(offs) else len(d)
        check("block fits before next block", b.end <= nxt)
        check("reloc offsets inside data", all(r % 4 == 0 and r + 4 <= b.size for r in b.relocs))
        check("reloc values inside data or NULL",
              all(arc.u32(b.data_offset + r) <= b.size or arc.u32(b.data_offset + r) == NULL_PTR for r in b.relocs))
        check("import offsets inside data", all(r % 4 == 0 and r + 4 <= b.size for r in b.imports))
        check("+0x0C zero unless type 5/12", b.sub == 0 or b.type in (5, 12))
        if b.type == 4:
            dds = b.tex["dds_offset"]
            ok = check("type4 DDS magic after descriptor", d[dds:dds + 4] == b"DDS ")
            if ok:
                hh, ww = struct.unpack_from("<II", d, dds + 0x0C)
                check("type4 descriptor w/h == DDS w/h", (ww, hh) == (b.tex["width"], b.tex["height"]))
            check("type4 descriptor data_ptr == 0 (SBF)", b.tex["data_ptr"] == 0)
            # placement: DDS payload 0x1000-aligned (a few *_model.sbf textures are packed instead)
            bias = (b.data_offset - b.offset) + TEX_DESC_SIZE + DDS_HEADER_SIZE
            pred = ru(prev_end + bias, 0x1000) - bias
            check("placement: type4 payload 0x1000-aligned", b.offset in (pred, prev_end))
        else:
            a = TYPE_ALIGN.get(b.type, 4)
            bias = b.data_offset - b.offset
            pred = ru(prev_end + bias, a) - bias
            check("placement: data start aligned per type", b.offset == pred or b.offset == prev_end
                  or (b.offset - prev_end) < 0x18)
        pad = d[b.end:nxt]
        if arc.version >= 4:
            check("v4 padding zero-filled", pad.count(0) == len(pad))
        prev_end = b.end
    if arc.blocks:
        if arc.version >= 4:
            check("v4 EOF == roundUp(end,4)+0x800", len(d) == ru(prev_end, 4) + 0x800)
        else:
            check("v3 EOF == roundUp(end,0x800)", len(d) == ru(prev_end, 0x800))


def iter_archives(root):
    for dp, _, fn in os.walk(root):
        for f in fn:
            lf = f.lower()
            if lf.endswith((".sbf", ".vbf", "_sbf", "_vbf")):
                yield os.path.join(dp, f)


def main(argv):
    if len(argv) >= 2 and argv[0] == "--validate":
        report = collections.Counter()
        types = collections.Counter()
        failures = []
        nfiles = 0
        for root in argv[1:]:
            for path in iter_archives(root):
                nfiles += 1
                try:
                    arc = Archive(path)
                    validate(arc, report)
                    for b in arc.blocks:
                        types[("PS3" if arc.e == ">" else "PC", "VBF" if arc.is_vbf else "SBF", b.type)] += 1
                except (FormatError, struct.error, zlib.error, ValueError) as ex:
                    failures.append((path, str(ex)))
        print("files: %d  parse failures: %d" % (nfiles, len(failures)))
        for p, m in failures[:20]:
            print("  FAIL", p, m)
        print("\nblock types:")
        for k in sorted(types):
            print("  %-4s %-3s type %2d %-13s %7d" % (k[0], k[1], k[2], TYPE_NAMES.get(k[2], "?"), types[k]))
        print("\nrule checks (pass / fail):")
        rules = sorted({k[:3] for k in report})
        for r in rules:
            ok, ng = report[r + (True,)], report[r + (False,)]
            print("  %-4s %-3s %-58s %8d / %d" % (r[0], r[1], r[2], ok, ng))
        return 0
    if len(argv) != 1:
        print(__doc__)
        return 1
    arc = Archive(argv[0])
    print("%s  endian=%s compressed=%s version=%d flag=%d +08=%X blocks=%d (%s)" % (
        argv[0], "BE" if arc.e == ">" else "LE", arc.compressed, arc.version, arc.flag,
        arc.main_size, len(arc.blocks), "VBF" if arc.is_vbf else "SBF"))
    for b in arc.blocks:
        extra = ""
        if b.tex:
            t = b.tex
            extra = " %s %dx%d addr=%d/%d/%d filt=%d bias=%g aniso=%d ptr=%X" % (
                t["kind"], t["width"], t["height"], t["addressU"], t["addressV"], t["addressW"],
                t["filter"], t["lod_bias"], t["max_aniso"], t["data_ptr"])
        if b.symbols:
            extra = " symbols=%d first=%s" % (len(b.symbols), b.symbols[0][0])
        if b.type == 12:
            extra = " class=%08X" % b.sub
        print("  #%-4d %-13s hash=%08X off=%08X size=%08X reloc=%-5d import=%-4d%s" % (
            b.index, TYPE_NAMES.get(b.type, b.type), b.hash, b.offset, b.size,
            len(b.relocs), len(b.imports), extra))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

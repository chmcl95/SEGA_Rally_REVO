"""Build in-game test archives with SegaRallyRevoTool.

For one SBF (PC) or VBF (PS3) archive, writes these variants:

  A_identity  unpack -> pack, nothing changed (must be byte-identical to the input)
  B_tint      every texture: red and blue swapped (same size)
  C_shrink    B + texture 0 halved (top mip level dropped)       -> later data moves back
  D_grow      B + texture 0 doubled (nearest-neighbour new mip)  -> later data moves forward

Only DXT1 / DXT3 / DXT5 textures are modified; others are left alone.

usage: py -3 -I tools/make_test_variants.py <archive> <out_dir> [--exe <SegaRallyRevoTool.exe>]
"""
import os
import shutil
import struct
import subprocess
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.join(HERE, "..", "SegaRallyRevoTool", "bin", "Release", "net5.0", "SegaRallyRevoTool.exe")

DDSD_MIPMAPCOUNT = 0x20000


# ---------------------------------------------------------------- DDS helpers

class Dds:
    def __init__(self, data):
        if data[:4] != b"DDS " or struct.unpack_from("<I", data, 4)[0] != 124:
            raise ValueError("not a DDS file")
        self.header = bytearray(data[:0x80])
        self.flags, self.height, self.width, self.linear = struct.unpack_from("<4I", data, 8)
        self.mips = struct.unpack_from("<I", data, 28)[0] if self.flags & DDSD_MIPMAPCOUNT else 1
        self.mips = max(1, self.mips)
        self.fourcc = bytes(data[84:88])
        self.caps2 = struct.unpack_from("<I", data, 112)[0]
        self.block = {b"DXT1": 8, b"DXT3": 16, b"DXT5": 16}.get(self.fourcc)
        self.cube = bool(self.caps2 & 0x200)
        self.payload_len = sum(self.mip_size(i) for i in range(self.mips)) * (6 if self.cube else 1)
        self.payload = bytearray(data[0x80:0x80 + self.payload_len])
        self.tail = bytes(data[0x80 + self.payload_len:])
        if len(self.payload) != self.payload_len:
            raise ValueError("DDS truncated")

    @property
    def editable(self):
        return self.block is not None and not self.cube

    def mip_size(self, i, w=None, h=None):
        w = max(1, (w or self.width) >> i)
        h = max(1, (h or self.height) >> i)
        if self.block:
            return max(1, (w + 3) // 4) * max(1, (h + 3) // 4) * self.block
        bpp = struct.unpack_from("<I", self.header, 88)[0]
        return w * h * bpp // 8

    def to_bytes(self, keep_tail):
        struct.pack_into("<I", self.header, 12, self.height)
        struct.pack_into("<I", self.header, 16, self.width)
        struct.pack_into("<I", self.header, 20, self.mip_size(0))
        if self.mips > 1 or self.flags & DDSD_MIPMAPCOUNT:
            struct.pack_into("<I", self.header, 28, self.mips)
        return bytes(self.header) + bytes(self.payload) + (self.tail if keep_tail else b"")


def _color_offset(dds):
    return 8 if dds.block == 16 else 0


def swap_red_blue(dds):
    """Swap R and B of both 565 endpoints of every colour block, keeping the c0 > c1 ordering."""
    p = dds.payload
    co = _color_offset(dds)
    four_colour_always = dds.block == 16   # DXT3/5 colour blocks are always 4-colour
    for off in range(0, len(p), dds.block):
        o = off + co
        c0, c1, idx = struct.unpack_from("<HHI", p, o)
        n0 = ((c0 & 0x1F) << 11) | (c0 & 0x07E0) | (c0 >> 11)
        n1 = ((c1 & 0x1F) << 11) | (c1 & 0x07E0) | (c1 >> 11)
        if not four_colour_always and (c0 > c1) != (n0 > n1) and n0 != n1:
            # ordering flipped: swap the endpoints back and remap the indices
            n0, n1 = n1, n0
            four = c0 > c1
            new = 0
            for i in range(16):
                v = (idx >> (2 * i)) & 3
                if v < 2:
                    v ^= 1
                elif four:
                    v ^= 1   # 2 <-> 3
                new |= v << (2 * i)
            idx = new
        struct.pack_into("<HHI", p, o, n0, n1, idx)


def drop_top_mip(dds):
    if dds.mips < 2 or dds.width < 8 or dds.height < 8:
        raise ValueError("texture too small to shrink")
    first = dds.mip_size(0)
    del dds.payload[:first]
    dds.width //= 2
    dds.height //= 2
    dds.mips -= 1


def _upscale_block(src, block):
    """One source 4x4 block -> four 4x4 blocks (2x nearest), returned as [q00, q10, q01, q11]."""
    co = 8 if block == 16 else 0
    c0c1 = src[co:co + 4]
    idx = struct.unpack_from("<I", src, co + 4)[0]
    if block == 16:
        alpha = src[:8]
        if alpha_is_dxt5:
            a_end = alpha[:2]
            a_idx = int.from_bytes(alpha[2:8], "little")
        else:
            a_bits = int.from_bytes(alpha, "little")
    out = []
    for qy in range(2):
        for qx in range(2):
            nidx = 0
            na = 0
            for y in range(4):
                for x in range(4):
                    sx = qx * 2 + x // 2
                    sy = qy * 2 + y // 2
                    s = sy * 4 + sx
                    d = y * 4 + x
                    nidx |= ((idx >> (2 * s)) & 3) << (2 * d)
                    if block == 16:
                        if alpha_is_dxt5:
                            na |= ((a_idx >> (3 * s)) & 7) << (3 * d)
                        else:
                            na |= ((a_bits >> (4 * s)) & 15) << (4 * d)
            colour = c0c1 + struct.pack("<I", nidx)
            if block == 16:
                if alpha_is_dxt5:
                    a = a_end + na.to_bytes(6, "little")
                else:
                    a = na.to_bytes(8, "little")
                out.append(a + colour)
            else:
                out.append(colour)
    return out


alpha_is_dxt5 = False


def add_top_mip(dds):
    global alpha_is_dxt5
    alpha_is_dxt5 = dds.fourcc == b"DXT5"
    bw = max(1, (dds.width + 3) // 4)
    bh = max(1, (dds.height + 3) // 4)
    if dds.width % 4 or dds.height % 4:
        raise ValueError("texture size must be a multiple of 4 to grow")
    src = dds.payload[:dds.mip_size(0)]
    nbw = bw * 2
    rows = [[None] * nbw for _ in range(bh * 2)]
    for by in range(bh):
        for bx in range(bw):
            o = (by * bw + bx) * dds.block
            q = _upscale_block(bytes(src[o:o + dds.block]), dds.block)
            rows[by * 2][bx * 2] = q[0]
            rows[by * 2][bx * 2 + 1] = q[1]
            rows[by * 2 + 1][bx * 2] = q[2]
            rows[by * 2 + 1][bx * 2 + 1] = q[3]
    new_mip = b"".join(b for row in rows for b in row)
    dds.payload[:0] = new_mip
    dds.width *= 2
    dds.height *= 2
    dds.mips += 1
    dds.flags |= DDSD_MIPMAPCOUNT
    struct.pack_into("<I", dds.header, 8, dds.flags)


# ---------------------------------------------------------------- tool driver

def run(exe, args, log):
    with open(log, "ab") as f:
        f.write(("\n> " + " ".join(args) + "\n").encode())
        f.flush()
        rc = subprocess.run([exe] + args, stdout=f, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL).returncode
    if rc != 0:
        raise RuntimeError("SegaRallyRevoTool %s failed (see %s)" % (args[0], log))


def load(path):
    b = open(path, "rb").read()
    return (zlib.decompress(b[8:]), True) if b[:4] == b"SBZ1" else (b, False)


def make(archive, out_dir, exe):
    archive = os.path.abspath(archive)
    name = os.path.basename(archive)
    stem, ext = os.path.splitext(name)
    raw, compressed = load(archive)
    work = os.path.join(out_dir, "_work", stem)
    shutil.rmtree(work, ignore_errors=True)
    os.makedirs(work)
    log = os.path.join(out_dir, "_work", stem + "_log.txt")
    if os.path.exists(log):
        os.remove(log)
    results = []

    for variant in ("A_identity", "B_tint", "C_shrink", "D_grow"):
        u = os.path.join(work, variant, "u")
        p = os.path.join(work, variant, "p")
        run(exe, ["unpack", "-i", archive, "-o", u], log)
        udir = os.path.join(u, os.listdir(u)[0])
        tex_dir = os.path.join(udir, "4")
        notes = []
        if variant != "A_identity" and os.path.isdir(tex_dir):
            files = sorted(f for f in os.listdir(tex_dir) if f.upper().endswith(".DDS"))
            for n, f in enumerate(files):
                path = os.path.join(tex_dir, f)
                dds = Dds(open(path, "rb").read())
                if not dds.editable:
                    notes.append("%s: %s%s left as is" % (f, dds.fourcc.decode(errors="replace"), " cube" if dds.cube else ""))
                    continue
                before = (dds.width, dds.height)
                swap_red_blue(dds)
                keep_tail = True
                if n == 0 and variant == "C_shrink":
                    drop_top_mip(dds)
                    keep_tail = False
                elif n == 0 and variant == "D_grow":
                    add_top_mip(dds)
                    keep_tail = False
                open(path, "wb").write(dds.to_bytes(keep_tail))
                notes.append("%s: %s %dx%d -> %dx%d, red/blue swapped" % (
                    f, dds.fourcc.decode(), before[0], before[1], dds.width, dds.height))
        args = ["pack", "-i", udir, "-o", p]
        if not compressed:
            args.append("--nocomp")
        run(exe, args, log)
        out = os.path.join(p, os.listdir(p)[0])
        final = os.path.join(out_dir, variant, name)
        os.makedirs(os.path.dirname(final), exist_ok=True)
        shutil.copyfile(out, final)
        new_raw, _ = load(final)
        same = new_raw == raw
        results.append((variant, final, same, notes))
    return results


def main():
    argv = sys.argv[1:]
    exe = DEFAULT_EXE
    if "--exe" in argv:
        i = argv.index("--exe")
        exe = argv[i + 1]
        del argv[i:i + 2]
    if len(argv) != 2:
        sys.exit(__doc__)
    for variant, final, same, notes in make(argv[0], argv[1], os.path.abspath(exe)):
        print("%-11s %s  (%s)" % (variant, final, "identical to input" if same else "differs from input"))
        for n in notes:
            print("            " + n)


if __name__ == "__main__":
    main()

"""Dumps the D3D11 vertex programs of a pass of a shader from a Unity asset bundle, with the keywords each variant is compiled for.

Usage: shader_dump.py <bundle> <shader name> <pass index> [keyword,keyword,...] [outdir]
Without keywords it lists the pass' keywords and the number of vertex variants. With keywords it writes the DXBC and the disassembly (through
d3dcompiler_47.dll) of the variants whose keyword set contains all of them. Needs UnityPy and lz4.
"""
import ctypes
import os
import struct
import sys

import lz4.block
import UnityPy

bundle, shader_name, pass_index = sys.argv[1], sys.argv[2], int(sys.argv[3])
want = [k for k in (sys.argv[4].split(",") if len(sys.argv) > 4 and sys.argv[4] else [])]
outdir = sys.argv[5] if len(sys.argv) > 5 else os.getcwd()

env = UnityPy.load(bundle)
shader = None
for o in env.objects:
    if o.type.name == "Shader":
        d = o.read()
        if d.m_ParsedForm.m_Name == shader_name:
            shader = d
            break
if shader is None:
    sys.exit("shader not found")

pf = shader.m_ParsedForm
p = pf.m_SubShaders[0].m_Passes[pass_index]
names = {idx: n for n, idx in p.m_NameIndices}
prog = p.progVertex
print("keywords of the pass:", " ".join(names[i] for i in sorted(names)))
print("vertex variants:", len(prog.m_SubPrograms))
blob = bytes(shader.compressedBlob)
chunks = [lz4.block.decompress(blob[shader.offsets[0][k]:shader.offsets[0][k] + shader.compressedLengths[0][k]],
                               uncompressed_size=shader.decompressedLengths[0][k]) for k in range(len(shader.offsets[0]))]
# The first segment starts with a table: count, then (offset, length, segment) per sub-program blob index.
count = struct.unpack_from("<I", chunks[0], 0)[0]
table = [struct.unpack_from("<3I", chunks[0], 4 + 12 * i) for i in range(count)]


def chunk(i):
    off, length, seg = table[prog.m_SubPrograms[i].m_BlobIndex]
    return chunks[seg][off:off + length]


def keywords(i):
    s = prog.m_SubPrograms[i]
    idx = list(s.m_GlobalKeywordIndices or []) + list(s.m_LocalKeywordIndices or []) + list(s.m_KeywordIndices or [])
    return sorted(set(names.get(k, "?%d" % k) for k in idx))


def dxbc(data):
    at = data.find(b"DXBC")
    if at < 0:
        return None
    size = struct.unpack_from("<I", data, at + 24)[0]
    return data[at:at + size]


d3d = ctypes.WinDLL("d3dcompiler_47.dll")


def disassemble(code):
    blobp = ctypes.c_void_p()
    hr = d3d.D3DDisassemble(code, ctypes.c_size_t(len(code)), 0, None, ctypes.byref(blobp))
    if hr != 0:
        return "disassembly failed 0x%08X" % (hr & 0xFFFFFFFF)
    get_ptr = ctypes.WINFUNCTYPE(ctypes.c_void_p, ctypes.c_void_p)(ctypes.cast(ctypes.cast(blobp, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))[3])
    get_size = ctypes.WINFUNCTYPE(ctypes.c_size_t, ctypes.c_void_p)(ctypes.cast(ctypes.cast(blobp, ctypes.POINTER(ctypes.c_void_p))[0], ctypes.POINTER(ctypes.c_void_p))[4])
    return ctypes.string_at(get_ptr(blobp), get_size(blobp)).decode("latin-1")


if want:
    os.makedirs(outdir, exist_ok=True)
    n = 0
    for i in range(len(prog.m_SubPrograms)):
        kw = keywords(i)
        if all(k in kw for k in want):
            code = dxbc(chunk(i))
            if code is None:
                continue
            n += 1
            tag = "v%03d" % i
            open(os.path.join(outdir, tag + ".dxbc"), "wb").write(code)
            open(os.path.join(outdir, tag + ".txt"), "w", encoding="utf-8").write("// keywords: " + " ".join(kw) + "\n" + disassemble(code))
            print(tag, "keywords:", " ".join(kw), "bytes", len(code))
    print("written", n)
else:
    sizes = {}
    for i in range(len(prog.m_SubPrograms)):
        for k in keywords(i):
            sizes[k] = sizes.get(k, 0) + 1
    print("variants per keyword:", sizes)

"""Temporal stability of DLSS's own output on moving characters vs scenery.
For consecutive captured frames the output (jitter-free, full resolution) of frame k-1 is reprojected onto frame k with the captured motion
vectors (scaled to output resolution) and compared with the output of frame k. Errors are normalised by local image detail so that
characters (more detail) can be compared with scenery. High normalised error on characters = their detail flickers or is not accumulated."""
import sys, os, json
import numpy as np
from scipy import ndimage as ndi
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from jitter_response import load, lum

d = sys.argv[1]
fr = []
for i in range(16):
    if not os.path.exists(os.path.join(d, "f%d_meta.json" % i)): break
    fr.append(dict(out=load(d, "f%d_out" % i), mv=load(d, "f%d_mv" % i), obj=load(d, "f%d_obj" % i), col=load(d, "f%d_color" % i)))
if any(f["out"] is None for f in fr):
    print("needs a capture with output of every frame"); sys.exit(1)

H, W = fr[0]["out"].shape[:2]
h, w = fr[0]["mv"].shape[:2]
sy, sx = H / h, W / w
print("render %dx%d output %dx%d scale %.2f" % (w, h, W, H, sx))
for k in range(1, len(fr)):
    cur, prev = fr[k], fr[k - 1]
    lo_c, lo_p = lum(cur["out"]), lum(prev["out"])
    mv = np.nan_to_num(cur["mv"])
    mvx = ndi.zoom(mv[..., 0], (sy, sx), order=1)[:H, :W] * sx
    mvy = ndi.zoom(mv[..., 1], (sy, sx), order=1)[:H, :W] * sy
    char_in = cur["obj"][..., 3] > 0.5
    char = ndi.zoom(char_in.astype(np.uint8), (sy, sx), order=0)[:H, :W] > 0
    core = ndi.binary_erosion(char, iterations=int(2 * sx))
    scen = ~ndi.binary_dilation(char, iterations=int(6 * sx))
    gx = ndi.sobel(lo_c, axis=1) / 8.0; gy = ndi.sobel(lo_c, axis=0) / 8.0
    grad = np.hypot(gx, gy)
    tex = grad > 0.01
    print("\nframe %d->%d  mean|mv| (render px): scenery %.2f  characters %.2f" % (k - 1, k, np.hypot(mv[..., 0], mv[..., 1])[~char_in].mean(), np.hypot(mv[..., 0], mv[..., 1])[char_in].mean() if char_in.any() else 0))
    for name, m in (("scenery", scen & tex), ("character core", core & tex)):
        ys, xs = np.nonzero(m)
        if len(ys) > 400000:
            sel = np.random.RandomState(2).choice(len(ys), 400000, replace=False); ys, xs = ys[sel], xs[sel]
        ys = ys.astype(np.float32); xs = xs.astype(np.float32)
        v = ndi.map_coordinates(lo_p, [ys + mvy[ys.astype(int), xs.astype(int)], xs + mvx[ys.astype(int), xs.astype(int)]], order=1, mode="nearest")
        tgt = lo_c[ys.astype(int), xs.astype(int)]
        e = np.abs(v - tgt)
        g = grad[ys.astype(int), xs.astype(int)]
        print("   %-15s px %7d  err %.5f  mean grad %.4f  err/grad %.3f" % (name, len(ys), e.mean(), g.mean(), e.mean() / g.mean()))

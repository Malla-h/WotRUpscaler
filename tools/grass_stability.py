"""Does vegetation (wind-animated, no motion vectors of its own) reproject worse than static scenery?

Usage: grass_stability.py <capture dir> [region] [all]   region = ur (default), ul, lr, ll: the screen quadrant that holds the plants; "all" also
measures the pixels covered by the object motion pass (needed once plants get their own motion vectors).
For each pair of consecutive captured frames the DLSS input colour of frame k-1 is reprojected onto frame k with the captured motion
vectors (and the jitter difference). The error, normalised by local detail, is compared between the plant region and the rest of the
scenery. A block search then estimates how far the plants really moved between the frames beyond what the motion vectors say.
Rows in the raw files are bottom-up, so the screen's upper half is the high row indices.
"""
import os
import sys
import json

import numpy as np
from PIL import Image
from scipy import ndimage as ndi

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from jitter_response import load, lum

d = sys.argv[1]
region = sys.argv[2] if len(sys.argv) > 2 else "ur"
include_objects = len(sys.argv) > 3 and sys.argv[3] == "all"      # also measure the pixels the object motion pass covers (plants now are among them)
fr = []
for i in range(16):
    mp = os.path.join(d, "f%d_meta.json" % i)
    if not os.path.exists(mp):
        break
    fr.append(dict(meta=json.load(open(mp)), col=load(d, "f%d_color" % i), mv=load(d, "f%d_mv" % i), obj=load(d, "f%d_obj" % i), out=load(d, "f%d_out" % i)))
h, w = fr[0]["mv"].shape[:2]
yy, xx = np.mgrid[0:h, 0:w]
upper = yy >= h // 2
right = xx >= w // 2
quad = {"ur": upper & right, "ul": upper & ~right, "lr": ~upper & right, "ll": ~upper & ~right}[region]
adir = os.path.join(d, "analysis")
os.makedirs(adir, exist_ok=True)

for k in range(1, len(fr)):
    cur, prev = fr[k], fr[k - 1]
    lc, lp = lum(cur["col"]), lum(prev["col"])
    mv = np.nan_to_num(cur["mv"])
    js = np.array(cur["meta"]["jitterPx"]) - np.array(prev["meta"]["jitterPx"])
    char = cur["obj"][..., 3] > 0.5 if cur["obj"] is not None else np.zeros((h, w), bool)
    away = np.ones((h, w), bool) if include_objects else ~ndi.binary_dilation(char, iterations=8)
    gx = ndi.sobel(lc, axis=1) / 8.0
    gy = ndi.sobel(lc, axis=0) / 8.0
    grad = np.hypot(gx, gy)
    tex = grad > 0.01

    def pred(dx=0.0, dy=0.0):
        return ndi.map_coordinates(lp, [yy + mv[..., 1] + js[1] + dy, xx + mv[..., 0] + js[0] + dx], order=1, mode="nearest")

    base = pred()
    err = np.abs(base - lc)
    print("\nframe %d->%d  jitter delta (%.2f, %.2f)  mean |mv| %.3f" % (k - 1, k, js[0], js[1], np.hypot(mv[..., 0], mv[..., 1]).mean()))
    for name, m in (("plants (%s)" % region, quad & away & tex), ("rest of scenery", ~quad & away & tex)):
        print("   %-18s px %8d  err %.5f  mean grad %.4f  err/grad %.3f" % (name, m.sum(), err[m].mean(), grad[m].mean(), err[m].mean() / grad[m].mean()))

    # block search for the residual displacement inside the plant region
    B = 16
    shifts = np.arange(-3.0, 3.01, 0.5)
    ys0, ys1 = (h // 2, h) if region in ("ur", "ul") else (0, h // 2)
    xs0, xs1 = (w // 2, w) if region in ("ur", "lr") else (0, w // 2)
    sub = (slice(ys0, ys1), slice(xs0, xs1))
    cost = np.zeros((len(shifts), len(shifts)) + lc[sub].shape, np.float32)
    for iy, dy in enumerate(shifts):
        for ix, dx in enumerate(shifts):
            cost[iy, ix] = np.abs(pred(dx, dy)[sub] - lc[sub])
    bh, bw = lc[sub].shape[0] // B, lc[sub].shape[1] // B
    cost = cost[:, :, : bh * B, : bw * B].reshape(len(shifts), len(shifts), bh, B, bw, B).mean(axis=(3, 5))
    flat = cost.reshape(-1, bh, bw)
    best = flat.argmin(axis=0)
    by, bx = np.unravel_index(best, (len(shifts), len(shifts)))
    dy_map, dx_map = shifts[by], shifts[bx]
    c0 = cost[len(shifts) // 2, len(shifts) // 2]           # error with zero residual displacement
    cb = flat.min(axis=0)
    detail = ndi.uniform_filter(grad[sub][: bh * B, : bw * B], B)[B // 2::B, B // 2::B][:bh, :bw]
    ok = detail > 0.02
    mag = np.hypot(dx_map, dy_map)
    print("   residual displacement of textured plant blocks (render px): median %.2f  mean %.2f  90%% %.2f  blocks moving >=1 px: %.0f%%"
          % (np.median(mag[ok]), mag[ok].mean(), np.percentile(mag[ok], 90), 100.0 * (mag[ok] >= 1.0).mean()))
    print("   block error: with the motion vectors only %.5f, with the best residual displacement %.5f (%.0f%% lower)"
          % (c0[ok].mean(), cb[ok].mean(), 100.0 * (1 - cb[ok].mean() / c0[ok].mean())))

    if k == len(fr) - 1:
        # pictures: DLSS output crop of the plant quadrant and a heat map of the block displacement
        def pv(a):
            return np.clip(a / (1.0 + a), 0, 1)
        if cur["out"] is not None:
            s = cur["out"].shape[1] // w
            crop = cur["out"][ys0 * s:ys1 * s, xs0 * s:xs1 * s, :3]
            img = (pv(np.nan_to_num(crop)) ** (1 / 2.2) * 255).astype(np.uint8)[::-1]
            Image.fromarray(img).resize((img.shape[1] // 2, img.shape[0] // 2), Image.LANCZOS).save(os.path.join(adir, "plants_out.png"))
        heat = np.clip(mag / 3.0, 0, 1)
        hm = (np.stack([heat, 1 - heat, np.zeros_like(heat)], -1) * 255).astype(np.uint8)[::-1]
        Image.fromarray(hm).resize((hm.shape[1] * B, hm.shape[0] * B), Image.NEAREST).save(os.path.join(adir, "plants_motion.png"))
        print("   pictures written to", adir)

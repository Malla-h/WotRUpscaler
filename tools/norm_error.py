import sys, os, json, glob
import numpy as np
from scipy import ndimage as ndi
sys.path.insert(0, os.path.dirname(__file__))
from jitter_response import load, lum

d = sys.argv[1]
frames = []
for i in range(16):
    mp = os.path.join(d, "f%d_meta.json" % i)
    if not os.path.exists(mp): break
    frames.append(dict(meta=json.load(open(mp)), col=load(d, "f%d_color" % i), obj=load(d, "f%d_obj" % i), mv=load(d, "f%d_mv" % i)))

def shifted(img, ys, xs, dx, dy):
    return ndi.map_coordinates(img, [ys + dy, xs + dx], order=1, mode="nearest")

for k in range(1, len(frames)):
    cur, prev = frames[k], frames[k-1]
    lc, lp = lum(cur["col"]), lum(prev["col"])
    char = cur["obj"][..., 3] > 0.5
    jp = np.array(prev["meta"]["jitterPx"]); jc = np.array(cur["meta"]["jitterPx"])
    s = jc - jp                       # content shift measured earlier: sample prev at x + (jc - jp)
    gx = ndi.sobel(lc, axis=1) / 8.0; gy = ndi.sobel(lc, axis=0) / 8.0
    grad = np.hypot(gx, gy)
    core = ndi.binary_erosion(char, iterations=3)
    ys0, xs0 = np.nonzero(char)
    win = np.zeros_like(char); win[max(ys0.min()-60, 8):min(ys0.max()+60, lc.shape[0]-8), max(xs0.min()-60, 8):min(xs0.max()+60, lc.shape[1]-8)] = True
    scen = win & ~ndi.binary_dilation(char, iterations=12)
    print("\nframe %d->%d  shift (%.3f, %.3f)" % (k-1, k, s[0], s[1]))
    for name, m in (("scenery", scen), ("character core", core)):
        ys, xs = np.nonzero(m)
        ys = ys.astype(np.float32); xs = xs.astype(np.float32)
        tgt = lc[m]
        v = shifted(lp, ys, xs, s[0], s[1])
        e = np.abs(v - tgt)
        g = grad[m]
        # reference: how much a pure bilinear resample of the SAME frame by the same sub-pixel amount changes it (resampling loss)
        self_v = shifted(lc, ys, xs, s[0], s[1])
        loss = np.abs(self_v - tgt)
        print("   %-15s px %7d  err %.5f  resample-only %.5f  excess %.5f  mean grad %.4f  err/grad %.3f" % (name, m.sum(), e.mean(), loss.mean(), e.mean() - loss.mean(), g.mean(), e.mean() / max(g.mean(), 1e-6)))

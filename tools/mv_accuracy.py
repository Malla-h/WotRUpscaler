"""How accurate are the captured motion vectors? For scenery and character pixels: best scale k and best residual shift so that prev sampled at x + k*mv + residual matches cur."""
import sys, os, json
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

def err_for(lp, lc, ys, xs, mvx, mvy, js, k, rx, ry, tgt):
    v = ndi.map_coordinates(lp, [ys + k * mvy + js[1] + ry, xs + k * mvx + js[0] + rx], order=1, mode="nearest")
    return np.abs(v - tgt).mean()

for k_ in range(1, len(frames)):
    cur, prev = frames[k_], frames[k_ - 1]
    lc, lp = lum(cur["col"]), lum(prev["col"])
    mv = np.nan_to_num(cur["mv"])
    char = cur["obj"][..., 3] > 0.5
    core = ndi.binary_erosion(char, iterations=3)
    js = np.array(cur["meta"]["jitterPx"]) - np.array(prev["meta"]["jitterPx"])
    gx = ndi.sobel(lc, axis=1); gy = ndi.sobel(lc, axis=0)
    tex = np.hypot(gx, gy) > 0.4
    scen = ~ndi.binary_dilation(char, iterations=10) & tex
    # subsample for speed
    print("\nframe %d->%d" % (k_ - 1, k_))
    for name, m in (("scenery", scen), ("character core", core & tex)):
        ys, xs = np.nonzero(m)
        sel = np.random.RandomState(1).choice(len(ys), min(len(ys), 60000), replace=False)
        ys, xs = ys[sel].astype(np.float32), xs[sel].astype(np.float32)
        mvx, mvy = mv[..., 0][m][sel], mv[..., 1][m][sel]
        tgt = lc[m][sel]
        mag = np.hypot(mvx, mvy)
        # best scale
        ks = np.arange(0.0, 2.01, 0.1)
        es = [err_for(lp, lc, ys, xs, mvx, mvy, js, k, 0, 0, tgt) for k in ks]
        kb = ks[int(np.argmin(es))]
        # best residual shift with k = 1
        grid = np.arange(-2.0, 2.01, 0.25)
        best = (0, 0, 9)
        for ry in grid:
            for rx in grid:
                e = err_for(lp, lc, ys, xs, mvx, mvy, js, 1.0, rx, ry, tgt)
                if e < best[2]: best = (rx, ry, e)
        e1 = err_for(lp, lc, ys, xs, mvx, mvy, js, 1.0, 0, 0, tgt)
        print("   %-15s mean|mv| %.2f  best scale k=%.1f (err %.5f)  err at k=1 %.5f  best residual shift (%.2f, %.2f) err %.5f" % (name, mag.mean(), kb, min(es), e1, best[0], best[1], best[2]))

"""Same measurement as output_stability.py, but on the DLSS INPUT colour (render resolution, jittered), so the two can be compared:
if characters are much less stable in the output than in the input, DLSS is losing something; if equally unstable, it is the content (animation)."""
import sys, os, json
import numpy as np
from scipy import ndimage as ndi
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from jitter_response import load, lum

d = sys.argv[1]
fr = []
for i in range(16):
    mp = os.path.join(d, "f%d_meta.json" % i)
    if not os.path.exists(mp): break
    fr.append(dict(meta=json.load(open(mp)), col=load(d, "f%d_color" % i), mv=load(d, "f%d_mv" % i), obj=load(d, "f%d_obj" % i)))
for k in range(1, len(fr)):
    cur, prev = fr[k], fr[k - 1]
    lc, lp = lum(cur["col"]), lum(prev["col"])
    mv = np.nan_to_num(cur["mv"])
    js = np.array(cur["meta"]["jitterPx"]) - np.array(prev["meta"]["jitterPx"])
    char = cur["obj"][..., 3] > 0.5
    core = ndi.binary_erosion(char, iterations=2)
    scen = ~ndi.binary_dilation(char, iterations=6)
    gx = ndi.sobel(lc, axis=1) / 8.0; gy = ndi.sobel(lc, axis=0) / 8.0
    grad = np.hypot(gx, gy)
    tex = grad > 0.01
    print("\nframe %d->%d" % (k - 1, k))
    for name, m in (("scenery", scen & tex), ("character core", core & tex)):
        ys, xs = np.nonzero(m)
        if len(ys) > 400000:
            sel = np.random.RandomState(2).choice(len(ys), 400000, replace=False); ys, xs = ys[sel], xs[sel]
        v = ndi.map_coordinates(lp, [ys + mv[ys, xs, 1] + js[1], xs + mv[ys, xs, 0] + js[0]], order=1, mode="nearest")
        e = np.abs(v - lc[ys, xs]); g = grad[ys, xs]
        print("   %-15s px %7d  err %.5f  mean grad %.4f  err/grad %.3f" % (name, len(ys), e.mean(), g.mean(), e.mean() / g.mean()))

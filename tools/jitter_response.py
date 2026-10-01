"""Does the image content of different regions (scenery vs. characters) respond to the camera jitter the same way?

For each pair of consecutive captured frames this estimates the sub-pixel shift that best aligns the previous frame with the current one,
separately for scenery and character pixels, by brute-force search over a grid of shifts, and prints it next to the jitter delta.
Static camera is assumed (idle character), so any consistent shift is the jitter.
"""
import glob
import json
import os
import sys

import numpy as np
from scipy import ndimage as ndi


def load(d, prefix):
    files = glob.glob(os.path.join(d, prefix + "_*h.raw"))
    if not files:
        return None
    name = os.path.basename(files[0])
    parts = name.split("_")
    w, h = map(int, parts[-2].split("x"))
    ch = int(parts[-1][0])
    a = np.fromfile(files[0], dtype=np.float16).astype(np.float32)
    return a.reshape(h, w, ch)


def lum(c):
    y = 0.2126 * c[..., 0] + 0.7152 * c[..., 1] + 0.0722 * c[..., 2]
    y = np.nan_to_num(np.maximum(y, 0.0))
    return y / (1.0 + y)


def best_shift(prev, cur, mask, rng=1.0, step=0.05):
    """Shift (dx, dy) such that prev sampled at (x+dx, y+dy) best matches cur inside mask."""
    h, w = cur.shape
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    ys, xs = np.nonzero(mask)
    xs = xs.astype(np.float32)
    ys = ys.astype(np.float32)
    target = cur[mask]
    best = (None, 1e9)
    grid = np.arange(-rng, rng + 1e-6, step)
    errs = np.zeros((len(grid), len(grid)), np.float32)
    for iy, dy in enumerate(grid):
        for ix, dx in enumerate(grid):
            v = ndi.map_coordinates(prev, [ys + dy, xs + dx], order=1, mode="nearest")
            e = np.abs(v - target).mean()
            errs[iy, ix] = e
            if e < best[1]:
                best = ((dx, dy), e)
    # refine around the best with a parabola fit would be overkill; report the grid best and the error at zero shift
    e0 = errs[len(grid) // 2, len(grid) // 2]
    return best[0], best[1], e0


def main():
    d = sys.argv[1]
    frames = []
    for i in range(16):
        mp = os.path.join(d, "f%d_meta.json" % i)
        if not os.path.exists(mp):
            break
        frames.append(dict(meta=json.load(open(mp)), col=load(d, "f%d_color" % i), mv=load(d, "f%d_mv" % i), obj=load(d, "f%d_obj" % i)))
    for k in range(1, len(frames)):
        cur, prev = frames[k], frames[k - 1]
        lc, lp = lum(cur["col"]), lum(prev["col"])
        char = cur["obj"][..., 3] > 0.5
        # work in a window around the character, away from the boundary
        ys, xs = np.nonzero(char)
        y0, y1 = max(ys.min() - 60, 8), min(ys.max() + 60, lc.shape[0] - 8)
        x0, x1 = max(xs.min() - 60, 8), min(xs.max() + 60, lc.shape[1] - 8)
        win = np.zeros_like(char)
        win[y0:y1, x0:x1] = True
        core = ndi.binary_erosion(char, iterations=3)
        near = ndi.binary_dilation(char, iterations=12)
        scen = win & ~near
        # only textured scenery pixels carry information
        gx = ndi.sobel(lc, axis=1)
        gy = ndi.sobel(lc, axis=0)
        tex = np.hypot(gx, gy) > 0.05
        jp = np.array(prev["meta"]["jitterPx"])
        jc = np.array(cur["meta"]["jitterPx"])
        dj = jp - jc
        print("\nframe %d -> %d   jitter prev %s cur %s   (prev - cur) = (%.3f, %.3f)" % (k - 1, k, jp.round(3), jc.round(3), dj[0], dj[1]))
        for name, m in (("scenery (textured)", scen & tex), ("character core", core)):
            if m.sum() < 500:
                print("   %-22s too few pixels (%d)" % (name, m.sum()))
                continue
            (dx, dy), e, e0 = best_shift(lp, lc, m)
            print("   %-22s best shift (%.2f, %.2f)  err %.5f  (err at zero shift %.5f)  px %d" % (name, dx, dy, e, e0, m.sum()))
    print("\nIf the content follows the jitter, 'best shift' of scenery equals +/-(prev - cur); characters should match scenery.")


if __name__ == "__main__":
    main()

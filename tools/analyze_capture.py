"""Offline analysis of a WotRDLSS frame capture (debug button in the mod panel).

Loads the raw half-float dumps (colour input, final motion vectors, character motion target, DLSS output), reprojects each frame
onto the next with the motion vectors under several hypotheses, and reports how well each hypothesis explains the image, separately
for scenery, characters and moving characters. Also writes PNG previews and zoomed crops of the moving character.

Conventions (memory coordinates of the captured textures): motion vector = previous minus current position in render pixels;
the projection jitter moves the image content by +jitterPx in these coordinates (derived from the projection setup, verified here).

Usage: python analyze_capture.py <capture folder>
"""
import glob
import json
import os
import sys

import numpy as np
from PIL import Image


def load(d, prefix):
    files = glob.glob(os.path.join(d, prefix + "_*h.raw"))
    if not files:
        return None
    name = os.path.basename(files[0])
    parts = name.split("_")
    w, h = map(int, parts[-2].split("x"))
    ch = int(parts[-1][0])
    a = np.fromfile(files[0], dtype=np.float16).astype(np.float32)
    if a.size != w * h * ch:
        print("size mismatch", name, a.size, w * h * ch)
        return None
    return a.reshape(h, w, ch)


def lum(c):
    y = 0.2126 * c[..., 0] + 0.7152 * c[..., 1] + 0.0722 * c[..., 2]
    y = np.nan_to_num(np.maximum(y, 0.0))
    return y / (1.0 + y)  # tonemapped so highlights do not dominate the error


def bilinear(img, x, y):
    h, w = img.shape
    x0 = np.floor(x).astype(np.int64)
    y0 = np.floor(y).astype(np.int64)
    fx = x - x0
    fy = y - y0
    valid = (x0 >= 0) & (y0 >= 0) & (x0 + 1 < w) & (y0 + 1 < h)
    x0c = np.clip(x0, 0, w - 2)
    y0c = np.clip(y0, 0, h - 2)
    v = (img[y0c, x0c] * (1 - fx) * (1 - fy) + img[y0c, x0c + 1] * fx * (1 - fy)
         + img[y0c + 1, x0c] * (1 - fx) * fy + img[y0c + 1, x0c + 1] * fx * fy)
    return v, valid


def to_png(arr01, path, flip=True):
    a = np.clip(arr01, 0, 1)
    if flip:
        a = a[::-1]
    Image.fromarray((a * 255 + 0.5).astype(np.uint8)).save(path)


def color_preview(c):
    t = np.nan_to_num(np.maximum(c[..., :3], 0))
    t = t / (1 + t)
    return t ** (1 / 2.2)


def mv_preview(mv, scale=0.125):
    h, w, _ = mv.shape
    out = np.zeros((h, w, 3), np.float32)
    out[..., 0] = 0.5 + mv[..., 0] * scale
    out[..., 1] = 0.5 + mv[..., 1] * scale
    out[..., 2] = 0.5
    return out


def main():
    d = sys.argv[1]
    frames = []
    for i in range(16):
        mp = os.path.join(d, "f%d_meta.json" % i)
        if not os.path.exists(mp):
            break
        meta = json.load(open(mp))
        fr = dict(meta=meta, col=load(d, "f%d_color" % i), mv=load(d, "f%d_mv" % i), obj=load(d, "f%d_obj" % i), out=load(d, "f%d_out" % i))
        frames.append(fr)
    print("frames:", len(frames))
    for fr in frames:
        m = fr["meta"]
        print(" frame %d (#%d) jitter %s reset %s dt %.4f beforePost %s preset %s" % (m["index"], m["frame"], m["jitterPx"], m["reset"], m["deltaTime"], m["beforePost"], m["preset"]))
        missing = [k for k in ("col", "mv", "obj") if fr[k] is None]
        if missing:
            print("   missing:", missing)

    out_dir = os.path.join(d, "analysis")
    os.makedirs(out_dir, exist_ok=True)

    for i, fr in enumerate(frames):
        if fr["col"] is not None:
            to_png(color_preview(fr["col"]), os.path.join(out_dir, "f%d_color.png" % i))
        if fr["mv"] is not None:
            to_png(mv_preview(fr["mv"]), os.path.join(out_dir, "f%d_mv.png" % i))
        if fr["obj"] is not None:
            to_png(np.repeat(fr["obj"][..., 3:4], 3, axis=2), os.path.join(out_dir, "f%d_objmask.png" % i))
        if fr["out"] is not None:
            to_png(color_preview(fr["out"]), os.path.join(out_dir, "f%d_out.png" % i))

    # Reprojection: sample the previous frame at x + sx*mv.x + (jitter cur - jitter prev).x  (same for y), where mv is the captured
    # texture (my convention: previous minus current position, y pointing down) and the jitter term accounts for the projection offset
    # (content shifts by minus the jitter in memory coordinates; verified by tools/jitter_response.py).
    combos = [(1, 1), (1, -1), (-1, 1), (-1, -1), (0, 0)]

    for k in range(1, len(frames)):
        cur, prev = frames[k], frames[k - 1]
        if cur["col"] is None or prev["col"] is None or cur["mv"] is None:
            continue
        lc = lum(cur["col"])
        lp = lum(prev["col"])
        h, w = lc.shape
        yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
        mv = np.nan_to_num(cur["mv"])
        char = cur["obj"][..., 3] > 0.5 if cur["obj"] is not None else np.zeros((h, w), bool)
        mag = np.hypot(mv[..., 0], mv[..., 1])
        moving = char & (mag > 0.75)
        static = ~char
        js = np.array(cur["meta"]["jitterPx"], np.float32) - np.array(prev["meta"]["jitterPx"], np.float32)
        print("\nframe %d -> %d: character px %d, moving character px %d; mean |mv| scenery %.2f, moving chars %.2f (max %.2f)" % (
            k - 1, k, char.sum(), moving.sum(), mag[static].mean(), mag[moving].mean() if moving.any() else 0, mag[moving].max() if moving.any() else 0))
        print("   %-26s %10s %10s %12s" % ("sample prev at x + s*mv", "scenery", "chars", "moving chars"))
        for sx, sy in combos:
            dx = sx * mv[..., 0] + js[0]
            dy = sy * mv[..., 1] + js[1]
            v, valid = bilinear(lp, xx + dx, yy + dy)
            err = np.abs(v - lc)
            def m(mask):
                mm = mask & valid
                return err[mm].mean() if mm.any() else float("nan")
            print("   sx=%+d sy=%+d                %10.5f %10.5f %12.5f" % (sx, sy, m(static), m(char), m(moving)))
            if (sx, sy) == (1, 1):
                e = np.clip(err * 8, 0, 1)
                to_png(np.stack([e, e, e], -1), os.path.join(out_dir, "err_f%d.png" % k))

    # Zoomed crops around the moving character in the last frame.
    last = frames[-1]
    if last["obj"] is not None and last["mv"] is not None:
        mv = np.nan_to_num(last["mv"])
        char = last["obj"][..., 3] > 0.5
        mag = np.hypot(mv[..., 0], mv[..., 1])
        moving = char & (mag > 0.75)
        sel = moving if moving.sum() > 200 else char
        if sel.any():
            ys, xs = np.nonzero(sel)
            cy, cx = int(np.median(ys)), int(np.median(xs))
            r = 90
            y0, y1 = max(cy - r, 0), min(cy + r, sel.shape[0])
            x0, x1 = max(cx - r, 0), min(cx + r, sel.shape[1])
            print("\ncrop around moving character: x %d..%d y %d..%d (render px, memory rows)" % (x0, x1, y0, y1))
            for i, fr in enumerate(frames):
                if fr["col"] is not None:
                    c = color_preview(fr["col"][y0:y1, x0:x1])
                    c = np.kron(c, np.ones((4, 4, 1)))
                    to_png(c, os.path.join(out_dir, "crop_f%d_color.png" % i))
                if fr["mv"] is not None:
                    c = mv_preview(fr["mv"][y0:y1, x0:x1], 0.1)
                    to_png(np.kron(c, np.ones((4, 4, 1))), os.path.join(out_dir, "crop_f%d_mv.png" % i))
            if last["out"] is not None:
                sx = last["out"].shape[1] / sel.shape[1]
                sy = last["out"].shape[0] / sel.shape[0]
                c = color_preview(last["out"][int(y0 * sy):int(y1 * sy), int(x0 * sx):int(x1 * sx)])
                to_png(np.kron(c, np.ones((2, 2, 1))), os.path.join(out_dir, "crop_out.png"))
    print("\nwrote previews to", out_dir)


if __name__ == "__main__":
    main()

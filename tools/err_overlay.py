import sys, os, json
import numpy as np
from scipy import ndimage as ndi
from PIL import Image
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from jitter_response import load, lum
from analyze_capture import color_preview

d = sys.argv[1]; k = int(sys.argv[2]) if len(sys.argv) > 2 else 2
cur = dict(col=load(d, "f%d_color" % k), mv=load(d, "f%d_mv" % k), obj=load(d, "f%d_obj" % k), meta=json.load(open(os.path.join(d, "f%d_meta.json" % k))))
prev = dict(col=load(d, "f%d_color" % (k - 1)), meta=json.load(open(os.path.join(d, "f%d_meta.json" % (k - 1)))))
lc, lp = lum(cur["col"]), lum(prev["col"])
h, w = lc.shape
mv = np.nan_to_num(cur["mv"])
js = np.array(cur["meta"]["jitterPx"]) - np.array(prev["meta"]["jitterPx"])
yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
v = ndi.map_coordinates(lp, [yy + mv[..., 1] + js[1], xx + mv[..., 0] + js[0]], order=1, mode="nearest")
err = np.abs(v - lc)
char = cur["obj"][..., 3] > 0.5
ys, xs = np.nonzero(char)
# largest connected character blob
lab, n = ndi.label(ndi.binary_dilation(char, iterations=6))
sizes = ndi.sum(char, lab, range(1, n + 1))
b = int(np.argmax(sizes)) + 1
yb, xb = np.nonzero(lab == b)
y0, y1, x0, x1 = max(yb.min() - 20, 0), min(yb.max() + 20, h), max(xb.min() - 20, 0), min(xb.max() + 20, w)
print("main character blob: x %d..%d y %d..%d (%d px)" % (x0, x1, y0, y1, sizes[b - 1]))
col = color_preview(cur["col"])[y0:y1, x0:x1]
e = np.clip(err[y0:y1, x0:x1] * 10, 0, 1)
m = char[y0:y1, x0:x1]
over = np.stack([e, e * 0.6, e * 0.6], -1)
over[~m] *= 0.25                      # dim outside the character mask
z = 4
def save(a, name):
    a = np.kron(np.clip(a, 0, 1), np.ones((z, z, 1)))[::-1]
    Image.fromarray((a * 255).astype(np.uint8)).save(os.path.join(d, "analysis", name))
save(col, "blob_color.png"); save(over, "blob_err.png")
mag = np.hypot(mv[..., 0], mv[..., 1])[y0:y1, x0:x1]
save(np.stack([np.clip(0.5 + mv[y0:y1, x0:x1, 0] * 0.08, 0, 1), np.clip(0.5 + mv[y0:y1, x0:x1, 1] * 0.08, 0, 1), np.where(m, 1.0, 0.3)], -1), "blob_mv.png")
sub = err[y0:y1, x0:x1]
print("error inside mask mean %.5f, p90 %.5f; at mask border band (3px) mean %.5f; interior mean %.5f" % (
    sub[m].mean(), np.percentile(sub[m], 90), sub[m & ~ndi.binary_erosion(m, iterations=3)].mean(), sub[ndi.binary_erosion(m, iterations=3)].mean()))

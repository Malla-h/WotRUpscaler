import sys, os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image
from analyze_capture import load, color_preview

d = sys.argv[1]
out = load(d, 'f3_out'); col = load(d, 'f3_color'); obj = load(d, 'f3_obj')
m = obj[..., 3] > 0.5
ys, xs = np.nonzero(m)
cy, cx = int(np.median(ys)), int(np.median(xs))
if len(sys.argv) > 3:
    cx, cy = int(sys.argv[2]), int(sys.argv[3])
print('centre', cx, cy, 'out', out.shape, 'in', col.shape)
r = 100
s = out.shape[1] / col.shape[1]
adir = os.path.join(d, 'analysis'); os.makedirs(adir, exist_ok=True)
def save(a, name, size):
    Image.fromarray((np.clip(a, 0, 1) * 255).astype(np.uint8)).resize(size, Image.NEAREST).save(os.path.join(adir, name))
prev = color_preview(out)[::-1]
Image.fromarray((np.clip(prev, 0, 1) * 255).astype(np.uint8)).resize((1280, 720), Image.LANCZOS).save(os.path.join(adir, 'out_full_small.png'))
y0, y1 = int((cy - r) * s), int((cy + r) * s); x0, x1 = int((cx - r) * s), int((cx + r) * s)
save(color_preview(out[y0:y1, x0:x1])[::-1], 'out_crop.png', (900, 900))
save(color_preview(col[cy - r:cy + r, cx - r:cx + r])[::-1], 'in_crop.png', (900, 900))

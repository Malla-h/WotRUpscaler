"""How much detail did DLSS recover in the last captured frame, relative to a plain upscale of its input? Scenery vs characters.
Metric: mean absolute Laplacian of tonemapped luminance in the output, divided by the same for a bicubic upscale of the input colour.
Values clearly above 1 mean DLSS added detail beyond interpolation; around 1 means it did no better than interpolation."""
import sys, os
import numpy as np
from scipy import ndimage as ndi
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from analyze_capture import load, lum

def lap(a):
    return np.abs(ndi.laplace(a))

for d in sys.argv[1:]:
    out = load(d, 'f3_out'); col = load(d, 'f3_color'); obj = load(d, 'f3_obj'); mv = load(d, 'f3_mv')
    if out is None or col is None:
        print(d, 'incomplete'); continue
    zf = (out.shape[0] / col.shape[0], out.shape[1] / col.shape[1])
    s = int(round(zf[1]))
    lo = lum(out)
    li = ndi.zoom(lum(col), zf, order=3)[:out.shape[0], :out.shape[1]]
    mask_in = obj[..., 3] > 0.5
    mask_out = ndi.zoom(mask_in.astype(np.uint8), zf, order=0)[:out.shape[0], :out.shape[1]] > 0
    core = ndi.binary_erosion(mask_out, iterations=2 * s)
    scen = ~ndi.binary_dilation(mask_out, iterations=6 * s)
    lo_l, li_l = lap(lo), lap(li)
    mag = np.hypot(mv[..., 0], mv[..., 1]).mean()
    print(os.path.basename(d), 'scale', s, 'mean|mv| %.2f' % mag)
    for name, m in (('scenery', scen), ('character core', core)):
        print('   %-15s Laplacian out %.5f  bicubic-in %.5f  ratio %.2f   (px %d)' % (name, lo_l[m].mean(), li_l[m].mean(), lo_l[m].mean() / li_l[m].mean(), m.sum()))

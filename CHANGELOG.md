# Changelog

## 0.9.2
- New option **Smooth outlines** (on by default): the hover and selection outlines, which the game draws after its own anti-aliasing and so stay jagged, are anti-aliased
  with the game's own SMAA. Only the area around the highlighted units is processed and only where the outline is, and only while something is highlighted: the rest
  of the image is untouched. In the Ctrl+F10 Advanced options and on the ModMenu page.

## 0.9.1
- The character previews (inventory, character sheet, character creation) are now upscaled and anti-aliased too, with the selected upscaler (DLSS or TAA),
  each with its own jitter, motion vectors (characters and what they carry) and history. Before, they only had the game's SMAA.
- New option **Character preview size**: Original (the game's small 760x920 texture), Follow the upscaler (the default: the quality mode applies to the
  previews as to the 3D scene) or Size on screen (as many pixels as the preview covers, the sharpest). In the Ctrl+F10 panel and on the ModMenu page.
- The previews always use DLSS preset Automatic, so a heavy preset chosen for the 3D scene does not make them cost more than needed.
- Automatic is now the default DLSS preset.
- The ModMenu tooltips say that the upscaler and quality mode also apply to the previews.
- The native plugin keeps a separate DLSS feature (with its own parameters) for the previews, next to the one for the 3D scene.
- The log lists each camera the game renders and how the mod treats it, to help with reports about screens that look wrong.

## 0.9.0
First public version.
- Low-resolution 3D rendering with NVIDIA DLSS upscaling (and DLAA), full-resolution interface, post-processing and ground markers.
- Quality modes from Native to Ultra Performance, a custom render scale, the DLSS presets K, J, L, M and Automatic (any other preset number can be typed
  in), and two upscalers that work on any graphics card: the mod's own TAA (temporal upscaling, also usable as anti-aliasing at native resolution) and a
  bilinear "Simple scaling" mode.
- Options in the Unity Mod Manager panel (Ctrl+F10) and, if the ModMenu mod is installed, on the game's own Mods settings page. Both stay in step.
- DLSS runs on the unprocessed scene by default (HDR input), so bloom, depth of field and colour grading work at full resolution. This can be switched off.
- Motion vectors: camera motion from depth, exact motion for characters and what they carry, and exact motion for the physics-animated trees, bushes, grass,
  cloaks, flags and tents.
- Selection circle and click marker are rebuilt at full resolution only around themselves (a small cost), stay steady, and are cut off correctly by characters.
- Character and object outlines are drawn at full resolution.
- The game's SMAA and FXAA are switched off while DLSS or TAA runs (option), and the texture mip map bias follows the render scale (option).
- If DLSS cannot start (no RTX card, runtime file missing) the game keeps its normal renderer and the panel explains why; TAA and Simple scaling still work.
- Developer tools (captures, a benchmark with per-stage GPU timing, probes, censuses) are hidden behind `"debug": true`. Nothing is bound to a hotkey.
- Release builds embed no build paths. The release download includes NVIDIA's DLSS runtime and a native plugin with NVIDIA's license text and a notice.

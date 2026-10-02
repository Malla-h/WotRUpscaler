# Changelog

## 0.9.0
First public version.
- Low-resolution 3D rendering with NVIDIA DLSS upscaling (and DLAA), full-resolution interface, post-processing and ground markers.
- Quality modes from Native (DLAA) to Ultra Performance, a custom render scale, the DLSS presets K, J, L, M and Automatic (any other preset number can be typed
  in), and a "Simple scaling" mode that works on any graphics card.
- Options in the Unity Mod Manager panel (Ctrl+F10) and, if the ModMenu mod is installed, on the game's own Mods settings page. Both stay in step.
- DLSS runs on the unprocessed scene by default (HDR input), so bloom, depth of field and colour grading work at full resolution. This can be switched off.
- Motion vectors: camera motion from depth, exact motion for characters and what they carry, and exact motion for the physics-animated trees, bushes, grass,
  cloaks, flags and tents.
- Selection circle and click marker are rebuilt at full resolution only around themselves (a small cost), stay steady, and are cut off correctly by characters.
- Character and object outlines are drawn at full resolution.
- The game's SMAA and FXAA are switched off while DLSS runs (option), and the texture mip map bias follows the render scale (option).
- If DLSS cannot start (no RTX card, runtime file missing) the game keeps its normal renderer and the panel explains why.
- Developer tools (captures, a benchmark with per-stage GPU timing, probes, censuses) are hidden behind `"debug": true`. Nothing is bound to a hotkey.
- Release builds embed no build paths. The release download includes NVIDIA's DLSS runtime and a native plugin with NVIDIA's license text and a notice.

# WotR Upscaler

Renders the 3D scene of Pathfinder: Wrath of the Righteous at a lower resolution and upscales it, so the game runs faster at the same sharpness, or
looks better at the same speed. The interface, the selection circle and the click marker stay at your full screen resolution. Today the upscaler is
NVIDIA DLSS (including DLAA, which uses DLSS only as anti-aliasing at full resolution). A plain "Simple scaling" mode works on any graphics card.
FSR and XeSS are not built in; see "FSR and XeSS" below for what works today through a separate tool.

## Requirements
- Pathfinder: Wrath of the Righteous on Windows, Direct3D 11 (the game's default). Tested with Unity 2020.3.48f1 on one machine (Windows 10, RTX 5060).
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21) (UMM) set up for the game, version 0.21.3 or newer. The mod uses Harmony patches that come with it.
- For DLSS: an NVIDIA RTX graphics card with a recent driver. NVIDIA's runtime, `nvngx_dlss.dll`, is **in the release download** (see Credits and licenses).
  The source repository does not contain it.
- Optional: the **ModMenu** mod, which adds a Mods page to the game's own settings. The mod's options then also appear there. Without it, the Ctrl+F10
  panel of Unity Mod Manager is the only place.

## Install
1. Set up Unity Mod Manager for the game (follow its own instructions) so that the game folder has a `Mods` folder.
2. Download `WotRUpscaler-<version>.zip` from the Releases page and extract the `WotRUpscaler` folder into the game's `Mods` folder. It already contains
   everything: `WotRUpscaler.dll`, `WotRUpscalerNative.dll`, `nvngx_dlss.dll`, the shader bundle `wotrupscaler`, `Info.json`, and the license and notice files.
3. Start the game. Open the Unity Mod Manager panel with **Ctrl+F10**. The mod shows "DLSS is running" once DLSS works, in the game's 3D view (not in menus).

**If DLSS cannot start** (no RTX card, or `nvngx_dlss.dll` is missing because an antivirus removed it), the game keeps its normal renderer, so nothing looks
worse, and the panel says why. Re-extract the mod, then press **Retry DLSS** in the panel's Advanced options. Otherwise check `WotRUpscaler.log` and
`WotRUpscaler.native.log` in the mod folder; an issue report with those two files is the most useful thing you can send.

## Use
Open the panel with Ctrl+F10 (and, if ModMenu is installed, the game's Settings, then Mods, then WotR Upscaler). The same options are in both places and
stay in step. Nothing is bound to a hotkey by this mod.

- **Scale 3D rendering**: the master switch. Off means the game renders as usual.
- **Upscaler**: NVIDIA DLSS, or Simple scaling (the scene is rendered smaller and just stretched; no AI, any graphics card).
- **Quality mode**: Native (DLAA), Ultra Quality (0.77x), Quality (0.67x), Balanced (0.58x), Performance (0.5x), Ultra Performance (0.33x), or Custom with the
  **Render scale** slider. The number is the multiplier of your screen resolution that the 3D scene is rendered at. The panel shows the resulting internal resolution.
- **DLSS preset**: Automatic (NVIDIA decides per mode), K (recommended), J, L, M. K is the best choice for image quality and is the lightest of the newer
  models. L and M cost two to three times as much GPU time. The panel also takes any other preset number, for presets NVIDIA adds later.
- **Run DLSS before post-processing (HDR input)**: gives DLSS the unprocessed scene, so bloom, depth of field and colour grading work at full resolution.
  Costs a little performance. Off runs DLSS on the finished image.
- **Switch off the game's SMAA and FXAA while an AI upscaler is on**: the upscaler does its own anti-aliasing, and the game's would only soften what it receives.
- **Automatic mip map bias**: adjusts the texture mip map bias to the render scale, as upscalers expect, so textures stay sharp at lower resolutions.

Settings are stored in `Settings.xml` in the mod folder. Delete it to reset. The panel's Advanced options also hold the marker gap setting and a few
tools. Developer tools (captures, a benchmark, probes) appear only if you set `"debug": true` in `Settings.xml`; they are not needed for normal use.

## Performance
Measured with the built-in benchmark on an RTX 5060 at 3840x2160, one scene, preset K, HDR input on:

| Mode | Frames per second | Compared with native |
|---|---|---|
| Native, mod off | 43.7 | |
| Quality | 63.3 | +45 % |
| Balanced | 69.5 | +59 % |
| Performance | 75.6 | +73 % |

Your numbers will differ with the GPU, the resolution and the scene. Presets L and M, and DLAA, cost noticeably more than K.

## FSR and XeSS
They are not part of this mod. A separate tool, [OptiScaler](https://github.com/optiscaler/OptiScaler), can take the DLSS calls this mod makes and run FSR or XeSS
instead. It worked in the author's tests on an NVIDIA card, with the FSR and XeSS outputs. **It is untested on AMD and Intel graphics cards**, and OptiScaler's
own notes say that FSR 4 is limited to recent AMD cards and that XeSS in Direct3D 11 is limited to Intel Arc. OptiScaler is its own project with its own
license (GPL-3.0); it is not included here and this mod is not affiliated with it. Follow its instructions, and expect to experiment.

## What the mod covers, and what it does not
- Camera motion, characters (skinned parts and things they carry), trees, bushes, grass, cloaks, flags and tents all get exact motion vectors, so the
  upscaler does not smear them when they move.
- Spell effects and other particles are drawn at the lower resolution like everything else and have no motion vectors, so fast ones can smear.
- Water, fog and the extra wind flutter some flags have also lack motion vectors.
- The game's physics animation (cloth, trees, grass) advances in steps, not every frame. That looks slightly choppy and can still ghost a little, with any upscaler.
- Only the 3D view is scaled. Menus and the interface are untouched.

## How it works (short)
The game's render scale is lowered for the 3D camera and a small jitter is added to its projection each frame. Harmony patches hand the low-resolution colour,
depth and motion vectors to NVIDIA's DLSS (through a small native plugin) and give the rest of the pipeline a full-resolution result, so post-processing, the
interface and the UI camera work at output resolution. Camera motion vectors are computed from depth. A shader (in an asset bundle) draws exact per-vertex
motion for characters and for the physics-animated plants and cloth. The ground markers (selection circle, click marker) are rebuilt around themselves at full
resolution so they stay sharp and stable.

## Building
- Managed mod: `dotnet build -c Release` in `managed`. It references the game's `Wrath_Data\Managed` folder. Point it at your game with the `WOTR_DIR`
  environment variable, `-p:WotRDir=...`, or a git-ignored `managed\local.props` (see the comment in `WotRUpscaler.csproj`). If ModMenu is installed in the game's
  `Mods` folder the in-game menu page is built in; otherwise it is left out.
- Native plugin: run `native\build.bat` with the Visual Studio C++ build tools and NVIDIA's DLSS SDK (https://github.com/NVIDIA/DLSS) in `..\ThirdParty\DLSS`, or
  set `DLSS_SDK`. It produces `native\bin\WotRUpscalerNative.dll`.
- Shader bundle: open `unity` with Unity 2020.3.48f1, or run it headless: `Unity.exe -batchmode -nographics -quit -projectPath unity -executeMethod BuildBundle.Build`.
  It writes `unity\bundle\wotrupscaler`.
- `package.ps1` assembles a clean release folder and zip. It refuses to continue if a build fails, if NVIDIA's license text is missing, or if the output contains
  a forbidden string (a build path or personal name).
- `tools` holds the Python scripts used to analyse captures (reprojection error, jitter response, motion vector accuracy).

## Credits and licenses
- WotR Upscaler is released under the [MIT License](LICENSE). That covers the code in this repository only.
- **DLSS is a technology of NVIDIA Corporation.** This mod is unofficial and not sponsored or endorsed by NVIDIA. NVIDIA, DLSS, RTX and GeForce RTX are
  trademarks of NVIDIA Corporation. FSR is a trademark of Advanced Micro Devices, Inc. and XeSS of Intel Corporation; they are named here only to say what
  is and is not supported.
- This source repository contains no NVIDIA files. The release download includes two NVIDIA-licensed files: `nvngx_dlss.dll` (NVIDIA's DLSS runtime) and
  `WotRUpscalerNative.dll` (which contains statically linked NVIDIA NGX code). They are provided under the NVIDIA RTX SDKs License; the license text and a
  notice come with the download (`licenses` and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)).
- [Harmony](https://github.com/pardeike/Harmony) and Unity Mod Manager do the runtime patching and loading. Neither is included here.
- Pathfinder: Wrath of the Righteous is a game by Owlcat Games, based on Pathfinder by Paizo Inc. This is an unofficial fan modification and no game files are included.

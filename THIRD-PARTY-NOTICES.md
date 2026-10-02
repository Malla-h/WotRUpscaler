# Third-party notices

## NVIDIA DLSS / NGX (nvngx_dlss.dll and WotRUpscalerNative.dll)
This download contains NVIDIA software:
- `nvngx_dlss.dll`, NVIDIA's DLSS runtime (version 310.9.1), and
- `WotRUpscalerNative.dll`, which contains NVIDIA NGX / DLSS SDK code, statically linked.

That software is copyright NVIDIA Corporation and is provided under the **NVIDIA RTX SDKs License** (v. March 14, 2024). The full text is included in this download as
`licenses/NVIDIA-RTX-SDKs-LICENSE.txt`, and the license governs those files. In summary, and without replacing the license text:

- The NVIDIA files are included only as part of this software. You may not copy, sell, rent, sublicense, transfer or distribute them on their own or as
  a stand-alone product.
- You may not reverse engineer, decompile or disassemble any portion of the NVIDIA software, or remove copyright or other proprietary notices from it.
- NVIDIA and its suppliers keep all rights, title and interest in the NVIDIA software. It is provided as-is without warranty, and NVIDIA's liability is
  limited as stated in the license.
- NVIDIA DLSS runs only on NVIDIA GPUs that support it.
- This software is an unofficial fan modification and is **not sponsored or endorsed by NVIDIA**. NVIDIA, DLSS, RTX and GeForce RTX are trademarks of
  NVIDIA Corporation.

The source code of WotR Upscaler is released under the MIT License (see `LICENSE`); that license does not apply to the NVIDIA software above. If you want a
different DLSS runtime version, NVIDIA publishes them in its DLSS SDK repository (https://github.com/NVIDIA/DLSS).

## Playdead: Temporal Reprojection Anti-Aliasing in INSIDE (TAA)
The TAA upscaler's resolve shader (`WotRTaa.shader`, inside the shader bundle `wotrupscaler`) is based on Playdead's temporal reprojection code
(https://github.com/playdeadgames/temporal): clipping the history to the colour neighbourhood in YCoCg space, the closest-fragment motion vector, and the
luminance based feedback weight. It has been changed for this mod (upscaling, HDR colour handling). The original is under the MIT License:

> Copyright (c) 2015 Playdead
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

## Harmony and Unity Mod Manager
Runtime patching uses [Harmony](https://github.com/pardeike/Harmony) (MIT License) and the mod is loaded by Unity Mod Manager. Both come with the game's mod
setup and are not redistributed here.

## ModMenu (optional)
If the ModMenu mod is installed, this mod adds its options to ModMenu's Mods settings page. ModMenu is a separate mod with its own license and authors; it is
not included here.

## OptiScaler (optional, external)
FSR and XeSS can be used through [OptiScaler](https://github.com/optiscaler/OptiScaler), a separate project under the GPL-3.0 license. It is not included in
this download and this mod is not affiliated with it. FSR is a trademark of Advanced Micro Devices, Inc. and XeSS of Intel Corporation; they are mentioned only
to say what is and is not supported.

## Pathfinder: Wrath of the Righteous
The game is by Owlcat Games, based on Pathfinder by Paizo Inc. This is an unofficial fan modification; no game files are included.

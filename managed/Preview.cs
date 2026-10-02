using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRUpscaler
{
    // The character preview (inventory, character sheet, character creation) is drawn by a camera of its own into a texture. It is not scaled,
    // but it gets the selected temporal upscaler at its own resolution: DLAA with DLSS, or the TAA, with its own jitter, motion vectors and
    // history. It follows the same hand-off as the world camera, only without the full-resolution depth and ground-marker work (there are no
    // markers in the preview): the result replaces the camera's colour buffer, which has the same size.
    public static class Preview
    {
        static RenderTexture full, colorIn, depthCopy, motion;
        static readonly CommandBuffer cb = new CommandBuffer { name = "WotRUpscaler preview" };
        static readonly int DepthId = Shader.PropertyToID("_CameraDepthRT");
        static bool logged;

        // ---- the size of the preview texture ----
        // The game draws the preview into a small texture (760x920) that the interface stretches over a much larger area. The texture is made as
        // large as the area it appears in (never smaller than the game's own size), keeping its proportions. The texture object stays the same,
        // so the interface keeps showing it.
        static RenderTexture sized;            // the texture whose original size is remembered
        static int origW, origH;
        static UnityEngine.UI.Graphic shownBy;
        static int lookupFrame = -1000, missLogs;
        static string lastFit = "";

        // The interface element that shows the texture (any kind of graphic: a raw image, or an image whose material holds the texture).
        static UnityEngine.UI.Graphic FindDisplay(RenderTexture rt)
        {
            if (shownBy != null && shownBy.isActiveAndEnabled && shownBy.mainTexture == rt) return shownBy;
            if (Time.frameCount - lookupFrame < 30) return null;           // the search is not cheap: at most twice a second
            lookupFrame = Time.frameCount;
            shownBy = null;
            foreach (var g in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Graphic>())
                if (g != null && g.isActiveAndEnabled && g.mainTexture == rt) { shownBy = g; break; }
            if (shownBy == null && missLogs < 12)
            {
                // Not found: say what shows render textures, so the search can be corrected.
                missLogs++;
                int n = 0;
                foreach (var img in Resources.FindObjectsOfTypeAll<UnityEngine.UI.RawImage>())
                {
                    var t = img != null ? img.texture as RenderTexture : null;
                    if (t != null && n++ < 6) Main.Log("preview display search: raw image " + img.name + " shows render texture " + t.name + " " + t.width + "x" + t.height + ", active " + img.isActiveAndEnabled + (t == rt ? " (the preview texture)" : ""));
                }
                Main.Log("preview display search: nothing active shows the preview texture (" + rt.width + "x" + rt.height + "), " + n + " raw images with render textures");
            }
            return shownBy;
        }

        public static void FitTarget(Camera cam)
        {
            var rt = cam.targetTexture;
            if (rt == null) return;
            if (sized != rt) { sized = rt; origW = rt.width; origH = rt.height; shownBy = null; Main.Log("character preview texture: the game's size is " + origW + "x" + origH); }
            int w = origW, h = origH;
            if (Main.S.previewSize != 0)
            {
                var img = FindDisplay(rt);
                if (img != null)
                {
                    var corners = new Vector3[4];
                    img.rectTransform.GetWorldCorners(corners);
                    var canvas = img.canvas;
                    var uiCam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
                    Vector2 a = RectTransformUtility.WorldToScreenPoint(uiCam, corners[0]), b = RectTransformUtility.WorldToScreenPoint(uiCam, corners[2]);
                    float onH = Mathf.Abs(b.y - a.y);
                    if (onH > 1f)
                    {
                        float f = Mathf.Clamp(onH / origH, 1f, 4f);
                        h = Mathf.Min(Mathf.RoundToInt(origH * f), 2160);
                        w = Mathf.RoundToInt(origW * (h / (float)origH));
                    }
                }
                else if (rt.width != origW) { w = rt.width; h = rt.height; }       // keep the last size while the display is not found for a moment
            }
            if (Mathf.Abs(rt.height - h) <= 8 && Mathf.Abs(rt.width - w) <= 8) return;
            rt.Release();
            rt.width = w; rt.height = h;
            rt.Create();
            string fit = origW + "x" + origH + " -> " + w + "x" + h;
            if (fit != lastFit) { lastFit = fit; Main.Log("character preview texture resized " + fit); }
        }

        static RenderTexture Make(RenderTextureDescriptor d, string name, FilterMode f)
        {
            var rt = new RenderTexture(d) { name = name, filterMode = f };
            rt.Create();
            return rt;
        }

        static void Release(ref RenderTexture rt) { if (rt != null) { rt.Release(); rt = null; } }

        static RenderTextureDescriptor Plain(RenderTextureDescriptor cd)
        {
            var c = cd; c.depthBufferBits = 0; c.msaaSamples = 1; c.useMipMap = false; c.autoGenerateMips = false; c.enableRandomWrite = false;
            return c;
        }

        // Inputs at render resolution (cd), the upscaled result at output resolution (w x h, the size of the preview texture).
        static void Ensure(RenderTextureDescriptor cd, int w, int h)
        {
            if (colorIn != null && colorIn.width == cd.width && colorIn.height == cd.height && full != null && full.width == w && full.height == h) return;
            Release(ref colorIn); Release(ref depthCopy); Release(ref motion); Release(ref full);
            var c = Plain(cd);
            colorIn = Make(c, "WotRUpscaler preview color", FilterMode.Point);
            var d = c; d.depthBufferBits = 32; d.colorFormat = RenderTextureFormat.Depth; d.stencilFormat = GraphicsFormat.R8_UInt;
            depthCopy = Make(d, "WotRUpscaler preview depth", FilterMode.Point);
            var m = c; m.colorFormat = RenderTextureFormat.RGHalf; m.sRGB = false; m.enableRandomWrite = true;
            motion = Make(m, "WotRUpscaler preview MV", FilterMode.Point);
            var f = c; f.width = w; f.height = h; f.enableRandomWrite = true;
            full = Make(f, "WotRUpscaler preview output", FilterMode.Bilinear);
            Main.Log("character preview target " + cd.width + "x" + cd.height + " -> " + w + "x" + h + " " + full.format);
        }

        static readonly int ColorRtId = Shader.PropertyToID("_CameraColorRT"), AfterPostId = Shader.PropertyToID("_AfterPostProcessColorRT"), ScreenSizeId = Shader.PropertyToID("_ScreenSize");
        static int phaseR, phaseW;

        // Upscales the preview camera's colour buffer: before post-processing (hdr, the pipeline pass and its descriptor field are then given, as
        // the world camera's hand-off needs them) or after it. The result replaces the camera's colour buffers at output resolution. Does
        // nothing while the upscaler is not ready.
        public static void Run(ScriptableRenderContext ctx, ref RenderingData rd, bool hdr, object pass, System.Reflection.FieldInfo descriptorField)
        {
            var cam = rd.CameraData.Camera;
            var cd = rd.CameraData.CameraTargetDescriptor;
            int rw = cd.width, rh = cd.height;
            var tex = cam.targetTexture;
            int w = tex != null ? tex.width : rw, h = tex != null ? tex.height : rh;
            bool scaled = rw != w || rh != h;
            Ensure(cd, w, h);
            bool taa = Upscalers.IsTaa(Main.S);
            if (!(taa ? Taa.Tick(rw, rh, w, h, 1) : Dlss.Tick(rw, rh, w, h, full, 1))) return;
            if (rw != phaseR || w != phaseW) { phaseR = rw; phaseW = w; Jitter.SetPreviewPhases(rw, w); }

            int srcId = hdr ? ColorRtId : AfterPostId;
            var src = new RenderTargetIdentifier(srcId);
            cb.Clear();
            cb.CopyTexture(src, colorIn);
            cb.CopyTexture(new RenderTargetIdentifier(DepthId), depthCopy);
            Dlss.QueueCameraMv(cb, depthCopy, motion, rw, rh);
            bool objects = Main.S.characterMotion && ObjectMv.Prepare(cb, rw, rh, depthCopy);
            ctx.ExecuteCommandBuffer(cb);
            cb.Clear();
            if (objects)
            {
                ObjectMv.Draw(ctx, ref rd);
                Dlss.QueueComposite(cb, ObjectMv.Target, motion, rw, rh);
            }
            bool reset = Jitter.ResetPending || Time.frameCount - (taa ? Taa.PreviewFrame : Dlss.PreviewEvalFrame) > 1;
            Jitter.ResetPending = false;
            if (taa) Taa.Resolve(cb, colorIn, depthCopy, motion, full, rw, rh, w, h, reset, 1);
            else Dlss.QueueEval(cb, colorIn, depthCopy, motion, full, rw, rh, reset, 1);

            if (scaled)
            {
                // From here on the camera works at output resolution, as in the world camera's hand-off (without the depth and marker work).
                var fd = Plain(cd); fd.width = w; fd.height = h;
                cb.ReleaseTemporaryRT(srcId);
                cb.GetTemporaryRT(srcId, fd, FilterMode.Bilinear);
                if (!hdr)
                {
                    cb.ReleaseTemporaryRT(ColorRtId);
                    cb.GetTemporaryRT(ColorRtId, fd, FilterMode.Bilinear);
                }
                cb.SetGlobalVector(ScreenSizeId, new Vector4(w, h, 1f / w, 1f / h));
            }
            cb.CopyTexture(full, src);
            ctx.ExecuteCommandBuffer(cb);
            if (scaled && hdr && pass != null && descriptorField != null)
            {
                var d = (RenderTextureDescriptor)descriptorField.GetValue(pass);
                d.width = w; d.height = h;
                descriptorField.SetValue(pass, d);
            }
            Scaler.PostDlss = true;
            if (!logged) { logged = true; Main.Log("character preview upscaled (" + Upscalers.Name(Main.S) + ", " + rw + "x" + rh + " -> " + w + "x" + h + ", " + (hdr ? "before" : "after") + " post-processing)"); }
        }
    }
}

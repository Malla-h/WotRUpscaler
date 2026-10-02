using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;
using Owlcat.Runtime.Visual.RenderPipeline.RendererFeatures.Highlighting.Passes;

namespace WotRUpscaler
{
    // The game's outline pass (hover, selection) runs at the very end of the main camera's chain. Without help it would draw the outlines
    // at the low render resolution with the jittered projection and stretch them over the upscaled image: thick, blocky and shimmering.
    // Once the camera works at output resolution, draw them at output resolution with the un-jittered projection instead.
    //
    // The outlines are drawn after the upscaler and after the game's own anti-aliasing, so their edges stay jagged (as in a native render). Smooth
    // anti-aliases them with the game's own SMAA, limited to the screen area around the highlighted units and written back only where the outline
    // layer has pixels: the rest of the image is untouched, and nothing runs when nothing is highlighted.
    public static class Highlight
    {
        public static object PostPass;      // the world camera's post-processing pass (its private SMAA method is reused)
        static MethodInfo smaa;
        static FieldInfo postDescriptor;
        static readonly FieldInfo Renderers = AccessTools.Field(typeof(HighlighterPass), "m_Renderers");
        static readonly FieldInfo ColorAttachment = AccessTools.Field(typeof(HighlighterPass), "m_ColorAttachment");
        static readonly int HighlightId = Shader.PropertyToID("_HighlightRT"), TmpId = Shader.PropertyToID("_WotRSmaaTmp"),
            SrcId = Shader.PropertyToID("_WotRTaaColor"), DstId = Shader.PropertyToID("_WotRTaaDstSize");
        static Material mat;
        static bool matTried, failedLogged, loggedRect;
        static readonly CommandBuffer cb = new CommandBuffer { name = "WotRUpscaler outline smoothing" };
        static readonly Vector3[] Corner = new Vector3[8];

        public static void Remember(object postPass)
        {
            if (PostPass == postPass) return;
            PostPass = postPass;
            smaa = postPass != null ? AccessTools.Method(postPass.GetType(), "DoSubpixelMorphologicalAntialiasing") : null;
            postDescriptor = postPass != null ? AccessTools.Field(postPass.GetType(), "m_Descriptor") : null;
        }

        // Screen rectangle (pixels, origin at the lower left as Unity's scissor wants it) around the visible highlighted renderers, or false.
        static bool HighlightRect(HighlighterPass pass, Camera cam, out Rect rect)
        {
            rect = default(Rect);
            var list = Renderers != null ? Renderers.GetValue(pass) as List<Renderer> : null;
            if (list == null || list.Count == 0) return false;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            var vp = Jitter.Applied ? Jitter.VPCurrent : GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            bool any = false;
            foreach (var r in list)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || !r.isVisible) continue;
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var p = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) * 2 - 1, ((i >> 1) & 1) * 2 - 1, ((i >> 2) & 1) * 2 - 1));
                    var clip = vp * new Vector4(p.x, p.y, p.z, 1f);
                    if (clip.w <= 1e-3f) return false;                  // a corner at or behind the camera: leave it alone
                    float x = (clip.x / clip.w * 0.5f + 0.5f) * w, y = (0.5f - clip.y / clip.w * 0.5f) * h;      // rows from the top
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                any = true;
            }
            if (!any) return false;
            const float margin = 40f;                                   // the glow around the silhouette
            x0 = Mathf.Max(0f, x0 - margin); y0 = Mathf.Max(0f, y0 - margin); x1 = Mathf.Min(w, x1 + margin); y1 = Mathf.Min(h, y1 + margin);
            if (x1 - x0 < 2f || y1 - y0 < 2f) return false;
            rect = new Rect(Mathf.Floor(x0), h - Mathf.Ceil(y1), Mathf.Ceil(x1) - Mathf.Floor(x0), Mathf.Ceil(y1) - Mathf.Floor(y0));
            return true;
        }

        // Called after the outlines were composited (the camera is at output resolution). colorId: the target they were drawn onto.
        public static void Smooth(ScriptableRenderContext ctx, ref RenderingData rd, HighlighterPass pass)
        {
            if (!Main.S.smoothOutlines || PostPass == null || smaa == null || postDescriptor == null || ColorAttachment == null) return;
            var cam = rd.CameraData.Camera;
            Rect rect;
            if (!HighlightRect(pass, cam, out rect)) return;
            if (!matTried)
            {
                matTried = true;
                var sh = Bundle.Shader("Assets/Shaders/WotRTaa.shader");
                if (sh != null) mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            }
            if (mat == null) return;
            try
            {
                int colorId = ((RenderTargetHandle)ColorAttachment.GetValue(pass)).Id;
                int w = cam.pixelWidth, h = cam.pixelHeight;
                var d = rd.CameraData.CameraTargetDescriptor;
                d.width = w; d.height = h; d.depthBufferBits = 0; d.msaaSamples = 1; d.useMipMap = false; d.autoGenerateMips = false; d.enableRandomWrite = false;

                cb.Clear();
                cb.GetTemporaryRT(TmpId, d, FilterMode.Point);
                cb.EnableScissorRect(rect);
                // The game's SMAA reads the pass's descriptor for the size of its buffers: give it the output size while it runs.
                var saved = postDescriptor.GetValue(PostPass);
                var pd = (RenderTextureDescriptor)saved; pd.width = w; pd.height = h;
                postDescriptor.SetValue(PostPass, pd);
                object cameraData = rd.CameraData;
                try { smaa.Invoke(PostPass, new object[] { cameraData, cb, colorId, TmpId }); }
                finally { postDescriptor.SetValue(PostPass, saved); }
                // The SMAA result goes back only where the outline layer has pixels.
                cb.SetGlobalTexture(SrcId, new RenderTargetIdentifier(TmpId));
                cb.SetGlobalVector(DstId, new Vector4(w, h, 0f, 0f));
                cb.SetRenderTarget(new RenderTargetIdentifier(colorId));
                cb.DrawProcedural(Matrix4x4.identity, mat, 3, MeshTopology.Triangles, 3);
                cb.DisableScissorRect();
                cb.ReleaseTemporaryRT(TmpId);
                // The game's SMAA ends by setting the camera's own (jittered) projection: put the un-jittered one back.
                cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, Jitter.Applied ? Jitter.BaseProjection : cam.projectionMatrix);
                ctx.ExecuteCommandBuffer(cb);
                if (!loggedRect) { loggedRect = true; Main.Log("outline smoothing area " + rect + " of " + w + "x" + h); }
            }
            catch (System.Exception e)
            {
                if (!failedLogged) { failedLogged = true; Main.Log("outline smoothing failed: " + e); }
            }
        }
    }

    [HarmonyPatch(typeof(HighlighterPass), "Execute")]
    static class HighlighterPatch
    {
        static void Prefix(ScriptableRenderContext context, ref RenderingData renderingData, out int __state)
        {
            __state = 0;
            var cam = renderingData.CameraData.Camera;
            if (!Scaler.PostDlss || !Scaler.IsScaled(cam)) return;

            var d = renderingData.CameraData.CameraTargetDescriptor;
            __state = d.width;
            d.width = cam.pixelWidth; d.height = cam.pixelHeight;
            renderingData.CameraData.CameraTargetDescriptor = d;

            var cb = new CommandBuffer { name = "WotRUpscaler outline projection" };
            // (Without DLSS there is no jitter: the camera's own projection is already the un-jittered one.)
            cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, Jitter.Applied ? Jitter.BaseProjection : cam.projectionMatrix);
            context.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        static void Postfix(object __instance, ScriptableRenderContext context, ref RenderingData renderingData, int __state)
        {
            if (__state == 0) return;
            Highlight.Smooth(context, ref renderingData, (HighlighterPass)__instance);
            var cam = renderingData.CameraData.Camera;
            var d = renderingData.CameraData.CameraTargetDescriptor;
            float s = PixelSize.Snapped();
            d.width = (int)(cam.pixelWidth * s); d.height = (int)(cam.pixelHeight * s);
            renderingData.CameraData.CameraTargetDescriptor = d;
        }
    }
}

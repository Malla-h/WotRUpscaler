using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;
using Kingmaker.Visual.Decals;

namespace WotRUpscaler
{
    // Upscales the scaled main camera and hands a full-resolution image to the rest of the pipeline.
    //
    // HDR path (default): DLSS runs on the scene colour right before post-processing (linear HDR, what DLSS is built for). The colour
    // buffer is then replaced by the full-resolution result and the whole post-processing chain (bloom, depth of field, tonemapping)
    // runs at output resolution.
    // LDR path (fallback / option): DLSS runs on the finished post-processed image instead.
    //
    // Either way the UI camera, which shares the main camera's render targets, gets full-resolution targets with the finished image in
    // its colour buffer, so the UI renders and composites at full resolution.
    public static class Upscale
    {
        public static RenderTexture Full, Holder;
        public static int HdrFrame = -1;        // frame in which the HDR path produced the full-resolution colour buffer
        static readonly int AfterPP = Shader.PropertyToID("_AfterPostProcessColorRT");
        static readonly int ColorRt = Shader.PropertyToID("_CameraColorRT");
        static readonly int DepthId = Shader.PropertyToID("_CameraDepthRT");
        static CommandBuffer cb = new CommandBuffer { name = "WotRUpscaler upscale" };

        public static readonly string[] GBufferIds =
        {
            "_CameraColorRT", "_CameraColorPyramidRT", "_CameraDepthRT", "_CameraDepthCopyRT", "_CameraAlbedoRT", "_CameraNormalsRT",
            "_CameraBakedGIRT", "_CameraShadowmaskRT", "_CameraTranslucencyRT", "_CameraDeferredReflectionsRT"
        };

        static RenderTexture colorIn, depthCopy, motion;
        public static RenderTexture LowResDepth { get { return depthCopy; } }   // debug probes
        static Material depthUpMat;
        static Material[] stencilMats;
        static bool depthUpTried, depthUpLogged;
        static readonly int DepthCopyId = Shader.PropertyToID("_CameraDepthCopyRT");
        static readonly int ScreenSizeId = Shader.PropertyToID("_ScreenSize");
        static readonly int GuidedId = Shader.PropertyToID("_WotRGuided"), GuideLowId = Shader.PropertyToID("_WotRGuideLow"), GuideFullId = Shader.PropertyToID("_WotRGuideFull");
        static readonly int NormalsId = Shader.PropertyToID("_CameraNormalsRT"), SrcColor = Shader.PropertyToID("_WotRSrcColor"), JitterId = Shader.PropertyToID("_WotRJitter");
        static readonly int InvViewProjId = Shader.PropertyToID("_InvCameraViewProj"), InvProjId = Shader.PropertyToID("_InvProjMatrix");
        static RenderTexture normalsFull, normalsHist, depthHist, stillFlag, stillHist;
        static int histFrame = -1;
        static readonly int NormalsHistId = Shader.PropertyToID("_WotRNormalsHist"), MotionId = Shader.PropertyToID("_WotRMotion"),
            DepthFullId = Shader.PropertyToID("_WotRDepthFull"), DepthHistId = Shader.PropertyToID("_WotRDepthHist"), TemporalId = Shader.PropertyToID("_WotRTemporal"),
            StillHistId = Shader.PropertyToID("_WotRStillHist"), RectId = Shader.PropertyToID("_WotRRect");

        // Creates or resizes a persistent full-resolution texture; returns true when it was (re)created.
        static bool EnsureFullRT(ref RenderTexture rt, int w, int h, RenderTextureFormat f, string name)
        {
            if (rt != null && rt.width == w && rt.height == h) return false;
            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, 0, f, RenderTextureReadWrite.Linear) { name = name, filterMode = FilterMode.Point };
            rt.Create();
            return true;
        }
        static readonly int CameraDepthTextureId = Shader.PropertyToID("_CameraDepthTexture"), PyramidRatioId = Shader.PropertyToID("_DepthPyramidSamplingRatio");
        static readonly int SrcDepth = Shader.PropertyToID("_WotRDepth"), SrcSize = Shader.PropertyToID("_WotRSrcSize"), DstSize = Shader.PropertyToID("_WotRDstSize"), SrcStencil = Shader.PropertyToID("_WotRStencil");

        static RenderTexture Make(RenderTextureDescriptor d, string name, FilterMode f)
        {
            var rt = new RenderTexture(d) { name = name, filterMode = f };
            rt.Create();
            return rt;
        }

        static RenderTextureDescriptor FullDesc(RenderTextureDescriptor cd, int w, int h)
        {
            var d = cd; d.width = w; d.height = h; d.depthBufferBits = 0; d.msaaSamples = 1; d.useMipMap = false; d.autoGenerateMips = false; d.enableRandomWrite = false;
            return d;
        }

        static void EnsureInputs(RenderTextureDescriptor cd)
        {
            int rw = cd.width, rh = cd.height;
            if (colorIn != null && colorIn.width == rw && colorIn.height == rh) return;
            if (colorIn != null) colorIn.Release();
            if (depthCopy != null) depthCopy.Release();
            if (motion != null) motion.Release();
            var c = FullDesc(cd, rw, rh);
            colorIn = Make(c, "WotRUpscaler Color", FilterMode.Point);
            var d = c; d.depthBufferBits = 32; d.colorFormat = RenderTextureFormat.Depth;
            // A stencil format makes the stencil readable from shaders (RenderTextureSubElement.Stencil); without it the copy of the
            // stencil bits into the full-resolution buffer reads zeros, and the ground markers (stencil-tested decals) disappear.
            d.stencilFormat = GraphicsFormat.R8_UInt;
            depthCopy = Make(d, "WotRUpscaler Depth", FilterMode.Point);
            var m = c; m.colorFormat = RenderTextureFormat.RGHalf; m.sRGB = false; m.enableRandomWrite = true;
            motion = Make(m, "WotRUpscaler MV", FilterMode.Point);
            Main.Log("DLSS inputs " + rw + "x" + rh + " color " + colorIn.format + " depth " + depthCopy.format);
        }

        static void EnsureFull(RenderTextureDescriptor cd, int w, int h)
        {
            if (Full != null && Full.width == w && Full.height == h) return;
            if (Full != null) Full.Release();
            if (Holder != null) { Holder.Release(); Holder = null; }
            var d = FullDesc(cd, w, h); d.enableRandomWrite = true;
            Full = Make(d, "WotRUpscaler Full", FilterMode.Bilinear);
            Main.Log("full-res target " + w + "x" + h + " " + Full.format + ", camera target " + cd.width + "x" + cd.height);
        }

        // Runs motion vectors, optional object motion and the DLSS evaluate with srcId (render resolution) as colour, output in Full.
        // Returns false while DLSS is not ready (the caller then falls back to a plain upscale).
        static bool RunDlss(ScriptableRenderContext ctx, ref RenderingData rd, int srcId)
        {
            var cam = rd.CameraData.Camera;
            var cd = rd.CameraData.CameraTargetDescriptor;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            EnsureFull(cd, w, h);
            EnsureInputs(cd);
            if (!Dlss.Tick(cd.width, cd.height, w, h, Full)) return false;

            int rw = cd.width, rh = cd.height;
            cb.Clear();
            GpuTimer.Mark(cb, 1);
            cb.CopyTexture(new RenderTargetIdentifier(srcId), colorIn);
            cb.CopyTexture(new RenderTargetIdentifier(DepthId), depthCopy);
            Dlss.QueueCameraMv(cb, depthCopy, motion, rw, rh);
            bool objects = Main.S.characterMotion && ObjectMv.Prepare(cb, rw, rh, depthCopy);
            ctx.ExecuteCommandBuffer(cb);
            cb.Clear();
            if (objects)
            {
                ObjectMv.Draw(ctx, ref rd);
                Dlss.QueueComposite(cb, ObjectMv.Target, motion, rw, rh);
                PbdMotion.Snapshot(cb);
            }
            bool reset = Jitter.ResetPending || Time.frameCount - Dlss.LastEvalFrame > 1;
            Jitter.ResetPending = false;
            Dlss.QueueEval(cb, colorIn, depthCopy, motion, Full, rw, rh, reset);
            Capture.Queue(cb, colorIn, motion, objects ? ObjectMv.Target : null, Full, rw, rh, reset);
            GpuTimer.Mark(cb, 2);
            ctx.ExecuteCommandBuffer(cb);
            return true;
        }

        // Screen areas (output pixels, rows as the rasteriser counts them: from the top) where the game's GUI decals (selection circle, click
        // marker) can draw this frame. Only there do the stencil, normals and held values have to be rebuilt at full resolution.
        struct Box { public int x0, y0, x1, y1; }
        static readonly List<Box> boxes = new List<Box>();

        static void MarkerAreas(Camera cam, int w, int h)
        {
            boxes.Clear();
            var full = new Box { x0 = 0, y0 = 0, x1 = w, y1 = h };
            if (Main.S.debugFullMarkerBuffers) { boxes.Add(full); return; }
            var vp = Jitter.Applied ? Jitter.VPCurrent : GL.GetGPUProjectionMatrix(cam.projectionMatrix, true) * cam.worldToCameraMatrix;
            const int margin = 24;
            foreach (var d in ScreenSpaceDecal.All)
            {
                if (d.Type != ScreenSpaceDecal.DecalType.GUI || !d.isActiveAndEnabled || !d.IsVisible) continue;
                var m = d.transform.localToWorldMatrix;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                for (int i = 0; i < 8; i++)
                {
                    var wp = m.MultiplyPoint3x4(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f));
                    var clip = vp * new Vector4(wp.x, wp.y, wp.z, 1f);
                    if (clip.w <= 1e-3f) { boxes.Clear(); boxes.Add(full); return; }       // a corner at or behind the camera: be safe
                    float x = (clip.x / clip.w * 0.5f + 0.5f) * w, y = (0.5f - clip.y / clip.w * 0.5f) * h;
                    if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                }
                var b = new Box
                {
                    x0 = Mathf.Max(0, Mathf.FloorToInt(x0) - margin), y0 = Mathf.Max(0, Mathf.FloorToInt(y0) - margin),
                    x1 = Mathf.Min(w, Mathf.CeilToInt(x1) + margin), y1 = Mathf.Min(h, Mathf.CeilToInt(y1) + margin)
                };
                if (b.x1 > b.x0 && b.y1 > b.y0) boxes.Add(b);
            }
            // Merge overlapping areas; many scattered ones become one.
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < boxes.Count && !merged; i++)
                    for (int j = i + 1; j < boxes.Count && !merged; j++)
                    {
                        var a = boxes[i]; var c = boxes[j];
                        if (a.x0 < c.x1 && c.x0 < a.x1 && a.y0 < c.y1 && c.y0 < a.y1)
                        {
                            boxes[i] = new Box { x0 = Mathf.Min(a.x0, c.x0), y0 = Mathf.Min(a.y0, c.y0), x1 = Mathf.Max(a.x1, c.x1), y1 = Mathf.Max(a.y1, c.y1) };
                            boxes.RemoveAt(j);
                            merged = true;
                        }
                    }
            }
            if (boxes.Count > 5)
            {
                var u = boxes[0];
                foreach (var b in boxes) u = new Box { x0 = Mathf.Min(u.x0, b.x0), y0 = Mathf.Min(u.y0, b.y0), x1 = Mathf.Max(u.x1, b.x1), y1 = Mathf.Max(u.y1, b.y1) };
                boxes.Clear(); boxes.Add(u);
            }
        }

        static void DrawBoxes(CommandBuffer c, int pass)
        {
            foreach (var b in boxes)
            {
                c.SetGlobalVector(RectId, new Vector4(b.x0, b.y0, b.x1, b.y1));
                c.DrawProcedural(Matrix4x4.identity, depthUpMat, pass, MeshTopology.Triangles, 6);
            }
        }

        static void CopyBoxes(CommandBuffer c, RenderTargetIdentifier src, RenderTargetIdentifier dst)
        {
            foreach (var b in boxes) c.CopyTexture(src, 0, 0, b.x0, b.y0, b.x1 - b.x0, b.y1 - b.y0, dst, 0, 0, b.x0, b.y0);
        }

        // The rest of the camera's chain (decals for the selection circle and click marker, depth of field, ...) expects depth buffers of the
        // same size as the colour buffer. Replace both depth targets by nearest-neighbour upscales of the low-resolution scene depth.
        // Needs depthCopy to hold this frame's depth.
        static void UpscaleDepth(CommandBuffer c, RenderTextureDescriptor cd, int w, int h, Camera cam, bool guided)
        {
            // Edges follow the upscaled image when there is one: the DLSS input (colorIn) and output (Full) of this frame.
            c.SetGlobalFloat(GuidedId, guided ? 1f : 0f);
            if (guided) { c.SetGlobalTexture(GuideLowId, colorIn); c.SetGlobalTexture(GuideFullId, Full); }

            // The pipeline sets _ScreenSize once per camera, from the (scaled) size the scene was rendered at. Everything after this point
            // works at output resolution, so the constant has to follow, from here on in the command stream.
            c.SetGlobalVector(ScreenSizeId, new Vector4(w, h, 1f / w, 1f / h));

            if (!depthUpTried)
            {
                depthUpTried = true;
                var sh = Bundle.Shader("Assets/Shaders/WotRDepthUpscale.shader");
                if (sh != null)
                {
                    depthUpMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    // The stencil reference is render state, so it cannot change per draw: one material per bit. Only the bit the decal
                    // shaders test after DLSS ("receive decals", 1) is rebuilt.
                    stencilMats = new Material[1];
                    stencilMats[0] = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
                    stencilMats[0].SetFloat("_Ref", 1);
                }
            }
            if (depthUpMat == null) { if (!depthUpLogged) { depthUpLogged = true; Main.Log("depth upscale shader missing: " + Bundle.Error); } return; }

            var depthDesc = FullDesc(cd, w, h); depthDesc.depthBufferBits = 32; depthDesc.colorFormat = RenderTextureFormat.Depth;
            var copyDesc = FullDesc(cd, w, h); copyDesc.colorFormat = RenderTextureFormat.RFloat; copyDesc.sRGB = false; copyDesc.enableRandomWrite = true;

            c.SetGlobalTexture(SrcDepth, depthCopy);
            c.SetGlobalVector(SrcSize, new Vector4(cd.width, cd.height, 0f, 0f));
            c.SetGlobalVector(DstSize, new Vector4(w, h, 0f, 0f));
            // Everything from here on works with the un-jittered camera: read the low-resolution pixel that holds what that camera sees.
            var j = Jitter.FrameJitter;
            c.SetGlobalVector(JitterId, new Vector4(j.x, j.y, 0f, 0f));

            // Last frame's values for the ground markers: the jittered low-resolution sources change a little every frame, and decals that read
            // them (selection circle, click marker) flickered. A pixel that was already still in the previous frame and is still now keeps its
            // exact value (see the DepthHold / NormalsHold shader passes). Only the screen areas where GUI decals can draw are processed.
            bool fresh = EnsureFullRT(ref depthHist, w, h, RenderTextureFormat.RFloat, "WotRUpscaler Depth history");
            fresh |= EnsureFullRT(ref normalsFull, w, h, RenderTextureFormat.ARGB32, "WotRUpscaler Normals");
            fresh |= EnsureFullRT(ref normalsHist, w, h, RenderTextureFormat.ARGB32, "WotRUpscaler Normals history");
            fresh |= EnsureFullRT(ref stillFlag, w, h, RenderTextureFormat.R8, "WotRUpscaler Still");
            fresh |= EnsureFullRT(ref stillHist, w, h, RenderTextureFormat.R8, "WotRUpscaler Still history");
            if (fresh) histFrame = -1;
            bool temporal = guided && Jitter.Applied && motion != null && histFrame == Time.frameCount - 1 && !Jitter.ResetPending;
            var swap = stillFlag; stillFlag = stillHist; stillHist = swap;          // last frame's flags become the history
            if (motion != null) c.SetGlobalTexture(MotionId, motion);
            c.SetGlobalTexture(DepthHistId, depthHist);
            c.SetGlobalTexture(StillHistId, stillHist);
            c.SetGlobalVector(TemporalId, new Vector4(temporal ? 1f : 0f, Main.S.holdTolerance, 0f, 0f));
            MarkerAreas(cam, w, h);

            // Full-resolution depth, the whole screen: the depth buffer and the sampled copy (_CameraDepthCopyRT) in one pass.
            c.ReleaseTemporaryRT(DepthId);
            c.GetTemporaryRT(DepthId, depthDesc, FilterMode.Point);
            c.ReleaseTemporaryRT(DepthCopyId);
            c.GetTemporaryRT(DepthCopyId, copyDesc, FilterMode.Point);
            c.SetRenderTarget(new RenderTargetIdentifier(DepthCopyId), new RenderTargetIdentifier(DepthId));
            c.ClearRenderTarget(true, false, Color.clear, 0f);           // a fresh temporary target holds garbage, stencil included
            c.DrawProcedural(Matrix4x4.identity, depthUpMat, 7, MeshTopology.Triangles, 3);

            // The geometry pass tags pixels in the stencil ("receive decals"); the decal shaders test that bit.
            if (boxes.Count > 0)
            {
                c.SetRenderTarget(new RenderTargetIdentifier(DepthId));
                c.SetGlobalTexture(SrcStencil, new RenderTargetIdentifier(depthCopy), RenderTextureSubElement.Stencil);
                foreach (var b in boxes)
                {
                    c.SetGlobalVector(RectId, new Vector4(b.x0, b.y0, b.x1, b.y1));
                    c.DrawProcedural(Matrix4x4.identity, stencilMats[0], 2, MeshTopology.Triangles, 6);
                }
            }
            GpuTimer.Mark(c, 3);

            // Held depth for the sampled copy, and the history for the next frame.
            if (boxes.Count > 0)
            {
                c.SetRenderTarget(new RenderTargetIdentifier(DepthCopyId));
                DrawBoxes(c, 5);
                CopyBoxes(c, new RenderTargetIdentifier(DepthCopyId), depthHist);
            }

            // Restore what the pipeline's depth copy pass leaves behind: shaders that read "the depth" get the copy, while the real depth
            // buffer stays bound as the depth target. (GetTemporaryRT above rebound _CameraDepthRT to the depth buffer itself; a draw that
            // samples it while it is also the depth target reads nothing on D3D11, which is what hid the ground markers.)
            c.SetGlobalTexture(DepthId, new RenderTargetIdentifier(DepthCopyId));
            c.SetGlobalTexture(CameraDepthTextureId, new RenderTargetIdentifier(DepthCopyId));
            c.SetGlobalVector(PyramidRatioId, new Vector4(1f, 1f, 0f, 0f));

            // G-buffer normals: the decal shaders read them by pixel position (outside the low-resolution size they read nothing, which
            // limited the ground markers to one quarter of the screen).
            c.SetGlobalTexture(SrcColor, new RenderTargetIdentifier(NormalsId));        // the low-resolution ones, still allocated here
            c.SetGlobalTexture(NormalsHistId, normalsHist);
            c.SetGlobalTexture(DepthFullId, new RenderTargetIdentifier(DepthCopyId));
            if (boxes.Count > 0)
            {
                c.SetRenderTarget(normalsFull);
                DrawBoxes(c, 4);
            }
            // Which pixels are still, for the next frame (cleared everywhere else).
            c.SetRenderTarget(stillFlag);
            c.ClearRenderTarget(false, true, Color.clear);
            if (boxes.Count > 0) DrawBoxes(c, 6);
            histFrame = guided && Jitter.Applied ? Time.frameCount : -1;

            var normalsDesc = FullDesc(cd, w, h); normalsDesc.colorFormat = RenderTextureFormat.ARGB32; normalsDesc.sRGB = false;
            c.ReleaseTemporaryRT(NormalsId);
            c.GetTemporaryRT(NormalsId, normalsDesc, FilterMode.Bilinear);
            if (boxes.Count > 0)
            {
                CopyBoxes(c, normalsFull, new RenderTargetIdentifier(NormalsId));
                CopyBoxes(c, normalsFull, normalsHist);
            }
            GpuTimer.Mark(c, 4);

            // Un-jittered matrices for the rest of the chain, set in the command stream. (The camera itself must keep its jittered projection:
            // the pipeline reads it when the frame's commands run, so changing it here would remove the jitter from the whole scene.)
            if (Jitter.Applied)
            {
                var proj = Jitter.BaseProjection;
                var gpu = GL.GetGPUProjectionMatrix(proj, true);
                c.SetGlobalMatrix(InvViewProjId, (gpu * cam.worldToCameraMatrix).inverse);
                c.SetGlobalMatrix(InvProjId, gpu.inverse);
                c.SetViewProjectionMatrices(cam.worldToCameraMatrix, proj);
            }
        }

        // Exact, un-jittered character silhouettes in the stencil of the full-resolution depth buffer (see ObjectMv.DrawCharacterMask).
        static void CharacterMask(ScriptableRenderContext ctx, ref RenderingData rd, Camera cam)
        {
            if (depthUpMat == null || boxes.Count == 0) return;
            ObjectMv.DrawCharacterMask(ctx, ref rd, DepthId, Jitter.Applied ? Jitter.BaseProjection : cam.projectionMatrix);
        }

        // Debug: copies the current _CameraDepthRT (whatever size it has, as long as it matches dst) into a float texture for inspection.
        public static void ProbeDepth(CommandBuffer c, RenderTexture dst)
        {
            if (depthUpMat == null) { Main.Log("probe: depth upscale material missing"); return; }
            c.SetGlobalTexture(SrcDepth, new RenderTargetIdentifier(DepthId));
            c.SetGlobalVector(SrcSize, new Vector4(dst.width, dst.height, 0f, 0f));
            c.SetGlobalVector(DstSize, new Vector4(dst.width, dst.height, 0f, 0f));
            c.SetGlobalVector(JitterId, Vector4.zero);
            c.SetGlobalFloat(GuidedId, 0f);
            c.SetRenderTarget(dst);
            c.DrawProcedural(Matrix4x4.identity, depthUpMat, 1, MeshTopology.Triangles, 3);
        }

        // HDR path. Called before the post-processing pass of the scaled camera. Returns true when the colour buffer was replaced.
        public static bool BeforePost(ScriptableRenderContext ctx, ref RenderingData rd, object pass, FieldInfo descriptorField)
        {
            var cam = rd.CameraData.Camera;
            var cd = rd.CameraData.CameraTargetDescriptor;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            if (!RunDlss(ctx, ref rd, ColorRt)) return false;

            // Replace the (low-res) colour buffer by the full-resolution DLSS output.
            var full = FullDesc(cd, w, h);
            cb.Clear();
            cb.ReleaseTemporaryRT(ColorRt);
            cb.GetTemporaryRT(ColorRt, full, FilterMode.Bilinear);
            cb.CopyTexture(Full, new RenderTargetIdentifier(ColorRt));
            UpscaleDepth(cb, cd, w, h, cam, true);
            ctx.ExecuteCommandBuffer(cb);
            CharacterMask(ctx, ref rd, cam);
            GpuTimer.Mark(ctx, 5);

            // The post-processing chain allocates its buffers from this descriptor, so it now has to describe the full-resolution image.
            var d = (RenderTextureDescriptor)descriptorField.GetValue(pass);
            d.width = w; d.height = h;
            descriptorField.SetValue(pass, d);

            Scaler.PostDlss = true;
            HdrFrame = Time.frameCount;
            Scaler.MainFrame = Time.frameCount;
            return true;
        }

        // LDR path. Called after the post-processing pass of the scaled camera.
        public static void AfterPost(ScriptableRenderContext ctx, ref RenderingData rd)
        {
            var cam = rd.CameraData.Camera;
            var cd = rd.CameraData.CameraTargetDescriptor;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            bool ok = Main.S.dlss && !Main.S.dlssBeforePost && RunDlss(ctx, ref rd, AfterPP);
            if (!ok)
            {
                EnsureFull(cd, w, h);
                cb.Clear();
                GpuTimer.Mark(cb, 1);
                cb.Blit(new RenderTargetIdentifier(AfterPP), Full);
                GpuTimer.Mark(cb, 2);
                ctx.ExecuteCommandBuffer(cb);
            }

            // From here on the camera works at output resolution: the final blit, the outline pass and the UI hand-off all expect
            // full-resolution buffers. The post-processed image becomes the full-resolution one.
            var full = FullDesc(cd, w, h);
            cb.Clear();
            cb.ReleaseTemporaryRT(AfterPP);
            cb.GetTemporaryRT(AfterPP, full, FilterMode.Bilinear);
            cb.CopyTexture(Full, new RenderTargetIdentifier(AfterPP));
            cb.ReleaseTemporaryRT(ColorRt);
            cb.GetTemporaryRT(ColorRt, full, FilterMode.Bilinear);
            if (!ok) { EnsureInputs(cd); cb.CopyTexture(new RenderTargetIdentifier(DepthId), depthCopy); }
            UpscaleDepth(cb, cd, w, h, cam, ok);
            ctx.ExecuteCommandBuffer(cb);
            CharacterMask(ctx, ref rd, cam);
            GpuTimer.Mark(ctx, 5);
            Scaler.PostDlss = true;
            HdrFrame = Time.frameCount;
            Scaler.MainFrame = Time.frameCount;
        }

        // Called for the UI camera before its buffers are released: keep the finished full-resolution image.
        public static void Stash(CommandBuffer rel)
        {
            if (HdrFrame != Time.frameCount) return;
            if (Holder == null || Holder.width != Full.width || Holder.height != Full.height)
            {
                if (Holder != null) Holder.Release();
                var d = Full.descriptor; d.enableRandomWrite = false;
                Holder = Make(d, "WotRUpscaler Holder", FilterMode.Bilinear);
            }
            rel.CopyTexture(new RenderTargetIdentifier(ColorRt), Holder);
        }

        public static void CopyIntoColor(ScriptableRenderContext ctx)
        {
            cb.Clear();
            cb.Blit(HdrFrame == Time.frameCount ? Holder : Full, new RenderTargetIdentifier(ColorRt));
            ctx.ExecuteCommandBuffer(cb);
        }
    }

    [HarmonyPatch]
    static class PostProcessPatch
    {
        static FieldInfo isFinal, descriptor;
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Owlcat.Runtime.Visual.RenderPipeline.Passes.PostProcessPass");
            isFinal = AccessTools.Field(t, "m_IsFinalPass");
            descriptor = AccessTools.Field(t, "m_Descriptor");
            return AccessTools.Method(t, "Execute");
        }

        static void Prefix(object __instance, ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cam = renderingData.CameraData.Camera;
            if (!Scaler.IsScaled(cam) || !Main.S.dlss || !Main.S.dlssBeforePost || (bool)isFinal.GetValue(__instance)) return;
            Upscale.BeforePost(context, ref renderingData, __instance, descriptor);
        }

        static void Postfix(object __instance, ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cam = renderingData.CameraData.Camera;
            if (!Scaler.IsScaled(cam) || (bool)isFinal.GetValue(__instance)) return;
            if (Scaler.PostDlss) return;            // the HDR path already produced the full-resolution image
            Upscale.AfterPost(context, ref renderingData);
        }
    }

    // The UI camera is in the same chain as the main camera, so it would reuse the main camera's buffers. Make it allocate its own at full res.
    [HarmonyPatch(typeof(GBuffer), "Initialize")]
    static class UiGBufferPatch
    {
        static void Prefix(ScriptableRenderContext context, ref RenderingData renderingData, out bool __state)
        {
            __state = false;
            var cd = renderingData.CameraData;
            if (!Scaler.Active || cd.IsFirstInChain || !Scaler.IsUiCamera(cd.Camera) || Scaler.MainFrame != Time.frameCount || Upscale.Full == null) return;
            var rel = new CommandBuffer { name = "WotRUpscaler release gbuffer" };
            Upscale.Stash(rel);
            foreach (var n in Upscale.GBufferIds) rel.ReleaseTemporaryRT(Shader.PropertyToID(n));
            context.ExecuteCommandBuffer(rel);
            rel.Release();
            renderingData.CameraData.IsFirstInChain = true;
            __state = true;
        }

        static void Postfix(ScriptableRenderContext context, ref RenderingData renderingData, bool __state)
        {
            if (!__state) return;
            renderingData.CameraData.IsFirstInChain = false;
            Upscale.CopyIntoColor(context);
        }
    }
}

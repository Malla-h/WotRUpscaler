using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRUpscaler
{
    // Sub-pixel camera jitter for the upscaled cameras, and the view-projection matrices the depth-based motion vector pass needs.
    // Two cameras are upscaled, each with its own jitter sequence and previous-frame matrices: the world camera (scaled) and the character
    // preview camera (inventory, character creation; drawn into a texture, anti-aliased at its own resolution). The static members below
    // describe the camera that is rendering right now.
    public static class Jitter
    {
        class State
        {
            public Vector2 JitterPx;
            public Matrix4x4 InvVPJittered = Matrix4x4.identity, VPCurrent = Matrix4x4.identity, VPPrevious = Matrix4x4.identity, VPJittered = Matrix4x4.identity;
            public Matrix4x4 baseProj, lastVP;
            public bool HavePrevious, Reset, customProj, applied;
            public int phases = 18, index, lastFrame = -100;
            public OwlcatAdditionalCameraData aaData;
            public AntialiasingMode aaOrig;
        }

        static readonly State world = new State(), preview = new State { phases = 8 };
        static State cur = world;

        public static Vector2 JitterPx { get { return cur.JitterPx; } }
        public static Matrix4x4 InvVPJittered { get { return cur.InvVPJittered; } }
        public static Matrix4x4 VPCurrent { get { return cur.VPCurrent; } }
        public static Matrix4x4 VPPrevious { get { return cur.VPPrevious; } }
        public static Matrix4x4 VPJittered { get { return cur.VPJittered; } }
        public static Matrix4x4 BaseProjection { get { return cur.baseProj; } }   // the camera projection without jitter
        public static bool Applied { get { return cur.applied; } }                 // jitter is active for the camera being rendered
        public static Vector2 FrameJitter { get { return cur.applied ? cur.JitterPx : Vector2.zero; } }
        public static bool HavePrevious { get { return cur.HavePrevious; } }
        // Set to true to restart every history (a setting changed); a camera clears its own once it has used it.
        public static bool ResetPending
        {
            get { return cur.Reset; }
            set { if (value) { world.Reset = true; preview.Reset = true; } else cur.Reset = false; }
        }

        public static void SetPhases(int rw, int outW)
        {
            float r = outW / (float)rw;
            world.phases = Mathf.Clamp(Mathf.CeilToInt(8f * r * r), 8, 96);
            world.index = 0;
        }

        public static void SetPreviewPhases(int rw, int outW)
        {
            float r = outW / (float)rw;
            preview.phases = Mathf.Clamp(Mathf.CeilToInt(8f * r * r), 8, 96);
            preview.index = 0;
        }

        static float Halton(int i, int radix)
        {
            float r = 0f, f = 1f / radix;
            for (; i > 0; i /= radix) { r += f * (i % radix); f /= radix; }
            return r;
        }

        static bool Approx(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (Mathf.Abs(a[i] - b[i]) > 1e-5f) return false;
            return true;
        }

        public static void Install()
        {
            RenderPipelineManager.beginCameraRendering += OnBegin;
            RenderPipelineManager.endCameraRendering += OnEnd;
        }

        public static void Uninstall()
        {
            RenderPipelineManager.beginCameraRendering -= OnBegin;
            RenderPipelineManager.endCameraRendering -= OnEnd;
        }

        // Every distinct way a camera is rendered is logged once (name, type, whether it draws into a texture, size, whether the mod scales it and the
        // game's anti-aliasing mode), so a screen that is not treated as expected can be understood from the log.
        static readonly System.Collections.Generic.HashSet<string> seenCameras = new System.Collections.Generic.HashSet<string>();
        static void LogCamera(Camera cam)
        {
            if (cam == null) return;
            var data = cam.GetComponent<OwlcatAdditionalCameraData>();
            string key = cam.name + "|" + cam.cameraType + "|" + (cam.targetTexture != null) + "|" + Scaler.IsScaled(cam) + "|" + cam.pixelWidth + "x" + cam.pixelHeight
                + "|" + (data != null ? data.Antialiasing.ToString() : "-");
            if (!seenCameras.Add(key)) return;
            Main.Log("camera: " + cam.name + " type " + cam.cameraType + ", draws into a texture " + (cam.targetTexture != null) + ", " + cam.pixelWidth + "x" + cam.pixelHeight
                + ", scaled by the mod " + Scaler.IsScaled(cam) + ", game anti-aliasing " + (data != null ? data.Antialiasing.ToString() : "none") + ", depth " + cam.depth);
        }

        static void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            GpuTimer.CameraBegin(ctx, cam);
            LogCamera(cam);
            bool isPreview = Scaler.IsPreview(cam);
            if (isPreview) { MipBias.DollFrame = Time.frameCount; try { Preview.FitTarget(cam); } catch (System.Exception e) { Main.Log("character preview resize failed: " + e.Message); } }
            cur = isPreview ? preview : world;
            var st = cur;
            st.applied = false;
            Scaler.PostDlss = false;
            Scaler.PreviewUpscaled = false;
            if (isPreview) { if (!Scaler.PreviewActive(cam)) return; }
            else if (!Scaler.IsScaled(cam)) return;
            // Unity only records previous-frame object transforms and skinned positions for cameras that ask for motion vectors. Needed for
            // the character motion vectors and for the character silhouettes the ground markers are clipped against (also without DLSS).
            cam.depthTextureMode |= DepthTextureMode.MotionVectors;
            if (!Upscalers.Temporal(Main.S)) return;

            // Work out whether something else already set a custom projection, so it can be put back untouched.
            var orig = cam.projectionMatrix;
            cam.ResetProjectionMatrix();
            st.customProj = !Approx(orig, cam.projectionMatrix);
            st.baseProj = orig;

            int rw = PixelSize.Width(cam), rh = PixelSize.Height(cam);
            st.index = (st.index + 1) % st.phases;
            st.JitterPx = Main.S.noJitter ? Vector2.zero : new Vector2(Halton(st.index + 1, 2) - 0.5f, Halton(st.index + 1, 3) - 0.5f);

            var jit = st.baseProj;
            jit[0, 2] += st.JitterPx.x * 2f / rw;
            jit[1, 2] += st.JitterPx.y * 2f / rh;
            cam.projectionMatrix = jit;
            cam.nonJitteredProjectionMatrix = st.baseProj;

            var view = cam.worldToCameraMatrix;
            st.VPCurrent = GL.GetGPUProjectionMatrix(st.baseProj, true) * view;
            st.VPJittered = GL.GetGPUProjectionMatrix(jit, true) * view;
            st.InvVPJittered = st.VPJittered.inverse;
            bool gap = Time.frameCount - st.lastFrame > 1;
            st.VPPrevious = (st.HavePrevious && !gap && !st.Reset) ? st.lastVP : st.VPCurrent;
            st.applied = true;
            Scaler.PreviewUpscaled = isPreview;
            if (isPreview) MipBias.DollUpscaledFrame = Time.frameCount;

            // The game's SMAA or FXAA (whichever the camera uses: both are values of the same antialiasing mode) would run on the image around
            // the upscaler and soften what it gets to work with.
            st.aaData = null;
            if (Main.S.disableGameAA && Upscalers.TemporalReady(Main.S))
            {
                st.aaData = cam.GetComponent<OwlcatAdditionalCameraData>();
                if (st.aaData != null) { st.aaOrig = st.aaData.Antialiasing; st.aaData.Antialiasing = AntialiasingMode.None; }
            }
        }

        static void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            GpuTimer.CameraEnd(ctx, cam);
            bool isPreview = Scaler.IsPreview(cam);
            var st = isPreview ? preview : world;
            if (!st.applied || !(isPreview || Scaler.IsScaled(cam))) return;
            Scaler.PostDlss = false;
            Scaler.PreviewUpscaled = false;
            if (st.aaData != null) { st.aaData.Antialiasing = st.aaOrig; st.aaData = null; }
            if (st.customProj) cam.projectionMatrix = st.baseProj; else cam.ResetProjectionMatrix();
            st.lastVP = st.VPCurrent;
            st.HavePrevious = true;
            st.lastFrame = Time.frameCount;
            st.applied = false;
        }
    }
}

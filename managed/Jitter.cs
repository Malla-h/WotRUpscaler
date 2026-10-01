using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRDLSS
{
    // Sub-pixel camera jitter for the scaled camera, and the view-projection matrices the depth-based motion vector pass needs.
    public static class Jitter
    {
        public static Vector2 JitterPx;
        public static Matrix4x4 InvVPJittered = Matrix4x4.identity, VPCurrent = Matrix4x4.identity, VPPrevious = Matrix4x4.identity;
        public static Matrix4x4 VPJittered = Matrix4x4.identity;
        public static Matrix4x4 BaseProjection { get { return baseProj; } }   // the camera projection without jitter
        public static bool Applied { get { return applied; } }                 // jitter is active for the camera being rendered
        public static Vector2 FrameJitter { get { return applied ? JitterPx : Vector2.zero; } }
        public static bool HavePrevious;
        public static bool ResetPending;

        static int phases = 18, index;
        static Matrix4x4 baseProj, lastVP;
        static bool customProj, applied;
        static int lastFrame = -100;
        static OwlcatAdditionalCameraData aaData;
        static AntialiasingMode aaOrig;

        public static void SetPhases(int rw, int outW)
        {
            float r = outW / (float)rw;
            phases = Mathf.Clamp(Mathf.CeilToInt(8f * r * r), 8, 96);
            index = 0;
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

        static void OnBegin(ScriptableRenderContext ctx, Camera cam)
        {
            applied = false;
            Scaler.PostDlss = false;
            if (!Scaler.IsScaled(cam)) return;
            // Unity only records previous-frame object transforms and skinned positions for cameras that ask for motion vectors. Needed for
            // the character motion vectors and for the character silhouettes the ground markers are clipped against (also without DLSS).
            cam.depthTextureMode |= DepthTextureMode.MotionVectors;
            if (!Main.S.dlss) return;

            // Unity only records previous-frame object transforms and skinned positions for cameras that ask for motion vectors.
            // Without this, every renderer reports "camera motion only" to the per-object motion vector shader.
            if (Main.S.characterMotion) cam.depthTextureMode |= DepthTextureMode.MotionVectors;

            // Work out whether something else already set a custom projection, so it can be put back untouched.
            var orig = cam.projectionMatrix;
            cam.ResetProjectionMatrix();
            customProj = !Approx(orig, cam.projectionMatrix);
            baseProj = orig;

            int rw = PixelSize.Width(cam), rh = PixelSize.Height(cam);
            index = (index + 1) % phases;
            JitterPx = Main.S.noJitter ? Vector2.zero : new Vector2(Halton(index + 1, 2) - 0.5f, Halton(index + 1, 3) - 0.5f);

            var jit = baseProj;
            jit[0, 2] += JitterPx.x * 2f / rw;
            jit[1, 2] += JitterPx.y * 2f / rh;
            cam.projectionMatrix = jit;
            cam.nonJitteredProjectionMatrix = baseProj;

            var view = cam.worldToCameraMatrix;
            VPCurrent = GL.GetGPUProjectionMatrix(baseProj, true) * view;
            VPJittered = GL.GetGPUProjectionMatrix(jit, true) * view;
            InvVPJittered = VPJittered.inverse;
            bool gap = Time.frameCount - lastFrame > 1;
            VPPrevious = (HavePrevious && !gap && !ResetPending) ? lastVP : VPCurrent;
            applied = true;

            // The game's SMAA would run on the low-resolution image before DLSS and soften what DLSS gets to work with.
            aaData = null;
            if (Main.S.disableGameAA && Dlss.Ready)
            {
                aaData = cam.GetComponent<OwlcatAdditionalCameraData>();
                if (aaData != null) { aaOrig = aaData.Antialiasing; aaData.Antialiasing = AntialiasingMode.None; }
            }
        }

        static void OnEnd(ScriptableRenderContext ctx, Camera cam)
        {
            if (!applied || !Scaler.IsScaled(cam)) return;
            Scaler.PostDlss = false;
            if (aaData != null) { aaData.Antialiasing = aaOrig; aaData = null; }
            if (customProj) cam.projectionMatrix = baseProj; else cam.ResetProjectionMatrix();
            lastVP = VPCurrent;
            HavePrevious = true;
            lastFrame = Time.frameCount;
            applied = false;
        }
    }
}

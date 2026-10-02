using UnityEngine;
using UnityEngine.Rendering;

namespace WotRUpscaler
{
    // The mod's own temporal upscaler (TAA), for graphics cards that cannot run DLSS and as a native-resolution anti-aliasing mode. It takes the
    // same inputs as DLSS (jittered colour, depth, exact motion vectors) and produces the same output (the full-resolution colour buffer); the
    // resolve itself is the WotRTaa shader (see its header for where it comes from).
    public static class Taa
    {
        public static string LastFailure = "";
        public static int LastFrame = -100;                  // frame of the last resolve (a gap resets the history)
        public static bool Ready { get { return ready; } }
        public static bool Failed { get { return failed; } }

        const float FeedbackMin = 0.88f, FeedbackMax = 0.97f;   // how much of the history is kept: at least / at most (the shader picks per pixel)

        static bool ready, failed, shaderTried;
        static Material mat;
        static RenderTexture[] hist = new RenderTexture[2];
        static int cur;                                       // the history that holds the previous result
        static int phaseW, phaseR;
        static readonly int ColorId = Shader.PropertyToID("_WotRTaaColor"), DepthId = Shader.PropertyToID("_WotRTaaDepth"), MotionId = Shader.PropertyToID("_WotRTaaMotion"),
            HistoryId = Shader.PropertyToID("_WotRTaaHistory"), SrcId = Shader.PropertyToID("_WotRTaaSrcSize"), DstId = Shader.PropertyToID("_WotRTaaDstSize"),
            JitterId = Shader.PropertyToID("_WotRTaaJitter"), ParamsId = Shader.PropertyToID("_WotRTaaParams");

        public static string Describe()
        {
            return "TAA " + (failed ? "failed [" + LastFailure + "]" : ready ? "ready" : "not started");
        }

        public static void Retry() { if (failed) { failed = false; LastFailure = ""; shaderTried = false; } }

        static void Fail(string why)
        {
            failed = true; ready = false; LastFailure = why;
            Main.Log("TAA failed: " + why);
        }

        // True when the resolve can run this frame.
        public static bool Tick(int rw, int rh, int ow, int oh)
        {
            if (failed) return false;
            if (!Dlss.EnsureLoaded()) { Fail(Dlss.NativeError.Length > 0 ? Dlss.NativeError : "the native plugin did not load"); return false; }   // motion vectors are computed there
            if (!EnsureMaterial()) { Fail("shader missing (" + Bundle.Error + ")"); return false; }
            if (!ready) { ready = true; Main.Log("TAA ready"); }
            if (rw != phaseR || ow != phaseW) { phaseR = rw; phaseW = ow; Jitter.SetPhases(rw, ow); }
            return true;
        }

        static bool EnsureMaterial()
        {
            if (mat != null) return true;
            if (shaderTried) return false;
            shaderTried = true;
            var sh = Bundle.Shader("Assets/Shaders/WotRTaa.shader");
            if (sh == null) return false;
            mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        // Simple scaling: stretches 'src' (render resolution) to 'output' with bilinear filtering. False when the shader is not available.
        public static bool Stretch(CommandBuffer c, RenderTargetIdentifier src, RenderTexture output, int ow, int oh)
        {
            if (!EnsureMaterial()) return false;
            c.SetGlobalTexture(ColorId, src);
            c.SetGlobalVector(DstId, new Vector4(ow, oh, 0f, 0f));
            c.SetRenderTarget(output);
            c.DrawProcedural(Matrix4x4.identity, mat, 2, MeshTopology.Triangles, 3);
            return true;
        }

        static bool Ensure(ref RenderTexture rt, int w, int h)
        {
            if (rt != null && rt.width == w && rt.height == h) return false;
            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            { name = "WotRUpscaler TAA history", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            rt.Create();
            return true;
        }

        // Resolves this frame (render resolution) into 'output' (output resolution) and keeps the result as the next frame's history.
        public static void Resolve(CommandBuffer c, RenderTexture color, RenderTexture depth, RenderTexture motion, RenderTexture output, int rw, int rh, int ow, int oh, bool reset)
        {
            bool fresh = Ensure(ref hist[0], ow, oh);
            fresh |= Ensure(ref hist[1], ow, oh);
            var read = hist[cur];
            var write = hist[cur ^ 1];
            bool valid = !reset && !fresh;

            c.SetGlobalTexture(ColorId, color);
            c.SetGlobalTexture(DepthId, depth);
            c.SetGlobalTexture(MotionId, motion);
            c.SetGlobalTexture(HistoryId, read);
            c.SetGlobalVector(SrcId, new Vector4(rw, rh, 0f, 0f));
            c.SetGlobalVector(DstId, new Vector4(ow, oh, 0f, 0f));
            var j = Jitter.FrameJitter;
            c.SetGlobalVector(JitterId, new Vector4(j.x, j.y, 0f, 0f));
            c.SetGlobalVector(ParamsId, new Vector4(valid ? 1f : 0f, FeedbackMin, FeedbackMax, Main.S.taaSharpness));

            c.SetRenderTarget(write);
            c.DrawProcedural(Matrix4x4.identity, mat, 0, MeshTopology.Triangles, 3);
            c.SetGlobalTexture(HistoryId, write);
            c.SetRenderTarget(output);
            c.DrawProcedural(Matrix4x4.identity, mat, 1, MeshTopology.Triangles, 3);

            cur ^= 1;
            LastFrame = Time.frameCount;
        }
    }
}

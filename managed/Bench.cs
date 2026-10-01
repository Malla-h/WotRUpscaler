using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace WotRDLSS
{
    // GPU timestamps at fixed points of a frame (see the native DoMark). Only issued while the benchmark runs.
    //   0 start of the main camera   1 start of the DLSS stage   2 end of the DLSS stage (motion vectors + evaluate)
    //   3 end of the full-resolution depth/normals/stencil rebuild for the ground markers
    //   4 end of the main camera     7 end of the UI camera
    public static class GpuTimer
    {
        public static bool On;
        static CommandBuffer cb;

        public static void Mark(CommandBuffer c, int slot) { if (On) Dlss.QueueMark(c, slot); }

        public static void Mark(ScriptableRenderContext ctx, int slot)
        {
            if (!On) return;
            if (cb == null) cb = new CommandBuffer { name = "WotRDLSS timing mark" };
            cb.Clear();
            Dlss.QueueMark(cb, slot);
            ctx.ExecuteCommandBuffer(cb);
        }

        static bool Relevant(Camera c) { return c != null && c.cameraType == CameraType.Game && c.targetTexture == null; }

        public static void CameraBegin(ScriptableRenderContext ctx, Camera cam)
        {
            if (On && Relevant(cam) && !Scaler.IsUiCamera(cam)) Mark(ctx, 0);
        }

        public static void CameraEnd(ScriptableRenderContext ctx, Camera cam)
        {
            if (On && Relevant(cam)) Mark(ctx, Scaler.IsUiCamera(cam) ? 7 : 4);
        }
    }

    // Debug tool: steps through native rendering and the main settings combinations on a still camera, and logs frame rate, 1 % low and the
    // GPU time of each stage (the frame rate alone can hit a CPU limit, in which case several modes read the same).
    public class Bench : MonoBehaviour
    {
        struct Step
        {
            public string name;
            public bool enabled, dlss, hdr, charMv, noGameAA;
            public float scale;
            public int preset;
        }

        static Step S(string name, float scale, int preset = 11, bool enabled = true, bool dlss = true, bool hdr = true, bool charMv = true, bool noGameAA = true)
        {
            return new Step { name = name, scale = scale, preset = preset, enabled = enabled, dlss = dlss, hdr = hdr, charMv = charMv, noGameAA = noGameAA };
        }

        static readonly Step[] Steps =
        {
            S("Native (mod off)", 1f, enabled: false, dlss: false),
            S("DLAA (1.0x)", 1f),
            S("Ultra Quality (0.77x)", 0.77f),
            S("Quality (0.667x)", 0.6667f),
            S("Balanced (0.58x)", 0.58f),
            S("Performance (0.5x)", 0.5f),
            S("Ultra Performance (0.333x)", 0.3333f),
            S("Quality, DLSS on finished image (LDR)", 0.6667f, hdr: false),
            S("Performance, DLSS on finished image (LDR)", 0.5f, hdr: false),
            S("Quality, no character motion vectors", 0.6667f, charMv: false),
            S("Quality, game SMAA left on", 0.6667f, noGameAA: false),
            S("Quality, DLSS off (plain upscale)", 0.6667f, dlss: false),
            S("Quality, preset J", 0.6667f, preset: 10),
            S("Quality, preset L", 0.6667f, preset: 12),
            S("Quality, preset M", 0.6667f, preset: 13),
            S("Performance, preset L", 0.5f, preset: 12),
            S("Performance, preset M", 0.5f, preset: 13),
        };

        const float Settle = 3f, Sample = 8f, ReadyTimeout = 15f;
        static Bench instance;
        public static bool Running;
        public static string Status = "";
        static float startAt = -1f;

        void Awake() { instance = this; }

        public static void Arm(float delaySeconds)
        {
            if (Running || instance == null) return;
            startAt = Time.unscaledTime + delaySeconds;
            Status = "benchmark starts in " + delaySeconds.ToString("F0") + " s: close the panel and leave the camera alone";
        }

        void Update()
        {
            if (startAt >= 0f && !Running && Time.unscaledTime >= startAt) { startAt = -1f; StartCoroutine(Run()); }
        }

        void OnGUI()
        {
            if (Running || startAt >= 0f) GUI.Label(new Rect(10, 30, 1200, 24), Status);
        }

        static string Ms(double v) { return v.ToString("F2").PadLeft(6); }

        IEnumerator Run()
        {
            Running = true;
            var s = Main.S;
            bool enabled0 = s.enabled, dlss0 = s.dlss, hdr0 = s.dlssBeforePost, charMv0 = s.characterMotion, noAA0 = s.disableGameAA;
            float scale0 = s.renderScale; int preset0 = s.preset;

            if (!Dlss.EnsureLoaded()) { Main.Log("BENCH: native plugin not available"); Status = ""; Running = false; yield break; }
            Dlss.SetFrameTiming(true);
            GpuTimer.On = true;

            var report = new StringBuilder();
            string head = SystemInfo.graphicsDeviceName + ", " + Screen.width + "x" + Screen.height + ", vSync " + QualitySettings.vSyncCount + ", target fps " + Application.targetFrameRate;
            Main.Log("BENCH start: " + head);
            report.AppendLine("WotR DLSS benchmark: " + head + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            report.AppendLine("Stages (GPU ms per frame): pre = scene before the DLSS stage, dlss = motion vectors + evaluate, marker = full-res depth/normals/stencil rebuild,");
            report.AppendLine("post = rest of the main camera (full-res post-processing in the HDR path, decals, outlines), ui = UI camera, gpu = whole frame, eval = DLSS evaluate alone.");
            report.AppendLine("A mode without DLSS reports everything of the main camera under 'post'.");
            report.AppendLine();

            for (int i = 0; i < Steps.Length; i++)
            {
                var st = Steps[i];
                s.enabled = st.enabled; s.dlss = st.dlss; s.dlssBeforePost = st.hdr; s.characterMotion = st.charMv; s.disableGameAA = st.noGameAA;
                s.renderScale = st.scale; s.preset = st.preset;
                Scaler.Update();

                string label = "Benchmark " + (i + 1) + "/" + Steps.Length + ": " + st.name;
                float t0 = Time.unscaledTime;
                bool ready = false;
                while (Time.unscaledTime - t0 < ReadyTimeout)
                {
                    Status = label + " (switching)";
                    ready = Scaler.Active == st.enabled && (!st.enabled || !st.dlss || Dlss.Ready);
                    if (ready) break;
                    yield return null;
                }
                if (!ready) Main.Log("BENCH: " + st.name + " did not become ready in " + ReadyTimeout + " s, measuring anyway");

                t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < Settle) { Status = label + " (settling)"; yield return null; }

                Dlss.ResetFrameTiming();
                Dlss.ResetEvalTiming();
                var dts = new List<float>(2048);
                t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < Sample)
                {
                    Status = label + " (measuring " + (Sample - (Time.unscaledTime - t0)).ToString("F0") + " s)";
                    dts.Add(Time.unscaledDeltaTime);
                    yield return null;
                }
                yield return null; yield return null; yield return null;   // results arrive a few frames late

                double sum = 0; foreach (var d in dts) sum += d;
                var sorted = dts.ToArray(); Array.Sort(sorted);
                float avgFps = (float)(dts.Count / sum);
                float p50 = sorted[sorted.Length / 2] * 1000f;
                float p99 = sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.99f))] * 1000f;
                double pre, dl, mk, post, ui, tot, ev = 0; ulong n;
                bool g1 = Dlss.FrameTimingMs(1, out pre, out n), g2 = Dlss.FrameTimingMs(2, out dl, out n), g3 = Dlss.FrameTimingMs(3, out mk, out n);
                bool g4 = Dlss.FrameTimingMs(4, out post, out n), g7 = Dlss.FrameTimingMs(7, out ui, out n), g8 = Dlss.FrameTimingMs(8, out tot, out n);
                bool ge = st.enabled && st.dlss && Dlss.EvalMs(out ev, out n);
                ulong use, budget; bool vram = Dlss.VramMB(out use, out budget);
                string line = string.Format("{0,-44} {1,6:F1} fps  med {2,6:F2} ms  1% low {3,6:F1} fps | GPU pre {4} dlss {5} marker {6} post {7} ui {8} = {9} ms (eval {10}) | VRAM {11}",
                    st.name, avgFps, p50, 1000f / p99, Ms(g1 ? pre : 0), Ms(g2 ? dl : 0), Ms(g3 ? mk : 0), Ms(g4 ? post : 0), Ms(g7 ? ui : 0), Ms(g8 ? tot : 0), ge ? ev.ToString("F2") : "n/a", vram ? use + " MB" : "n/a");
                report.AppendLine(line);
                Main.Log("BENCH " + line);
            }

            GpuTimer.On = false;
            Dlss.SetFrameTiming(false);
            s.enabled = enabled0; s.dlss = dlss0; s.dlssBeforePost = hdr0; s.characterMotion = charMv0; s.disableGameAA = noAA0;
            s.renderScale = scale0; s.preset = preset0;
            Scaler.Update();
            try
            {
                string path = Path.Combine(Main.Dir, "bench_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, report.ToString());
                Main.Log("BENCH written: " + path);
            }
            catch (Exception e) { Main.Log("BENCH write failed: " + e.Message); }
            Status = "";
            Running = false;
        }
    }
}

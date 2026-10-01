using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WotRDLSS
{
    // Managed side of WotRDLSS.dll (native NGX bridge). All D3D11 work happens on the render thread via plugin events.
    public static class Dlss
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("WotRDLSSNative", CharSet = CharSet.Unicode)] static extern void WotRDLSS_SetLogPath(string path);
        [DllImport("WotRDLSSNative")] static extern IntPtr WotRDLSS_GetEventFunc();
        [DllImport("WotRDLSSNative")] static extern int WotRDLSS_GetStatus();
        [DllImport("WotRDLSSNative")] static extern uint WotRDLSS_GetLastResult();
        [DllImport("WotRDLSSNative")] static extern int WotRDLSS_GetEvalCount();
        [DllImport("WotRDLSSNative")] static extern int WotRDLSS_StructSizes(int which);
        [DllImport("WotRDLSSNative")] static extern void WotRDLSS_SetDebugStats(int on);
        [DllImport("WotRDLSSNative")] static extern void WotRDLSS_SetFrameTiming(int on);
        [DllImport("WotRDLSSNative")] static extern void WotRDLSS_ResetFrameTiming();
        [DllImport("WotRDLSSNative")] static extern int WotRDLSS_GetFrameTiming(int slot, out ulong us, out ulong n);
        [DllImport("WotRDLSSNative")] static extern void WotRDLSS_ResetEvalTiming();
        [DllImport("WotRDLSSNative")] static extern int WotRDLSS_GetEvalTiming(out ulong us, out ulong n);
        [DllImport("WotRDLSSNative")] static extern int WotRDLSS_GetVramMB(out ulong usage, out ulong budget);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CreateData
        {
            public IntPtr anyTexture;
            public int renderW, renderH, outW, outH, quality, flags, preset;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string dir;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EvalData
        {
            public IntPtr color, depth, motion, output;
            public float jitterX, jitterY, mvScaleX, mvScaleY;
            public int reset, renderW, renderH;
            public float sharpness, preExposure;
            public float frameTimeMs;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MvData
        {
            public IntPtr depth, mv;
            public int w, h;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] invVPj;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] vpCur;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public float[] vpPrev;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct CompData
        {
            public IntPtr obj, mv;
            public int w, h;
            public float scaleX, scaleY;
        }

        // NVSDK_NGX_DLSS_Feature_Flags
        const int FlagHDR = 1 << 0, FlagMVLowRes = 1 << 1, FlagDepthInverted = 1 << 3, FlagAutoExposure = 1 << 6;
        const int PresetK = 11;

        enum St { Off, Creating, Ready, Failed }
        static St state = St.Off;
        static bool loaded;
        static IntPtr eventFn, evalRing, createRing, mvRing, compRing;
        static int slot, cw, ch, ow, oh, cq, cp, chdr, createFrame;
        static readonly int CreateSize = Marshal.SizeOf(typeof(CreateData)), EvalSize = Marshal.SizeOf(typeof(EvalData)), MvSize = Marshal.SizeOf(typeof(MvData)), CompSize = Marshal.SizeOf(typeof(CompData));
        static CommandBuffer cb;
        static bool statsOn;

        public static string LastFailure = "";
        public static int LastEvalFrame = -100;
        public static bool Ready { get { return state == St.Ready; } }
        public static bool Failed { get { return state == St.Failed; } }

        public static string Describe()
        {
            return "DLSS " + state + (loaded ? " status=" + WotRDLSS_GetStatus() + " evals=" + WotRDLSS_GetEvalCount() : "") + (LastFailure.Length > 0 ? " [" + LastFailure + "]" : "");
        }

        public static void Retry() { if (state == St.Failed) { state = St.Off; LastFailure = ""; } }

        static bool Load()
        {
            if (!File.Exists(Path.Combine(Main.Dir, "nvngx_dlss.dll")))
            {
                LastFailure = "nvngx_dlss.dll is missing from the WotRDLSS mod folder";
                Main.Log(LastFailure);
                return false;
            }
            try
            {
                var path = Path.Combine(Main.Dir, "WotRDLSSNative.dll");
                if (LoadLibraryW(path) == IntPtr.Zero) { LastFailure = "LoadLibrary failed err=" + Marshal.GetLastWin32Error(); Main.Log(LastFailure + " " + path); return false; }
                WotRDLSS_SetLogPath(Path.Combine(Main.Dir, "WotRDLSS.native.log"));
                int sc = WotRDLSS_StructSizes(0), se = WotRDLSS_StructSizes(1), sm = WotRDLSS_StructSizes(2), sp = WotRDLSS_StructSizes(3);
                if (sc != CreateSize || se != EvalSize || sm != MvSize || sp != CompSize)
                {
                    LastFailure = "struct size mismatch native " + sc + "/" + se + "/" + sm + " managed " + CreateSize + "/" + EvalSize + "/" + MvSize;
                    Main.Log(LastFailure);
                    return false;
                }
                eventFn = WotRDLSS_GetEventFunc();
                evalRing = Marshal.AllocHGlobal(se * 16);
                createRing = Marshal.AllocHGlobal(sc * 4);
                mvRing = Marshal.AllocHGlobal(sm * 16);
                compRing = Marshal.AllocHGlobal(sp * 16);
                cb = new CommandBuffer { name = "WotRDLSS" };
                loaded = true;
                Main.Log("native plugin loaded");
                return true;
            }
            catch (Exception e) { LastFailure = "load exception " + e.Message; Main.Log("native load exception: " + e); return false; }
        }

        static void Issue(CommandBuffer c, int id, IntPtr data) { c.IssuePluginEventAndData(eventFn, id, data); }

        // Debug: native plugin without DLSS (for GPU probes in any mode).
        public static bool EnsureLoaded()
        {
            if (loaded) return true;
            return Load();
        }

        // Debug: pipeline statistics and D3D11 state of everything drawn between these two events (logged in the native log).
        public static void QueuePassBegin(CommandBuffer c, IntPtr anyTexture) { if (loaded) Issue(c, 7, anyTexture); }
        public static void QueuePassEnd(CommandBuffer c, IntPtr extraDepth) { if (loaded) Issue(c, 8, extraDepth); }

        // Benchmark: GPU timestamps (see GpuTimer / the native DoMark) and the totals they produce.
        public static void QueueMark(CommandBuffer c, int slot) { if (loaded) Issue(c, 9, (IntPtr)slot); }
        public static void SetFrameTiming(bool on) { if (loaded) try { WotRDLSS_SetFrameTiming(on ? 1 : 0); } catch { } }
        public static void ResetFrameTiming() { if (loaded) try { WotRDLSS_ResetFrameTiming(); } catch { } }
        public static void ResetEvalTiming() { if (loaded) try { WotRDLSS_ResetEvalTiming(); } catch { } }

        // Average GPU milliseconds of the section that ends at mark 'slot' (8 = first to last mark of the frame) since the last reset.
        public static bool FrameTimingMs(int slot, out double ms, out ulong samples)
        {
            ms = 0; samples = 0;
            if (!loaded) return false;
            try { ulong us; if (WotRDLSS_GetFrameTiming(slot, out us, out samples) != 1 || samples == 0) return false; ms = us / (double)samples / 1000.0; return true; }
            catch { return false; }
        }

        // Average GPU milliseconds of the DLSS evaluate call alone.
        public static bool EvalMs(out double ms, out ulong samples)
        {
            ms = 0; samples = 0;
            if (!loaded) return false;
            try { ulong us; if (WotRDLSS_GetEvalTiming(out us, out samples) != 1 || samples == 0) return false; ms = us / (double)samples / 1000.0; return true; }
            catch { return false; }
        }

        public static bool VramMB(out ulong use, out ulong budget)
        {
            use = budget = 0;
            if (!loaded) return false;
            try { return WotRDLSS_GetVramMB(out use, out budget) == 1; } catch { return false; }
        }

        public static int QualityFor(float scale)
        {
            if (scale >= 0.999f) return 5;       // DLAA
            if (scale >= 0.62f) return 2;        // quality (also used for the 0.77 step, as NGX rejects the ultra quality value)
            if (scale >= 0.54f) return 1;        // balanced
            if (scale >= 0.4f) return 0;         // performance
            return 3;                            // ultra performance
        }

        // Drives load, (re)creation and readiness. Returns true when the feature can be evaluated this frame.
        public static bool Tick(int rw, int rh, int outW, int outH, RenderTexture anyTexture)
        {
            if (state == St.Failed) return false;
            if (!loaded && !Load()) { state = St.Failed; return false; }
            if (Main.S.debugStats != statsOn) { statsOn = Main.S.debugStats; try { WotRDLSS_SetDebugStats(statsOn ? 1 : 0); } catch { } }
            int q = QualityFor((float)rw / outW), preset = Main.S.preset, hdr = Main.S.dlssBeforePost ? 1 : 0;

            if (state == St.Creating)
            {
                if (Time.frameCount - createFrame < 3) return false;
                int s = WotRDLSS_GetStatus();
                if (s == 1) { state = St.Ready; Main.Log("DLSS ready"); }
                else if (s < 0)
                {
                    state = St.Failed;
                    LastFailure = "status " + s + " result 0x" + WotRDLSS_GetLastResult().ToString("X8");
                    Main.Log("DLSS failed: " + LastFailure);
                }
                return false;
            }
            if (state == St.Ready && cw == rw && ch == rh && ow == outW && oh == outH && cq == q && cp == preset && chdr == hdr) return true;

            var cd = new CreateData
            {
                anyTexture = anyTexture.GetNativeTexturePtr(),
                renderW = rw, renderH = rh, outW = outW, outH = outH,
                quality = q,
                flags = FlagMVLowRes | FlagDepthInverted | (hdr == 1 ? FlagHDR | FlagAutoExposure : 0),
                preset = preset,
                dir = Main.Dir
            };
            var p = createRing + (slot++ & 3) * CreateSize;
            Marshal.StructureToPtr(cd, p, false);
            var c = new CommandBuffer { name = "WotRDLSS create" };
            Issue(c, 1, p);
            Graphics.ExecuteCommandBuffer(c);
            c.Release();
            cw = rw; ch = rh; ow = outW; oh = outH; cq = q; cp = preset; chdr = hdr;
            createFrame = Time.frameCount;
            state = St.Creating;
            Jitter.SetPhases(rw, outW);
            Main.Log("DLSS create issued " + rw + "x" + rh + " -> " + outW + "x" + outH + " quality " + q + " preset " + preset + " hdr " + hdr);
            return false;
        }

        static float[] Flat(Matrix4x4 m)
        {
            var f = new float[16];
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) f[r * 4 + c] = m[r, c];
            return f;
        }

        // Camera motion vectors from the depth copy.
        public static void QueueCameraMv(CommandBuffer c, RenderTexture depth, RenderTexture motion, int rw, int rh)
        {
            var mv = new MvData
            {
                depth = depth.GetNativeTexturePtr(), mv = motion.GetNativeTexturePtr(), w = rw, h = rh,
                invVPj = Flat(Jitter.InvVPJittered), vpCur = Flat(Jitter.VPCurrent), vpPrev = Flat(Jitter.VPPrevious)
            };
            var pm = mvRing + (slot++ & 15) * MvSize;
            Marshal.StructureToPtr(mv, pm, false);
            Issue(c, 4, pm);
        }

        // Overwrites the camera motion vectors with per-object motion where objects were drawn.
        public static void QueueComposite(CommandBuffer c, RenderTexture obj, RenderTexture motion, int rw, int rh)
        {
            var cd = new CompData { obj = obj.GetNativeTexturePtr(), mv = motion.GetNativeTexturePtr(), w = rw, h = rh, scaleX = Main.S.objSignX, scaleY = Main.S.objSignY };
            var p = compRing + (slot++ & 15) * CompSize;
            Marshal.StructureToPtr(cd, p, false);
            Issue(c, 5, p);
        }

        public static void QueueEval(CommandBuffer c, RenderTexture color, RenderTexture depth, RenderTexture motion, RenderTexture output, int rw, int rh, bool reset)
        {
            var ed = new EvalData
            {
                color = color.GetNativeTexturePtr(), depth = depth.GetNativeTexturePtr(),
                motion = motion.GetNativeTexturePtr(), output = output.GetNativeTexturePtr(),
                jitterX = Main.S.jitSx * Jitter.JitterPx.x, jitterY = Main.S.jitSy * Jitter.JitterPx.y,
                mvScaleX = Main.S.mvSignX, mvScaleY = Main.S.mvSignY,
                reset = reset ? 1 : 0, renderW = rw, renderH = rh,
                sharpness = 0f, preExposure = 1f, frameTimeMs = Mathf.Clamp(Time.unscaledDeltaTime * 1000f, 1f, 200f)
            };
            var pe = evalRing + (slot++ & 15) * EvalSize;
            Marshal.StructureToPtr(ed, pe, false);
            Issue(c, 2, pe);
            LastEvalFrame = Time.frameCount;
        }
    }
}

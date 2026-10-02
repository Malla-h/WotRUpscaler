using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Rendering;

namespace WotRUpscaler
{
    // Managed side of WotRUpscaler.dll (native NGX bridge). All D3D11 work happens on the render thread via plugin events.
    public static class Dlss
    {
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("WotRUpscalerNative", CharSet = CharSet.Unicode)] static extern void WotRUpscaler_SetLogPath(string path);
        [DllImport("WotRUpscalerNative")] static extern IntPtr WotRUpscaler_GetEventFunc();
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_GetStatus();
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_GetStatusSlot(int slot);
        [DllImport("WotRUpscalerNative")] static extern uint WotRUpscaler_GetLastResult();
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_GetEvalCount();
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_StructSizes(int which);
        [DllImport("WotRUpscalerNative")] static extern void WotRUpscaler_SetDebugStats(int on);
        [DllImport("WotRUpscalerNative")] static extern void WotRUpscaler_SetFrameTiming(int on);
        [DllImport("WotRUpscalerNative")] static extern void WotRUpscaler_ResetFrameTiming();
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_GetFrameTiming(int slot, out ulong us, out ulong n);
        [DllImport("WotRUpscalerNative")] static extern void WotRUpscaler_ResetEvalTiming();
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_GetEvalTiming(out ulong us, out ulong n);
        [DllImport("WotRUpscalerNative")] static extern int WotRUpscaler_GetVramMB(out ulong usage, out ulong budget);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CreateData
        {
            public IntPtr anyTexture;
            public int renderW, renderH, outW, outH, quality, flags, preset, slot;
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
            public int slot;
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
        // One DLSS feature per camera that is upscaled: slot 0 is the world, slot 1 the character preview (inventory, character creation).
        class Slot { public St state = St.Off; public int cw, ch, ow, oh, cq, cp, chdr, createFrame, lastEval = -100; }
        static readonly Slot[] slots = { new Slot(), new Slot() };
        static St state { get { return slots[0].state; } set { slots[0].state = value; } }   // the world's, which the panel reports
        static bool loaded;
        static IntPtr eventFn, evalRing, createRing, mvRing, compRing, copyRing;
        static int copySlot;
        static int slot;
        static readonly int CreateSize = Marshal.SizeOf(typeof(CreateData)), EvalSize = Marshal.SizeOf(typeof(EvalData)), MvSize = Marshal.SizeOf(typeof(MvData)), CompSize = Marshal.SizeOf(typeof(CompData));
        static CommandBuffer cb;
        static bool statsOn;

        public static string LastFailure = "";
        public static int LastEvalFrame { get { return slots[0].lastEval; } }
        public static int PreviewEvalFrame { get { return slots[1].lastEval; } }
        public static bool PreviewReady { get { return slots[1].state == St.Ready; } }
        public static bool Ready { get { return state == St.Ready; } }
        public static bool Failed { get { return state == St.Failed; } }

        public static string Describe()
        {
            if (Main.S == null || !Main.S.debug) return Status();
            string detail = "DLSS " + state + (loaded ? " status=" + WotRUpscaler_GetStatus() + " evals=" + WotRUpscaler_GetEvalCount() : "") + (LastFailure.Length > 0 ? " [" + LastFailure + "]" : "");
            return detail;
        }

        // What the player should read about DLSS: running, starting, or why it cannot run.
        public static string Status()
        {
            switch (state)
            {
                case St.Ready: return "DLSS is running.";
                case St.Failed:
                    return "DLSS is NOT available: " + (LastFailure.Length > 0 ? LastFailure : "unknown reason") + ". While DLSS is selected the game renders normally. "
                        + "DLSS needs an NVIDIA RTX graphics card and nvngx_dlss.dll in the mod folder. Choose TAA or Simple scaling instead, or fix that and press Retry DLSS in the Mod Manager panel (Ctrl+F10).";
                default: return Main.S != null && Main.S.dlss ? "DLSS is starting (it starts once a scene with the 3D view is shown)." : "DLSS is not in use (" + Upscalers.Name(Main.S) + " is selected).";
            }
        }

        public static void Retry() { if (state == St.Failed) { state = St.Off; LastFailure = ""; nativeFailed = false; } slots[1].state = St.Off; }

        // The native plugin (motion vectors, buffer copies and timing for every upscaler; the DLSS bridge when NVIDIA's runtime is there).
        // Returns an empty string when it is loaded, otherwise the reason.
        static string nativeError = "";
        static bool Load()
        {
            try
            {
                var path = Path.Combine(Main.Dir, "WotRUpscalerNative.dll");
                if (LoadLibraryW(path) == IntPtr.Zero) { nativeError = LastFailure = "WotRUpscalerNative.dll could not be loaded (error " + Marshal.GetLastWin32Error() + ")"; Main.Log(LastFailure + " " + path); return false; }
                WotRUpscaler_SetLogPath(Path.Combine(Main.Dir, "WotRUpscaler.native.log"));
                int sc = WotRUpscaler_StructSizes(0), se = WotRUpscaler_StructSizes(1), sm = WotRUpscaler_StructSizes(2), sp = WotRUpscaler_StructSizes(3);
                if (sc != CreateSize || se != EvalSize || sm != MvSize || sp != CompSize)
                {
                    nativeError = LastFailure = "struct size mismatch native " + sc + "/" + se + "/" + sm + " managed " + CreateSize + "/" + EvalSize + "/" + MvSize;
                    Main.Log(LastFailure);
                    return false;
                }
                eventFn = WotRUpscaler_GetEventFunc();
                evalRing = Marshal.AllocHGlobal(se * 16);
                createRing = Marshal.AllocHGlobal(sc * 4);
                mvRing = Marshal.AllocHGlobal(sm * 16);
                compRing = Marshal.AllocHGlobal(sp * 16);
                copyRing = Marshal.AllocHGlobal(IntPtr.Size * 2 * 16);
                cb = new CommandBuffer { name = "WotRUpscaler" };
                loaded = true;
                Main.Log("native plugin loaded");
                return true;
            }
            catch (Exception e) { nativeError = LastFailure = "load exception " + e.Message; Main.Log("native load exception: " + e); return false; }
        }

        static void Issue(CommandBuffer c, int id, IntPtr data) { c.IssuePluginEventAndData(eventFn, id, data); }

        // The native plugin without DLSS (TAA, GPU probes in any mode). nativeFailed stops repeated attempts.
        static bool nativeFailed;
        public static string NativeError { get { return nativeError; } }
        public static bool EnsureLoaded()
        {
            if (loaded) return true;
            if (nativeFailed) return false;
            if (Load()) return true;
            nativeFailed = true;
            return false;
        }

        // Debug: pipeline statistics and D3D11 state of everything drawn between these two events (logged in the native log).
        public static void QueuePassBegin(CommandBuffer c, IntPtr anyTexture) { if (loaded) Issue(c, 7, anyTexture); }
        public static void QueuePassEnd(CommandBuffer c, IntPtr extraDepth) { if (loaded) Issue(c, 8, extraDepth); }

        // Benchmark: GPU timestamps (see GpuTimer / the native DoMark) and the totals they produce.
        // GPU buffer copy on the render thread (both pointers are native buffer pointers of equally sized buffers).
        public static void QueueCopyBuffer(CommandBuffer c, IntPtr dst, IntPtr src)
        {
            if (!loaded || dst == IntPtr.Zero || src == IntPtr.Zero) return;
            var p = copyRing + (copySlot++ & 15) * IntPtr.Size * 2;
            Marshal.WriteIntPtr(p, 0, dst);
            Marshal.WriteIntPtr(p, IntPtr.Size, src);
            Issue(c, 10, p);
        }

        public static void QueueMark(CommandBuffer c, int slot) { if (loaded) Issue(c, 9, (IntPtr)slot); }
        public static void SetFrameTiming(bool on) { if (loaded) try { WotRUpscaler_SetFrameTiming(on ? 1 : 0); } catch { } }
        public static void ResetFrameTiming() { if (loaded) try { WotRUpscaler_ResetFrameTiming(); } catch { } }
        public static void ResetEvalTiming() { if (loaded) try { WotRUpscaler_ResetEvalTiming(); } catch { } }

        // Average GPU milliseconds of the section that ends at mark 'slot' (8 = first to last mark of the frame) since the last reset.
        public static bool FrameTimingMs(int slot, out double ms, out ulong samples)
        {
            ms = 0; samples = 0;
            if (!loaded) return false;
            try { ulong us; if (WotRUpscaler_GetFrameTiming(slot, out us, out samples) != 1 || samples == 0) return false; ms = us / (double)samples / 1000.0; return true; }
            catch { return false; }
        }

        // Average GPU milliseconds of the DLSS evaluate call alone.
        public static bool EvalMs(out double ms, out ulong samples)
        {
            ms = 0; samples = 0;
            if (!loaded) return false;
            try { ulong us; if (WotRUpscaler_GetEvalTiming(out us, out samples) != 1 || samples == 0) return false; ms = us / (double)samples / 1000.0; return true; }
            catch { return false; }
        }

        public static bool VramMB(out ulong use, out ulong budget)
        {
            use = budget = 0;
            if (!loaded) return false;
            try { return WotRUpscaler_GetVramMB(out use, out budget) == 1; } catch { return false; }
        }

        public static int QualityFor(float scale)
        {
            if (scale >= 0.999f) return 5;       // DLAA
            if (scale >= 0.62f) return 2;        // quality (also used for the 0.77 step, as NGX rejects the ultra quality value)
            if (scale >= 0.54f) return 1;        // balanced
            if (scale >= 0.4f) return 0;         // performance
            return 3;                            // ultra performance
        }

        // Drives load, (re)creation and readiness of one feature (s: 0 the world, 1 the character preview).
        // Returns true when the feature can be evaluated this frame.
        public static bool Tick(int rw, int rh, int outW, int outH, RenderTexture anyTexture, int s = 0)
        {
            var sl = slots[s];
            if (sl.state == St.Failed) return false;
            if (s == 0)
            {
                if (!File.Exists(Path.Combine(Main.Dir, "nvngx_dlss.dll")))
                {
                    LastFailure = "nvngx_dlss.dll is missing from the WotRUpscaler mod folder";
                    Main.Log(LastFailure);
                    sl.state = St.Failed;
                    return false;
                }
                if (!EnsureLoaded()) { LastFailure = nativeError; sl.state = St.Failed; return false; }
            }
            else if (!loaded || slots[0].state != St.Ready) return false;      // the preview follows the world's DLSS
            if (Main.S.debugStats != statsOn) { statsOn = Main.S.debugStats; try { WotRUpscaler_SetDebugStats(statsOn ? 1 : 0); } catch { } }
            int q = QualityFor((float)rw / outW), preset = Main.S.preset, hdr = Main.S.dlssBeforePost ? 1 : 0;
            // The character preview always uses NVIDIA's automatic preset: the heavy presets are chosen for the world, and a large preview should not
            // cost more than it needs to.
            if (s == 1) preset = 0;

            if (sl.state == St.Creating)
            {
                if (Time.frameCount - sl.createFrame < 3) return false;
                int st = WotRUpscaler_GetStatusSlot(s);
                if (st == 1) { sl.state = St.Ready; Main.Log("DLSS" + (s == 1 ? " (character preview)" : "") + " ready"); }
                else if (st < 0)
                {
                    sl.state = St.Failed;
                    if (s == 0) LastFailure = "status " + st + " result 0x" + WotRUpscaler_GetLastResult().ToString("X8");
                    Main.Log("DLSS" + (s == 1 ? " (character preview)" : "") + " failed: status " + st + " result 0x" + WotRUpscaler_GetLastResult().ToString("X8"));
                }
                return false;
            }
            if (sl.state == St.Ready && sl.cw == rw && sl.ch == rh && sl.ow == outW && sl.oh == outH && sl.cq == q && sl.cp == preset && sl.chdr == hdr) return true;

            var cd = new CreateData
            {
                anyTexture = anyTexture.GetNativeTexturePtr(),
                renderW = rw, renderH = rh, outW = outW, outH = outH,
                quality = q,
                flags = FlagMVLowRes | FlagDepthInverted | (hdr == 1 ? FlagHDR | FlagAutoExposure : 0),
                preset = preset,
                slot = s,
                dir = Main.Dir
            };
            var p = createRing + (slot++ & 3) * CreateSize;
            Marshal.StructureToPtr(cd, p, false);
            var c = new CommandBuffer { name = "WotRUpscaler create" };
            Issue(c, 1, p);
            Graphics.ExecuteCommandBuffer(c);
            c.Release();
            sl.cw = rw; sl.ch = rh; sl.ow = outW; sl.oh = outH; sl.cq = q; sl.cp = preset; sl.chdr = hdr;
            sl.createFrame = Time.frameCount;
            sl.state = St.Creating;
            if (s == 0) Jitter.SetPhases(rw, outW);
            Main.Log("DLSS" + (s == 1 ? " (character preview)" : "") + " create issued " + rw + "x" + rh + " -> " + outW + "x" + outH + " quality " + q + " preset " + preset + " hdr " + hdr);
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

        public static void QueueEval(CommandBuffer c, RenderTexture color, RenderTexture depth, RenderTexture motion, RenderTexture output, int rw, int rh, bool reset, int s = 0)
        {
            var ed = new EvalData
            {
                color = color.GetNativeTexturePtr(), depth = depth.GetNativeTexturePtr(),
                motion = motion.GetNativeTexturePtr(), output = output.GetNativeTexturePtr(),
                jitterX = Main.S.jitSx * Jitter.JitterPx.x, jitterY = Main.S.jitSy * Jitter.JitterPx.y,
                mvScaleX = Main.S.mvSignX, mvScaleY = Main.S.mvSignY,
                reset = reset ? 1 : 0, renderW = rw, renderH = rh,
                sharpness = 0f, preExposure = 1f, frameTimeMs = Mathf.Clamp(Time.unscaledDeltaTime * 1000f, 1f, 200f),
                slot = s
            };
            var pe = evalRing + (slot++ & 15) * EvalSize;
            Marshal.StructureToPtr(ed, pe, false);
            Issue(c, 2, pe);
            slots[s].lastEval = Time.frameCount;
        }
    }
}

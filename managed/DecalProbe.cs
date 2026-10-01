using System;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;
using Owlcat.Runtime.Visual.RenderPipeline.Passes;
using Kingmaker.Visual.Decals;

namespace WotRDLSS
{
    // Debug probe for the ground markers (selection circle, click marker). Records the colour buffer around the game's "GUI decals" pass for a
    // couple of frames and counts the pixels the pass changed, together with the state of the decals in the scene.
    public static class DecalProbe
    {
        public static int Frames;                // GUI decal pass executions still to record
        static float armAt = -1f;
        public static bool Recording
        {
            get
            {
                if (armAt >= 0f && Time.unscaledTime >= armAt) { armAt = -1f; Frames = 2; Main.Log("decal probe: recording"); }
                return Frames > 0;
            }
        }
        public static string Status = "";
        static RenderTexture before, after;
        static FieldInfo drawGui, colorAttachment;
        static MethodInfo identifier;

        static string dumpFolder;

        public static void Arm(float delaySeconds)
        {
            armAt = Time.unscaledTime + delaySeconds;
            dumpFolder = Path.Combine(Main.Dir, "decalprobe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Status = "armed, records in " + delaySeconds.ToString("F0") + " s";
            int total = 0, gui = 0, guiActive = 0, guiVisible = 0, other = 0, otherVisible = 0;
            foreach (var d in ScreenSpaceDecal.All)
            {
                total++;
                var mr = d.GetComponent<MeshRenderer>();
                bool vis = mr != null && mr.enabled && mr.isVisible;
                if (d.Type == ScreenSpaceDecal.DecalType.GUI) { gui++; if (d.isActiveAndEnabled) guiActive++; if (vis) guiVisible++; }
                else { other++; if (vis) otherVisible++; }
            }
            Main.Log("decal probe armed: ScreenSpaceDecals " + total + " (GUI " + gui + ", active " + guiActive + ", visible " + guiVisible + "; other " + other + ", visible " + otherVisible + ")");
            // Every renderer in the scene whose material has a "DecalGUI" pass: those are what the pipeline's GUI decal pass draws.
            int guiPass = 0, guiPassVisible = 0, shown = 0;
            foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                var m = r.sharedMaterial;
                if (m == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (m.FindPass("DecalGUI") < 0) continue;
                guiPass++;
                if (r.isVisible) guiPassVisible++;
                if (shown++ < 10)
                {
                    string path = r.name; var t = r.transform.parent; for (int i = 0; i < 4 && t != null; i++, t = t.parent) path = t.name + "/" + path;
                    Main.Log("  DecalGUI renderer " + path + " layer " + r.gameObject.layer + " visible " + r.isVisible + " queue " + m.renderQueue + " shader " + m.shader.name + " bounds " + r.bounds.center + " " + r.bounds.size);
                }
            }
            Main.Log("renderers with a DecalGUI pass: " + guiPass + " (visible " + guiPassVisible + ")");
            foreach (var d in ScreenSpaceDecal.All)
            {
                foreach (var c in d.GetComponents<Component>()) Main.Log("  decal object component: " + (c != null ? c.GetType().FullName : "missing"));
                foreach (var r in d.GetComponentsInChildren<Renderer>(true))
                    Main.Log("  decal child renderer " + r.name + " " + r.GetType().Name + " enabled " + r.enabled + " visible " + r.isVisible + " layer " + r.gameObject.layer + " shader " + (r.sharedMaterial != null ? r.sharedMaterial.shader.name : "-"));
            }
            foreach (var d in ScreenSpaceDecal.All)
            {
                if (d.Type != ScreenSpaceDecal.DecalType.GUI || !d.isActiveAndEnabled) continue;
                var mr = d.GetComponent<MeshRenderer>();
                Main.Log("  GUI decal " + d.name + " layer " + d.gameObject.layer + " pos " + d.transform.position + " scale " + d.transform.lossyScale
                    + " renderer " + (mr != null ? "enabled=" + mr.enabled + " visible=" + mr.isVisible + " queue=" + (mr.sharedMaterial != null ? mr.sharedMaterial.renderQueue.ToString() : "-") + " shader=" + (mr.sharedMaterial != null ? mr.sharedMaterial.shader.name : "-") : "none"));
            }
        }

        static void Ensure(int w, int h)
        {
            if (before != null && before.width == w && before.height == h) return;
            if (before != null) { before.Release(); after.Release(); }
            before = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear); before.Create();
            after = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear); after.Create();
        }

        static RenderTargetIdentifier Color(object pass)
        {
            if (drawGui == null)
            {
                var t = typeof(DrawDecalsPass);
                drawGui = AccessTools.Field(t, "m_DrawGUIDecals");
                colorAttachment = AccessTools.Field(t, "m_ColorAttachment");
                identifier = AccessTools.Method(typeof(RenderTargetHandle), "Identifier");
            }
            var handle = colorAttachment.GetValue(pass);
            return (RenderTargetIdentifier)identifier.Invoke(handle, null);
        }

        public static bool IsGuiPass(object pass)
        {
            if (drawGui == null) Color(pass);
            return (bool)drawGui.GetValue(pass);
        }

        class Pair
        {
            public byte[] b, a, nrm; public float[] depthDsv, depthSampled;
            public int pending = 5; public string state; public int w, h, frame; public string folder;
        }
        static Pair current;
        static RenderTexture normalsDump;

        public static void Snapshot(ScriptableRenderContext ctx, object pass, bool isBefore, ref RenderingData rd)
        {
            var cam = rd.CameraData.Camera;
            int w = cam.pixelWidth, h = cam.pixelHeight;
            Ensure(w, h);
            var cb = new CommandBuffer { name = "WotRDLSS decal probe" };
            if (!isBefore) Dlss.QueuePassEnd(cb, Scaler.PostDlss && Upscale.LowResDepth != null ? Upscale.LowResDepth.GetNativeTexturePtr() : System.IntPtr.Zero);           // close the GPU statistics of the decal pass before our own blit
            cb.Blit(Color(pass), isBefore ? before : after);
            if (isBefore)
            {
                // What depth does the decal pass see? Read both depth targets it can use back to the CPU.
                if (depthA == null || depthA.width != w || depthA.height != h)
                {
                    if (depthA != null) { depthA.Release(); depthB.Release(); }
                    depthA = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear); depthA.Create();
                    depthB = new RenderTexture(w, h, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear); depthB.Create();
                }
                string st = "camera " + cam.name + " " + w + "x" + h;
                Upscale.ProbeDepth(cb, depthA);
                cb.CopyTexture(new RenderTargetIdentifier("_CameraDepthCopyRT"), depthB);
                cb.RequestAsyncReadback(depthA, req => DepthStats("_CameraDepthRT (depth target) " + st, req));
                cb.RequestAsyncReadback(depthB, req => DepthStats("_CameraDepthCopyRT (sampled) " + st, req));

                // Everything the decal pass reads, for offline analysis (written as crops around the changed pixels, see Done).
                if (normalsDump == null || normalsDump.width != w || normalsDump.height != h)
                {
                    if (normalsDump != null) normalsDump.Release();
                    normalsDump = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear); normalsDump.Create();
                }
                cb.CopyTexture(new RenderTargetIdentifier("_CameraNormalsRT"), normalsDump);
                var pr = new Pair { w = w, h = h, frame = Time.frameCount, folder = dumpFolder };
                pr.state = "camera " + cam.name + " target " + rd.CameraData.CameraTargetDescriptor.width + "x" + rd.CameraData.CameraTargetDescriptor.height + " pixel " + w + "x" + h + " PostDlss " + Scaler.PostDlss + " Active " + Scaler.Active + " first " + rd.CameraData.IsFirstInChain + " last " + rd.CameraData.IsLastInChain;
                current = pr;
                cb.RequestAsyncReadback(depthA, req => { if (!req.hasError) pr.depthDsv = req.GetData<float>().ToArray(); Done(pr); });
                cb.RequestAsyncReadback(depthB, req => { if (!req.hasError) pr.depthSampled = req.GetData<float>().ToArray(); Done(pr); });
                cb.RequestAsyncReadback(normalsDump, req => { if (!req.hasError) pr.nrm = req.GetData<byte>().ToArray(); Done(pr); });
            }
            if (!isBefore)
            {
                var pair = current;
                if (pair == null) Main.Log("decal probe: no before snapshot");
                else
                {
                    cb.RequestAsyncReadback(before, req => { if (!req.hasError) pair.b = req.GetData<byte>().ToArray(); Done(pair); });
                    cb.RequestAsyncReadback(after, req => { if (!req.hasError) pair.a = req.GetData<byte>().ToArray(); Done(pair); });
                }
            }
            if (isBefore && Dlss.EnsureLoaded())
            {
                Main.Log("decal probe: GPU statistics and state of the decal pass go to WotRDLSS.native.log (" + cam.name + ", PostDlss " + Scaler.PostDlss + ", Active " + Scaler.Active + ")");
                Dlss.QueuePassBegin(cb, before.GetNativeTexturePtr());   // everything the decal pass draws from here on is counted
            }
            ctx.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        static RenderTexture depthA, depthB;

        static void DepthStats(string what, AsyncGPUReadbackRequest req)
        {
            if (req.hasError) { Main.Log("decal probe depth: " + what + " readback failed"); return; }
            var d = req.GetData<float>();
            int n = d.Length, zero = 0, bad = 0;
            double sum = 0; float mn = float.MaxValue, mx = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float v = d[i];
                if (float.IsNaN(v) || float.IsInfinity(v)) { bad++; continue; }
                if (v == 0f) zero++;
                sum += v; if (v < mn) mn = v; if (v > mx) mx = v;
            }
            Main.Log("decal probe depth: " + what + ": min " + mn + " max " + mx + " mean " + (sum / Math.Max(1, n - bad)).ToString("F4") + " zero " + (100.0 * zero / n).ToString("F1") + "% bad " + bad);
        }

        static float Half(ushort h)
        {
            int e = (h >> 10) & 31, m = h & 1023;
            float v = e == 0 ? m * (float)Math.Pow(2, -24) : e == 31 ? 65504f : (1f + m / 1024f) * (float)Math.Pow(2, e - 15);
            return (h & 0x8000) != 0 ? -v : v;
        }

        static void Done(Pair p)
        {
            if (--p.pending > 0) return;
            if (p.a == null || p.b == null || p.a.Length != p.b.Length) { Main.Log("decal probe frame: " + p.state + " -> readback failed"); return; }
            int n = p.a.Length / 8, changed = 0;
            int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
            int w = p.w;
            for (int i = 0; i < n; i++)
            {
                bool diff = false;
                for (int c = 0; c < 3 && !diff; c++)
                {
                    int o = i * 8 + c * 2;
                    float x = Half((ushort)(p.a[o] | (p.a[o + 1] << 8))), y = Half((ushort)(p.b[o] | (p.b[o + 1] << 8)));
                    if (Math.Abs(x - y) > 0.004f) diff = true;
                }
                if (!diff) continue;
                changed++;
                int px = w > 0 ? i % w : 0, py = w > 0 ? i / w : 0;
                if (px < minX) minX = px; if (px > maxX) maxX = px; if (py < minY) minY = py; if (py > maxY) maxY = py;
            }
            Main.Log("decal probe frame: " + p.state + " -> GUI decal pass changed " + changed + " pixels" + (changed > 0 ? " in x " + minX + ".." + maxX + " y " + minY + ".." + maxY : ""));
            if (changed > 0 && p.depthDsv != null && p.depthSampled != null && p.nrm != null)
            {
                try { WriteCrops(p, minX, minY, maxX, maxY); } catch (Exception e) { Main.Log("decal probe dump failed: " + e.Message); }
            }
        }

        static string R(float v) { return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture); }

        // Crops of everything the decal pass saw, around the pixels it changed (+ margin). Rows are in texture memory order (bottom-up).
        static void WriteCrops(Pair p, int x0, int y0, int x1, int y1)
        {
            const int margin = 48;
            x0 = Math.Max(0, x0 - margin); y0 = Math.Max(0, y0 - margin); x1 = Math.Min(p.w - 1, x1 + margin); y1 = Math.Min(p.h - 1, y1 + margin);
            int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
            string dir = p.folder + "_f" + p.frame;
            Directory.CreateDirectory(dir);
            var meta = new System.Text.StringBuilder("{\n");
            meta.Append("\"frame\":").Append(p.frame).Append(",\n\"full\":[").Append(p.w).Append(",").Append(p.h).Append("],\n\"crop\":[").Append(x0).Append(",").Append(y0).Append(",").Append(cw).Append(",").Append(ch).Append("],\n");
            meta.Append("\"state\":\"").Append(p.state).Append("\",\n");
            meta.Append("\"jitterPx\":[").Append(R(Jitter.JitterPx.x)).Append(",").Append(R(Jitter.JitterPx.y)).Append("],\n");
            meta.Append("\"renderScale\":").Append(R(Main.S.renderScale)).Append(",\n");
            meta.Append("\"beforePost\":").Append(Main.S.dlssBeforePost ? "true" : "false").Append("\n}\n");
            File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToString());
            File.WriteAllBytes(Path.Combine(dir, "before_" + cw + "x" + ch + "_4h.raw"), Crop(p.b, p.w, 8, x0, y0, cw, ch));
            File.WriteAllBytes(Path.Combine(dir, "after_" + cw + "x" + ch + "_4h.raw"), Crop(p.a, p.w, 8, x0, y0, cw, ch));
            File.WriteAllBytes(Path.Combine(dir, "normals_" + cw + "x" + ch + "_4b.raw"), Crop(p.nrm, p.w, 4, x0, y0, cw, ch));
            File.WriteAllBytes(Path.Combine(dir, "depth_dsv_" + cw + "x" + ch + "_1f.raw"), CropF(p.depthDsv, p.w, x0, y0, cw, ch));
            File.WriteAllBytes(Path.Combine(dir, "depth_sampled_" + cw + "x" + ch + "_1f.raw"), CropF(p.depthSampled, p.w, x0, y0, cw, ch));
            Main.Log("decal probe dump: " + dir + " crop " + cw + "x" + ch + " at " + x0 + "," + y0);
        }

        static byte[] Crop(byte[] src, int w, int bpp, int x0, int y0, int cw, int ch)
        {
            var o = new byte[cw * ch * bpp];
            for (int y = 0; y < ch; y++) Buffer.BlockCopy(src, ((y0 + y) * w + x0) * bpp, o, y * cw * bpp, cw * bpp);
            return o;
        }

        static byte[] CropF(float[] src, int w, int x0, int y0, int cw, int ch)
        {
            var o = new byte[cw * ch * 4];
            for (int y = 0; y < ch; y++) Buffer.BlockCopy(src, ((y0 + y) * w + x0) * 4, o, y * cw * 4, cw * 4);
            return o;
        }
    }

    [HarmonyPatch(typeof(DrawDecalsPass), "Execute")]
    static class GuiDecalPassPatch
    {
        static void Prefix(DrawDecalsPass __instance, ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!DecalProbe.Recording || !DecalProbe.IsGuiPass(__instance)) return;
            DecalProbe.Snapshot(context, __instance, true, ref renderingData);
        }

        static void Postfix(DrawDecalsPass __instance, ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (!DecalProbe.Recording || !DecalProbe.IsGuiPass(__instance)) return;
            DecalProbe.Frames--;
            DecalProbe.Snapshot(context, __instance, false, ref renderingData);
        }
    }
}

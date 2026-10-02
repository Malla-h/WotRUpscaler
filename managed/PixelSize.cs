using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRUpscaler
{
    // The pipeline treats Camera.pixelRect/pixelWidth/pixelHeight as the size of its render targets. With a scaled camera the targets are
    // smaller than the camera, so every such read inside the pipeline assembly is redirected to the size the targets really have.
    // CameraChain/CameraHistoryInfo keep the real values (they group cameras by viewport) and so does the descriptor creation.
    public static class PixelSize
    {
        public static float Snapped()
        {
            var a = OwlcatRenderPipeline.Asset;
            float s = a != null ? a.RenderScale : 1f;
            return Mathf.Abs(1f - s) < 0.05f ? 1f : s;
        }

        public static int Width(Camera c) { return Scaler.IsScaledTarget(c) && !Scaler.PostDlss ? (int)(c.pixelWidth * Snapped()) : c.pixelWidth; }
        public static int Height(Camera c) { return Scaler.IsScaledTarget(c) && !Scaler.PostDlss ? (int)(c.pixelHeight * Snapped()) : c.pixelHeight; }
        public static Rect Rect(Camera c)
        {
            var r = c.pixelRect;
            if (!Scaler.IsScaledTarget(c) || Scaler.PostDlss) return r;
            float s = Snapped();
            return new Rect(r.x * s, r.y * s, (int)(r.width * s), (int)(r.height * s));
        }

        static readonly MethodInfo GetRect = AccessTools.PropertyGetter(typeof(Camera), "pixelRect");
        static readonly MethodInfo GetW = AccessTools.PropertyGetter(typeof(Camera), "pixelWidth");
        static readonly MethodInfo GetH = AccessTools.PropertyGetter(typeof(Camera), "pixelHeight");
        static readonly MethodInfo NewRect = AccessTools.Method(typeof(PixelSize), "Rect");
        static readonly MethodInfo NewW = AccessTools.Method(typeof(PixelSize), "Width");
        static readonly MethodInfo NewH = AccessTools.Method(typeof(PixelSize), "Height");

        static bool Skip(MethodBase m)
        {
            var t = m.DeclaringType;
            while (t.DeclaringType != null) t = t.DeclaringType;
            return t.Name == "CameraChain" || t.Name == "CameraHistoryInfo" || m.Name == "CreateRenderTextureDescriptor";
        }

        public static void Apply(Harmony harmony)
        {
            int n = 0;
            var tr = new HarmonyMethod(typeof(PixelSize), nameof(Transpile));
            foreach (var t in typeof(OwlcatRenderPipeline).Assembly.GetTypes())
            {
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.GetMethodBody() == null || Skip(m)) continue;
                    try
                    {
                        bool hit = PatchProcessor.GetOriginalInstructions(m).Any(i => i.operand is MethodInfo mi && (mi == GetRect || mi == GetW || mi == GetH));
                        if (!hit) continue;
                        harmony.Patch(m, transpiler: tr);
                        n++;
                        Main.Log("pixel-size patch: " + t.Name + "." + m.Name);
                    }
                    catch (Exception e) { Main.Log("pixel-size patch failed for " + t.Name + "." + m.Name + ": " + e.Message); }
                }
            }
            Main.Log("pixel-size patches: " + n);
        }

        static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> code)
        {
            foreach (var ins in code)
            {
                if (ins.operand is MethodInfo mi)
                {
                    if (mi == GetRect) { yield return new CodeInstruction(OpCodes.Call, NewRect) { labels = ins.labels, blocks = ins.blocks }; continue; }
                    if (mi == GetW) { yield return new CodeInstruction(OpCodes.Call, NewW) { labels = ins.labels, blocks = ins.blocks }; continue; }
                    if (mi == GetH) { yield return new CodeInstruction(OpCodes.Call, NewH) { labels = ins.labels, blocks = ins.blocks }; continue; }
                }
                yield return ins;
            }
        }
    }
}

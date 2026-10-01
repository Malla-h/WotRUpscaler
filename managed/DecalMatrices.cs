using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline.Passes;

namespace WotRUpscaler
{
    // The decal pass (selection circle, click marker) calls ScriptableRenderContext.SetupCameraProperties, which gives it the jittered
    // camera. Once the camera works at output resolution, follow that call with the un-jittered matrices so the decals stay still.
    [HarmonyPatch(typeof(DrawDecalsPass), "Execute")]
    static class DecalMatricesPatch
    {
        static readonly MethodInfo Setup = AccessTools.Method(typeof(ScriptableRenderContext), "SetupCameraProperties", new[] { typeof(Camera), typeof(bool) });
        static readonly MethodInfo Replacement = AccessTools.Method(typeof(DecalMatricesPatch), nameof(SetupUnjittered));
        static CommandBuffer cb;

        static void SetupUnjittered(ref ScriptableRenderContext ctx, Camera cam, bool stereo)
        {
            ctx.SetupCameraProperties(cam, stereo);
            if (!Scaler.PostDlss || !Jitter.Applied || !Scaler.IsScaled(cam)) return;
            if (cb == null) cb = new CommandBuffer { name = "WotRUpscaler un-jittered decals" };
            cb.Clear();
            cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, Jitter.BaseProjection);
            ctx.ExecuteCommandBuffer(cb);
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            int n = 0;
            foreach (var ins in code)
            {
                if (ins.operand is MethodInfo mi && mi == Setup) { n++; yield return new CodeInstruction(OpCodes.Call, Replacement) { labels = ins.labels, blocks = ins.blocks }; continue; }
                yield return ins;
            }
            if (n == 0) Main.Log("decal matrices patch: SetupCameraProperties call not found");
        }
    }
}

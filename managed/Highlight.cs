using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;
using Owlcat.Runtime.Visual.RenderPipeline.RendererFeatures.Highlighting.Passes;

namespace WotRUpscaler
{
    // The game's outline pass (hover, selection) runs at the very end of the main camera's chain. Without help it would draw the outlines
    // at the low render resolution with the jittered projection and stretch them over the upscaled image: thick, blocky and shimmering.
    // Once the camera works at output resolution, draw them at output resolution with the un-jittered projection instead.
    [HarmonyPatch(typeof(HighlighterPass), "Execute")]
    static class HighlighterPatch
    {
        static void Prefix(ScriptableRenderContext context, ref RenderingData renderingData, out int __state)
        {
            __state = 0;
            var cam = renderingData.CameraData.Camera;
            if (!Scaler.PostDlss || !Scaler.IsScaled(cam)) return;

            var d = renderingData.CameraData.CameraTargetDescriptor;
            __state = d.width;
            d.width = cam.pixelWidth; d.height = cam.pixelHeight;
            renderingData.CameraData.CameraTargetDescriptor = d;

            var cb = new CommandBuffer { name = "WotRUpscaler outline projection" };
            // (Without DLSS there is no jitter: the camera's own projection is already the un-jittered one.)
            cb.SetViewProjectionMatrices(cam.worldToCameraMatrix, Jitter.Applied ? Jitter.BaseProjection : cam.projectionMatrix);
            context.ExecuteCommandBuffer(cb);
            cb.Release();
        }

        static void Postfix(ref RenderingData renderingData, int __state)
        {
            if (__state == 0) return;
            var cam = renderingData.CameraData.Camera;
            var d = renderingData.CameraData.CameraTargetDescriptor;
            float s = PixelSize.Snapped();
            d.width = (int)(cam.pixelWidth * s); d.height = (int)(cam.pixelHeight * s);
            renderingData.CameraData.CameraTargetDescriptor = d;
        }
    }
}

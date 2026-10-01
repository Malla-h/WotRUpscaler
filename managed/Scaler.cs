using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRDLSS
{
    // Decides which cameras render at reduced resolution and keeps the pipeline asset's RenderScale in step with the setting.
    public static class Scaler
    {
        public static bool Active;
        public static float Scale = 1f;
        public static bool PostDlss;        // true from the moment DLSS has produced the full-resolution colour buffer until the camera is done
        public static int MainFrame = -1;   // frame in which a scaled camera last finished post-processing

        public static bool IsUiCamera(Camera c) { return c.name.StartsWith("UICamera"); }

        public static bool IsScaled(Camera c)
        {
            return Active && c != null && c.cameraType == CameraType.Game && c.targetTexture == null && !IsUiCamera(c);
        }

        public static string Describe() { return Active ? "active" : "off"; }

        public static void Update()
        {
            var asset = OwlcatRenderPipeline.Asset;
            if (asset == null) return;
            Active = Main.S.enabled && (Main.S.renderScale < 0.999f || Main.S.dlss);   // scale 1 with DLSS on is DLAA
            Scale = Active ? Mathf.Clamp(Main.S.renderScale, 0.33f, 1f) : 1f;
            // The pipeline truncates width * scale (3840 * 0.3333 = 1279); a tiny nudge makes exact fractions land on whole pixels.
            float s = Scale < 0.999f ? Scale + 0.0002f : Scale;
            if (Mathf.Abs(asset.RenderScale - s) > 1e-5f) asset.RenderScale = s;
        }
    }

    // Only scaled cameras get the reduced descriptor; everything else (UI camera, portraits) stays at full resolution.
    [HarmonyPatch(typeof(OwlcatRenderPipeline), "CreateRenderTextureDescriptor")]
    static class DescriptorPatch
    {
        static void Prefix(Camera camera, ref float renderScale)
        {
            if (renderScale < 1f && !Scaler.IsScaled(camera)) renderScale = 1f;
        }
    }
}

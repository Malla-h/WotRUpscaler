using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRUpscaler
{
    // Decides which cameras render at reduced resolution and keeps the pipeline asset's RenderScale in step with the setting.
    public static class Scaler
    {
        public static bool Active;
        public static float Scale = 1f;
        public static bool PostDlss;        // true from the moment DLSS has produced the full-resolution colour buffer until the camera is done
        public static int MainFrame = -1;
        static bool loggedActive, loggedUnavailable;
        static float loggedScale;   // frame in which a scaled camera last finished post-processing

        public static bool IsUiCamera(Camera c) { return c.name.StartsWith("UICamera"); }

        // The character preview (inventory, character sheet, character creation): a camera that draws into a texture of its own. It is not scaled, but
        // it gets the selected temporal upscaler at its own resolution (DLAA with DLSS), see Preview.
        public static bool PreviewUpscaled;     // the preview camera that is rendering right now is being upscaled
        public static bool IsPreview(Camera c) { return c != null && c.cameraType == CameraType.Game && c.targetTexture != null && c.name == "DollroomCamera"; }
        // The cameras whose render targets are smaller than the camera itself (the world camera, and the preview when it is upscaled).
        public static bool IsScaledTarget(Camera c) { return IsScaled(c) || (Main.S.previewSize == 1 && PreviewActive(c)); }
        public static bool PreviewActive(Camera c) { return Active && IsPreview(c) && Upscalers.Temporal(Main.S) && Upscalers.TemporalReady(Main.S); }

        public static bool IsScaled(Camera c)
        {
            return Active && c != null && c.cameraType == CameraType.Game && c.targetTexture == null && !IsUiCamera(c);
        }

        public static string Describe() { return Active ? "active" : "off"; }

        public static void Update()
        {
            var asset = OwlcatRenderPipeline.Asset;
            if (asset == null) return;
            // If the chosen upscaler cannot run (DLSS: no RTX card, runtime file missing), the game keeps its normal renderer instead of showing a
            // blurry stretched image; the panel says why and Retry DLSS tries again.
            bool dlssUnavailable = Upscalers.Unavailable(Main.S);
            Active = Main.S.enabled && !dlssUnavailable && (Main.S.renderScale < 0.999f || Upscalers.Temporal(Main.S));   // scale 1 with DLSS on is DLAA
            Scale = Active ? Mathf.Clamp(Main.S.renderScale, 0.33f, 1f) : 1f;
            if (Active != loggedActive || dlssUnavailable != loggedUnavailable || (Active && Mathf.Abs(Scale - loggedScale) > 0.001f))
            {
                loggedActive = Active; loggedUnavailable = dlssUnavailable; loggedScale = Scale;
                Main.Log("scaling " + (Active ? "on at " + Scale.ToString("F3") + "x" : "off") + ", upscaler " + Upscalers.Name(Main.S)
                    + (dlssUnavailable ? " (cannot run: " + (Main.S.dlss ? Dlss.LastFailure : Taa.LastFailure) + ")" : ""));
            }
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
            if (renderScale < 1f && !Scaler.IsScaledTarget(camera)) renderScale = 1f;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace WotRUpscaler
{
    public class Settings : UnityModManager.ModSettings
    {
        public bool enabled = true;
        public float renderScale = 0.6667f;   // multiplier of the output resolution
        public bool dlss = true;              // NVIDIA DLSS is the upscaler (false and taa false: plain upscale, the test mode)
        public bool taa = false;              // the mod's own TAA is the upscaler (used when dlss is off)
        public float taaSharpness = 0.15f;    // how much the TAA result is sharpened
        public int preset = 11;
        public int customPreset = 0;          // a preset number outside the named list, kept for the in-game menu's "Other" choice               // NGX render preset: 10=J 11=K 12=L 13=M, 0=default
        public bool dlssBeforePost = true;    // run DLSS on the HDR scene colour before post-processing (false: on the finished image)
        public bool noJitter = false;
        public float jitSx = -1f, jitSy = -1f;   // jitter sign towards DLSS (verified: displacement of the image = minus the projection jitter)
        public float mvSignX = 1f, mvSignY = 1f; // motion vector scale: the texture holds previous minus current position, which is what DLSS expects
        public bool mipAuto = true;           // follow the recommended bias for the current render scale
        public float mipStrength = 1f;        // fraction of the recommended texture LOD bias (log2(scale) - 1)
        public bool disableGameAA = true;     // switch the game's SMAA and FXAA off while DLSS is active
        public bool characterMotion = true;   // per-object motion vectors for characters (needs the wotrupscaler shader bundle)
        public float objSignX = 1f, objSignY = 1f;
        public float markerEdgeGapPx = 0.5f;            // the ground markers stop this many output pixels short of characters (covers their anti-aliased edge)
        public float holdTolerance = 0.004f;           // a still ground pixel keeps its depth/normals while the new depth is within this fraction of it
        public bool debugFullMarkerBuffers = false;   // debug: rebuild the marker buffers over the whole screen instead of around the decals
        public bool debugCharMv = false;      // log the raw per-object parameters instead of using the motion
        public bool debugStats = false;       // log character motion statistics (reads back from the GPU: small hitch every few seconds)
        public bool showAdvanced = false;
        public bool debug = false;            // set "debug": true in Settings.xml to show the developer tools in the panel
        public override void Save(UnityModManager.ModEntry e) { Save(this, e); }
    }

    public static class Main
    {
        public static UnityModManager.ModEntry Mod;
        public static Settings S;
        public static string Dir;
        static string logPath;
        static Harmony harmony;

        public static bool Load(UnityModManager.ModEntry entry)
        {
            Mod = entry; Dir = entry.Path;
            logPath = Path.Combine(Dir, "WotRUpscaler.log");
            try { File.WriteAllText(logPath, "WotRUpscaler " + DateTime.Now + "\n"); } catch { }
            S = UnityModManager.ModSettings.Load<Settings>(entry);
            if (!S.debug)
            {   // developer-only switches never stay active from an old settings file
                S.noJitter = false; S.jitSx = -1f; S.jitSy = -1f; S.mvSignX = 1f; S.mvSignY = 1f; S.objSignX = 1f; S.objSignY = 1f;
                S.debugFullMarkerBuffers = S.debugCharMv = S.debugStats = false;
                S.characterMotion = true;
                S.holdTolerance = 0.004f;
                S.taaSharpness = 0.15f;
            }
            entry.OnGUI = OnGUI;
            entry.OnSaveGUI = e => S.Save(e);
            entry.OnUpdate = (e, dt) => { Scaler.Update(); ModMenuBridge.Tick(); };
            entry.OnToggle = (e, on) => { S.enabled = on; Scaler.Update(); return true; };
            harmony = new Harmony("wotr.upscaler");
            try { harmony.PatchAll(Assembly.GetExecutingAssembly()); PixelSize.Apply(harmony); Jitter.Install(); var go = new GameObject("WotRUpscaler"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<MipBias>(); go.AddComponent<Bench>(); Log("patched"); }
            catch (Exception ex) { Log("patch failed: " + ex); return false; }
            return true;
        }

        public static void Log(string s)
        {
            Mod.Logger.Log(s);
            try { File.AppendAllText(logPath, s + "\n"); } catch { }
        }

        static string customPresetText;

        static void OnGUI(UnityModManager.ModEntry e)
        {
            if (Dlss.Failed)
            {
                var old = GUI.contentColor;
                GUI.contentColor = new Color(1f, 0.45f, 0.3f);
                GUILayout.Label(Dlss.Status());
                GUI.contentColor = old;
                if (GUILayout.Button("Retry DLSS", GUILayout.Width(120))) Dlss.Retry();
            }
            if (Taa.Failed)
            {
                var old = GUI.contentColor;
                GUI.contentColor = new Color(1f, 0.45f, 0.3f);
                GUILayout.Label("TAA cannot run: " + Taa.LastFailure + ". The game renders normally. Choose another upscaler.");
                GUI.contentColor = old;
                if (GUILayout.Button("Retry TAA", GUILayout.Width(120))) Taa.Retry();
            }
            S.enabled = GUILayout.Toggle(S.enabled, "Scale 3D rendering");
            int ow = Screen.width, oh = Screen.height;
            int rw = Mathf.Max(1, (int)(ow * S.renderScale)), rh = Mathf.Max(1, (int)(oh * S.renderScale));
            GUILayout.Label("Render scale: " + S.renderScale.ToString("F2") + "x    Internal resolution: " + rw + " x " + rh + "    Output: " + ow + " x " + oh);
            S.renderScale = GUILayout.HorizontalSlider(S.renderScale, 0.33f, 1f);
            GUILayout.BeginHorizontal();
            foreach (var p in Presets.Modes)
            {
                bool on = Mathf.Abs(S.renderScale - p.Scale) < 0.005f;
                if (GUILayout.Toggle(on, Presets.ModeLabel(p, S), GUI.skin.button) && !on) S.renderScale = p.Scale;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Upscaler");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < Upscalers.All.Length; i++)
            {
                bool on = Upscalers.Index(S) == i;
                if (GUILayout.Toggle(on, Upscalers.All[i].Name, GUI.skin.button) && !on) Upscalers.Select(S, i);
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(Upscalers.All[Upscalers.Index(S)].Info);
            if (Upscalers.IsTaa(S) && S.debug)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("TAA sharpening: " + S.taaSharpness.ToString("F2"), GUILayout.Width(200));
                S.taaSharpness = Mathf.Round(GUILayout.HorizontalSlider(S.taaSharpness, 0f, 0.6f, GUILayout.Width(200)) * 100f) / 100f;
                GUILayout.EndHorizontal();
                GUILayout.Label(Taa.Describe());
            }
            if (S.dlss)
            {
                GUILayout.Label(Dlss.Describe());
                GUILayout.Label("DLSS preset");
                GUILayout.BeginHorizontal();
                foreach (var p in Presets.DlssPresets)
                {
                    bool on = S.preset == p.Value;
                    if (GUILayout.Toggle(on, p.Name, GUI.skin.button) && !on) S.preset = p.Value;
                }
                GUILayout.EndHorizontal();
                GUILayout.Label(Presets.DlssInfo(S.preset));
            }
            S.showAdvanced = GUILayout.Toggle(S.showAdvanced, "Advanced options");
            if (S.showAdvanced)
            {
                S.disableGameAA = GUILayout.Toggle(S.disableGameAA, "Switch off the game's SMAA and FXAA while an upscaler is on");
                S.mipAuto = GUILayout.Toggle(S.mipAuto, "Texture mip bias: auto (log2(scale) - 1, follows the render scale)");
                GUILayout.Label("Applied mip bias: " + MipBias.Current.ToString("F2") + (S.mipAuto ? "" : "   manual strength " + S.mipStrength.ToString("F2") + " of the recommended"));
                if (!S.mipAuto) S.mipStrength = GUILayout.HorizontalSlider(S.mipStrength, 0f, 1.5f);
                S.dlssBeforePost = GUILayout.Toggle(S.dlssBeforePost, "Upscale before post-processing, HDR input (better quality, costs some performance). Off: on the finished image");
                GUILayout.BeginHorizontal();
                GUILayout.Label("Other DLSS preset number (for presets newer than the list above): ", GUILayout.Width(480));
                if (customPresetText == null) customPresetText = S.preset.ToString();
                customPresetText = GUILayout.TextField(customPresetText, GUILayout.Width(50));
                int typed;
                if (GUILayout.Button("Use", GUILayout.Width(60)) && int.TryParse(customPresetText, out typed) && typed >= 0)
                {
                    S.preset = typed;
                    if (Presets.DlssIndex(typed) < 0) S.customPreset = typed;
                }
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Ground marker gap at characters: " + S.markerEdgeGapPx.ToString("F1") + " px", GUILayout.Width(300));
                S.markerEdgeGapPx = Mathf.Round(GUILayout.HorizontalSlider(S.markerEdgeGapPx, 0f, 3f, GUILayout.Width(200)) * 2f) / 2f;
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Retry DLSS", GUILayout.Width(120))) Dlss.Retry();
                if (S.debug)
                {
                    GUILayout.Label("Developer tools");
                    S.characterMotion = GUILayout.Toggle(S.characterMotion, "Per-object motion vectors for characters (" + ObjectMv.Status + ")");
                    S.noJitter = GUILayout.Toggle(S.noJitter, "No jitter");
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Jitter X " + S.jitSx)) S.jitSx = -S.jitSx;
                    if (GUILayout.Button("Jitter Y " + S.jitSy)) S.jitSy = -S.jitSy;
                    if (GUILayout.Button("MV X " + S.mvSignX)) S.mvSignX = -S.mvSignX;
                    if (GUILayout.Button("MV Y " + S.mvSignY)) S.mvSignY = -S.mvSignY;
                    if (GUILayout.Button("Char MV X " + S.objSignX)) S.objSignX = -S.objSignX;
                    if (GUILayout.Button("Char MV Y " + S.objSignY)) S.objSignY = -S.objSignY;
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button("Capture 4 frames in 2 s", GUILayout.Width(260))) Capture.Arm(2f);
                    if (GUILayout.Button("Probe ground markers in 8 s", GUILayout.Width(220))) DecalProbe.Arm(8f);
                    if (GUILayout.Button("Benchmark in 8 s (about 3 min)", GUILayout.Width(260))) Bench.Arm(8f);
                    if (GUILayout.Button("Vegetation census", GUILayout.Width(200))) { try { VegetationCensus.Run(); } catch (System.Exception ex) { Log("vegetation census failed " + ex); } }
                    if (GUILayout.Button("Character census", GUILayout.Width(200))) { try { MipBias.Census(); } catch (System.Exception ex) { Log("census failed " + ex); } }
                    GUILayout.Label(Capture.Status);
                    GUILayout.EndHorizontal();
                    S.debugFullMarkerBuffers = GUILayout.Toggle(S.debugFullMarkerBuffers, "Marker buffers over the whole screen (slower; for comparison)");
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Marker hold tolerance: " + (S.holdTolerance * 100f).ToString("F2") + " % of depth", GUILayout.Width(300));
                    S.holdTolerance = Mathf.Round(GUILayout.HorizontalSlider(S.holdTolerance, 0.0005f, 0.02f, GUILayout.Width(200)) * 10000f) / 10000f;
                    GUILayout.EndHorizontal();
                    S.debugStats = GUILayout.Toggle(S.debugStats, "Log character motion statistics (small hitch every few seconds)");
                    S.debugCharMv = GUILayout.Toggle(S.debugCharMv, "Char MV debug");
                }
            }
        }
    }
}

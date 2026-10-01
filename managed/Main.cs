using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace WotRDLSS
{
    public class Settings : UnityModManager.ModSettings
    {
        public bool enabled = true;
        public float renderScale = 0.6667f;   // multiplier of the output resolution
        public bool dlss = true;              // false: plain upscale (test mode)
        public int preset = 11;               // NGX render preset: 10=J 11=K 12=L 13=M, 0=default
        public bool dlssBeforePost = true;    // run DLSS on the HDR scene colour before post-processing (false: on the finished image)
        public bool noJitter = false;
        public float jitSx = -1f, jitSy = -1f;   // jitter sign towards DLSS (verified: displacement of the image = minus the projection jitter)
        public float mvSignX = 1f, mvSignY = 1f; // motion vector scale: the texture holds previous minus current position, which is what DLSS expects
        public bool mipAuto = true;           // follow the recommended bias for the current render scale
        public float mipStrength = 1f;        // fraction of the recommended texture LOD bias (log2(scale) - 1)
        public bool disableGameAA = true;     // switch the game's SMAA off while DLSS is active
        public bool characterMotion = true;   // per-object motion vectors for characters (needs the wotrdlss shader bundle)
        public float objSignX = 1f, objSignY = 1f;
        public float markerEdgeGapPx = 0.5f;            // the ground markers stop this many output pixels short of characters (covers their anti-aliased edge)
        public float holdTolerance = 0.004f;           // a still ground pixel keeps its depth/normals while the new depth is within this fraction of it
        public bool debugFreezeMarkerDepth = false, debugFreezeMarkerNormals = false;   // debug: keep last frame's full-res depth copy / normals
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
            logPath = Path.Combine(Dir, "WotRDLSS.log");
            try { File.WriteAllText(logPath, "WotRDLSS " + DateTime.Now + "\n"); } catch { }
            S = UnityModManager.ModSettings.Load<Settings>(entry);
            if (!S.debug)
            {   // developer-only switches never stay active from an old settings file
                S.noJitter = false; S.jitSx = -1f; S.jitSy = -1f; S.mvSignX = 1f; S.mvSignY = 1f; S.objSignX = 1f; S.objSignY = 1f;
                S.debugFreezeMarkerDepth = S.debugFreezeMarkerNormals = S.debugCharMv = S.debugStats = false;
                S.holdTolerance = 0.004f;
            }
            entry.OnGUI = OnGUI;
            entry.OnSaveGUI = e => S.Save(e);
            entry.OnUpdate = (e, dt) => Scaler.Update();
            entry.OnToggle = (e, on) => { S.enabled = on; Scaler.Update(); return true; };
            harmony = new Harmony("wotr.dlss");
            try { harmony.PatchAll(Assembly.GetExecutingAssembly()); PixelSize.Apply(harmony); Jitter.Install(); var go = new GameObject("WotRDLSS"); UnityEngine.Object.DontDestroyOnLoad(go); go.AddComponent<MipBias>(); go.AddComponent<Bench>(); Log("patched"); }
            catch (Exception ex) { Log("patch failed: " + ex); return false; }
            return true;
        }

        public static void Log(string s)
        {
            Mod.Logger.Log(s);
            try { File.AppendAllText(logPath, s + "\n"); } catch { }
        }

        // DLSS quality modes and their render-scale ratios (Ultra Quality is the non-standard 0.77 step).
        static readonly KeyValuePair<string, float>[] Presets =
        {
            new KeyValuePair<string, float>("Native", 1f),
            new KeyValuePair<string, float>("Ultra Quality", 0.77f),
            new KeyValuePair<string, float>("Quality", 0.6667f),
            new KeyValuePair<string, float>("Balanced", 0.58f),
            new KeyValuePair<string, float>("Performance", 0.5f),
            new KeyValuePair<string, float>("Ultra Performance", 0.3333f),
        };

        static void OnGUI(UnityModManager.ModEntry e)
        {
            S.enabled = GUILayout.Toggle(S.enabled, "Scale 3D rendering");
            int ow = Screen.width, oh = Screen.height;
            int rw = Mathf.Max(1, (int)(ow * S.renderScale)), rh = Mathf.Max(1, (int)(oh * S.renderScale));
            GUILayout.Label("Render scale: " + S.renderScale.ToString("F2") + "x    Internal resolution: " + rw + " x " + rh + "    Output: " + ow + " x " + oh);
            S.renderScale = GUILayout.HorizontalSlider(S.renderScale, 0.33f, 1f);
            GUILayout.BeginHorizontal();
            foreach (var p in Presets)
            {
                bool on = Mathf.Abs(S.renderScale - p.Value) < 0.005f;
                if (GUILayout.Toggle(on, p.Key + " (" + p.Value.ToString("0.##") + "x)", GUI.skin.button) && !on) S.renderScale = p.Value;
            }
            GUILayout.EndHorizontal();

            S.dlss = GUILayout.Toggle(S.dlss, "Use DLSS (off = plain upscale)");
            GUILayout.Label(Dlss.Describe());
            S.showAdvanced = GUILayout.Toggle(S.showAdvanced, "Advanced options");
            if (S.showAdvanced)
            {
                S.characterMotion = GUILayout.Toggle(S.characterMotion, "Per-object motion vectors for characters (" + ObjectMv.Status + ")");
                S.disableGameAA = GUILayout.Toggle(S.disableGameAA, "Switch the game's SMAA/FXAA off while DLSS is active");
                S.mipAuto = GUILayout.Toggle(S.mipAuto, "Texture mip bias: auto (log2(scale) - 1, follows the render scale)");
                GUILayout.Label("Applied mip bias: " + MipBias.Current.ToString("F2") + (S.mipAuto ? "" : "   manual strength " + S.mipStrength.ToString("F2") + " of the recommended"));
                if (!S.mipAuto) S.mipStrength = GUILayout.HorizontalSlider(S.mipStrength, 0f, 1.5f);
                S.dlssBeforePost = GUILayout.Toggle(S.dlssBeforePost, "Run DLSS before post-processing, HDR input (better quality, costs some performance). Off: on the finished image");
                GUILayout.BeginHorizontal();
                GUILayout.Label("DLSS preset (0 default, 10 J, 11 K, 12 L, 13 M): ");
                int.TryParse(GUILayout.TextField(S.preset.ToString(), GUILayout.Width(40)), out S.preset);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label("Ground marker gap at characters: " + S.markerEdgeGapPx.ToString("F1") + " px", GUILayout.Width(300));
                S.markerEdgeGapPx = Mathf.Round(GUILayout.HorizontalSlider(S.markerEdgeGapPx, 0f, 3f, GUILayout.Width(200)) * 2f) / 2f;
                GUILayout.EndHorizontal();
                if (GUILayout.Button("Retry DLSS", GUILayout.Width(120))) Dlss.Retry();
                if (S.debug)
                {
                    GUILayout.Label("Developer tools");
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
                    if (GUILayout.Button("Capture 4 frames in 8 s", GUILayout.Width(260))) Capture.Arm(8f);
                    if (GUILayout.Button("Probe ground markers in 8 s", GUILayout.Width(220))) DecalProbe.Arm(8f);
                    if (GUILayout.Button("Benchmark in 8 s (about 3 min)", GUILayout.Width(260))) Bench.Arm(8f);
                    if (GUILayout.Button("Character census", GUILayout.Width(200))) { try { MipBias.Census(); } catch (System.Exception ex) { Log("census failed " + ex); } }
                    GUILayout.Label(Capture.Status);
                    GUILayout.EndHorizontal();
                    GUILayout.BeginHorizontal();
                    S.debugFreezeMarkerDepth = GUILayout.Toggle(S.debugFreezeMarkerDepth, "Freeze marker depth", GUILayout.Width(260));
                    S.debugFreezeMarkerNormals = GUILayout.Toggle(S.debugFreezeMarkerNormals, "Freeze marker normals", GUILayout.Width(260));
                    GUILayout.EndHorizontal();
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

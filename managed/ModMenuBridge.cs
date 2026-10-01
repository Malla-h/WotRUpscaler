#if MODMENU
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Kingmaker.Blueprints.JsonSystem;
using Kingmaker.Localization;
using ModMenu.Settings;
using UnityEngine;
using UnityModManagerNet;

namespace WotRUpscaler
{
    // Mirrors the player-facing options into the in-game "Mods" settings page that the ModMenu mod provides. ModMenu is optional: without
    // it nothing here runs and the Mods panel (Ctrl+F10) stays the only place. The settings file stays the source of truth: the menu is
    // brought in line with it, changes made in the menu are written back to it, and changes made in the Mods panel show up in the menu.
    public static class ModMenuBridge
    {
        static bool tried, registered, synced, inSync, failedLogged;
        static int lastTry = -1000;
        static readonly Dictionary<string, object> shown = new Dictionary<string, object>();
        static readonly List<KeyValuePair<string, string>> strings = new List<KeyValuePair<string, string>>();
        const int KeyCount = 8;

        static LocalizedString Str(string key, string text)
        {
            var s = new LocalizedString { Key = key };
            strings.Add(new KeyValuePair<string, string>(key, text));
            if (LocalizationManager.Initialized) LocalizationManager.CurrentPack.PutString(key, text);
            return s;
        }

        internal static void ReRegisterStrings()
        {
            foreach (var kv in strings) LocalizationManager.CurrentPack.PutString(kv.Key, kv.Value);
        }

        public static void TryRegister()
        {
            if (tried) return;
            tried = true;
            try
            {
                var mm = UnityModManager.FindMod("ModMenu");
                if (mm == null || !mm.Active) { Main.Log("ModMenu is not installed or not active: the in-game menu entries are skipped"); return; }
                Register();
                registered = true;
                Main.Log("options added to the ModMenu page");
            }
            catch (Exception e) { Main.Log("ModMenu bridge failed: " + e); }
        }

        // Called every frame: registers late if the game's own start-up hook was missed, then keeps the menu in line with the settings.
        public static void Tick()
        {
            int f = Time.frameCount;
            if (!tried && f > 300 && LocalizationManager.Initialized) TryRegister();
            if (!registered || f - lastTry < 20) return;
            lastTry = f;
            if (Bench.Running) return;                       // the benchmark changes settings temporarily
            Sync();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Register()
        {
            var info = Main.Mod.Info;
            var b = SettingsBuilder.New("wotrupscaler", Str("wotrupscaler.title", "Upscaling"))
                .SetMod(Main.Mod, false, false)
                .SetModName(Str("wotrupscaler.name", "WotR Upscaler"))
                .SetModDescription(Str("wotrupscaler.description", "Renders the 3D scene at a lower resolution and upscales it with NVIDIA DLSS. The interface stays at full resolution."))
                .SetModVersion(info.Version)
                .SetModAuthor(info.Author);

            b.AddToggle(Toggle.New("wotrupscaler.enabled", true, Str("wotrupscaler.enabled", "Scale 3D rendering"))
                .WithLongDescription(Str("wotrupscaler.enabled.long", "Renders the 3D scene at a lower resolution and upscales it. Off: the game renders as usual."))
                .OnValueChanged(v => Changed("enabled", v)));

            // One entry per upscaler (see Upscalers). Options that belong to a single upscaler sit in a section of their own below.
            var ups = new List<LocalizedString>();
            var upTip = new StringBuilder("How the lower-resolution 3D scene is turned back into a sharp image.");
            for (int i = 0; i < Upscalers.All.Length; i++)
            {
                ups.Add(Str("wotrupscaler.upscaler." + i, Upscalers.All[i].Name));
                upTip.Append("\n").Append(Upscalers.All[i].Name).Append(": ").Append(Upscalers.All[i].Info);
            }
            b.AddDropdownList(DropdownList.New("wotrupscaler.upscaler", 0, Str("wotrupscaler.upscaler", "Upscaler"), ups)
                .WithLongDescription(Str("wotrupscaler.upscaler.long", upTip.ToString()))
                .OnValueChanged(v => Changed("upscaler", v)));

            var modes = new List<LocalizedString>();
            for (int i = 0; i < Presets.Modes.Length; i++) modes.Add(Str("wotrupscaler.mode." + i, Presets.Modes[i].Label));
            modes.Add(Str("wotrupscaler.mode.custom", "Custom (use the slider)"));
            b.AddDropdownList(DropdownList.New("wotrupscaler.mode", Presets.ModeIndex(0.6667f), Str("wotrupscaler.mode", "Quality mode"), modes)
                .WithLongDescription(Str("wotrupscaler.mode.long", "How far below the screen resolution the 3D scene is rendered. Lower is faster, higher is sharper. Native (DLAA) renders at full resolution and uses the upscaler only for anti-aliasing."))
                .OnValueChanged(v => Changed("mode", v)));

            b.AddSliderFloat(SliderFloat.New("wotrupscaler.scale", 0.6667f, Str("wotrupscaler.scale", "Render scale"), 0.33f, 1f)
                .WithStep(0.01f).WithDecimalPlaces(2)
                .WithLongDescription(Str("wotrupscaler.scale.long", "The multiplier of the screen resolution that the 3D scene is rendered at. Picking a quality mode sets it; moving the slider selects Custom."))
                .OnValueChanged(v => Changed("scale", v)));

            b.AddSubHeader(Str("wotrupscaler.dlss.header", "NVIDIA DLSS"), true);
            var presets = new List<LocalizedString>();
            var tip = new StringBuilder("Which DLSS model runs.");
            for (int i = 0; i < Presets.DlssPresets.Length; i++)
            {
                presets.Add(Str("wotrupscaler.preset." + i, Presets.DlssPresets[i].Name));
                tip.Append("\n").Append(Presets.DlssPresets[i].Info);
            }
            presets.Add(Str("wotrupscaler.preset.other", "Other (number set in the Mods panel)"));
            tip.Append("\nOther: a preset number outside this list, typed into the Mods panel (Ctrl+F10), for presets NVIDIA adds later.");
            b.AddDropdownList(DropdownList.New("wotrupscaler.preset", Presets.DlssIndex(Presets.Recommended), Str("wotrupscaler.preset", "DLSS preset"), presets)
                .WithLongDescription(Str("wotrupscaler.preset.long", tip.ToString()))
                .OnValueChanged(v => Changed("preset", v)));
            b.AddToggle(Toggle.New("wotrupscaler.hdr", true, Str("wotrupscaler.hdr", "Run DLSS before post-processing (HDR input)"))
                .WithLongDescription(Str("wotrupscaler.hdr.long", "Gives DLSS the unprocessed scene, so bloom, depth of field and colour grading work at full resolution. Costs a little performance. Off: DLSS runs on the finished image."))
                .OnValueChanged(v => Changed("hdr", v)));

            b.AddSubHeader(Str("wotrupscaler.advanced", "More options"), false);
            b.AddToggle(Toggle.New("wotrupscaler.smaa", true, Str("wotrupscaler.smaa", "Switch off the game's SMAA and FXAA while an AI upscaler is on"))
                .WithLongDescription(Str("wotrupscaler.smaa.long", "The upscaler does its own anti-aliasing; the game's SMAA or FXAA would only soften the image it receives."))
                .OnValueChanged(v => Changed("smaa", v)));
            b.AddToggle(Toggle.New("wotrupscaler.mip", true, Str("wotrupscaler.mip", "Automatic mip map bias"))
                .WithLongDescription(Str("wotrupscaler.mip.long", "Adjusts the texture mip map bias to the render scale, as upscalers expect, so textures stay sharp at lower render resolutions."))
                .OnValueChanged(v => Changed("mip", v)));
            b.AddDefaultButton();
            global::ModMenu.ModMenu.AddSettings(b);
        }

        // A value changed in the menu.
        static void Changed(string key, object v)
        {
            shown[key] = v;
            if (inSync) return;                      // our own update of the menu: the settings already hold this value
            var s = Main.S;
            switch (key)
            {
                case "enabled": s.enabled = (bool)v; break;
                case "mode":
                    {
                        int i = (int)v;
                        if (i >= 0 && i < Presets.Modes.Length) s.renderScale = Presets.Modes[i].Scale;
                        break;
                    }
                case "scale":
                    {
                        float f = (float)v;
                        if (Mathf.Abs(f - s.renderScale) > 0.004f) s.renderScale = f;
                        break;
                    }
                case "upscaler": Upscalers.Select(s, (int)v); break;
                case "preset":
                    {
                        int i = (int)v;
                        s.preset = i >= 0 && i < Presets.DlssPresets.Length ? Presets.DlssPresets[i].Value : s.customPreset;
                        break;
                    }
                case "hdr": s.dlssBeforePost = (bool)v; break;
                case "smaa": s.disableGameAA = (bool)v; break;
                case "mip": s.mipAuto = (bool)v; break;
            }
            Scaler.Update();
            try { s.Save(Main.Mod); } catch { }
            Sync();                                  // the mode and the slider follow each other
        }

        // Brings the menu in line with the settings (only values that differ are pushed).
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Sync()
        {
            if (!registered || inSync) return;
            inSync = true;
            try
            {
                var s = Main.S;
                PushBool("enabled", s.enabled);
                int mi = Presets.ModeIndex(s.renderScale);
                PushInt("mode", mi < 0 ? Presets.Modes.Length : mi);
                PushFloat("scale", s.renderScale);
                PushInt("upscaler", Upscalers.Index(s));
                int pi = Presets.DlssIndex(s.preset);
                PushInt("preset", pi < 0 ? Presets.DlssPresets.Length : pi);
                PushBool("hdr", s.dlssBeforePost);
                PushBool("smaa", s.disableGameAA);
                PushBool("mip", s.mipAuto);
                synced = shown.Count >= KeyCount;
            }
            catch (Exception e)
            {
                if (!failedLogged) { failedLogged = true; Main.Log("ModMenu sync failed: " + e); }
            }
            finally { inSync = false; }
        }

        static void PushBool(string key, bool v)
        {
            object o;
            if (shown.TryGetValue(key, out o) && (bool)o == v) return;
            if (global::ModMenu.ModMenu.SetSetting("wotrupscaler." + key, v)) shown[key] = v;
        }

        static void PushInt(string key, int v)
        {
            object o;
            if (shown.TryGetValue(key, out o) && (int)o == v) return;
            if (global::ModMenu.ModMenu.SetSetting("wotrupscaler." + key, v)) shown[key] = v;
        }

        static void PushFloat(string key, float v)
        {
            object o;
            if (shown.TryGetValue(key, out o) && Mathf.Abs((float)o - v) < 0.0005f) return;
            if (global::ModMenu.ModMenu.SetSetting("wotrupscaler." + key, v)) shown[key] = v;
        }
    }

    [HarmonyPatch(typeof(BlueprintsCache), "Init")]
    static class ModMenuRegisterPatch
    {
        static void Postfix() { ModMenuBridge.TryRegister(); }
    }

    [HarmonyPatch(typeof(LocalizationManager), "OnLocaleChanged")]
    static class ModMenuLocalePatch
    {
        static void Postfix()
        {
            try { ModMenuBridge.ReRegisterStrings(); } catch { }
        }
    }
}
#else
namespace WotRUpscaler
{
    // Built without the ModMenu mod installed: no in-game menu entries.
    public static class ModMenuBridge
    {
        public static void TryRegister() { }
        public static void Tick() { }
        public static void Sync() { }
    }
}
#endif

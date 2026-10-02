namespace WotRUpscaler
{
    // The named choices offered in the menus. The stored settings stay plain numbers (a scale multiplier, an NGX preset number), so a
    // preset NVIDIA adds later keeps working: it can be typed in as a number, and showing it costs one new line in the table below.
    public static class Presets
    {
        public struct Mode
        {
            public string Name; public float Scale;
            public Mode(string name, float scale) { Name = name; Scale = scale; }
            // "Quality (0.67x)"; native resolution runs the upscaler as anti-aliasing only, so it carries no multiplier.
            public string Label { get { return Scale >= 0.999f ? Name : Name + " (" + Scale.ToString("0.##") + "x)"; } }
        }

        // DLSS quality modes and their render-scale multipliers (Ultra Quality is the non-standard 0.77 step).
        public static readonly Mode[] Modes =
        {
            new Mode("Native (DLAA)", 1f),
            new Mode("Ultra Quality", 0.77f),
            new Mode("Quality", 0.6667f),
            new Mode("Balanced", 0.58f),
            new Mode("Performance", 0.5f),
            new Mode("Ultra Performance", 0.3333f),
        };

        // The label shown in the menus. Full resolution is named after what the selected upscaler does there: DLAA, TAA, or nothing at all.
        public static string ModeLabel(Mode m, Settings s)
        {
            if (m.Scale < 0.999f) return m.Label;
            return s.dlss ? "Native (DLAA)" : s.taa ? "Native (TAA)" : "Native";
        }

        // Index of the mode whose scale matches, or -1 for a custom scale.
        public static int ModeIndex(float scale)
        {
            for (int i = 0; i < Modes.Length; i++) if (System.Math.Abs(scale - Modes[i].Scale) < 0.005f) return i;
            return -1;
        }

        public struct Preset
        {
            public int Value; public string Name, Info;
            public Preset(int value, string name, string info) { Value = value; Name = name; Info = info; }
        }

        // NGX render presets (values from NVIDIA's nvsdk_ngx_defs.h), in the order the menus list them.
        public const int Recommended = 11;
        public static readonly Preset[] DlssPresets =
        {
            new Preset(0, "Automatic", "Automatic: NVIDIA chooses the preset per quality mode, and may change it with driver updates."),
            new Preset(11, "K", "K: the best balance of image quality and speed, and the recommended choice from Quality mode up. Lighter on the GPU than L and M."),
            new Preset(10, "J", "J: like K, with slightly less ghosting at the cost of a little more flicker."),
            new Preset(12, "L", "L: NVIDIA's default for Ultra Performance mode. Heavier on the GPU than K."),
            new Preset(13, "M", "M: NVIDIA's default for Performance mode. Heavier on the GPU than K, but the better image can be worth it at Performance and lower."),
        };

        // Index in DlssPresets, or -1 when the number is not in the table.
        public static int DlssIndex(int value)
        {
            for (int i = 0; i < DlssPresets.Length; i++) if (DlssPresets[i].Value == value) return i;
            return -1;
        }

        public static string DlssName(int value)
        {
            int i = DlssIndex(value);
            return i >= 0 ? DlssPresets[i].Name : "Preset " + value;
        }

        public static string DlssInfo(int value)
        {
            int i = DlssIndex(value);
            return i >= 0 ? DlssPresets[i].Info : "Preset number " + value + ": not in the list above; NVIDIA's documentation says what it does.";
        }
    }
}

namespace WotRDLSS
{
    // The named choices offered in the menus. The stored settings stay plain numbers (a scale multiplier, an NGX preset number), so a
    // preset NVIDIA adds later keeps working: it can be typed in as a number, and showing it costs one new line in the table below.
    public static class Presets
    {
        public struct Mode { public string Name; public float Scale; public Mode(string name, float scale) { Name = name; Scale = scale; } }

        // DLSS quality modes and their render-scale multipliers (Ultra Quality is the non-standard 0.77 step).
        public static readonly Mode[] Modes =
        {
            new Mode("Native", 1f),
            new Mode("Ultra Quality", 0.77f),
            new Mode("Quality", 0.6667f),
            new Mode("Balanced", 0.58f),
            new Mode("Performance", 0.5f),
            new Mode("Ultra Performance", 0.3333f),
        };

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

        // NGX render presets (values from NVIDIA's nvsdk_ngx_defs.h). The costs are GPU time of the DLSS pass measured on an RTX 5060 at 4K.
        public static readonly Preset[] DlssPresets =
        {
            new Preset(11, "K", "K: the best image quality and the recommended choice. Cheap (about 3.5 ms at 4K on an RTX 5060)."),
            new Preset(10, "J", "J: like K, with slightly less ghosting at the cost of a little more flicker."),
            new Preset(12, "L", "L: NVIDIA's default for Ultra Performance mode. Costs 2 to 3 times as much GPU time as K."),
            new Preset(13, "M", "M: NVIDIA's default for Performance mode. Costs about twice as much GPU time as K."),
            new Preset(0, "Automatic", "Automatic: NVIDIA chooses per quality mode, and may change it with driver updates."),
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

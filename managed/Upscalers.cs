namespace WotRUpscaler
{
    // The upscalers the menus offer: NVIDIA DLSS, TAA (our own temporal upscaler, any graphics card) and plain scaling (the 3D scene rendered
    // smaller and just stretched, for comparison).
    // A new upscaler (FSR, XeSS) is one more entry here, a section of its own options in the menus (see ModMenuBridge.Register and the
    // Mods panel), and its own evaluate step in Upscale.cs; the render scale, quality modes and the rest of the pipeline are shared.
    public static class Upscalers
    {
        public struct Upscaler
        {
            public string Id, Name, Info;
            public Upscaler(string id, string name, string info) { Id = id; Name = name; Info = info; }
        }

        public static readonly Upscaler[] All =
        {
            new Upscaler("dlss", "NVIDIA DLSS", "Needs an NVIDIA RTX graphics card."),
            new Upscaler("taa", "TAA", "Temporal anti-aliasing and upscaling. Works on any graphics card; not as sharp or as stable as DLSS."),
            new Upscaler("plain", "Simple scaling", "The 3D scene is rendered smaller and stretched to the screen size."),
        };

        public static bool IsDlss(Settings s) { return s.dlss; }
        public static bool IsTaa(Settings s) { return !s.dlss && s.taa; }
        // Jitter, motion vectors and the full-resolution hand-off are needed by both temporal upscalers.
        public static bool Temporal(Settings s) { return s.dlss || s.taa; }
        // The selected temporal upscaler is ready to produce frames (it is not for the first moments after it is chosen).
        public static bool TemporalReady(Settings s) { return s.dlss ? Dlss.Ready : s.taa && Taa.Ready; }
        // The selected upscaler cannot run on this machine (the game then keeps its normal renderer).
        public static bool Unavailable(Settings s) { return s.dlss ? Dlss.Failed : s.taa && Taa.Failed; }
        public static string Name(Settings s) { return All[Index(s)].Name; }

        public static int Index(Settings s) { return s.dlss ? 0 : s.taa ? 1 : 2; }

        public static void Select(Settings s, int index) { s.dlss = index == 0; s.taa = index == 1; }
    }
}

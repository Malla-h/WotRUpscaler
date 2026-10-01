namespace WotRDLSS
{
    // The upscalers the menus offer. Today: NVIDIA DLSS, and plain scaling (the 3D scene rendered smaller and just stretched, for comparison).
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
            new Upscaler("plain", "Simple scaling", "The 3D scene is rendered smaller and stretched to the screen size."),
        };

        public static int Index(Settings s) { return s.dlss ? 0 : 1; }

        public static void Select(Settings s, int index) { s.dlss = index == 0; }
    }
}

using System.IO;
using UnityEngine;

namespace WotRUpscaler
{
    // The shader asset bundle (built with the game's own Unity version, see the unity folder of the source).
    public static class Bundle
    {
        static AssetBundle bundle;
        static bool tried;
        public static string Error = "";

        public static Shader Shader(string assetPath)
        {
            if (!tried)
            {
                tried = true;
                string path = Path.Combine(Main.Dir, "wotrupscaler");
                if (!File.Exists(path)) { Error = "wotrupscaler bundle missing"; Main.Log("shader bundle missing: " + path); }
                else
                {
                    bundle = AssetBundle.LoadFromFile(path);
                    if (bundle == null) { Error = "bundle failed to load"; Main.Log("AssetBundle.LoadFromFile failed for " + path); }
                }
            }
            if (bundle == null) return null;
            var sh = bundle.LoadAsset<Shader>(assetPath);
            if (sh == null) { Error = "shader not in bundle: " + assetPath; Main.Log(Error); return null; }
            if (!sh.isSupported) { Error = "shader unsupported: " + assetPath; Main.Log(Error); return null; }
            return sh;
        }
    }
}

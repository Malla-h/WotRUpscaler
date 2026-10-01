using System.IO;
using UnityEditor;
using UnityEngine;

// Builds the shader asset bundle the mod loads at runtime. Run in batch mode:
//   Unity.exe -batchmode -nographics -quit -projectPath <this project> -executeMethod BuildBundle.Build
public static class BuildBundle
{
    public static void Build()
    {
        // Direct3D 11 only: that is what the game uses, and it keeps the bundle small.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 });

        int count = 0;
        foreach (var guid in AssetDatabase.FindAssets("t:Shader", new[] { "Assets/Shaders" }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var importer = AssetImporter.GetAtPath(path);
            if (importer == null) continue;
            importer.assetBundleName = "wotrdlss";
            importer.SaveAndReimport();
            count++;
            Debug.Log("bundle shader: " + path);
        }
        if (count == 0) { Debug.LogError("no shaders found"); EditorApplication.Exit(2); return; }

        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "../bundle"));
        Directory.CreateDirectory(outDir);
        var manifest = BuildPipeline.BuildAssetBundles(outDir, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null) { Debug.LogError("bundle build failed"); EditorApplication.Exit(3); return; }
        Debug.Log("bundle built into " + outDir + " with " + count + " shaders");
    }
}

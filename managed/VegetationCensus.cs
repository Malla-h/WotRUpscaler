using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using Owlcat.Runtime.Visual.RenderPipeline.IndirectRendering;
using UnityEngine;

namespace WotRUpscaler
{
    // Debug tool: which shaders draw the scene's static geometry (trees, bushes, grass, props) and which of them animate their vertices?
    // The result goes to the log. Used to work out how the game's wind animation could be reproduced for motion vectors.
    public static class VegetationCensus
    {
        class Entry
        {
            public int count, verts;
            public readonly HashSet<string> kinds = new HashSet<string>(), examples = new HashSet<string>();
            public readonly HashSet<int> layers = new HashSet<int>();
            public Material sample;
        }

        static readonly Regex WindLike = new Regex("wind|sway|bend|pbd|flex|anim|trunk|leaf|grass|fluid|vertex|noise|time", RegexOptions.IgnoreCase);

        static void Add(Dictionary<string, Entry> map, Material m, string kind, string objName, int layer, int verts)
        {
            if (m == null || m.shader == null) return;
            Entry e;
            string key = m.shader.name;
            if (!map.TryGetValue(key, out e)) map[key] = e = new Entry { sample = m };
            e.count++; e.verts += verts; e.kinds.Add(kind); e.layers.Add(layer);
            if (e.examples.Count < 4) e.examples.Add(objName);
        }

        public static void Run()
        {
            var map = new Dictionary<string, Entry>();
            foreach (var r in Object.FindObjectsOfType<Renderer>())
            {
                if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
                if (r is SkinnedMeshRenderer || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                var mf = r.GetComponent<MeshFilter>();
                int verts = mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount : 0;
                foreach (var m in r.sharedMaterials) Add(map, m, "renderer:" + r.GetType().Name, r.name, r.gameObject.layer, verts);
            }
            int indirect = 0;
            try
            {
                var f = AccessTools.Field(typeof(IndirectRenderingSystem), "m_MeshData");
                var dict = f.GetValue(IndirectRenderingSystem.Instance) as System.Collections.IDictionary;
                if (dict != null)
                    foreach (var k in dict.Keys)
                    {
                        var im = k as IIndirectMesh;
                        if (im == null || im.Materials == null) continue;
                        indirect++;
                        int verts = im.Mesh != null ? im.Mesh.vertexCount : 0;
                        string name = (im.Mesh != null ? im.Mesh.name : "?") + "@" + im.GetType().Name;
                        foreach (var m in im.Materials) Add(map, m, "indirect:" + im.GetType().Name, name, 0, verts);
                    }
            }
            catch (System.Exception e) { Main.Log("VEG census: indirect meshes unavailable: " + e.Message); }

            Main.Log("VEG census: " + map.Count + " shaders, " + indirect + " indirect (instanced) mesh groups");
            var list = new List<KeyValuePair<string, Entry>>(map);
            list.Sort((a, b) => b.Value.count.CompareTo(a.Value.count));
            foreach (var kv in list)
            {
                var e = kv.Value; var sh = e.sample.shader;
                var props = new StringBuilder();
                for (int i = 0; i < sh.GetPropertyCount(); i++)
                {
                    string pn = sh.GetPropertyName(i);
                    if (WindLike.IsMatch(pn)) props.Append(pn).Append(' ');
                }
                var passes = new StringBuilder();
                for (int i = 0; i < e.sample.passCount; i++) passes.Append(e.sample.GetPassName(i)).Append(i + 1 < e.sample.passCount ? "," : "");
                var kinds = new StringBuilder(); foreach (var k in e.kinds) kinds.Append(k).Append(' ');
                var layers = new StringBuilder(); foreach (var l in e.layers) layers.Append(l).Append(' ');
                var ex = new StringBuilder(); foreach (var x in e.examples) ex.Append(x).Append("; ");
                var kw = new StringBuilder(); foreach (var k in e.sample.shaderKeywords) kw.Append(k).Append(' ');
                Main.Log("VEG " + kv.Key + ": x" + e.count + " verts " + e.verts + " | " + kinds + "| layers " + layers + "| queue " + e.sample.renderQueue
                    + " | passes " + passes + " | keywords " + kw + "| props " + props + "| e.g. " + ex);
            }
        }
    }
}

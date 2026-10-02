using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WotRUpscaler
{
    // Rendering below output resolution samples textures at coarser mips than a native render would. Upscalers expect the game to
    // compensate with a negative texture LOD bias of log2(render/output) - 1 (about -1.585 for 2/3 scale).
    public class MipBias : MonoBehaviour
    {
        public static float Current;                       // bias currently applied to textures (0 = none)
        static readonly HashSet<int> done = new HashSet<int>();
        Coroutine scan;

        static float Target()
        {
            if (!Scaler.Active || !Upscalers.TemporalReady(Main.S) || (!Main.S.mipAuto && Main.S.mipStrength <= 0f)) return 0f;
            return (Mathf.Log(Scaler.Scale, 2f) - 1f) * (Main.S.mipAuto ? 1f : Main.S.mipStrength);
        }

        // Debug button: what kind of renderers draw them, and with which shaders?
        public static void Census()
        {
            var smrs = Object.FindObjectsOfType<SkinnedMeshRenderer>();
            var mrs = Object.FindObjectsOfType<MeshRenderer>();
            int vis = 0, withMv = 0, skMv = 0;
            var shaders = new Dictionary<string, int>();
            foreach (var r in smrs)
            {
                if (r.isVisible) vis++;
                if (r.motionVectorGenerationMode == MotionVectorGenerationMode.Object) withMv++;
                if (r.skinnedMotionVectors) skMv++;
                var m = r.sharedMaterial;
                string n = m != null && m.shader != null ? m.shader.name : "?";
                int c; shaders.TryGetValue(n, out c); shaders[n] = c + 1;
            }
            Main.Log("CENSUS SkinnedMeshRenderers " + smrs.Length + " (visible " + vis + ", mvMode=Object " + withMv + ", skinnedMotionVectors " + skMv + "), MeshRenderers " + mrs.Length);
            foreach (var kv in shaders) Main.Log("  SMR shader " + kv.Key + " x" + kv.Value);
            int shown = 0;
            foreach (var r in smrs) if (r.isVisible && shown++ < 8) Main.Log("  SMR " + r.name + " root=" + (r.rootBone != null ? r.rootBone.name : "-") + " verts=" + (r.sharedMesh != null ? r.sharedMesh.vertexCount : 0) + " parent=" + r.transform.root.name);
            foreach (var cam in Camera.allCameras) Main.Log("camera " + cam.name + " depthTextureMode=" + cam.depthTextureMode);
            // Non-skinned pieces attached to characters (bags, weapons, props): are they tracked for motion?
            var roots = new HashSet<Transform>();
            foreach (var r in smrs) if (r.isVisible) roots.Add(r.transform.root);
            int rootsShown = 0;
            foreach (var root in roots)
            {
                if (rootsShown++ >= 6) break;
                var rmrs = root.GetComponentsInChildren<MeshRenderer>(false);
                int obj = 0, cam = 0, none = 0;
                foreach (var m in rmrs) { if (m.motionVectorGenerationMode == MotionVectorGenerationMode.Object) obj++; else if (m.motionVectorGenerationMode == MotionVectorGenerationMode.Camera) cam++; else none++; }
                Main.Log("  root " + root.name + ": MeshRenderers " + rmrs.Length + " (mode Object " + obj + ", Camera " + cam + ", ForceNoMotion " + none + ")");
                int mrShown = 0;
                foreach (var m in rmrs)
                    if (mrShown++ < 10) Main.Log("     MR " + m.name + " mode=" + m.motionVectorGenerationMode + " parent=" + (m.transform.parent != null ? m.transform.parent.name : "-") + " verts=" + (m.GetComponent<MeshFilter>() != null && m.GetComponent<MeshFilter>().sharedMesh != null ? m.GetComponent<MeshFilter>().sharedMesh.vertexCount : 0) + " shader=" + (m.sharedMaterial != null ? m.sharedMaterial.shader.name : "?"));
            }
        }

        void Update()
        {
            float t = Target();
            if (Mathf.Abs(t - Current) > 0.001f)
            {
                Current = t;
                done.Clear();                              // re-apply everything at the new value (also restores 0 when switched off)
                if (scan != null) StopCoroutine(scan);
                scan = StartCoroutine(Scan());
                Main.Log("Texture mip bias target " + t.ToString("F3"));
            }
            else if (scan == null && Current != 0f && Time.frameCount % 600 == 0)
                scan = StartCoroutine(Scan());             // pick up textures loaded since the last pass
        }

        IEnumerator Scan()
        {
            float bias = Current;
            var all = Resources.FindObjectsOfTypeAll<Texture>();
            int changed = 0, n = 0;
            foreach (var tex in all)
            {
                if (tex == null) continue;
                if (++n % 250 == 0) yield return null;    // spread the work over frames
                if (!done.Add(tex.GetInstanceID())) continue;
                if (!(tex is Texture2D) && !(tex is Texture2DArray)) continue;
                if (tex.mipMapBias == bias) continue;
                tex.mipMapBias = bias;
                changed++;
            }
            if (changed > 0) Main.Log("Mip bias " + bias.ToString("F3") + " applied to " + changed + " of " + all.Length + " textures");
            scan = null;
        }
    }
}

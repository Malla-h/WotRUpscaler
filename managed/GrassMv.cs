using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Owlcat.Runtime.Visual.RenderPipeline.IndirectRendering;
using Owlcat.Runtime.Visual.RenderPipeline.RendererFeatures.PositionBasedDynamics;
using UnityEngine;
using UnityEngine.Rendering;

namespace WotRUpscaler
{
    // Motion vectors for the game's instanced physics grass. The game draws its detail meshes (grass blades) with DrawMeshInstancedIndirect
    // from IndirectRenderingSystem.DrawPass; this repeats that loop for the grass meshes with the motion vector shader (pass "GrassMV"), using
    // the system's own property block (instance, visibility and argument buffers) and the same argument offsets.
    public static class GrassMv
    {
        static FieldInfo meshData, argsBuffer, propertyBlock, instanceData;
        static bool resolved, failed, logged;
        static readonly int ArgsOffset = Shader.PropertyToID("_ArgsOffset"), PbdEnabledLocal = Shader.PropertyToID("_PbdEnabledLocal");
        static CommandBuffer cb;

        static bool Resolve()
        {
            if (resolved) return !failed;
            resolved = true;
            var t = typeof(IndirectRenderingSystem);
            meshData = AccessTools.Field(t, "m_MeshData");
            argsBuffer = AccessTools.Field(t, "m_ArgsBuffer");
            propertyBlock = AccessTools.Field(t, "m_MaterialPropertyBlock");
            failed = meshData == null || argsBuffer == null || propertyBlock == null;
            if (failed) Main.Log("grass motion vectors: the indirect rendering system's fields were not found");
            return !failed;
        }

        public static void Draw(ScriptableRenderContext ctx, Material mv)
        {
            var sys = IndirectRenderingSystem.Instance;
            if (sys == null || mv == null || !Resolve()) return;
            int pass = mv.FindPass("GrassMV");
            var args = argsBuffer.GetValue(sys) as ComputeBuffer;
            var mpb = propertyBlock.GetValue(sys) as MaterialPropertyBlock;
            var dict = meshData.GetValue(sys) as IDictionary;
            if (pass < 0 || args == null || !args.IsValid() || mpb == null || dict == null) return;
            if (PbdMotion.PrevParticles == null) return;

            if (cb == null) cb = new CommandBuffer { name = "WotRUpscaler grass motion" };
            cb.Clear();
            cb.SetRenderTarget(new RenderTargetIdentifier(ObjectMv.Target));
            int drawn = 0, num = 0, num2 = 4;
            foreach (DictionaryEntry e in dict)
            {
                var mesh = (IIndirectMesh)e.Key;
                if (instanceData == null) instanceData = e.Value.GetType().GetField("InstanceData");
                var list = instanceData.GetValue(e.Value) as IList;
                int subMeshes = mesh.Mesh.subMeshCount;
                if (list == null || list.Count == 0 || mesh.Materials.Count == 0)
                {
                    num += 20 * subMeshes;
                    num2 += 5 * subMeshes;
                    continue;
                }
                var grass = mesh.GetUnityComponent<PBDGrassBody>();
                bool initialised = grass != null && grass.IsPBDBodyDataInitialized;
                for (int i = 0; i < subMeshes; i++)
                {
                    var m = mesh.Materials[Mathf.Min(i, mesh.Materials.Count - 1)];
                    if (initialised && m != null && m.IsKeywordEnabled("PBD_GRASS"))
                    {
                        mpb.SetInt(ArgsOffset, num2);
                        mpb.SetFloat(PbdEnabledLocal, 1f);
                        cb.DrawMeshInstancedIndirect(mesh.Mesh, i, mv, pass, args, num, mpb);
                        drawn++;
                    }
                    num += 20;
                    num2 += 5;
                }
            }
            if (drawn > 0) ctx.ExecuteCommandBuffer(cb);
            if (!logged && drawn > 0) { logged = true; Main.Log("grass motion vector draws: " + drawn + " per frame"); }
        }
    }
}

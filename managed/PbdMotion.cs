using Owlcat.Runtime.Core.Physics.PositionBasedDynamics;
using UnityEngine;
using UnityEngine.Rendering;

namespace WotRUpscaler
{
    // Last frame's bone matrices of the game's GPU physics simulation (trees, bushes, tent cloth). The motion vector shader skins those
    // plants with this frame's matrices and with last frame's, which needs a copy of the previous ones: the simulation overwrites its buffer
    // every frame. Prepare binds the copy before the motion vector draw, Snapshot refreshes it after (the buffer then holds this frame's
    // matrices, which are next frame's "previous").
    public static class PbdMotion
    {
        static ComputeBuffer prev, dummy, prevParticles, prevMatrices;
        static int snapFrame = -10, particlesFrame = -10, matricesFrame = -10;
        public static ComputeBuffer PrevParticles { get { return prevParticles; } }
        static bool logged;
        static readonly int PrevId = Shader.PropertyToID("_WotRPbdPrevBindposes"), ValidId = Shader.PropertyToID("_WotRPbdPrevValid"),
            PrevParticlesId = Shader.PropertyToID("_WotRPbdPrevParticles"), PrevParticlesValidId = Shader.PropertyToID("_WotRPbdPrevParticlesValid"),
            PrevMatricesId = Shader.PropertyToID("_WotRPbdPrevBodyMatrices"), PrevMatricesValidId = Shader.PropertyToID("_WotRPbdPrevMatricesValid");

        static ComputeBuffer CurrentMatrices()
        {
            try
            {
                var g = PBD.GetGPUData();
                var b = g != null && g.BodyWorldToLocalMatricesSoA != null ? g.BodyWorldToLocalMatricesSoA.Buffer : null;
                return b != null && b.IsValid() ? b : null;
            }
            catch { return null; }
        }

        static ComputeBuffer CurrentParticles()
        {
            try
            {
                var g = PBD.GetGPUData();
                var b = g != null && g.ParticlesSoA != null ? g.ParticlesSoA.PositionBuffer : null;
                return b != null && b.IsValid() ? b : null;
            }
            catch { return null; }
        }

        static ComputeBuffer Current()
        {
            try
            {
                var g = PBD.GetGPUData();
                var b = g != null && g.SkinnedBodySoA != null ? g.SkinnedBodySoA.SimulatedBindposesBuffer : null;
                return b != null && b.IsValid() ? b : null;
            }
            catch { return null; }
        }

        public static void Prepare(CommandBuffer cb)
        {
            var cur = Current();
            bool ok = cur != null && prev != null && prev.IsValid() && prev.count == cur.count && Time.frameCount - snapFrame == 1;
            if (dummy == null) dummy = new ComputeBuffer(1, 64);
            cb.SetGlobalFloat(ValidId, ok ? 1f : 0f);
            cb.SetGlobalBuffer(PrevId, ok ? prev : dummy);
            var curP = CurrentParticles();
            bool okP = curP != null && prevParticles != null && prevParticles.IsValid() && prevParticles.count == curP.count && Time.frameCount - particlesFrame == 1;
            cb.SetGlobalFloat(PrevParticlesValidId, okP ? 1f : 0f);
            cb.SetGlobalBuffer(PrevParticlesId, okP ? prevParticles : dummy);
            var curM = CurrentMatrices();
            bool okM = curM != null && prevMatrices != null && prevMatrices.IsValid() && prevMatrices.count == curM.count && Time.frameCount - matricesFrame == 1;
            cb.SetGlobalFloat(PrevMatricesValidId, okM ? 1f : 0f);
            cb.SetGlobalBuffer(PrevMatricesId, okM ? prevMatrices : dummy);
        }

        public static void Snapshot(CommandBuffer cb)
        {
            SnapshotParticles(cb);
            SnapshotMatrices(cb);
            var cur = Current();
            if (cur == null) return;
            if (prev == null || !prev.IsValid() || prev.count != cur.count)
            {
                if (prev != null) prev.Release();
                prev = new ComputeBuffer(cur.count, cur.stride);
                snapFrame = -10;
                if (!logged) { logged = true; Main.Log("physics bone matrices: " + cur.count + " matrices, previous-frame copy created"); }
            }
            Dlss.QueueCopyBuffer(cb, prev.GetNativeBufferPtr(), cur.GetNativeBufferPtr());
            snapFrame = Time.frameCount;
        }

        // The per-body world to local matrices (cloth converts its particles with them).
        static void SnapshotMatrices(CommandBuffer cb)
        {
            var cur = CurrentMatrices();
            if (cur == null) return;
            if (prevMatrices == null || !prevMatrices.IsValid() || prevMatrices.count != cur.count)
            {
                if (prevMatrices != null) prevMatrices.Release();
                prevMatrices = new ComputeBuffer(cur.count, cur.stride);
            }
            Dlss.QueueCopyBuffer(cb, prevMatrices.GetNativeBufferPtr(), cur.GetNativeBufferPtr());
            matricesFrame = Time.frameCount;
        }

        // The simulated particle positions (grass blades are driven by them).
        static void SnapshotParticles(CommandBuffer cb)
        {
            var cur = CurrentParticles();
            if (cur == null) return;
            if (prevParticles == null || !prevParticles.IsValid() || prevParticles.count != cur.count)
            {
                if (prevParticles != null) prevParticles.Release();
                prevParticles = new ComputeBuffer(cur.count, cur.stride);
                if (!logged) Main.Log("physics particles: " + cur.count + ", previous-frame copy created");
            }
            Dlss.QueueCopyBuffer(cb, prevParticles.GetNativeBufferPtr(), cur.GetNativeBufferPtr());
            particlesFrame = Time.frameCount;
        }
    }
}

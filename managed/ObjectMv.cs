using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Owlcat.Runtime.Visual.RenderPipeline;

namespace WotRUpscaler
{
    // Per-object motion vectors for the characters. Unity already tracks previous-frame skinned positions for them (the game enables
    // skinnedMotionVectors on every character renderer), so drawing them with a motion vector shader gives exact per-vertex motion
    // without any skinning work of our own. The shader lives in the wotrupscaler asset bundle (built with the same Unity version as the game);
    // the result goes into a separate target that the native plugin merges into the camera motion vectors.
    public static class ObjectMv
    {
        static RenderTexture worldTarget, previewTarget;
        // The object motion of the camera being rendered (the character preview has its own, at its own size).
        public static RenderTexture Target { get { return Scaler.PreviewUpscaled ? previewTarget : worldTarget; } }
        public static string Status = "not started";
        static Material mat;
        static bool initTried, logged;
        static readonly ShaderTagId Tag = new ShaderTagId("GBuffer");
        static readonly int JitteredVP = Shader.PropertyToID("_WotRJitteredVP"), NonJitteredVP = Shader.PropertyToID("_WotRNonJitteredVP"),
            PreviousVP = Shader.PropertyToID("_WotRPreviousVP"), Size = Shader.PropertyToID("_WotRSize"), Debug = Shader.PropertyToID("_WotRDebug"),
            SceneDepth = Shader.PropertyToID("_WotRDepth");

        static bool Init()
        {
            if (mat != null) return true;
            if (initTried) return false;
            initTried = true;
            var sh = Bundle.Shader("Assets/Shaders/WotRObjectMV.shader");
            if (sh == null) { Status = Bundle.Error; return false; }
            mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            Status = "ready";
            Main.Log("object motion vector shader loaded");
            return true;
        }

        // Binds the object motion target (cleared to alpha 0) and hands the scene depth to the shader, which keeps only the pixels where a
        // character is the visible surface. Called before the draw.
        public static bool Prepare(CommandBuffer cb, int rw, int rh, RenderTexture depth)
        {
            if (!Init()) return false;
            ref RenderTexture target = ref (Scaler.PreviewUpscaled ? ref previewTarget : ref worldTarget);
            if (target == null || target.width != rw || target.height != rh)
            {
                if (target != null) target.Release();
                target = new RenderTexture(rw, rh, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) { name = "WotRUpscaler ObjMV", filterMode = FilterMode.Point };
                target.Create();
            }
            cb.SetGlobalMatrix(JitteredVP, Jitter.VPJittered);
            cb.SetGlobalMatrix(NonJitteredVP, Jitter.VPCurrent);
            cb.SetGlobalMatrix(PreviousVP, Jitter.VPPrevious);
            cb.SetGlobalVector(Size, new Vector4(rw, rh, 0f, 0f));
            cb.SetGlobalFloat(Debug, Main.S.debugCharMv ? 1f : 0f);
            cb.SetGlobalTexture(SceneDepth, depth);
            PbdMotion.Prepare(cb);
            cb.SetRenderTarget(new RenderTargetIdentifier(target));
            cb.ClearRenderTarget(false, true, new Color(0f, 0f, 0f, 0f));
            return true;
        }

        // The layer the game puts its characters on ("Unit"). Not every skinned mesh is a character: flags and banners are skinned too
        // and sit on the Default layer together with the ground, which must keep receiving decals.
        static int characterLayers = -1;
        static int CharacterLayers()
        {
            if (characterLayers >= 0) return characterLayers;
            int unit = LayerMask.NameToLayer("Unit");
            characterLayers = unit >= 0 ? 1 << unit : 0;
            Main.Log(unit >= 0 ? "character layer: " + unit + " (Unit)" : "no Unit layer: character silhouettes for the ground markers disabled");
            return characterLayers;
        }

        static readonly ShaderTagId GBufferTag = new ShaderTagId("GBuffer");

        // Character silhouettes at output resolution, un-jittered, clearing the "receive decals" stencil bit in the (upscaled) depth-stencil
        // buffer, so the ground markers stop exactly at the characters' edges as in a native render. Drawn with the characters' own material
        // pass (so the game's alpha cut-out applies: see-through cloak hems stay see-through), with the render state overridden to write
        // nothing but that stencil bit. proj: camera projection without jitter.
        public static void DrawCharacterMask(ScriptableRenderContext ctx, ref RenderingData rd, int depthId, Matrix4x4 proj)
        {
            int layers = CharacterLayers();
            if (layers == 0) return;
            var cam = rd.CameraData.Camera;
            var ss = new SortingSettings(cam) { criteria = SortingCriteria.None };
            var ds = new DrawingSettings(GBufferTag, ss) { perObjectData = PerObjectData.None, enableInstancing = true, enableDynamicBatching = false };
            var fs = new FilteringSettings(RenderQueueRange.opaque, layers);
            var state = new RenderStateBlock(RenderStateMask.Depth | RenderStateMask.Stencil | RenderStateMask.Blend)
            {
                depthState = new DepthState(false, CompareFunction.Always),
                stencilReference = 0,
                stencilState = new StencilState(true, 0xFF, 1, CompareFunction.Always, StencilOp.Replace, StencilOp.Keep, StencilOp.Keep),
                blendState = new BlendState { blendState0 = new RenderTargetBlendState((ColorWriteMask)0) }
            };
            // The silhouette is grown by a pixel or so (union of the mask drawn at small screen offsets): DLSS gives the character an
            // anti-aliased edge whose outermost pixels blend cloth with ground, and the marker must not be drawn over those either.
            float px = Mathf.Max(0f, Main.S.markerEdgeGapPx);
            int w = cam.pixelWidth, h = cam.pixelHeight;
            var offsets = px > 0f
                ? new[] { Vector2.zero, new Vector2(-px, -px), new Vector2(px, -px), new Vector2(-px, px), new Vector2(px, px) }
                : new[] { Vector2.zero };
            var c = new CommandBuffer { name = "WotRUpscaler character mask" };
            foreach (var o in offsets)
            {
                var p = proj;
                p[0, 2] += 2f * o.x / w;
                p[1, 2] += 2f * o.y / h;
                c.Clear();
                c.SetRenderTarget(new RenderTargetIdentifier(depthId));
                c.SetViewProjectionMatrices(cam.worldToCameraMatrix, p);
                ctx.ExecuteCommandBuffer(c);
                ctx.DrawRenderers(rd.CullResults, ref ds, ref fs, ref state);
            }
            c.Release();
        }

        public static void Draw(ScriptableRenderContext ctx, ref RenderingData rd)
        {
            var cam = rd.CameraData.Camera;
            var ss = new SortingSettings(cam) { criteria = SortingCriteria.None };
            var ds = new DrawingSettings(Tag, ss)
            {
                overrideMaterial = mat,
                overrideMaterialPassIndex = 0,
                perObjectData = PerObjectData.MotionVectors,
                enableInstancing = false,
                enableDynamicBatching = false
            };
            var fs = new FilteringSettings(RenderQueueRange.all, -1) { excludeMotionVectorObjects = false };
            ctx.DrawRenderers(rd.CullResults, ref ds, ref fs);
            if (!Scaler.PreviewUpscaled) GrassMv.Draw(ctx, mat);      // the grass of the world is not part of the character preview
            if (!logged) { logged = true; Main.Log("character motion vector draw issued"); }
        }
    }
}

// Per-object motion vectors and silhouettes of characters, for the WotR Upscaler mod.
// Drawn with ScriptableRenderContext.DrawRenderers using this material as the override. Unity supplies the previous-frame skinned
// position in TEXCOORD4 (the game enables skinnedMotionVectors on its character renderers) together with unity_MatrixPreviousM and
// unity_MotionVectorsParams (PerObjectData.MotionVectors, and the camera must request DepthTextureMode.MotionVectors).
// The camera matrices and the scene depth come from globals set by the mod.
Shader "Hidden/WotRUpscaler/ObjectMotionVectors"
{
    HLSLINCLUDE
    // Per-object data the engine fills in (same layout the scriptable pipelines use).
    cbuffer UnityPerDraw
    {
        float4x4 unity_ObjectToWorld;
        float4x4 unity_WorldToObject;
        float4 unity_LODFade;
        float4 unity_WorldTransformParams;
        float4 unity_LightData;
        float4 unity_LightIndices[2];
        float4 unity_ProbesOcclusion;
        float4 unity_SpecCube0_HDR;
        float4 unity_LightmapST;
        float4 unity_DynamicLightmapST;
        float4 unity_SHAr;
        float4 unity_SHAg;
        float4 unity_SHAb;
        float4 unity_SHBr;
        float4 unity_SHBg;
        float4 unity_SHBb;
        float4 unity_SHC;
        float4x4 unity_MatrixPreviousM;
        float4x4 unity_MatrixPreviousMI;
        // x: 1 when last-frame positions are available (skinned meshes), y: 0 when the object is forced to have no motion
        float4 unity_MotionVectorsParams;
    };

    float4x4 _WotRJitteredVP;
    float4x4 _WotRNonJitteredVP;
    float4x4 _WotRPreviousVP;
    float4 _WotRSize;           // render width, render height
    float _WotRDebug;           // 1: output the raw per-object parameters instead of motion, and do not discard anything
    Texture2D<float> _WotRDepth;  // scene depth at render resolution

    struct appdata
    {
        float3 vertex : POSITION;
        float3 oldPos : TEXCOORD4;
        float4 blendWeights : BLENDWEIGHTS;     // position based dynamics skinning (trees, bushes, tents), see PbdSkin
        uint4 blendIndices : BLENDINDICES;
    };

    // The game animates trees, bushes and tent cloth with a GPU physics simulation: every frame it writes one matrix per simulated bone into
    // a buffer, and the vertex shader skins each vertex with up to four of them. The same skinning is done here twice, with this frame's
    // buffer (the game's global) and last frame's (a copy the mod keeps), which gives the exact motion of every leaf.
    struct PbdBone { float4 c0, c1, c2, c3; };      // an affine matrix as four columns (xyz used), 64 bytes
    StructuredBuffer<PbdBone> _PbdBindposes;
    StructuredBuffer<PbdBone> _WotRPbdPrevBindposes;
    StructuredBuffer<int> _PbdSkinnedBodyBoneIndicesMap;
    float _PbdEnabledLocal, _PbdEnabledGlobal, _WotRPbdPrevValid;
    int _PbdBonesOffset, _PbdBoneIndicesOffset;
    float4 _PbdWeightMask;

    // Grass: the game draws its instanced detail meshes (grass blades) with DrawMeshInstancedIndirect. Each blade is a pair of simulated
    // physics particles (rest position and simulated position); the vertex shader bends the blade by how far the upper particle moved,
    // weighted by the square of the vertex height over the rest length. The same is evaluated here with this frame's particle positions
    // and with last frame's (a copy the mod keeps).
    struct GrassInstance { float4 m0, m1, m2, m3, n0, n1, n2, n3, q0, q1, q2; };    // 176 bytes: object to world and world to object columns, ..., particle index at byte 164
    StructuredBuffer<GrassInstance> _IndirectInstanceDataBuffer;
    StructuredBuffer<uint> _ArgsBuffer;
    StructuredBuffer<uint> _IsVisibleBuffer;
    StructuredBuffer<float3> _PbdParticlesBasePositionBuffer;
    StructuredBuffer<float3> _PbdParticlesPositionBuffer;
    StructuredBuffer<float3> _WotRPbdPrevParticles;
    float _WotRPbdPrevParticlesValid;
    int _ArgsOffset;

    float3 Affine(float4 c0, float4 c1, float4 c2, float4 c3, float3 p)
    {
        return c0.xyz * p.x + c1.xyz * p.y + c2.xyz * p.z + c3.xyz;
    }

    float3 GrassPosition(GrassInstance g, float3 vtx, bool previous)
    {
        float3 pos = vtx;
        if (_PbdEnabledLocal > 0.5 && _PbdEnabledGlobal > 0.5)
        {
            uint pi = asuint(g.q2.y);
            float3 p0 = Affine(g.n0, g.n1, g.n2, g.n3, _PbdParticlesBasePositionBuffer[pi]);
            float3 p1 = Affine(g.n0, g.n1, g.n2, g.n3, _PbdParticlesBasePositionBuffer[pi + 1]);
            float3 sim = previous ? _WotRPbdPrevParticles[pi + 1] : _PbdParticlesPositionBuffer[pi + 1];
            float3 q1 = Affine(g.n0, g.n1, g.n2, g.n3, sim);
            float k = vtx.y / length(p0 - p1);
            pos = vtx + k * k * (q1 - p1);
        }
        return pos;
    }

    // Set by the game per renderer (property block) for skinned bodies: not for the physics cloth or grass modes (no weight mask there).
    bool PbdSkinned()
    {
        return _PbdEnabledLocal > 0.5 && _PbdEnabledGlobal > 0.5 && dot(_PbdWeightMask, float4(1.0, 1.0, 1.0, 1.0)) > 0.5;
    }

    float3 PbdSkinCurrent(appdata v)
    {
        float4 w = v.blendWeights * _PbdWeightMask;
        float3 acc = float3(0.0, 0.0, 0.0);
        [unroll] for (int k = 0; k < 4; k++)
        {
            PbdBone m = _PbdBindposes[_PbdSkinnedBodyBoneIndicesMap[v.blendIndices[k] + _PbdBoneIndicesOffset] + _PbdBonesOffset];
            acc += w[k] * (m.c0.xyz * v.vertex.x + m.c1.xyz * v.vertex.y + m.c2.xyz * v.vertex.z + m.c3.xyz);
        }
        return acc;
    }

    float3 PbdSkinPrevious(appdata v)
    {
        float4 w = v.blendWeights * _PbdWeightMask;
        float3 acc = float3(0.0, 0.0, 0.0);
        [unroll] for (int k = 0; k < 4; k++)
        {
            PbdBone m = _WotRPbdPrevBindposes[_PbdSkinnedBodyBoneIndicesMap[v.blendIndices[k] + _PbdBoneIndicesOffset] + _PbdBonesOffset];
            acc += w[k] * (m.c0.xyz * v.vertex.x + m.c1.xyz * v.vertex.y + m.c2.xyz * v.vertex.z + m.c3.xyz);
        }
        return acc;
    }

    // Characters and what they carry: skinned meshes with previous-frame positions (x = 1), and rigid pieces (potions on a belt,
    // weapons, carried props) whose previous transform is valid and differs from the current one. Static scenery has identical
    // transforms; its motion is the camera motion, which the camera pass already provides.
    // Some objects (instanced or batched props, effects) carry a previous transform that is not a record of last frame at all (an identity
    // matrix, for instance): it differs from the current one by the object's whole distance from the world origin, which came out as
    // motion of thousands of pixels. Real per-frame motion is small, so a previous transform far from the current one is not trusted.
    bool PrevMatrixSane()
    {
        float4x4 pm = unity_MatrixPreviousM;
        float4x4 cm = unity_ObjectToWorld;
        float lin = 0.0;
        [unroll] for (int r = 0; r < 3; r++) lin += dot(abs(pm[r].xyz - cm[r].xyz), float3(1.0, 1.0, 1.0));
        float3 tr = float3(pm[0].w - cm[0].w, pm[1].w - cm[1].w, pm[2].w - cm[2].w);
        return lin < 1.5 && dot(tr, tr) < 16.0;
    }

    bool IsCharacterPart(out bool skinned)
    {
        skinned = unity_MotionVectorsParams.x > 0.5;
        float4x4 pm = unity_MatrixPreviousM;
        float4x4 cm = unity_ObjectToWorld;
        bool validPrev = abs(pm[3][3] - 1.0) < 1e-3 && abs(pm[3][0]) + abs(pm[3][1]) + abs(pm[3][2]) < 1e-3;
        float3 dt = abs(pm[0] - cm[0]).xyz + abs(pm[1] - cm[1]).xyz + abs(pm[2] - cm[2]).xyz;
        float dw = abs(pm[0].w - cm[0].w) + abs(pm[1].w - cm[1].w) + abs(pm[2].w - cm[2].w);
        bool rigidMoving = !skinned && validPrev && (dt.x + dt.y + dt.z + dw) > 1e-5 && PrevMatrixSane();
        return (skinned || rigidMoving) && unity_MotionVectorsParams.y > 0.5;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType" = "Opaque" }

        // Pass 0: motion vectors, in the same convention as the mod's camera motion vectors (previous minus current position, render
        // pixels, y pointing down). Alpha is 1 where a character is the visible surface.
        Pass
        {
            Name "ObjectMV"
            Tags { "LightMode" = "MotionVectors" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 cur : TEXCOORD0;
                float4 prev : TEXCOORD1;
                float4 dbg : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                bool pbd = PbdSkinned();
                float3 curPos = v.vertex, prevPos = v.vertex;
                if (pbd)
                {
                    curPos = PbdSkinCurrent(v);
                    prevPos = _WotRPbdPrevValid > 0.5 ? PbdSkinPrevious(v) : curPos;
                }
                float4 wp = mul(unity_ObjectToWorld, float4(curPos, 1.0));
                o.pos = mul(_WotRJitteredVP, wp);
                o.cur = mul(_WotRNonJitteredVP, wp);
                bool skinned;
                bool draw = IsCharacterPart(skinned) || pbd;
                float3 old = pbd ? prevPos : (skinned ? v.oldPos : v.vertex);
                // A skinned mesh with an untrustworthy previous transform keeps its skinning motion (old vertex position) only.
                float4x4 pmat = PrevMatrixSane() ? unity_MatrixPreviousM : unity_ObjectToWorld;
                o.prev = mul(_WotRPreviousVP, mul(pmat, float4(old, 1.0)));
                if (_WotRDebug < 0.5 && !draw)
                    o.pos = float4(2.0, 2.0, 2.0, 1.0);
                o.dbg = unity_MotionVectorsParams;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                if (_WotRDebug > 0.5) return float4(i.dbg.x, i.dbg.y, i.dbg.w, 1.0);
                // Keep only the pixels where this surface is what the scene actually shows: same depth within a small tolerance.
                // This handles occlusion and also the cut-out parts (hair, cloth edges) that the game's own shader discarded.
                float sceneZ = _WotRDepth.Load(int3(i.pos.xy, 0));
                if (abs(i.pos.z - sceneZ) > max(sceneZ * 0.002, 1e-6)) discard;
                float2 c = i.cur.xy / i.cur.w;
                float2 p = i.prev.xy / i.prev.w;
                float2 mv = (p - c) * 0.5 * float2(_WotRSize.x, -_WotRSize.y);
                return float4(mv, 0.0, 1.0);
            }
            ENDHLSL
        }

        // Pass 1: character silhouettes at output resolution with the un-jittered camera, clearing the "receive decals" stencil bit (1)
        // wherever a character covers the screen. Decals (selection circle, click marker) then stop exactly at the character's edge,
        // as in a native render, instead of at the blocky, jitter-dependent edge of the upscaled low-resolution stencil.
        // Depth-tested against the upscaled depth, pulled slightly towards the camera so the blocky depth edges do not cut it.
        Pass
        {
            Name "CharacterMask"
            Tags { "LightMode" = "MotionVectors" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            ColorMask 0
            Stencil
            {
                Ref 0
                WriteMask 1
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            struct v2f { float4 pos : SV_POSITION; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = mul(_WotRNonJitteredVP, mul(unity_ObjectToWorld, float4(v.vertex, 1.0)));
                o.pos.z *= 1.01;    // reversed depth: slightly closer
                bool skinned;
                if (!IsCharacterPart(skinned)) o.pos = float4(2.0, 2.0, 2.0, 1.0);
                return o;
            }

            Texture2D<uint2> _WotRStencil;  // low-resolution stencil of the real render
            float4 _WotRSrcSize;            // render width, height
            float4 _WotRDstSize;            // output width, height
            float4 _WotRJitter;             // projection jitter of this frame (render pixels)

            float4 frag(v2f i) : SV_Target
            {
                // The mesh is drawn without the game's alpha cut-out, so transparent parts (ragged cloak hems, gaps) would count as solid.
                // The real render knows better: only clear where at least one of the four nearest low-resolution samples is not ground.
                float2 c = i.pos.xy * (_WotRSrcSize.xy / _WotRDstSize.xy) - _WotRJitter.xy - 0.5;
                int2 i0 = int2(floor(c));
                int2 mx = int2(_WotRSrcSize.xy) - 1;
                uint all1 = _WotRStencil.Load(int3(clamp(i0, 0, mx), 0)).g & _WotRStencil.Load(int3(clamp(i0 + int2(1, 0), 0, mx), 0)).g
                          & _WotRStencil.Load(int3(clamp(i0 + int2(0, 1), 0, mx), 0)).g & _WotRStencil.Load(int3(clamp(i0 + int2(1, 1), 0, mx), 0)).g;
                if (all1 & 1) discard;
                return float4(0, 0, 0, 0);
            }
            ENDHLSL
        }

        // Pass 2: motion vectors of the instanced physics grass (drawn by the mod with DrawMeshInstancedIndirect, like the game's own pass).
        Pass
        {
            Name "GrassMV"
            Tags { "LightMode" = "MotionVectors" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            struct gin { float3 vertex : POSITION; uint iid : SV_InstanceID; };
            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 cur : TEXCOORD0;
                float4 prev : TEXCOORD1;
            };

            v2f vert(gin v)
            {
                v2f o;
                uint vis = _IsVisibleBuffer[_ArgsBuffer[_ArgsOffset] + v.iid];
                GrassInstance g = _IndirectInstanceDataBuffer[vis];
                float3 curPos = GrassPosition(g, v.vertex, false);
                float3 prevPos = _WotRPbdPrevParticlesValid > 0.5 ? GrassPosition(g, v.vertex, true) : curPos;
                float4 wp = float4(Affine(g.m0, g.m1, g.m2, g.m3, curPos), 1.0);
                float4 wpPrev = float4(Affine(g.m0, g.m1, g.m2, g.m3, prevPos), 1.0);
                o.pos = mul(_WotRJitteredVP, wp);
                o.cur = mul(_WotRNonJitteredVP, wp);
                o.prev = mul(_WotRPreviousVP, wpPrev);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float sceneZ = _WotRDepth.Load(int3(i.pos.xy, 0));
                if (abs(i.pos.z - sceneZ) > max(sceneZ * 0.002, 1e-6)) discard;
                float2 c = i.cur.xy / i.cur.w;
                float2 p = i.prev.xy / i.prev.w;
                float2 mv = (p - c) * 0.5 * float2(_WotRSize.x, -_WotRSize.y);
                return float4(mv, 0.0, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

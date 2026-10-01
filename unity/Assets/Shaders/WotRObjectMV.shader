// Per-object motion vectors and silhouettes of characters, for the WotR DLSS mod.
// Drawn with ScriptableRenderContext.DrawRenderers using this material as the override. Unity supplies the previous-frame skinned
// position in TEXCOORD4 (the game enables skinnedMotionVectors on its character renderers) together with unity_MatrixPreviousM and
// unity_MotionVectorsParams (PerObjectData.MotionVectors, and the camera must request DepthTextureMode.MotionVectors).
// The camera matrices and the scene depth come from globals set by the mod.
Shader "Hidden/WotRDLSS/ObjectMotionVectors"
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
    };

    // Characters and what they carry: skinned meshes with previous-frame positions (x = 1), and rigid pieces (potions on a belt,
    // weapons, carried props) whose previous transform is valid and differs from the current one. Static scenery has identical
    // transforms; its motion is the camera motion, which the camera pass already provides.
    bool IsCharacterPart(out bool skinned)
    {
        skinned = unity_MotionVectorsParams.x > 0.5;
        float4x4 pm = unity_MatrixPreviousM;
        float4x4 cm = unity_ObjectToWorld;
        bool validPrev = abs(pm[3][3] - 1.0) < 1e-3 && abs(pm[3][0]) + abs(pm[3][1]) + abs(pm[3][2]) < 1e-3;
        float3 dt = abs(pm[0] - cm[0]).xyz + abs(pm[1] - cm[1]).xyz + abs(pm[2] - cm[2]).xyz;
        float dw = abs(pm[0].w - cm[0].w) + abs(pm[1].w - cm[1].w) + abs(pm[2].w - cm[2].w);
        bool rigidMoving = !skinned && validPrev && (dt.x + dt.y + dt.z + dw) > 1e-5;
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
                float4 wp = mul(unity_ObjectToWorld, float4(v.vertex, 1.0));
                o.pos = mul(_WotRJitteredVP, wp);
                o.cur = mul(_WotRNonJitteredVP, wp);
                bool skinned;
                bool draw = IsCharacterPart(skinned);
                float3 old = skinned ? v.oldPos : v.vertex;
                o.prev = mul(_WotRPreviousVP, mul(unity_MatrixPreviousM, float4(old, 1.0)));
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
    }
    Fallback Off
}

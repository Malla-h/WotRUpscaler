// Nearest-neighbour, jitter-compensated upscale of the scene depth, stencil and normals for the WotR Upscaler mod.
// After the 3D scene has been upscaled to output resolution, the rest of the main camera's render chain (decals for the selection circle and
// the click marker, depth of field, ...) expects buffers of the same size as the colour buffer, and works with the un-jittered camera.
// Each output pixel reads the low-resolution pixel that holds what the un-jittered camera sees at that spot: the projection jitter moves
// the image content by minus the jitter (render pixels), so the source position is (output position * scale) - jitter.
// The game's decal shaders test the stencil bit "receive decals" that the geometry pass writes next to the depth, so the stencil is carried
// over too, one bit per draw.
Shader "Hidden/WotRUpscaler/DepthUpscale"
{
    Properties
    {
        [HideInInspector] _Ref ("Stencil bit", Float) = 1
    }

    HLSLINCLUDE
    float4 _WotRSrcSize;    // source (render) width, height
    float4 _WotRDstSize;    // destination (output) width, height
    float4 _WotRJitter;     // projection jitter of this frame in render pixels (xy), 0 when none

    struct v2f { float4 pos : SV_POSITION; };

    v2f vert(uint id : SV_VertexID)
    {
        v2f o;
        float2 p = float2((id << 1) & 2, id & 2);
        o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
        return o;
    }

    // The same for a pixel rectangle (x0, y0, x1, y1 in output pixels, rows counted as the rasteriser does): the passes that only matter
    // around the ground markers draw just that area.
    float4 _WotRRect;
    v2f vertRect(uint id : SV_VertexID)
    {
        static const float2 corner[6] = { float2(0, 0), float2(1, 0), float2(0, 1), float2(0, 1), float2(1, 0), float2(1, 1) };
        float2 p = lerp(_WotRRect.xy, _WotRRect.zw, corner[id]);
        v2f o;
        o.pos = float4(p.x / _WotRDstSize.x * 2.0 - 1.0, 1.0 - p.y / _WotRDstSize.y * 2.0, 0.0, 1.0);
        return o;
    }

    int2 SourcePixel(float2 pos)
    {
        float2 s = pos * (_WotRSrcSize.xy / _WotRDstSize.xy) - _WotRJitter.xy;
        return clamp(int2(floor(s)), int2(0, 0), int2(_WotRSrcSize.xy) - 1);
    }

    Texture2D<float> _WotRDepth;      // low-resolution scene depth (source of every pass)
    Texture2D<float4> _WotRGuideLow;  // low-resolution colour the depth belongs to (the DLSS input)
    Texture2D<float4> _WotRGuideFull; // full-resolution colour (the DLSS output)
    float _WotRGuided;                // 1 when the two guide images are valid for this frame
    Texture2D<float2> _WotRMotion;    // motion vectors (previous minus current, render pixels)
    Texture2D<float> _WotRDepthHist;  // full-resolution depth copy of the previous frame
    Texture2D<float> _WotRStillHist;  // 1 where the pixel was still in the previous frame (so its history is a valid, settled value)
    float4 _WotRTemporal;             // x: history valid, y: relative depth tolerance of a held value

    // A pixel may keep last frame's values only if nothing moves around it: none of the 3x3 low-resolution motion vectors around it
    // is above a hundredth of a pixel (characters sway slightly even when idle, which must refresh the ground next to them).
    bool StillNeighbourhood(float2 pos)
    {
        int2 c = int2(floor(pos * (_WotRSrcSize.xy / _WotRDstSize.xy)));
        int2 mx = int2(_WotRSrcSize.xy) - 1;
        [unroll] for (int y = -1; y <= 1; y++)
            [unroll] for (int x = -1; x <= 1; x++)
            {
                float2 mv = _WotRMotion.Load(int3(clamp(c + int2(x, y), 0, mx), 0));
                if (dot(mv, mv) > 1e-4) return false;
            }
        return true;
    }

    // A pixel may keep last frame's value only if it was already still in the previous frame (so that value is a settled sample of what
    // is there now, not what a moving object left behind when it uncovered this pixel) and is still now.
    bool CanHold(float2 pos)
    {
        return _WotRTemporal.x >= 0.5 && StillNeighbourhood(pos) && _WotRStillHist.Load(int3(int2(pos), 0)) > 0.5;
    }

    // Which low-resolution sample stands for this output pixel. On a smooth surface: the nearest one (and depth is interpolated, see
    // SampleDepth). At an object edge: always the closest surface among the four neighbours, so depth, stencil and normals agree and the
    // edge does not flip between foreground and background as the jitter changes from frame to frame.
    int2 ChooseSource(float2 pos, out bool smooth, out float4 d, out float2 f, out int2 i0)
    {
        float2 c = pos * (_WotRSrcSize.xy / _WotRDstSize.xy) - _WotRJitter.xy - 0.5;
        i0 = int2(floor(c));
        f = c - i0;
        int2 mx = int2(_WotRSrcSize.xy) - 1;
        d.x = _WotRDepth.Load(int3(clamp(i0, 0, mx), 0));
        d.y = _WotRDepth.Load(int3(clamp(i0 + int2(1, 0), 0, mx), 0));
        d.z = _WotRDepth.Load(int3(clamp(i0 + int2(0, 1), 0, mx), 0));
        d.w = _WotRDepth.Load(int3(clamp(i0 + int2(1, 1), 0, mx), 0));
        float lo = min(min(d.x, d.y), min(d.z, d.w)), hi = max(max(d.x, d.y), max(d.z, d.w));
        smooth = hi - lo <= 0.02 * hi;
        if (smooth) return SourcePixel(pos);
        int2 o = int2(0, 0);
        if (_WotRGuided > 0.5)
        {
            // Guided by the upscaled image: take the neighbour whose colour matches the full-resolution output at this pixel, so depth,
            // stencil and normals follow the visible (sharp, stable) edge instead of the blocky low-resolution one.
            float3 t = log2(1.0 + max(_WotRGuideFull.Load(int3(int2(pos), 0)).rgb, 0.0));
            float best = 1e9;
            [unroll] for (int k = 0; k < 4; k++)
            {
                int2 oo = int2(k & 1, k >> 1);
                float3 cl = log2(1.0 + max(_WotRGuideLow.Load(int3(clamp(i0 + oo, 0, mx), 0)).rgb, 0.0));
                float3 dd = abs(cl - t);
                float e = dd.r + dd.g + dd.b;
                if (e < best) { best = e; o = oo; }
            }
        }
        else
        {
            // No upscaled image to go by: always the closest surface (reversed depth: larger is closer).
            float best = d.x;
            if (d.y > best) { best = d.y; o = int2(1, 0); }
            if (d.z > best) { best = d.z; o = int2(0, 1); }
            if (d.w > best) { best = d.w; o = int2(1, 1); }
        }
        return clamp(i0 + o, 0, mx);
    }

    int2 ChooseSource(float2 pos)
    {
        bool smooth; float4 d; float2 f; int2 i0;
        return ChooseSource(pos, smooth, d, f, i0);
    }

    // Depth at the exact un-jittered position: bilinear on smooth surfaces (exact for planes, so the ground under decals stays still),
    // the chosen closest sample at edges.
    float SampleDepth(float2 pos)
    {
        bool smooth; float4 d; float2 f; int2 i0;
        int2 p = ChooseSource(pos, smooth, d, f, i0);
        if (smooth) return lerp(lerp(d.x, d.y, f.x), lerp(d.z, d.w, f.x), f.y);
        return _WotRDepth.Load(int3(p, 0));
    }
    ENDHLSL

    SubShader
    {
        Cull Off
        ZTest Always

        // Pass 0: write the depth buffer (depth-only target).
        Pass
        {
            Name "Depth"
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float frag(v2f i) : SV_Depth { return SampleDepth(i.pos.xy); }
            ENDHLSL
        }

        // Pass 1: the same depth as a colour value (single-channel float target).
        Pass
        {
            Name "DepthColor"
            ZWrite Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target { return float4(SampleDepth(i.pos.xy), 0.0, 0.0, 0.0); }
            ENDHLSL
        }

        // Pass 2: copy one stencil bit (_Ref, set per material instance) wherever the low-resolution stencil has it set.
        Pass
        {
            Name "Stencil"
            ZWrite Off
            ColorMask 0
            Stencil
            {
                Ref [_Ref]
                WriteMask [_Ref]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertRect
            #pragma fragment frag
            Texture2D<uint2> _WotRStencil;
            float _Ref;
            float4 frag(v2f i) : SV_Target
            {
                uint bit = (uint)(_Ref + 0.5);
                uint s;
                if (bit == 1)
                {
                    // "Receive decals": set if any of the four neighbours has it. Characters, which do not receive decals, shrink here and
                    // get their exact full-resolution silhouette from the character mask pass drawn afterwards.
                    float2 c = i.pos.xy * (_WotRSrcSize.xy / _WotRDstSize.xy) - _WotRJitter.xy - 0.5;
                    int2 i0 = int2(floor(c));
                    int2 mx = int2(_WotRSrcSize.xy) - 1;
                    s = _WotRStencil.Load(int3(clamp(i0, 0, mx), 0)).g | _WotRStencil.Load(int3(clamp(i0 + int2(1, 0), 0, mx), 0)).g
                      | _WotRStencil.Load(int3(clamp(i0 + int2(0, 1), 0, mx), 0)).g | _WotRStencil.Load(int3(clamp(i0 + int2(1, 1), 0, mx), 0)).g;
                }
                else s = _WotRStencil.Load(int3(ChooseSource(i.pos.xy), 0)).g;
                if ((s & bit) == 0) discard;
                return float4(0, 0, 0, 0);
            }
            ENDHLSL
        }

        // Pass 3: copy a colour buffer (for example the G-buffer normals) unchanged.
        Pass
        {
            Name "Color"
            ZWrite Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            Texture2D<float4> _WotRSrcColor;
            float4 frag(v2f i) : SV_Target { return _WotRSrcColor.Load(int3(ChooseSource(i.pos.xy), 0)); }
            ENDHLSL
        }

        // Pass 4: G-buffer normals, held still. The low-resolution normals change with every jitter step (on normal-mapped surfaces the
        // samples land on different detail), which made the decals that read them shimmer. The buffer is encoded (values must not be
        // averaged), so a still pixel (no motion, same depth as in the previous frame) keeps exactly last frame's value, and everything
        // else takes the current sample.
        Pass
        {
            Name "NormalsHold"
            ZWrite Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertRect
            #pragma fragment frag
            Texture2D<float4> _WotRSrcColor;     // low-resolution normals of this frame
            Texture2D<float4> _WotRNormalsHist;  // output of the previous frame
            Texture2D<float> _WotRDepthFull;     // full-resolution depth of this frame

            float4 frag(v2f i) : SV_Target
            {
                float4 cur = _WotRSrcColor.Load(int3(ChooseSource(i.pos.xy), 0));
                if (!CanHold(i.pos.xy)) return cur;
                int2 p = int2(i.pos.xy);
                float dCur = _WotRDepthFull.Load(int3(p, 0));
                float dPrev = _WotRDepthHist.Load(int3(p, 0));
                if (abs(dCur - dPrev) > _WotRTemporal.y * max(dCur, dPrev)) return cur;
                return _WotRNormalsHist.Load(int3(p, 0));
            }
            ENDHLSL
        }

        // Pass 5: the depth as a colour value (like pass 1), held still. A still pixel (no motion) keeps exactly last frame's depth as long as
        // that value is still plausible here, i.e. matches the current value or one of the four low-resolution depth samples around the pixel;
        // when a surface really changes (something moved away and uncovered the ground) it does not, and the current value is used.
        Pass
        {
            Name "DepthHold"
            ZWrite Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertRect
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                bool smooth; float4 d; float2 f; int2 i0;
                int2 p = ChooseSource(i.pos.xy, smooth, d, f, i0);
                float dNew = smooth ? lerp(lerp(d.x, d.y, f.x), lerp(d.z, d.w, f.x), f.y) : _WotRDepth.Load(int3(p, 0));
                if (!CanHold(i.pos.xy)) return float4(dNew, 0, 0, 0);
                float dPrev = _WotRDepthHist.Load(int3(int2(i.pos.xy), 0));
                // Still plausible here: close to the current value, or to one of the four samples (an edge choice that flipped).
                float tol = _WotRTemporal.y * max(dPrev, dNew);
                bool keep = abs(dPrev - dNew) <= tol || abs(dPrev - d.x) <= tol || abs(dPrev - d.y) <= tol || abs(dPrev - d.z) <= tol || abs(dPrev - d.w) <= tol;
                return float4(keep ? dPrev : dNew, 0, 0, 0);
            }
            ENDHLSL
        }
        // Pass 6: which pixels are still this frame (single-channel target), for the hold passes of the next frame. The first frame a pixel is
        // still it takes the fresh value (it may just have been uncovered by something that moved away); it is held from the next one on.
        Pass
        {
            Name "StillFlag"
            ZWrite Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vertRect
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target { return (_WotRTemporal.x >= 0.5 && StillNeighbourhood(i.pos.xy)) ? 1.0 : 0.0; }
            ENDHLSL
        }
        // Pass 7: the full-resolution depth in one pass: written to the depth buffer and to the sampled copy (single-channel colour target).
        Pass
        {
            Name "DepthBoth"
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            struct PSOut { float4 c : SV_Target; float d : SV_Depth; };
            PSOut frag(v2f i)
            {
                PSOut o;
                float d = SampleDepth(i.pos.xy);
                o.c = float4(d, 0, 0, 0);
                o.d = d;
                return o;
            }
            ENDHLSL
        }
    }
    Fallback Off
}

// Temporal anti-aliasing and upscaling for the WotR Upscaler mod (works on any graphics card).
//
// The resolve step is built on Playdead's "Temporal Reprojection Anti-Aliasing in INSIDE" (https://github.com/playdeadgames/temporal):
// clipping the reprojected history to the colour neighbourhood of the current frame in YCoCg space, the closest-fragment motion vector, and
// the luminance based feedback weight. Playdead's code is under the MIT License:
//
//   Copyright (c) <2015> <Playdead>
//   Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the
//   "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish,
//   distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
//   the following conditions: The above copyright notice and this permission notice shall be included in all copies or substantial portions
//   of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
//   WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS
//   BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
//   CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// Changes for this mod: the history lives at output resolution and the current frame at render resolution, so every output pixel
// gathers the jittered low-resolution samples around it with a Gaussian weight (this is what makes it an upscaler); colours are compressed
// (x / (1 + luma)) before they are compared and blended, so bright HDR values do not flicker; the history is not clamped to 0..1.
Shader "Hidden/WotRUpscaler/Taa"
{
    HLSLINCLUDE
    float4 _WotRTaaSrcSize;      // render width, height
    float4 _WotRTaaDstSize;      // output width, height
    float4 _WotRTaaJitter;       // jitter of this frame in render pixels, as the camera projection was shifted (xy)
    float4 _WotRTaaParams;       // x: history valid (1) or not (0), y: feedback minimum, z: feedback maximum, w: sharpness
    Texture2D<float4> _WotRTaaColor;     // render resolution scene colour of this frame
    Texture2D<float> _WotRTaaDepth;      // render resolution depth of this frame (reversed: larger is nearer)
    Texture2D<float2> _WotRTaaMotion;    // render resolution motion vectors (previous minus current, render pixels, rows from the top)
    Texture2D<float4> _WotRTaaHistory;   // output resolution result of the previous frame
    SamplerState sampler_linear_clamp;

    struct v2f { float4 pos : SV_POSITION; };
    v2f vert(uint id : SV_VertexID)
    {
        v2f o;
        float2 p = float2((id << 1) & 2, id & 2);
        o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);
        return o;
    }

    // Colour space: YCoCg of the luma-compressed colour (the compression is undone before anything is stored or shown).
    float3 Compress(float3 c) { return c / (1.0 + max(max(c.r, c.g), c.b)); }
    float3 Decompress(float3 c) { return c / max(1.0 - max(max(c.r, c.g), c.b), 1e-4); }
    float3 RGB_YCoCg(float3 c) { return float3(c.x * 0.25 + c.y * 0.5 + c.z * 0.25, c.x * 0.5 - c.z * 0.5, -c.x * 0.25 + c.y * 0.5 - c.z * 0.25); }
    float3 YCoCg_RGB(float3 c) { return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z); }
    float3 ToWork(float3 rgb) { return RGB_YCoCg(Compress(max(rgb, 0.0))); }
    float3 FromWork(float3 w) { return Decompress(max(YCoCg_RGB(w), 0.0)); }

    // Playdead's clip towards the box centre: the history is pulled to the surface of the box along the line to its centre.
    float3 ClipAabb(float3 bmin, float3 bmax, float3 q)
    {
        float3 centre = 0.5 * (bmax + bmin);
        float3 ext = 0.5 * (bmax - bmin) + 1e-6;
        float3 v = q - centre;
        float3 a = abs(v / ext);
        float m = max(a.x, max(a.y, a.z));
        return m > 1.0 ? centre + v / m : q;
    }

    // The history, sampled with a Catmull-Rom filter (5 bilinear taps) so that it does not blur a little more every frame.
    float4 SampleHistory(float2 uv)
    {
        float2 size = _WotRTaaDstSize.xy;
        float2 pos = uv * size;
        float2 centre = floor(pos - 0.5) + 0.5;
        float2 f = pos - centre;
        float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
        float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
        float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
        float2 w3 = f * f * (-0.5 + 0.5 * f);
        float2 w12 = w1 + w2;
        float2 t12 = w2 / w12;
        float2 uv0 = (centre - 1.0) / size, uv3 = (centre + 2.0) / size, uv12 = (centre + t12) / size;
        float4 r = 0.0;
        r += _WotRTaaHistory.SampleLevel(sampler_linear_clamp, float2(uv12.x, uv0.y), 0) * (w12.x * w0.y);
        r += _WotRTaaHistory.SampleLevel(sampler_linear_clamp, float2(uv0.x, uv12.y), 0) * (w0.x * w12.y);
        r += _WotRTaaHistory.SampleLevel(sampler_linear_clamp, uv12, 0) * (w12.x * w12.y);
        r += _WotRTaaHistory.SampleLevel(sampler_linear_clamp, float2(uv3.x, uv12.y), 0) * (w3.x * w12.y);
        r += _WotRTaaHistory.SampleLevel(sampler_linear_clamp, float2(uv12.x, uv3.y), 0) * (w12.x * w3.y);
        return max(r, 0.0);
    }

    // History and output: rgb is the colour, a is how many frames of history are behind it (capped), kept for the sharpening and the weight.
    float4 Resolve(float2 pixel)
    {
        float2 srcSize = _WotRTaaSrcSize.xy;
        // The un-jittered position of this output pixel in render pixels. A render pixel i holds the scene at i + 0.5 + jitter (the same
        // relation the depth upscale uses: the projection jitter moves the image content by minus the jitter).
        float2 lrPos = (pixel + 0.5) * (srcSize / _WotRTaaDstSize.xy);
        float2 jit = _WotRTaaJitter.xy;
        int2 base = int2(floor(lrPos - jit));
        float scaleUp = max(srcSize.x / _WotRTaaDstSize.x, 1e-3);

        // Gather the 3x3 render pixels around that spot: Gaussian weighted colour (the current frame's contribution), their box (for the clip),
        // and the closest of them (its motion vector is the one to follow: it belongs to the surface in front).
        float3 acc = 0.0, wsum3 = 0.0;
        float wsum = 0.0;
        float3 cmin = 1e9, cmax = -1e9, cavg = 0.0;
        float nearest = -1.0; int2 nearestPx = base;
        [unroll] for (int y = -1; y <= 1; y++)
        [unroll] for (int x = -1; x <= 1; x++)
        {
            int2 p = clamp(base + int2(x, y), int2(0, 0), int2(srcSize) - 1);
            float3 c = ToWork(_WotRTaaColor.Load(int3(p, 0)).rgb);
            float2 d = (float2(p) + 0.5 + jit) - lrPos;
            // The kernel is about one render pixel wide: a render pixel counts where its centre is close to the output pixel's centre.
            float w = exp(-2.29 * dot(d, d));
            acc += c * w; wsum += w;
            cmin = min(cmin, c); cmax = max(cmax, c); cavg += c;
            float dz = _WotRTaaDepth.Load(int3(p, 0));
            if (dz > nearest) { nearest = dz; nearestPx = p; }
        }
        float3 cur = acc / max(wsum, 1e-5);
        cavg /= 9.0;

        float2 mv = _WotRTaaMotion.Load(int3(nearestPx, 0));
        if (!all(isfinite(mv))) mv = 0.0;
        float2 prevUv = (lrPos + mv) / srcSize;
        bool inside = all(prevUv > 0.0) && all(prevUv < 1.0);
        float3 outWork = cur;
        float histAge = 1.0;
        if (_WotRTaaParams.x > 0.5 && inside)
        {
            float4 h = SampleHistory(prevUv);
            float3 hw = ToWork(h.rgb);
            // Clip to the colour box of the neighbourhood (variance box: mean plus or minus 1.25 standard deviations, within min/max).
            float3 mean = cavg;
            float3 var3 = 0.0;
            [unroll] for (int y2 = -1; y2 <= 1; y2++)
            [unroll] for (int x2 = -1; x2 <= 1; x2++)
            {
                int2 p = clamp(base + int2(x2, y2), int2(0, 0), int2(srcSize) - 1);
                float3 c = ToWork(_WotRTaaColor.Load(int3(p, 0)).rgb);
                var3 += (c - mean) * (c - mean);
            }
            float3 sd = sqrt(var3 / 9.0);
            float3 bmin = max(cmin, mean - 1.25 * sd), bmax = min(cmax, mean + 1.25 * sd);
            // Chroma is allowed less room than luma: colour fringes are what looks wrong first (as in Playdead's shader).
            float chromaExtent = 0.25 * 0.5 * (bmax.x - bmin.x);
            bmin.yz = cur.yz - chromaExtent; bmax.yz = cur.yz + chromaExtent;
            hw = ClipAabb(bmin, bmax, hw);

            // Feedback from the unbiased luminance difference (T. Lottes, as in Playdead's shader).
            float lum0 = cur.x, lum1 = hw.x;
            float diff = abs(lum0 - lum1) / max(lum0, max(lum1, 0.2));
            float wgt = 1.0 - diff;
            float k = lerp(_WotRTaaParams.y, _WotRTaaParams.z, wgt * wgt);
            // Upscaling gives each frame only a part of the samples an output pixel needs: lean on the history more, in proportion.
            k = max(k, 1.0 - (1.0 - k) * scaleUp);
            // A short history (just after a reset or an uncovered area) is averaged, not exponentially weighted.
            k = min(k, h.a / (h.a + 1.0));
            outWork = lerp(cur, hw, k);
            histAge = min(h.a + 1.0, 64.0);
        }
        float3 rgb = FromWork(outWork);
        return float4(rgb, histAge);
    }
    ENDHLSL

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // Pass 0: the resolve, written to the history target (half precision, with the age in alpha).
        Pass
        {
            Name "Resolve"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target { return Resolve(floor(i.pos.xy)); }
            ENDHLSL
        }
        // Pass 1: the history target copied into the camera's colour format, with a little sharpening (a bilinear upscale softens slightly).
        Pass
        {
            Name "Present"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target
            {
                int2 p = int2(i.pos.xy);
                float3 c = _WotRTaaHistory.Load(int3(p, 0)).rgb;
                float s = _WotRTaaParams.w;
                if (s > 0.0)
                {
                    float3 n = _WotRTaaHistory.Load(int3(p + int2(1, 0), 0)).rgb + _WotRTaaHistory.Load(int3(p - int2(1, 0), 0)).rgb
                             + _WotRTaaHistory.Load(int3(p + int2(0, 1), 0)).rgb + _WotRTaaHistory.Load(int3(p - int2(0, 1), 0)).rgb;
                    float3 cw = ToWork(c), nw = ToWork(n * 0.25);
                    c = FromWork(cw + (cw - nw) * s);
                }
                return float4(max(c, 0.0), 1.0);
            }
            ENDHLSL
        }
        // Pass 2: Simple scaling. The render-resolution colour stretched to the output with bilinear filtering (the game's own post-processing
        // target is point filtered, which would make a plain copy blocky).
        Pass
        {
            Name "Stretch"
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            float4 frag(v2f i) : SV_Target { return _WotRTaaColor.SampleLevel(sampler_linear_clamp, i.pos.xy / _WotRTaaDstSize.xy, 0); }
            ENDHLSL
        }
    }
    Fallback Off
}

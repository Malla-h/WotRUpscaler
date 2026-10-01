// WotRDLSS: minimal NGX DLSS (D3D11) bridge plus a depth-to-motion-vector compute pass, for Unity 2020.3 (Pathfinder: WotR). Loaded with LoadLibrary from the mod folder.
// Everything that touches the D3D11 context runs from Unity plugin events on the render thread.
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <cstdio>
#include <cstdarg>
#include <cwchar>
#include <cmath>
#include <atomic>

#include "nvsdk_ngx.h"
#include "nvsdk_ngx_defs.h"
#include "nvsdk_ngx_helpers.h"
#include "nvsdk_ngx_helpers_d3d.h"

#define EXPORT extern "C" __declspec(dllexport)

// ---- shared with C# (must match layout in Dlss.cs) ----
struct CreateData
{
    void* anyTexture;           // ID3D11Texture2D* from Unity, only used to reach the device
    int   renderW, renderH, outW, outH;
    int   quality;              // NVSDK_NGX_PerfQuality_Value
    int   flags;                // NVSDK_NGX_DLSS_Feature_Flags
    int   preset;               // NVSDK_NGX_DLSS_Hint_Render_Preset, 0 = default
    wchar_t dir[260];           // folder holding nvngx_dlss.dll, also used as NGX data path
};

struct EvalData
{
    void* color;                // ID3D11Texture2D*, render resolution
    void* depth;
    void* motion;
    void* output;               // UAV-capable, output resolution
    float jitterX, jitterY;     // render-pixel units
    float mvScaleX, mvScaleY;
    int   reset;
    int   renderW, renderH;
    float sharpness;
    float preExposure;
    float frameTimeMs;          // frame time in milliseconds, helps DLSS judge how fast things move
};

struct MvData
{
    void* depth;                // ID3D11Texture2D*, depth buffer copy at render resolution
    void* mv;                   // ID3D11Texture2D*, R16G16_FLOAT with UAV, render resolution
    int   w, h;
    float invVPj[16];           // inverse of the jittered view-projection the depth was rendered with (row-major, column-vector convention)
    float vpCur[16];            // un-jittered view-projection of this frame
    float vpPrev[16];           // un-jittered view-projection of the previous frame
};

struct CompData
{
    void* obj;                  // ID3D11Texture2D*, per-object motion (Unity convention) or a large negative x where nothing was drawn
    void* mv;                   // ID3D11Texture2D*, the camera motion vectors, overwritten where an object was drawn
    int   w, h;
    float scaleX, scaleY;       // converts the object motion to render-pixel units in DLSS's convention
};

// ---- state ----
static ID3D11Device*        g_device = nullptr;
static ID3D11DeviceContext* g_ctx = nullptr;
static NVSDK_NGX_Parameter* g_params = nullptr;
static NVSDK_NGX_Handle*    g_feature = nullptr;
static bool                 g_ngxInit = false;
static std::atomic<int>     g_status(0);    // 0 idle, 1 ready, <0 failure
static std::atomic<int>     g_evalCount(0);
static std::atomic<unsigned> g_lastResult(0);
static std::atomic<unsigned long long> g_evalUs(0), g_evalTimed(0);   // GPU time spent in DLSS evaluate, from timestamp queries

struct TsSet { ID3D11Query* disjoint = nullptr; ID3D11Query* start = nullptr; ID3D11Query* end = nullptr; bool inflight = false; };
static TsSet g_ts[4];
static bool g_tsReady = false;
static wchar_t              g_logPath[260] = L"";
static int                  g_cw = 0, g_ch = 0, g_ow = 0, g_oh = 0;

static void Log(const char* fmt, ...)
{
    if (!g_logPath[0]) return;
    FILE* f = nullptr;
    if (_wfopen_s(&f, g_logPath, L"a") != 0 || !f) return;
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(f, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a; va_start(a, fmt); vfprintf(f, fmt, a); va_end(a);
    fputc('\n', f);
    fclose(f);
}

static ID3D11Resource* Res(void* p) { return reinterpret_cast<ID3D11Resource*>(p); }

static void ReleaseFeature()
{
    if (g_feature) { NVSDK_NGX_D3D11_ReleaseFeature(g_feature); g_feature = nullptr; }
}

// ---- render-thread work ----
static void DoCreate(CreateData* d)
{
    g_status = 0;
    if (!d->anyTexture) { Log("Create: no texture"); g_status = -1; return; }

    if (!g_device)
    {
        reinterpret_cast<ID3D11Texture2D*>(d->anyTexture)->GetDevice(&g_device);   // AddRef'd, kept for the process lifetime
        g_device->GetImmediateContext(&g_ctx);
        Log("Got device %p context %p", g_device, g_ctx);
    }

    if (!g_ngxInit)
    {
        wchar_t path[260]; wcscpy_s(path, d->dir);
        const wchar_t* paths[1] = { path };
        NVSDK_NGX_FeatureCommonInfo info = {};
        info.PathListInfo.Path = paths;
        info.PathListInfo.Length = 1;
        NVSDK_NGX_Result r = NVSDK_NGX_D3D11_Init_with_ProjectID("3f1c8e52-7b4a-4d19-a6e0-92c5d8b17f34", NVSDK_NGX_ENGINE_TYPE_UNITY,
                                                                 "2020.3.48", path, g_device, &info);
        Log("NGX Init: 0x%08X", (unsigned)r);
        if (NVSDK_NGX_FAILED(r)) { g_status = -2; g_lastResult = (unsigned)r; return; }
        g_ngxInit = true;

        r = NVSDK_NGX_D3D11_GetCapabilityParameters(&g_params);
        Log("GetCapabilityParameters: 0x%08X", (unsigned)r);
        if (NVSDK_NGX_FAILED(r) || !g_params) { g_status = -3; g_lastResult = (unsigned)r; return; }

        int avail = 0, needsUpdate = 0;
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_NeedsUpdatedDriver, &needsUpdate);
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_Available, &avail);
        unsigned major = 0, minor = 0;
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMajor, &major);
        g_params->Get(NVSDK_NGX_Parameter_SuperSampling_MinDriverVersionMinor, &minor);
        Log("SuperSampling available=%d needsUpdatedDriver=%d minDriver=%u.%u", avail, needsUpdate, major, minor);
        if (!avail) { g_status = -4; return; }
    }

    ReleaseFeature();

    NVSDK_NGX_DLSS_Create_Params cp = {};
    cp.Feature.InWidth = d->renderW;
    cp.Feature.InHeight = d->renderH;
    cp.Feature.InTargetWidth = d->outW;
    cp.Feature.InTargetHeight = d->outH;
    cp.Feature.InPerfQualityValue = (NVSDK_NGX_PerfQuality_Value)d->quality;
    cp.InFeatureCreateFlags = d->flags;

    // 0 clears the hint so NGX picks its own default for the chosen mode.
    unsigned preset = (unsigned)d->preset;
    g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_DLAA, preset);
    g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraQuality, preset);
    g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Quality, preset);
    g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Balanced, preset);
    g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_Performance, preset);
    g_params->Set(NVSDK_NGX_Parameter_DLSS_Hint_Render_Preset_UltraPerformance, preset);

    NVSDK_NGX_Result r = NGX_D3D11_CREATE_DLSS_EXT(g_ctx, &g_feature, g_params, &cp);
    Log("Create DLSS %dx%d -> %dx%d quality=%d flags=0x%X preset=%d: 0x%08X", d->renderW, d->renderH, d->outW, d->outH, d->quality, d->flags, d->preset, (unsigned)r);
    g_lastResult = (unsigned)r;
    if (NVSDK_NGX_FAILED(r)) { g_feature = nullptr; g_status = -5; return; }
    g_cw = d->renderW; g_ch = d->renderH; g_ow = d->outW; g_oh = d->outH;
    g_status = 1;
}

static void InitTimestamps()
{
    if (g_tsReady || !g_device) return;
    D3D11_QUERY_DESC dd = { D3D11_QUERY_TIMESTAMP_DISJOINT, 0 }, td = { D3D11_QUERY_TIMESTAMP, 0 };
    for (auto& s : g_ts)
    {
        if (FAILED(g_device->CreateQuery(&dd, &s.disjoint)) || FAILED(g_device->CreateQuery(&td, &s.start)) || FAILED(g_device->CreateQuery(&td, &s.end))) return;
    }
    g_tsReady = true;
}

// Collect finished timestamp queries without stalling the GPU (results arrive a few frames late).
static void HarvestTimestamps()
{
    for (auto& s : g_ts)
    {
        if (!s.inflight) continue;
        D3D11_QUERY_DATA_TIMESTAMP_DISJOINT dj;
        if (g_ctx->GetData(s.disjoint, &dj, sizeof(dj), D3D11_ASYNC_GETDATA_DONOTFLUSH) != S_OK) continue;
        UINT64 t0 = 0, t1 = 0;
        if (g_ctx->GetData(s.start, &t0, sizeof(t0), 0) == S_OK && g_ctx->GetData(s.end, &t1, sizeof(t1), 0) == S_OK && !dj.Disjoint && dj.Frequency)
        {
            g_evalUs += (unsigned long long)((t1 - t0) * 1000000.0 / (double)dj.Frequency);
            g_evalTimed++;
        }
        s.inflight = false;
    }
}

static void DoEval(EvalData* e)
{
    if (g_status != 1 || !g_feature) return;
    InitTimestamps();
    TsSet* ts = nullptr;
    if (g_tsReady)
    {
        HarvestTimestamps();
        for (auto& s : g_ts) if (!s.inflight) { ts = &s; break; }
    }
    if (ts) { g_ctx->Begin(ts->disjoint); g_ctx->End(ts->start); }

    NVSDK_NGX_D3D11_DLSS_Eval_Params ep = {};
    ep.Feature.pInColor = Res(e->color);
    ep.Feature.pInOutput = Res(e->output);
    ep.Feature.InSharpness = e->sharpness;
    ep.pInDepth = Res(e->depth);
    ep.pInMotionVectors = Res(e->motion);
    ep.InJitterOffsetX = e->jitterX;
    ep.InJitterOffsetY = e->jitterY;
    ep.InRenderSubrectDimensions.Width = e->renderW;
    ep.InRenderSubrectDimensions.Height = e->renderH;
    ep.InReset = e->reset;
    ep.InMVScaleX = e->mvScaleX;
    ep.InMVScaleY = e->mvScaleY;
    ep.InPreExposure = e->preExposure > 0.f ? e->preExposure : 1.f;
    ep.InExposureScale = 1.f;
    ep.InFrameTimeDeltaInMsec = e->frameTimeMs;

    NVSDK_NGX_Result r = NGX_D3D11_EVALUATE_DLSS_EXT(g_ctx, g_feature, g_params, &ep);
    if (ts) { g_ctx->End(ts->end); g_ctx->End(ts->disjoint); ts->inflight = true; }
    g_lastResult = (unsigned)r;
    int n = ++g_evalCount;
    if (NVSDK_NGX_FAILED(r) && (n < 5 || n % 300 == 0)) Log("Evaluate failed: 0x%08X (call %d)", (unsigned)r, n);
    else if (n == 1) Log("First evaluate ok (color=%p depth=%p mv=%p out=%p)", e->color, e->depth, e->motion, e->output);
}


// ---- camera motion vectors from depth ----
static const char* kMvHlsl = R"(
cbuffer C : register(b0)
{
    row_major float4x4 invVPj;
    row_major float4x4 vpCur;
    row_major float4x4 vpPrev;
    uint2 size;
    uint2 pad;
};
Texture2D<float> depthTex : register(t0);
RWTexture2D<float2> mvOut : register(u0);
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= size.x || id.y >= size.y) return;
    float d = depthTex.Load(int3(id.xy, 0));
    float2 uv = (float2(id.xy) + 0.5) / float2(size);
    float4 ndc = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, d, 1.0);
    float4 wp = mul(invVPj, ndc);
    wp /= wp.w;
    float4 c = mul(vpCur, wp);
    float4 p = mul(vpPrev, wp);
    float2 nc = c.xy / c.w;
    float2 np = p.xy / p.w;
    float2 mv = (np - nc) * 0.5 * float2(size.x, -(float)size.y);
    mvOut[id.xy] = mv;
}
)";

static ID3D11ComputeShader* g_mvCS = nullptr;
static ID3D11Buffer*        g_mvCB = nullptr;

struct MvCB { float invVPj[16], vpCur[16], vpPrev[16]; unsigned size[2], pad[2]; };

static DXGI_FORMAT DepthSrvFormat(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R32_TYPELESS: case DXGI_FORMAT_D32_FLOAT: case DXGI_FORMAT_R32_FLOAT: return DXGI_FORMAT_R32_FLOAT;
    case DXGI_FORMAT_R24G8_TYPELESS: case DXGI_FORMAT_D24_UNORM_S8_UINT: return DXGI_FORMAT_R24_UNORM_X8_TYPELESS;
    case DXGI_FORMAT_R32G8X24_TYPELESS: case DXGI_FORMAT_D32_FLOAT_S8X24_UINT: return DXGI_FORMAT_R32_FLOAT_X8X24_TYPELESS;
    case DXGI_FORMAT_R16_TYPELESS: case DXGI_FORMAT_D16_UNORM: return DXGI_FORMAT_R16_UNORM;
    default: return f;
    }
}

static bool EnsureMvShader()
{
    if (g_mvCS) return true;
    ID3DBlob* code = nullptr; ID3DBlob* err = nullptr;
    HRESULT hr = D3DCompile(kMvHlsl, strlen(kMvHlsl), "mv", nullptr, nullptr, "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
    if (FAILED(hr)) { Log("MV shader compile failed 0x%08X: %s", (unsigned)hr, err ? (const char*)err->GetBufferPointer() : "?"); if (err) err->Release(); return false; }
    if (err) err->Release();
    hr = g_device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_mvCS);
    code->Release();
    if (FAILED(hr)) { Log("CreateComputeShader failed 0x%08X", (unsigned)hr); g_mvCS = nullptr; return false; }
    D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = (sizeof(MvCB) + 15) & ~15u; bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    hr = g_device->CreateBuffer(&bd, nullptr, &g_mvCB);
    if (FAILED(hr)) { Log("CreateBuffer failed 0x%08X", (unsigned)hr); return false; }
    Log("MV compute shader ready");
    return true;
}

static void DoMotion(MvData* m)
{
    if (!g_device || !g_ctx || !m->depth || !m->mv || !EnsureMvShader()) return;
    ID3D11Texture2D* dt = reinterpret_cast<ID3D11Texture2D*>(m->depth);
    ID3D11Texture2D* mt = reinterpret_cast<ID3D11Texture2D*>(m->mv);
    D3D11_TEXTURE2D_DESC dd, md; dt->GetDesc(&dd); mt->GetDesc(&md);

    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {}; sd.Format = DepthSrvFormat(dd.Format); sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = 1;
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {}; ud.Format = (md.Format == DXGI_FORMAT_R16G16_TYPELESS) ? DXGI_FORMAT_R16G16_FLOAT : (md.Format == DXGI_FORMAT_R32G32_TYPELESS) ? DXGI_FORMAT_R32G32_FLOAT : md.Format; ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
    ID3D11ShaderResourceView* srv = nullptr; ID3D11UnorderedAccessView* uav = nullptr;
    HRESULT h1 = g_device->CreateShaderResourceView(dt, &sd, &srv);
    HRESULT h2 = g_device->CreateUnorderedAccessView(mt, &ud, &uav);
    static int fails = 0;
    if (FAILED(h1) || FAILED(h2))
    {
        if (fails++ < 5) Log("MV view creation failed srv=0x%08X (depth fmt %d -> %d) uav=0x%08X (mv fmt %d)", (unsigned)h1, (int)dd.Format, (int)sd.Format, (unsigned)h2, (int)md.Format);
        if (srv) srv->Release(); if (uav) uav->Release();
        return;
    }

    MvCB cb;
    memcpy(cb.invVPj, m->invVPj, 64); memcpy(cb.vpCur, m->vpCur, 64); memcpy(cb.vpPrev, m->vpPrev, 64);
    cb.size[0] = m->w; cb.size[1] = m->h; cb.pad[0] = cb.pad[1] = 0;
    g_ctx->UpdateSubresource(g_mvCB, 0, nullptr, &cb, 0, 0);

    g_ctx->CSSetShader(g_mvCS, nullptr, 0);
    g_ctx->CSSetConstantBuffers(0, 1, &g_mvCB);
    g_ctx->CSSetShaderResources(0, 1, &srv);
    UINT initial = 0;
    g_ctx->CSSetUnorderedAccessViews(0, 1, &uav, &initial);
    g_ctx->Dispatch((m->w + 7) / 8, (m->h + 7) / 8, 1);
    ID3D11ShaderResourceView* nsrv = nullptr; ID3D11UnorderedAccessView* nuav = nullptr; ID3D11Buffer* ncb = nullptr;
    g_ctx->CSSetShaderResources(0, 1, &nsrv);
    g_ctx->CSSetUnorderedAccessViews(0, 1, &nuav, &initial);
    g_ctx->CSSetConstantBuffers(0, 1, &ncb);
    g_ctx->CSSetShader(nullptr, nullptr, 0);
    srv->Release(); uav->Release();
    static int n = 0;
    if (++n == 1) Log("First MV dispatch ok (%dx%d, depth fmt %d)", m->w, m->h, (int)dd.Format);
}


// ---- merge per-object motion into the camera motion vectors ----
static const char* kCompHlsl = R"(
cbuffer C : register(b0) { float2 scale; uint2 size; };
Texture2D<float4> objMv : register(t0);
RWTexture2D<float2> mvOut : register(u0);
[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= size.x || id.y >= size.y) return;
    float4 o = objMv.Load(int3(id.xy, 0));
    if (o.a > 0.5 && all(isfinite(o.xy)) && all(abs(o.xy) < 4096.0)) mvOut[id.xy] = o.xy * scale;
}
)";
static ID3D11ComputeShader* g_compCS = nullptr;
static ID3D11Buffer*        g_compCB = nullptr;
struct CompCB { float scale[2]; unsigned size[2]; };

static DXGI_FORMAT FloatFormat(DXGI_FORMAT f)
{
    switch (f)
    {
    case DXGI_FORMAT_R16G16_TYPELESS: return DXGI_FORMAT_R16G16_FLOAT;
    case DXGI_FORMAT_R32G32_TYPELESS: return DXGI_FORMAT_R32G32_FLOAT;
    case DXGI_FORMAT_R16G16B16A16_TYPELESS: return DXGI_FORMAT_R16G16B16A16_FLOAT;
    default: return f;
    }
}


static float HalfToFloat(unsigned short h)
{
    unsigned s = (h >> 15) & 1, e = (h >> 10) & 31, m = h & 1023;
    float v;
    if (e == 0) v = ldexpf((float)m, -24);
    else if (e == 31) v = 65504.0f;
    else v = ldexpf(1.0f + (float)m / 1024.0f, (int)e - 15);
    return s ? -v : v;
}

// Debug: how many pixels did the object motion draw write, and how big is the motion? Logged now and then.
static ID3D11Texture2D* g_stage = nullptr;
static std::atomic<int> g_debugStats(0);    // set from the managed side; the readback stalls the GPU, so off by default
static void ObjStats(ID3D11Texture2D* ot, const D3D11_TEXTURE2D_DESC& od)
{
    if (!g_stage)
    {
        D3D11_TEXTURE2D_DESC sd = od; sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0; sd.MipLevels = 1; sd.ArraySize = 1;
        if (FAILED(g_device->CreateTexture2D(&sd, nullptr, &g_stage))) { g_stage = nullptr; return; }
    }
    g_ctx->CopyResource(g_stage, ot);
    D3D11_MAPPED_SUBRESOURCE ms;
    if (FAILED(g_ctx->Map(g_stage, 0, D3D11_MAP_READ, 0, &ms))) return;
    unsigned long long drawn = 0, bad = 0, cx = 0, cy = 0, cz = 0; double sumX = 0, sumY = 0; float maxX = 0, maxY = 0;
    for (UINT y = 0; y < od.Height; y++)
    {
        const unsigned short* row = reinterpret_cast<const unsigned short*>(reinterpret_cast<const unsigned char*>(ms.pData) + (size_t)y * ms.RowPitch);
        for (UINT x = 0; x < od.Width; x++)
        {
            unsigned short hx = row[x * 4], hy = row[x * 4 + 1], ha = row[x * 4 + 3];
            if (HalfToFloat(ha) < 0.5f) continue;
            drawn++;
            if (((hx >> 10) & 31) == 31 || ((hy >> 10) & 31) == 31) { bad++; continue; }
            float vx = HalfToFloat(hx), vy = HalfToFloat(hy), vz = HalfToFloat(row[x * 4 + 2]);
            if (vx > 0.5f) cx++; if (vy > 0.5f) cy++; if (vz > 0.5f) cz++;
            sumX += vx; sumY += vy; if (fabsf(vx) > maxX) maxX = fabsf(vx); if (fabsf(vy) > maxY) maxY = fabsf(vy);
        }
    }
    g_ctx->Unmap(g_stage, 0);
    Log("ObjMV stats: drawn=%llu invalid=%llu x>0.5:%llu y>0.5:%llu z>0.5:%llu meanX=%.3f meanY=%.3f maxAbsX=%.3f maxAbsY=%.3f (render pixels)", drawn, bad, cx, cy, cz, drawn ? sumX / drawn : 0.0, drawn ? sumY / drawn : 0.0, maxX, maxY);
}

static void DoComposite(CompData* c)
{
    if (!g_device || !g_ctx || !c->obj || !c->mv) return;
    if (!g_compCS)
    {
        ID3DBlob* code = nullptr; ID3DBlob* err = nullptr;
        HRESULT hr = D3DCompile(kCompHlsl, strlen(kCompHlsl), "comp", nullptr, nullptr, "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
        if (FAILED(hr)) { Log("Composite shader compile failed 0x%08X: %s", (unsigned)hr, err ? (const char*)err->GetBufferPointer() : "?"); if (err) err->Release(); return; }
        if (err) err->Release();
        hr = g_device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_compCS);
        code->Release();
        if (FAILED(hr)) { Log("Composite CreateComputeShader failed 0x%08X", (unsigned)hr); g_compCS = nullptr; return; }
        D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 16; bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        g_device->CreateBuffer(&bd, nullptr, &g_compCB);
        Log("Composite compute shader ready");
    }
    ID3D11Texture2D* ot = reinterpret_cast<ID3D11Texture2D*>(c->obj);
    ID3D11Texture2D* mt = reinterpret_cast<ID3D11Texture2D*>(c->mv);
    D3D11_TEXTURE2D_DESC od, md; ot->GetDesc(&od); mt->GetDesc(&md);
    D3D11_SHADER_RESOURCE_VIEW_DESC sd = {}; sd.Format = FloatFormat(od.Format); sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; sd.Texture2D.MipLevels = 1;
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {}; ud.Format = FloatFormat(md.Format); ud.ViewDimension = D3D11_UAV_DIMENSION_TEXTURE2D;
    ID3D11ShaderResourceView* srv = nullptr; ID3D11UnorderedAccessView* uav = nullptr;
    HRESULT h1 = g_device->CreateShaderResourceView(ot, &sd, &srv);
    HRESULT h2 = g_device->CreateUnorderedAccessView(mt, &ud, &uav);
    static int fails = 0;
    if (FAILED(h1) || FAILED(h2))
    {
        if (fails++ < 5) Log("Composite view creation failed srv=0x%08X (fmt %d) uav=0x%08X (fmt %d)", (unsigned)h1, (int)od.Format, (unsigned)h2, (int)md.Format);
        if (srv) srv->Release(); if (uav) uav->Release();
        return;
    }
    CompCB cb = { { c->scaleX, c->scaleY }, { (unsigned)c->w, (unsigned)c->h } };
    g_ctx->UpdateSubresource(g_compCB, 0, nullptr, &cb, 0, 0);
    // The object motion target was just drawn into and is usually still bound as the render target. D3D11 refuses to bind a resource
    // for reading while it is an output (the read view silently becomes null and every pixel reads as empty), so unbind the output
    // merger for the dispatch and put it back afterwards.
    ID3D11RenderTargetView* keepRtv[8] = {}; ID3D11DepthStencilView* keepDsv = nullptr;
    g_ctx->OMGetRenderTargets(8, keepRtv, &keepDsv);
    g_ctx->OMSetRenderTargets(0, nullptr, nullptr);
    g_ctx->CSSetShader(g_compCS, nullptr, 0);
    g_ctx->CSSetConstantBuffers(0, 1, &g_compCB);
    g_ctx->CSSetShaderResources(0, 1, &srv);
    UINT initial = 0;
    g_ctx->CSSetUnorderedAccessViews(0, 1, &uav, &initial);
    g_ctx->Dispatch((c->w + 7) / 8, (c->h + 7) / 8, 1);
    ID3D11ShaderResourceView* nsrv = nullptr; ID3D11UnorderedAccessView* nuav = nullptr; ID3D11Buffer* ncb = nullptr;
    g_ctx->CSSetShaderResources(0, 1, &nsrv);
    g_ctx->CSSetUnorderedAccessViews(0, 1, &nuav, &initial);
    g_ctx->CSSetConstantBuffers(0, 1, &ncb);
    g_ctx->CSSetShader(nullptr, nullptr, 0);
    g_ctx->OMSetRenderTargets(8, keepRtv, keepDsv);
    for (int i = 0; i < 8; i++) if (keepRtv[i]) keepRtv[i]->Release();
    if (keepDsv) keepDsv->Release();
    srv->Release(); uav->Release();
    static int n = 0;
    if (g_debugStats && n % 240 == 0 && od.Format == DXGI_FORMAT_R16G16B16A16_TYPELESS) ObjStats(ot, od);
    if (++n == 1) Log("First composite dispatch ok (%dx%d, obj fmt %d, mv fmt %d)", c->w, c->h, (int)od.Format, (int)md.Format);
}

// ---- debug: GPU-side view of one render pass (pipeline statistics + the state left by its last draw) ----
static ID3D11Query* g_qStats = nullptr;
static ID3D11Query* g_qOcc = nullptr;

static void DescResource(ID3D11Resource* r, char* out, size_t n)
{
    out[0] = 0;
    if (!r) { strcpy_s(out, n, "none"); return; }
    ID3D11Texture2D* t = nullptr;
    if (SUCCEEDED(r->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) && t)
    {
        D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
        sprintf_s(out, n, "%ux%u fmt %d mips %u samples %u bind 0x%X", d.Width, d.Height, (int)d.Format, d.MipLevels, d.SampleDesc.Count, d.BindFlags);
        t->Release();
    }
    else strcpy_s(out, n, "not a 2D texture");
}

static void DoPassBegin(void* anyTexture)
{
    if (!g_device && anyTexture)
    {
        reinterpret_cast<ID3D11Texture2D*>(anyTexture)->GetDevice(&g_device);
        g_device->GetImmediateContext(&g_ctx);
    }
    if (!g_device || !g_ctx) return;
    if (!g_qStats)
    {
        D3D11_QUERY_DESC s = { D3D11_QUERY_PIPELINE_STATISTICS, 0 }, o = { D3D11_QUERY_OCCLUSION, 0 };
        g_device->CreateQuery(&s, &g_qStats);
        g_device->CreateQuery(&o, &g_qOcc);
    }
    if (g_qStats) g_ctx->Begin(g_qStats);
    if (g_qOcc) g_ctx->Begin(g_qOcc);
}

// Debug: histogram of the stencil bits of a depth-stencil texture (counts per bit, plus the total pixel count).
static const char* kStencilHlsl = R"(
Texture2D<uint2> st : register(t0);
RWStructuredBuffer<uint> counts : register(u0);
cbuffer C : register(b0) { uint2 size; uint2 pad; };
groupshared uint g[9];
[numthreads(16, 16, 1)]
void main(uint3 id : SV_DispatchThreadID, uint gi : SV_GroupIndex)
{
    if (gi < 9) g[gi] = 0;
    GroupMemoryBarrierWithGroupSync();
    if (id.x < size.x && id.y < size.y)
    {
        uint s = st.Load(int3(id.xy, 0)).g;
        InterlockedAdd(g[8], 1);
        [unroll] for (uint k = 0; k < 8; k++) if (s & (1u << k)) InterlockedAdd(g[k], 1);
    }
    GroupMemoryBarrierWithGroupSync();
    if (gi < 9 && g[gi] != 0) InterlockedAdd(counts[gi], g[gi]);
}
)";
static ID3D11ComputeShader* g_stCS = nullptr;
static ID3D11Buffer* g_stCB = nullptr;
static ID3D11Buffer* g_stBuf = nullptr;
static ID3D11Buffer* g_stStage = nullptr;
static ID3D11UnorderedAccessView* g_stUav = nullptr;

static void StencilStats(const char* what, ID3D11Resource* res)
{
    if (!res) { Log("  stencil %s: no texture", what); return; }
    ID3D11Texture2D* t = nullptr;
    if (FAILED(res->QueryInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&t))) || !t) { Log("  stencil %s: not a 2D texture", what); return; }
    D3D11_TEXTURE2D_DESC d; t->GetDesc(&d);
    DXGI_FORMAT sf;
    if (d.Format == DXGI_FORMAT_R32G8X24_TYPELESS || d.Format == DXGI_FORMAT_D32_FLOAT_S8X24_UINT) sf = DXGI_FORMAT_X32_TYPELESS_G8X24_UINT;
    else if (d.Format == DXGI_FORMAT_R24G8_TYPELESS || d.Format == DXGI_FORMAT_D24_UNORM_S8_UINT) sf = DXGI_FORMAT_X24_TYPELESS_G8_UINT;
    else { Log("  stencil %s: %ux%u format %d has no stencil plane", what, d.Width, d.Height, (int)d.Format); t->Release(); return; }
    if (!(d.BindFlags & D3D11_BIND_SHADER_RESOURCE)) { Log("  stencil %s: texture cannot be read (bind 0x%X)", what, d.BindFlags); t->Release(); return; }
    if (!g_stCS)
    {
        ID3DBlob* code = nullptr; ID3DBlob* err = nullptr;
        HRESULT hr = D3DCompile(kStencilHlsl, strlen(kStencilHlsl), "stencil", nullptr, nullptr, "main", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &err);
        if (FAILED(hr)) { Log("stencil shader compile failed: %s", err ? (const char*)err->GetBufferPointer() : "?"); if (err) err->Release(); t->Release(); return; }
        if (err) err->Release();
        g_device->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_stCS);
        code->Release();
        D3D11_BUFFER_DESC cbd = {}; cbd.ByteWidth = 16; cbd.Usage = D3D11_USAGE_DEFAULT; cbd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        g_device->CreateBuffer(&cbd, nullptr, &g_stCB);
        D3D11_BUFFER_DESC bd = {}; bd.ByteWidth = 9 * 4; bd.Usage = D3D11_USAGE_DEFAULT; bd.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
        bd.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; bd.StructureByteStride = 4;
        g_device->CreateBuffer(&bd, nullptr, &g_stBuf);
        D3D11_BUFFER_DESC sd = {}; sd.ByteWidth = 9 * 4; sd.Usage = D3D11_USAGE_STAGING; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        g_device->CreateBuffer(&sd, nullptr, &g_stStage);
        D3D11_UNORDERED_ACCESS_VIEW_DESC ud = {}; ud.Format = DXGI_FORMAT_UNKNOWN; ud.ViewDimension = D3D11_UAV_DIMENSION_BUFFER; ud.Buffer.NumElements = 9;
        g_device->CreateUnorderedAccessView(g_stBuf, &ud, &g_stUav);
    }
    if (!g_stCS || !g_stUav) { t->Release(); return; }
    D3D11_SHADER_RESOURCE_VIEW_DESC vd = {}; vd.Format = sf; vd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D; vd.Texture2D.MipLevels = 1;
    ID3D11ShaderResourceView* srv = nullptr;
    HRESULT hr = g_device->CreateShaderResourceView(t, &vd, &srv);
    if (FAILED(hr)) { Log("  stencil %s: SRV creation failed 0x%08X", what, (unsigned)hr); t->Release(); return; }
    unsigned zeros[9] = {}; g_ctx->UpdateSubresource(g_stBuf, 0, nullptr, zeros, 0, 0);
    unsigned cbv[4] = { d.Width, d.Height, 0, 0 }; g_ctx->UpdateSubresource(g_stCB, 0, nullptr, cbv, 0, 0);
    g_ctx->CSSetShader(g_stCS, nullptr, 0);
    g_ctx->CSSetConstantBuffers(0, 1, &g_stCB);
    g_ctx->CSSetShaderResources(0, 1, &srv);
    UINT initial = 0; g_ctx->CSSetUnorderedAccessViews(0, 1, &g_stUav, &initial);
    g_ctx->Dispatch((d.Width + 15) / 16, (d.Height + 15) / 16, 1);
    ID3D11ShaderResourceView* nsrv = nullptr; ID3D11UnorderedAccessView* nuav = nullptr; ID3D11Buffer* ncb = nullptr;
    g_ctx->CSSetShaderResources(0, 1, &nsrv); g_ctx->CSSetUnorderedAccessViews(0, 1, &nuav, &initial); g_ctx->CSSetConstantBuffers(0, 1, &ncb); g_ctx->CSSetShader(nullptr, nullptr, 0);
    g_ctx->CopyResource(g_stStage, g_stBuf);
    D3D11_MAPPED_SUBRESOURCE ms;
    if (SUCCEEDED(g_ctx->Map(g_stStage, 0, D3D11_MAP_READ, 0, &ms)))
    {
        const unsigned* c = reinterpret_cast<const unsigned*>(ms.pData);
        Log("  stencil %s (%ux%u): pixels %u | bit1 %u bit2 %u bit4 %u bit8 %u bit16 %u bit32 %u bit64 %u bit128 %u", what, d.Width, d.Height, c[8], c[0], c[1], c[2], c[3], c[4], c[5], c[6], c[7]);
        g_ctx->Unmap(g_stStage, 0);
    }
    srv->Release();
    t->Release();
}

static void DoPassEnd(void* extraDepth)
{
    if (!g_ctx || !g_qStats || !g_qOcc) return;
    g_ctx->End(g_qStats);
    g_ctx->End(g_qOcc);

    char a[256], b[256];
    ID3D11RenderTargetView* rtv[8] = {}; ID3D11DepthStencilView* dsv = nullptr;
    g_ctx->OMGetRenderTargets(8, rtv, &dsv);
    for (int i = 0; i < 8; i++)
    {
        if (!rtv[i]) continue;
        ID3D11Resource* r = nullptr; rtv[i]->GetResource(&r); DescResource(r, a, sizeof(a)); if (r) r->Release();
        D3D11_RENDER_TARGET_VIEW_DESC vd; rtv[i]->GetDesc(&vd);
        Log("  RTV%d: %s (view fmt %d)", i, a, (int)vd.Format);
        rtv[i]->Release();
    }
    ID3D11Resource* dsvRes = nullptr;
    if (dsv)
    {
        dsv->GetResource(&dsvRes); DescResource(dsvRes, a, sizeof(a));
        D3D11_DEPTH_STENCIL_VIEW_DESC vd; dsv->GetDesc(&vd);
        Log("  DSV: %s (view fmt %d flags 0x%X)", a, (int)vd.Format, vd.Flags);
    }
    else Log("  DSV: none");

    ID3D11DepthStencilState* dss = nullptr; UINT sref = 0;
    g_ctx->OMGetDepthStencilState(&dss, &sref);
    if (dss)
    {
        D3D11_DEPTH_STENCIL_DESC d; dss->GetDesc(&d);
        Log("  depth: enable %d write %d func %d | stencil: enable %d ref %u read 0x%X write 0x%X front func %d pass %d fail %d zfail %d | back func %d",
            d.DepthEnable, (int)d.DepthWriteMask, (int)d.DepthFunc, d.StencilEnable, sref, d.StencilReadMask, d.StencilWriteMask,
            (int)d.FrontFace.StencilFunc, (int)d.FrontFace.StencilPassOp, (int)d.FrontFace.StencilFailOp, (int)d.FrontFace.StencilDepthFailOp, (int)d.BackFace.StencilFunc);
        dss->Release();
    }
    ID3D11BlendState* bs = nullptr; float bf[4]; UINT sm = 0;
    g_ctx->OMGetBlendState(&bs, bf, &sm);
    if (bs)
    {
        D3D11_BLEND_DESC d; bs->GetDesc(&d);
        Log("  blend RT0: enable %d src %d dst %d op %d srcA %d dstA %d mask 0x%X", d.RenderTarget[0].BlendEnable, (int)d.RenderTarget[0].SrcBlend, (int)d.RenderTarget[0].DestBlend,
            (int)d.RenderTarget[0].BlendOp, (int)d.RenderTarget[0].SrcBlendAlpha, (int)d.RenderTarget[0].DestBlendAlpha, d.RenderTarget[0].RenderTargetWriteMask);
        bs->Release();
    }
    else Log("  blend: default");
    ID3D11RasterizerState* rs = nullptr;
    g_ctx->RSGetState(&rs);
    if (rs)
    {
        D3D11_RASTERIZER_DESC d; rs->GetDesc(&d);
        Log("  raster: cull %d frontCCW %d depthClip %d scissor %d", (int)d.CullMode, d.FrontCounterClockwise, d.DepthClipEnable, d.ScissorEnable);
        rs->Release();
    }
    UINT nvp = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE; D3D11_VIEWPORT vp[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    g_ctx->RSGetViewports(&nvp, vp);
    if (nvp) Log("  viewport: %.0f,%.0f %.0fx%.0f depth %.2f..%.2f", vp[0].TopLeftX, vp[0].TopLeftY, vp[0].Width, vp[0].Height, vp[0].MinDepth, vp[0].MaxDepth);
    UINT nsc = D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE; D3D11_RECT sc[D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE];
    g_ctx->RSGetScissorRects(&nsc, sc);
    if (nsc) Log("  scissor: %ld,%ld..%ld,%ld", sc[0].left, sc[0].top, sc[0].right, sc[0].bottom);
    ID3D11ShaderResourceView* srv[16] = {};
    g_ctx->PSGetShaderResources(0, 16, srv);
    for (int i = 0; i < 16; i++)
    {
        if (!srv[i]) continue;
        ID3D11Resource* r = nullptr; srv[i]->GetResource(&r); DescResource(r, a, sizeof(a)); if (r) r->Release();
        D3D11_SHADER_RESOURCE_VIEW_DESC vd; srv[i]->GetDesc(&vd);
        sprintf_s(b, sizeof(b), "view fmt %d dim %d", (int)vd.Format, (int)vd.ViewDimension);
        Log("  PS t%d: %s (%s)", i, a, b);
        srv[i]->Release();
    }

    // Stencil content of the bound depth-stencil buffer (unbind it from the output merger while reading, then put everything back),
    // and of the extra texture the caller passed (the low-resolution source of the upscaled depth).
    if (dsvRes)
    {
        ID3D11RenderTargetView* keepRtv[8] = {}; ID3D11DepthStencilView* keepDsv = nullptr;
        g_ctx->OMGetRenderTargets(8, keepRtv, &keepDsv);
        g_ctx->OMSetRenderTargets(0, nullptr, nullptr);
        StencilStats("bound DSV", dsvRes);
        g_ctx->OMSetRenderTargets(8, keepRtv, keepDsv);
        for (int i = 0; i < 8; i++) if (keepRtv[i]) keepRtv[i]->Release();
        if (keepDsv) keepDsv->Release();
        dsvRes->Release();
    }
    if (dsv) dsv->Release();
    if (extraDepth) StencilStats("low-res source", reinterpret_cast<ID3D11Resource*>(extraDepth));

    // Results: wait for the GPU (debug only, a short stall).
    D3D11_QUERY_DATA_PIPELINE_STATISTICS ps = {}; UINT64 occ = 0;
    ULONGLONG t0 = GetTickCount64();
    HRESULT h1 = S_FALSE, h2 = S_FALSE;
    while (GetTickCount64() - t0 < 500 && (h1 != S_OK || h2 != S_OK))
    {
        if (h1 != S_OK) h1 = g_ctx->GetData(g_qStats, &ps, sizeof(ps), 0);
        if (h2 != S_OK) h2 = g_ctx->GetData(g_qOcc, &occ, sizeof(occ), 0);
        if (h1 != S_OK || h2 != S_OK) Sleep(1);
    }
    if (h1 != S_OK || h2 != S_OK) Log("  stats: not available after 500 ms");
    else Log("  stats: IAVertices %llu VSInvocations %llu CInvocations %llu CPrimitives %llu PSInvocations %llu | samples passing depth/stencil %llu",
        ps.IAVertices, ps.VSInvocations, ps.CInvocations, ps.CPrimitives, ps.PSInvocations, occ);
}

static void DoShutdown()
{
    ReleaseFeature();
    if (g_ngxInit && g_device) { NVSDK_NGX_D3D11_Shutdown1(g_device); g_ngxInit = false; }
    g_status = 0;
}

// Unity plugin event with data: eventId 1 = create/recreate, 2 = evaluate, 3 = shutdown, 4 = camera motion vectors from depth, 5 = merge object motion
static void __stdcall OnEvent(int eventId, void* data)
{
    __try
    {
        switch (eventId)
        {
        case 1: DoCreate(reinterpret_cast<CreateData*>(data)); break;
        case 2: DoEval(reinterpret_cast<EvalData*>(data)); break;
        case 3: DoShutdown(); break;
        case 4: DoMotion(reinterpret_cast<MvData*>(data)); break;
        case 5: DoComposite(reinterpret_cast<CompData*>(data)); break;
        case 7: DoPassBegin(data); break;
        case 8: DoPassEnd(data); break;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        Log("Exception 0x%08X in event %d", GetExceptionCode(), eventId);
        g_status = -99;
    }
}

// ---- exports ----
EXPORT void WotRDLSS_SetLogPath(const wchar_t* path) { wcscpy_s(g_logPath, path); }
EXPORT void* WotRDLSS_GetEventFunc() { return reinterpret_cast<void*>(&OnEvent); }
EXPORT int WotRDLSS_GetStatus() { return g_status; }
EXPORT unsigned WotRDLSS_GetLastResult() { return g_lastResult; }
EXPORT int WotRDLSS_GetEvalCount() { return g_evalCount; }
// GPU time of the DLSS evaluate call, accumulated from timestamp queries since the last reset.
EXPORT void WotRDLSS_ResetEvalTiming() { g_evalUs = 0; g_evalTimed = 0; }
EXPORT int WotRDLSS_GetEvalTiming(unsigned long long* totalUs, unsigned long long* count) { *totalUs = g_evalUs; *count = g_evalTimed; return g_tsReady ? 1 : 0; }

// Video memory used by this process on the GPU, and the budget the OS gives it, in MB. Returns 0 if unavailable (no device yet).
EXPORT int WotRDLSS_GetVramMB(unsigned long long* usageMB, unsigned long long* budgetMB)
{
    *usageMB = 0; *budgetMB = 0;
    if (!g_device) return 0;
    int ok = 0;
    IDXGIDevice* dev = nullptr;
    if (SUCCEEDED(g_device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dev))) && dev)
    {
        IDXGIAdapter* ad = nullptr;
        if (SUCCEEDED(dev->GetAdapter(&ad)) && ad)
        {
            IDXGIAdapter3* ad3 = nullptr;
            if (SUCCEEDED(ad->QueryInterface(__uuidof(IDXGIAdapter3), reinterpret_cast<void**>(&ad3))) && ad3)
            {
                DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
                if (SUCCEEDED(ad3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info)))
                {
                    *usageMB = info.CurrentUsage / (1024ull * 1024ull);
                    *budgetMB = info.Budget / (1024ull * 1024ull);
                    ok = 1;
                }
                ad3->Release();
            }
            ad->Release();
        }
        dev->Release();
    }
    return ok;
}

EXPORT void WotRDLSS_SetDebugStats(int on) { g_debugStats = on; }
EXPORT int WotRDLSS_StructSizes(int which) { return which == 0 ? (int)sizeof(CreateData) : which == 1 ? (int)sizeof(EvalData) : which == 2 ? (int)sizeof(MvData) : (int)sizeof(CompData); }


namespace UnnamedGame.Graphics;

internal static class Shaders
{
    public const int MaxPointLights = 16;

    /// <summary>Reinhard tone mapping; without it a light close to a wall clips straight to white.</summary>
    private const string ToneMapping = """
    float3 ToneMap(float3 color)
    {
        const float exposure = 1.6;
        color *= exposure;
        return pow(saturate(color / (1.0 + color)), 1.0 / 2.2);
    }
    """;

    /// <summary>Shared declarations: both passes use the same per-object data and matrices.</summary>
    private const string Common = """
    cbuffer PerPass : register(b0)
    {
        row_major float4x4 ViewProjection;   // camera for the G-buffer pass, light for shadow passes
    };

    cbuffer PerObject : register(b1)
    {
        row_major float4x4 World;
        float4   Color;
        float3   TexScale;   // world units per checker tile
        float    Checker;    // 0 = flat color, 1 = checkered, 2 = sample AlbedoTexture
    };

    struct VSIn { float3 Position : POSITION; float3 Normal : NORMAL; float2 Uv : TEXCOORD0; };
    """;

    /// <summary>Geometry pass: albedo into target 0, world normal into target 1, depth into the DSV.</summary>
    public static readonly string GBuffer = Common + """

    Texture2D<float4> AlbedoTexture : register(t0);
    SamplerState AlbedoSampler : register(s0);

    struct VSOut
    {
        float4 Position : SV_POSITION;
        float3 WorldPos : TEXCOORD0;
        float3 Normal   : TEXCOORD1;
        float2 Uv       : TEXCOORD2;
    };

    struct PSOut
    {
        float4 Albedo : SV_TARGET0;
        float4 Normal : SV_TARGET1;
    };

    VSOut VSMain(VSIn input)
    {
        VSOut o;
        float4 world = mul(float4(input.Position, 1.0), World);
        o.WorldPos = world.xyz;
        o.Position = mul(world, ViewProjection);
        o.Normal   = normalize(mul(float4(input.Normal, 0.0), World).xyz);
        o.Uv       = input.Uv;
        return o;
    }

    PSOut PSMain(VSOut input)
    {
        float3 n = normalize(input.Normal);
        float3 albedo = Color.rgb;

        if (Checker > 1.5)
        {
            // Textured model surface: the material colour tints the sampled albedo.
            float4 sampled = AlbedoTexture.Sample(AlbedoSampler, input.Uv);
            clip(sampled.a - 0.35);
            albedo *= sampled.rgb;
        }
        else if (Checker > 0.5)
        {
            // Project onto whichever plane the face normal is least aligned with.
            float3 p = input.WorldPos / max(TexScale, 0.001);
            float3 a = abs(n);
            float2 uv = a.y > 0.5 ? p.xz : (a.x > 0.5 ? p.zy : p.xy);
            float tile = fmod(floor(uv.x) + floor(uv.y), 2.0);
            albedo *= lerp(0.72, 1.0, tile);
        }

        PSOut o;
        o.Albedo = float4(albedo, 1.0);
        o.Normal = float4(n * 0.5 + 0.5, 1.0);
        return o;
    }
    """;

    /// <summary>Depth-only pass used for both shadow maps. No pixel shader is bound.</summary>
    public static readonly string Shadow = Common + """

    float4 VSMain(VSIn input) : SV_POSITION
    {
        return mul(mul(float4(input.Position, 1.0), World), ViewProjection);
    }
    """;

    /// <summary>
    /// Lighting pass over a full-screen triangle: reconstructs world position from depth and
    /// accumulates the sun (shadow mapped), the player's flashlight (shadow mapped) and up to
    /// 16 unshadowed point lights.
    /// </summary>
    public static readonly string Lighting = ToneMapping + """

    #define MAX_POINT_LIGHTS 16

    cbuffer LightingData : register(b0)
    {
        row_major float4x4 InverseViewProjection;
        row_major float4x4 SunViewProjection;
        row_major float4x4 SpotViewProjection;

        float3 CameraPosition;  float PointLightCount;
        float3 SunDirection;    float SunIntensity;
        float3 SunColor;        float SunTexelSize;

        float3 SpotPosition;    float SpotRange;
        float3 SpotDirection;   float SpotInnerCos;
        float3 SpotColor;       float SpotOuterCos;
        float  SpotEnabled;     float SpotIntensity; float SpotTexelSize; float _pad;

        float4 PointPositionRange[MAX_POINT_LIGHTS];
        float4 PointColorIntensity[MAX_POINT_LIGHTS];
    };

    Texture2D<float4> GBufferAlbedo : register(t0);
    Texture2D<float4> GBufferNormal : register(t1);
    Texture2D<float>  GBufferDepth  : register(t2);
    Texture2D<float>  SunShadowMap  : register(t3);
    Texture2D<float>  SpotShadowMap : register(t4);

    SamplerComparisonState ShadowSampler : register(s0);

    struct VSOut
    {
        float4 Position : SV_POSITION;
        float2 UV       : TEXCOORD0;
    };

    // Full-screen triangle straight from the vertex id — no vertex or index buffer bound.
    VSOut VSMain(uint id : SV_VertexID)
    {
        VSOut o;
        o.UV = float2((id << 1) & 2, id & 2);
        o.Position = float4(o.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
        return o;
    }

    float SampleShadow(Texture2D<float> map, float4x4 lightViewProjection, float3 worldPos, float3 normal, float texelSize, float normalOffset)
    {
        float4 clip = mul(float4(worldPos + normal * normalOffset, 1.0), lightViewProjection);
        clip.xyz /= clip.w;
        if (clip.z <= 0.0 || clip.z >= 1.0) return 1.0;

        float2 uv = clip.xy * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) return 1.0;

        // 3x3 PCF; the comparison sampler does the depth test and the bilinear blend.
        float sum = 0.0;
        [unroll] for (int y = -1; y <= 1; y++)
        {
            [unroll] for (int x = -1; x <= 1; x++)
                sum += map.SampleCmpLevelZero(ShadowSampler, uv + float2(x, y) * texelSize, clip.z - 0.0015);
        }
        return sum / 9.0;
    }

    float3 Shade(float3 albedo, float3 n, float3 v, float3 lightDirection, float3 radiance)
    {
        float ndotl = saturate(dot(n, lightDirection));
        if (ndotl <= 0.0) return 0.0;

        // Lambert diffuse plus a tight Blinn-Phong highlight, enough for blockout geometry.
        float3 h = normalize(lightDirection + v);
        float specular = pow(saturate(dot(n, h)), 48.0) * 0.25;
        return radiance * ndotl * (albedo + specular);
    }

    float4 PSMain(VSOut input) : SV_TARGET
    {
        int3 pixel = int3(input.Position.xy, 0);
        float depth = GBufferDepth.Load(pixel);

        float3 skyColor = float3(0.20, 0.25, 0.34);
        if (depth >= 1.0)
            return float4(ToneMap(skyColor), 1.0);

        float3 albedo = GBufferAlbedo.Load(pixel).rgb;
        float3 n = normalize(GBufferNormal.Load(pixel).xyz * 2.0 - 1.0);

        // World position from the depth buffer.
        float4 ndc = float4(input.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0);
        float4 world = mul(ndc, InverseViewProjection);
        float3 worldPos = world.xyz / world.w;
        float3 v = normalize(CameraPosition - worldPos);

        float3 ambient = lerp(float3(0.030, 0.035, 0.050), float3(0.075, 0.085, 0.110), n.y * 0.5 + 0.5);
        float3 lit = albedo * ambient;

        // Sun.
        float sunShadow = SampleShadow(SunShadowMap, SunViewProjection, worldPos, n, SunTexelSize, 0.06);
        lit += Shade(albedo, n, v, -SunDirection, SunColor * SunIntensity * sunShadow);

        // Flashlight.
        if (SpotEnabled > 0.5)
        {
            float3 toLight = SpotPosition - worldPos;
            float distance = length(toLight);
            if (distance < SpotRange)
            {
                float3 l = toLight / max(distance, 0.0001);
                float cone = smoothstep(SpotOuterCos, SpotInnerCos, dot(-l, SpotDirection));
                if (cone > 0.0)
                {
                    float attenuation = saturate(1.0 - distance / SpotRange);
                    attenuation *= attenuation / (distance * distance * 0.05 + 1.0);
                    float shadow = SampleShadow(SpotShadowMap, SpotViewProjection, worldPos, n, SpotTexelSize, 0.03);
                    lit += Shade(albedo, n, v, l, SpotColor * (SpotIntensity * cone * attenuation * shadow));
                }
            }
        }

        // Point lights, no shadows.
        int count = (int)PointLightCount;
        for (int i = 0; i < count; i++)
        {
            float3 position = PointPositionRange[i].xyz;
            float range = PointPositionRange[i].w;
            float3 toLight = position - worldPos;
            float distance = length(toLight);
            if (distance >= range) continue;

            float3 l = toLight / max(distance, 0.0001);
            float window = saturate(1.0 - pow(distance / range, 4.0));
            float attenuation = window * window / (distance * distance + 1.0);
            lit += Shade(albedo, n, v, l, PointColorIntensity[i].rgb * (PointColorIntensity[i].w * attenuation));
        }

        float fog = saturate((length(CameraPosition - worldPos) - 25.0) / 65.0);
        float3 final = lerp(lit, skyColor, fog);
        return float4(ToneMap(final), 1.0);
    }
    """;

    /// <summary>
    /// Forward pass for glass, drawn after lighting and blended over it. Deferred shading has
    /// nowhere to put a translucent surface, so the windows are shaded here directly: a Fresnel
    /// term (glass turns mirror-like at grazing angles), a sky reflection and a sun highlight.
    /// </summary>
    public static readonly string Glass = Common + ToneMapping + """

    cbuffer GlassFrame : register(b2)
    {
        float3 CameraPosition; float _gpad0;
        float3 SunDirection;   float SunIntensity;
        float3 SunColor;       float _gpad1;
        float3 SkyColor;       float _gpad2;
    };

    Texture2D<float4> AlphaTexture : register(t0);
    SamplerState AlphaSampler : register(s0);

    struct VSOut
    {
        float4 Position : SV_POSITION;
        float3 WorldPos : TEXCOORD0;
        float3 Normal   : TEXCOORD1;
        float2 Uv       : TEXCOORD2;
    };

    VSOut VSMain(VSIn input)
    {
        VSOut o;
        float4 world = mul(float4(input.Position, 1.0), World);
        o.WorldPos = world.xyz;
        o.Position = mul(world, ViewProjection);
        o.Normal   = normalize(mul(float4(input.Normal, 0.0), World).xyz);
        o.Uv       = input.Uv;
        return o;
    }

    float4 PSMain(VSOut input) : SV_TARGET
    {
        float3 v = normalize(CameraPosition - input.WorldPos);
        float3 n = normalize(input.Normal);
        if (dot(n, v) < 0.0) n = -n;   // the pass is two-sided: windows are seen from inside too

        float alpha = Color.a;
        if (Checker > 1.5)
            alpha *= AlphaTexture.Sample(AlphaSampler, input.Uv).a;

        // Schlick: nearly clear head-on, close to a mirror at a glancing angle.
        float fresnel = pow(1.0 - saturate(dot(n, v)), 5.0);
        alpha = saturate(alpha + fresnel * (1.0 - alpha) * 0.85);

        float3 halfway = normalize(-SunDirection + v);
        float specular = pow(saturate(dot(n, halfway)), 220.0) * 2.2;

        float3 color = Color.rgb * 0.05
                     + SkyColor * lerp(0.35, 1.6, fresnel)
                     + SunColor * (SunIntensity * specular);

        return float4(ToneMap(color), alpha);
    }
    """;

    /// <summary>Overlay drawn after lighting (the crosshair), unaffected by any light.</summary>
    public static readonly string Unlit = Common + """

    float4 VSMain(VSIn input) : SV_POSITION
    {
        return mul(mul(float4(input.Position, 1.0), World), ViewProjection);
    }

    float4 PSMain() : SV_TARGET
    {
        return float4(pow(saturate(Color.rgb), 1.0 / 2.2), 1.0);
    }
    """;
}

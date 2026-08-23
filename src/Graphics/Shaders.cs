namespace UnnamedGame.Graphics;

internal static class Shaders
{
    // Single forward pass: one directional light + hemisphere ambient, plus a
    // cheap procedural checker so the flat-shaded MVP geometry reads as a space.
    public const string Hlsl = """
    cbuffer PerFrame : register(b0)
    {
        row_major float4x4 ViewProjection;
        float3   CameraPosition;
        float    _pad0;
        float3   LightDirection;
        float    _pad1;
    };

    cbuffer PerObject : register(b1)
    {
        row_major float4x4 World;
        float4   Color;
        float3   TexScale;   // world units per checker tile, per axis
        float    Checker;    // 0 = flat color, 1 = checkered
    };

    struct VSIn  { float3 Position : POSITION; float3 Normal : NORMAL; };
    struct VSOut
    {
        float4 Position : SV_POSITION;
        float3 WorldPos : TEXCOORD0;
        float3 Normal   : TEXCOORD1;
    };

    VSOut VSMain(VSIn input)
    {
        VSOut o;
        float4 world = mul(float4(input.Position, 1.0), World);
        o.WorldPos = world.xyz;
        o.Position = mul(world, ViewProjection);
        o.Normal   = normalize(mul(float4(input.Normal, 0.0), World).xyz);
        return o;
    }

    float4 PSMain(VSOut input) : SV_TARGET
    {
        float3 n = normalize(input.Normal);
        float ndotl = saturate(dot(n, -LightDirection));
        float3 ambient = lerp(float3(0.16, 0.17, 0.22), float3(0.34, 0.36, 0.42), n.y * 0.5 + 0.5);
        float3 albedo = Color.rgb;

        if (Checker > 0.5)
        {
            // Project onto whichever plane the face normal is least aligned with.
            float3 p = input.WorldPos / max(TexScale, 0.001);
            float3 a = abs(n);
            float2 uv = a.y > 0.5 ? p.xz : (a.x > 0.5 ? p.zy : p.xy);
            float tile = fmod(floor(uv.x) + floor(uv.y), 2.0);
            albedo *= lerp(0.72, 1.0, tile);
        }

        float3 lit = albedo * (ambient + ndotl * float3(1.0, 0.97, 0.9) * 0.85);

        // Specular-ish rim to keep silhouettes readable against the fog.
        float3 v = normalize(CameraPosition - input.WorldPos);
        float rim = pow(1.0 - saturate(dot(n, v)), 3.0) * 0.12;

        float dist = length(CameraPosition - input.WorldPos);
        float fog = saturate((dist - 25.0) / 65.0);
        float3 final = lerp(lit + rim, float3(0.55, 0.62, 0.72), fog);

        return float4(pow(saturate(final), 1.0 / 2.2), 1.0);
    }
    """;
}

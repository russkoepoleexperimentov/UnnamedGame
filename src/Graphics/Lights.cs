using System.Numerics;

namespace UnnamedGame.Graphics;

/// <summary>Unshadowed omni light. <see cref="Range"/> is where its contribution reaches zero.</summary>
public readonly record struct PointLight(Vector3 Position, float Range, Vector3 Color, float Intensity);

/// <summary>Shadow-mapped cone light. The player's flashlight is the only one for now.</summary>
public readonly record struct Spotlight(
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    float Range,
    float InnerAngle,
    float OuterAngle)
{
    public static Spotlight Flashlight(Vector3 position, Vector3 direction) => new(
        position, direction,
        Color: new Vector3(1f, 0.96f, 0.86f),
        Intensity: 9f,
        Range: 34f,
        InnerAngle: 0.20f,
        OuterAngle: 0.34f);
}

/// <summary>One queued mesh draw. The renderer replays the queue once per pass.</summary>
public readonly record struct DrawCommand(Mesh Mesh, Matrix4x4 World, Vector4 Color, Vector3 TexScale, bool Checker);

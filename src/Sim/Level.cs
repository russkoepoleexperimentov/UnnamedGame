using System.Numerics;
using BepuPhysics;
using UnnamedGame.Graphics;

namespace UnnamedGame.Sim;

public readonly record struct StaticProp(Vector3 Center, Vector3 Size, Quaternion Orientation, Vector4 Color, bool Checker);

public record struct DynamicProp(BodyHandle Handle, Vector3 Size, float Radius, Vector4 Color)
{
    /// <summary>Pose at the start of the current physics step; rendering interpolates from it.</summary>
    public RigidPose PreviousPose = RigidPose.Identity;

    public readonly bool IsSphere => Radius > 0;
}

/// <summary>Hand-built blockout: a courtyard with ramps, a catwalk and stacks of crates.</summary>
public sealed class Level
{
    private static readonly Vector4 FloorColor = new(0.42f, 0.45f, 0.50f, 1f);
    private static readonly Vector4 WallColor = new(0.50f, 0.47f, 0.44f, 1f);
    private static readonly Vector4 RampColor = new(0.38f, 0.46f, 0.55f, 1f);
    private static readonly Vector4 CrateColor = new(0.62f, 0.44f, 0.24f, 1f);
    private static readonly Vector4 PillarColor = new(0.34f, 0.38f, 0.44f, 1f);

    /// <summary>Animated omni lights placed around the courtyard.</summary>
    private readonly (Vector3 Anchor, Vector3 Color, float Range, float Intensity, float Orbit, float Speed)[] _lampSpecs =
    [
        (new Vector3(-6, 3.2f, 2), new Vector3(1.0f, 0.55f, 0.25f), 14f, 18f, 0f, 1.7f),      // warm, flickering
        (new Vector3(8, 2.6f, -6), new Vector3(0.25f, 0.6f, 1.0f), 14f, 16f, 3.5f, 0.8f),     // blue, orbiting
        (new Vector3(-13, 5.0f, -12), new Vector3(0.45f, 1.0f, 0.5f), 16f, 18f, 0f, 0.5f),    // green, over the platform
        (new Vector3(14, 6.0f, 6), new Vector3(1.0f, 0.85f, 0.6f), 17f, 20f, 4.5f, -0.6f),    // orbiting over the catwalk
    ];

    private readonly List<PointLight> _lights = [];

    public List<StaticProp> Statics { get; } = [];
    public List<DynamicProp> Dynamics { get; } = [];
    public Vector3 SpawnPoint { get; } = new(0, 2f, 14f);

    public Level(PhysicsWorld physics)
    {
        // Ground and the outer walls of a 40 x 40 courtyard.
        AddStatic(physics, new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40), FloorColor, checker: true);
        AddStatic(physics, new Vector3(0, 3, -20.5f), new Vector3(41, 8, 1), WallColor, checker: true);
        AddStatic(physics, new Vector3(0, 3, 20.5f), new Vector3(41, 8, 1), WallColor, checker: true);
        AddStatic(physics, new Vector3(-20.5f, 3, 0), new Vector3(1, 8, 41), WallColor, checker: true);
        AddStatic(physics, new Vector3(20.5f, 3, 0), new Vector3(1, 8, 41), WallColor, checker: true);

        // A raised platform in the back corner, reachable by a ramp. The ramp is placed so its
        // top surface meets the platform edge exactly (x = -4, y = 3) and reaches the floor at
        // x = 3.5 — a mismatch here reads as the player falling off a ledge, not walking down.
        AddStatic(physics, new Vector3(-11, 1.5f, -12), new Vector3(14, 3, 12), PillarColor, checker: true);
        AddStatic(physics, new Vector3(-0.36f, 1.22f, -12),
            new Vector3(8.2f, 0.6f, 12), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.38f), RampColor, checker: true);

        // Catwalk along the east wall, plus the pillars holding it up.
        AddStatic(physics, new Vector3(15, 4, 0), new Vector3(8, 0.5f, 26), PillarColor, checker: true);
        AddStatic(physics, new Vector3(11.5f, 2, -10), new Vector3(1, 4, 1), PillarColor, checker: false);
        AddStatic(physics, new Vector3(11.5f, 2, 0), new Vector3(1, 4, 1), PillarColor, checker: false);
        AddStatic(physics, new Vector3(11.5f, 2, 10), new Vector3(1, 4, 1), PillarColor, checker: false);
        // Ramp up to the catwalk: floor at z = 19.5 to the catwalk deck at z = 13, y = 4.25.
        AddStatic(physics, new Vector3(13.5f, 1.87f, 16.09f),
            new Vector3(5, 0.6f, 7.77f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5796f), RampColor, checker: true);

        // Two flights of stairs — the reason the character does step-up probing.
        for (int i = 0; i < 6; i++)
        {
            float height = 0.5f * (i + 1);
            AddStatic(physics, new Vector3(-16f, height * 0.5f, -3.5f - i * 0.6f),
                new Vector3(6, height, 0.6f), PillarColor, checker: false);
        }
        for (int i = 0; i < 4; i++)
        {
            float height = 0.5f * (i + 1);
            AddStatic(physics, new Vector3(-6f, height * 0.5f, 8.6f + i * 0.55f),
                new Vector3(4, height, 0.55f), PillarColor, checker: false);
        }

        // Free-standing cover blocks.
        AddStatic(physics, new Vector3(-6, 1, 6), new Vector3(4, 2, 4), WallColor, checker: true);
        AddStatic(physics, new Vector3(4, 0.75f, -4), new Vector3(6, 1.5f, 3), WallColor, checker: true);

        // Crate pyramid to knock over.
        for (int row = 0; row < 4; row++)
        {
            for (int i = 0; i < 4 - row; i++)
            {
                var position = new Vector3(-3.6f + i * 1.05f + row * 0.525f, 0.55f + row * 1.02f, 2f);
                AddCrate(physics, position, 1f, 12f, CrateColor);
            }
        }

        // A loose row of heavier crates near the ramp.
        for (int i = 0; i < 5; i++)
            AddCrate(physics, new Vector3(6f + i * 1.6f, 0.75f + i * 0.1f, 8f), 1.4f, 30f, new Vector4(0.55f, 0.5f, 0.3f, 1f));
    }

    private void AddStatic(PhysicsWorld physics, Vector3 center, Vector3 size, Vector4 color, bool checker)
        => AddStatic(physics, center, size, Quaternion.Identity, color, checker);

    private void AddStatic(PhysicsWorld physics, Vector3 center, Vector3 size, Quaternion orientation, Vector4 color, bool checker)
    {
        physics.AddStaticBox(center, size, orientation);
        Statics.Add(new StaticProp(center, size, orientation, color, checker));
    }

    private void AddCrate(PhysicsWorld physics, Vector3 center, float size, float mass, Vector4 color)
    {
        var extents = new Vector3(size);
        var handle = physics.AddDynamicBox(center, extents, mass);
        Dynamics.Add(new DynamicProp(handle, extents, 0f, color) { PreviousPose = new RigidPose(center) });
    }

    /// <summary>Recomputes the animated lights for this frame.</summary>
    public IReadOnlyList<PointLight> UpdateLights(float time)
    {
        _lights.Clear();
        for (int i = 0; i < _lampSpecs.Length; i++)
        {
            var (anchor, color, range, intensity, orbit, speed) = _lampSpecs[i];
            float phase = time * speed + i;
            var position = anchor + new Vector3(MathF.Cos(phase) * orbit, 0, MathF.Sin(phase) * orbit);

            // Gentle pulse, plus a faster flicker on the first lamp so movement is obvious.
            float pulse = 0.75f + 0.25f * MathF.Sin(phase * 2.1f);
            if (i == 0) pulse *= 0.85f + 0.15f * MathF.Sin(time * 17f) * MathF.Sin(time * 6.3f);

            _lights.Add(new PointLight(position, range, color, intensity * pulse));
        }
        return _lights;
    }

    public void SpawnBall(PhysicsWorld physics, Vector3 position, Vector3 velocity)
    {
        const float radius = 0.28f;
        var handle = physics.AddDynamicSphere(position, radius, 6f, velocity);
        Dynamics.Add(new DynamicProp(handle, Vector3.Zero, radius, new Vector4(0.85f, 0.30f, 0.25f, 1f))
        {
            PreviousPose = new RigidPose(position),
        });
    }
}

using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using UnnamedGame.Graphics;

namespace UnnamedGame.Sim;

public readonly record struct StaticProp(Vector3 Center, Vector3 Size, Quaternion Orientation, Vector4 Color, bool Checker);

/// <summary>Picks which footstep bank plays when the player walks on a surface.</summary>
public enum Surface { Concrete, Metal, Gravel, Earth, Grass }

public record struct DynamicProp(BodyHandle Handle, Vector3 Size, float Radius, Vector4 Color)
{
    /// <summary>Pose at the start of the current physics step; rendering interpolates from it.</summary>
    public RigidPose PreviousPose = RigidPose.Identity;

    public readonly bool IsSphere => Radius > 0;
}

/// <summary>A level built from <see cref="MapData"/>: static brushes, loose props and lamps.</summary>
public sealed class Level
{
    private readonly MapLight[] _lamps;
    private readonly List<PointLight> _lights = [];
    private readonly Dictionary<int, Surface> _surfaces = [];

    public MapData Map { get; }
    public List<StaticProp> Statics { get; } = [];
    public List<DynamicProp> Dynamics { get; } = [];
    public Vector3 SpawnPoint => Map.PlayerSpawn;
    public int LightCount => _lamps.Length;

    public Level(PhysicsWorld physics, MapData map)
    {
        Map = map;
        _lamps = [.. map.Lights];

        foreach (var brush in map.Brushes)
        {
            var handle = physics.AddStaticBox(brush.Center, brush.Size, brush.Orientation);
            _surfaces[handle.Value] = brush.Surface;
            Statics.Add(new StaticProp(brush.Center, brush.Size, brush.Orientation,
                new Vector4(brush.Color, 1f), brush.Checker));
        }

        foreach (var terrain in map.Terrains)
        {
            var triangles = BuildTerrainTriangles(terrain);
            if (triangles.Length == 0) continue;

            var handle = physics.AddStaticMesh(Vector3.Zero, triangles);
            _surfaces[handle.Value] = terrain.Surface;
        }

        foreach (var prop in map.Props)
        {
            var extents = new Vector3(prop.Size);
            var handle = physics.AddDynamicBox(prop.Center, extents, prop.Mass);
            Dynamics.Add(new DynamicProp(handle, extents, 0f, new Vector4(prop.Color, 1f))
            {
                PreviousPose = new RigidPose(prop.Center),
            });
        }
    }

    /// <summary>Two triangles per cell, in world space (the static sits at the origin).</summary>
    public static Triangle[] BuildTerrainTriangles(MapTerrain terrain)
    {
        if (terrain.Columns < 2 || terrain.Rows < 2) return [];

        var triangles = new Triangle[(terrain.Columns - 1) * (terrain.Rows - 1) * 2];
        int index = 0;

        for (int row = 0; row < terrain.Rows - 1; row++)
        {
            for (int column = 0; column < terrain.Columns - 1; column++)
            {
                var a = terrain.Vertex(column, row);
                var b = terrain.Vertex(column + 1, row);
                var c = terrain.Vertex(column, row + 1);
                var d = terrain.Vertex(column + 1, row + 1);

                // Bepu's mesh triangles are one-sided and it takes the normal as
                // cross(C - A, B - A), so this winding is the one that faces up. The opposite
                // order looks identical in the editor and silently lets everything fall through.
                triangles[index++] = new Triangle(a, b, c);
                triangles[index++] = new Triangle(b, d, c);
            }
        }
        return triangles;
    }

    /// <summary>Surface the given collidable is made of; anything unregistered is concrete.</summary>
    public Surface SurfaceOf(CollidableReference collidable)
        => collidable.Mobility == CollidableMobility.Static && _surfaces.TryGetValue(collidable.RawHandleValue, out var surface)
            ? surface
            : Surface.Concrete;

    /// <summary>Recomputes the animated lights for this frame.</summary>
    public IReadOnlyList<PointLight> UpdateLights(float time)
    {
        _lights.Clear();
        for (int i = 0; i < _lamps.Length; i++)
        {
            var lamp = _lamps[i];
            float phase = time * lamp.Speed + i;
            var position = lamp.Position + new Vector3(MathF.Cos(phase) * lamp.Orbit, 0, MathF.Sin(phase) * lamp.Orbit);

            float pulse = 0.75f + 0.25f * MathF.Sin(phase * 2.1f);
            if (lamp.Flicker) pulse *= 0.85f + 0.15f * MathF.Sin(time * 17f) * MathF.Sin(time * 6.3f);

            _lights.Add(new PointLight(position, lamp.Range, lamp.Color, lamp.Intensity * pulse));
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

    /// <summary>
    /// The hand-built courtyard the project grew up with, kept in code as the fallback map and
    /// as the source for the shipped .ugmap file.
    /// </summary>
    public static MapData CreateDefaultMap()
    {
        var floor = new Vector3(0.42f, 0.45f, 0.50f);
        var wall = new Vector3(0.50f, 0.47f, 0.44f);
        var ramp = new Vector3(0.38f, 0.46f, 0.55f);
        var pillar = new Vector3(0.34f, 0.38f, 0.44f);
        var crate = new Vector3(0.62f, 0.44f, 0.24f);

        var map = new MapData { Name = "courtyard" };

        void Brush(Vector3 center, Vector3 size, Vector3 color, bool checker,
            Surface surface = Surface.Concrete, Vector3 rotation = default)
            => map.Brushes.Add(new MapBrush
            {
                Center = center,
                Size = size,
                Color = color,
                Checker = checker,
                Surface = surface,
                Rotation = rotation,
            });

        const float toDegrees = 180f / MathF.PI;

        // Ground and the outer walls of a 40 x 40 courtyard.
        Brush(new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40), floor, true);
        Brush(new Vector3(0, 3, -20.5f), new Vector3(41, 8, 1), wall, true);
        Brush(new Vector3(0, 3, 20.5f), new Vector3(41, 8, 1), wall, true);
        Brush(new Vector3(-20.5f, 3, 0), new Vector3(1, 8, 41), wall, true);
        Brush(new Vector3(20.5f, 3, 0), new Vector3(1, 8, 41), wall, true);

        // Raised platform, with the ramp meeting its edge exactly.
        Brush(new Vector3(-11, 1.5f, -12), new Vector3(14, 3, 12), pillar, true, Surface.Gravel);
        Brush(new Vector3(-0.36f, 1.22f, -12), new Vector3(8.2f, 0.6f, 12), ramp, true, Surface.Metal,
            new Vector3(0, 0, -0.38f * toDegrees));

        // Catwalk along the east wall, its pillars, and the ramp up to it.
        Brush(new Vector3(15, 4, 0), new Vector3(8, 0.5f, 26), pillar, true, Surface.Metal);
        Brush(new Vector3(11.5f, 2, -10), new Vector3(1, 4, 1), pillar, false, Surface.Metal);
        Brush(new Vector3(11.5f, 2, 0), new Vector3(1, 4, 1), pillar, false, Surface.Metal);
        Brush(new Vector3(11.5f, 2, 10), new Vector3(1, 4, 1), pillar, false, Surface.Metal);
        Brush(new Vector3(13.5f, 1.87f, 16.09f), new Vector3(5, 0.6f, 7.77f), ramp, true, Surface.Metal,
            new Vector3(0.5796f * toDegrees, 0, 0));

        // Two flights of stairs.
        for (int i = 0; i < 6; i++)
        {
            float height = 0.5f * (i + 1);
            Brush(new Vector3(-16f, height * 0.5f, -3.5f - i * 0.6f), new Vector3(6, height, 0.6f),
                pillar, false, Surface.Metal);
        }
        for (int i = 0; i < 4; i++)
        {
            float height = 0.5f * (i + 1);
            Brush(new Vector3(-6f, height * 0.5f, 8.6f + i * 0.55f), new Vector3(4, height, 0.55f),
                pillar, false, Surface.Metal);
        }

        // A low slab to crawl under: 1.25 m of clearance.
        Brush(new Vector3(5f, 1.45f, 5f), new Vector3(6, 0.4f, 3), wall, true);
        Brush(new Vector3(2.2f, 0.625f, 5f), new Vector3(0.4f, 1.25f, 3), pillar, false);
        Brush(new Vector3(7.8f, 0.625f, 5f), new Vector3(0.4f, 1.25f, 3), pillar, false);

        // Free-standing cover.
        Brush(new Vector3(-6, 1, 6), new Vector3(4, 2, 4), wall, true);
        Brush(new Vector3(4, 0.75f, -4), new Vector3(6, 1.5f, 3), wall, true);

        // Crate pyramid, then a row of heavier crates.
        for (int row = 0; row < 4; row++)
        {
            for (int i = 0; i < 4 - row; i++)
            {
                map.Props.Add(new MapProp
                {
                    Center = new Vector3(-3.6f + i * 1.05f + row * 0.525f, 0.55f + row * 1.02f, 2f),
                    Size = 1f,
                    Mass = 12f,
                    Color = crate,
                });
            }
        }

        for (int i = 0; i < 5; i++)
        {
            map.Props.Add(new MapProp
            {
                Center = new Vector3(6f + i * 1.6f, 0.75f + i * 0.1f, 8f),
                Size = 1.4f,
                Mass = 30f,
                Color = new Vector3(0.55f, 0.5f, 0.3f),
            });
        }

        map.Lights.Add(new MapLight { Position = new(-6, 3.2f, 2), Color = new(1.0f, 0.55f, 0.25f), Range = 14f, Intensity = 18f, Speed = 1.7f, Flicker = true });
        map.Lights.Add(new MapLight { Position = new(8, 2.6f, -6), Color = new(0.25f, 0.6f, 1.0f), Range = 14f, Intensity = 16f, Orbit = 3.5f, Speed = 0.8f });
        map.Lights.Add(new MapLight { Position = new(-13, 5.0f, -12), Color = new(0.45f, 1.0f, 0.5f), Range = 16f, Intensity = 18f, Speed = 0.5f });
        map.Lights.Add(new MapLight { Position = new(14, 6.0f, 6), Color = new(1.0f, 0.85f, 0.6f), Range = 17f, Intensity = 20f, Orbit = 4.5f, Speed = -0.6f });

        return map;
    }
}

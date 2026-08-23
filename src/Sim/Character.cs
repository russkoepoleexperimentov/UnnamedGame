using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace UnnamedGame.Sim;

/// <summary>Ray hit filter that skips one collidable (the player's own capsule).</summary>
internal struct ClosestHitExcluding(CollidableReference self) : IRayHitHandler
{
    public float T = float.MaxValue;
    public Vector3 Normal;
    public bool Hit;

    public readonly bool AllowTest(CollidableReference collidable) => collidable.Packed != self.Packed;
    public readonly bool AllowTest(CollidableReference collidable, int childIndex) => true;

    public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal,
        CollidableReference collidable, int childIndex)
    {
        if (t >= T) return;
        T = t;
        Normal = normal;
        Hit = true;
        maximumT = t;   // let the broadphase prune everything further away
    }
}

/// <summary>
/// Quake/Source style first-person movement: instant ground acceleration up to a cap,
/// exponential ground friction, and capped air acceleration that still allows air-strafing.
/// The body itself is a dynamic capsule with rotation locked, so the solver resolves walls.
/// </summary>
public sealed class Character
{
    public const float Radius = 0.4f;
    public const float CylinderHalfLength = 0.6f;   // total height = 2 * (Radius + HalfLength) = 2.0
    public const float EyeHeight = 0.72f;           // above the capsule centre

    // Tuned to feel close to HL2: 320 u/s ≈ 8 m/s at 40 units per metre.
    public const float MaxGroundSpeed = 8.0f;
    public const float GroundAcceleration = 12.0f;
    public const float AirAcceleration = 14.0f;
    public const float AirSpeedCap = 1.2f;          // classic bunny-hop knob
    public const float Friction = 8.0f;
    public const float StopSpeed = 1.5f;
    public const float JumpSpeed = 6.2f;
    public const float MaxGroundSlopeCos = 0.7f;    // ≈ 45°

    private readonly PhysicsWorld _physics;
    private readonly BodyHandle _handle;
    private float _coyoteTimer;
    private float _jumpBufferTimer;

    public bool OnGround { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.UnitY;
    public float Yaw;      // radians, 0 = looking down -Z
    public float Pitch;

    public Character(PhysicsWorld physics, Vector3 spawnPosition)
    {
        _physics = physics;
        var capsule = new Capsule(Radius, CylinderHalfLength * 2f);
        var shape = physics.Simulation.Shapes.Add(capsule);

        var inertia = capsule.ComputeInertia(75f);
        inertia.InverseInertiaTensor = default;   // never let the capsule tip over

        var description = BodyDescription.CreateDynamic(spawnPosition, inertia, shape, 0.01f);
        description.Activity.SleepThreshold = -1f;   // never sleep: we drive it every frame
        _handle = physics.Simulation.Bodies.Add(description);
    }

    public BodyReference Body => _physics.Simulation.Bodies[_handle];
    public Vector3 Position => Body.Pose.Position;
    public Vector3 Velocity => Body.Velocity.Linear;
    public Vector3 EyePosition => Position + new Vector3(0, EyeHeight, 0);

    public Vector3 Forward => new(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public Vector3 FlatForward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    public Vector3 FlatRight => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

    public void Look(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Yaw = MathF.IEEERemainder(Yaw, MathF.Tau);
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);
    }

    /// <param name="wish">Local move input: X = right, Y = forward, each in [-1, 1].</param>
    public void Move(Vector2 wish, bool jumpHeld, float dt)
    {
        var body = Body;
        var velocity = body.Velocity.Linear;

        ProbeGround(body.Pose.Position);

        if (jumpHeld) _jumpBufferTimer = 0.12f;
        _coyoteTimer = OnGround ? 0.1f : MathF.Max(0, _coyoteTimer - dt);
        _jumpBufferTimer = MathF.Max(0, _jumpBufferTimer - dt);

        var wishDirection = FlatRight * wish.X + FlatForward * wish.Y;
        float wishLength = wishDirection.Length();
        if (wishLength > 1e-4f) wishDirection /= wishLength;
        float wishSpeed = MathF.Min(wishLength, 1f) * MaxGroundSpeed;

        if (OnGround)
        {
            ApplyFriction(ref velocity, dt);
            Accelerate(ref velocity, wishDirection, wishSpeed, GroundAcceleration, dt);

            if (_jumpBufferTimer > 0 && _coyoteTimer > 0)
            {
                velocity.Y = JumpSpeed;
                OnGround = false;
                _coyoteTimer = 0;
                _jumpBufferTimer = 0;
            }
            else if (velocity.Y < 0)
            {
                // Stick to the ground instead of skipping down slopes.
                velocity.Y = 0;
            }
        }
        else
        {
            Accelerate(ref velocity, wishDirection, MathF.Min(wishSpeed, AirSpeedCap), AirAcceleration, dt);
        }

        body.Velocity.Linear = velocity;
        body.Awake = true;
    }

    public void Teleport(Vector3 position)
    {
        var body = Body;
        body.Pose.Position = position;
        body.Velocity.Linear = Vector3.Zero;
        body.Awake = true;
    }

    private static void Accelerate(ref Vector3 velocity, Vector3 wishDirection, float wishSpeed, float acceleration, float dt)
    {
        float current = Vector3.Dot(velocity, wishDirection);
        float add = wishSpeed - current;
        if (add <= 0) return;
        float accelerated = MathF.Min(acceleration * MaxGroundSpeed * dt, add);
        velocity += wishDirection * accelerated;
    }

    private static void ApplyFriction(ref Vector3 velocity, float dt)
    {
        var horizontal = new Vector3(velocity.X, 0, velocity.Z);
        float speed = horizontal.Length();
        if (speed < 0.01f)
        {
            velocity.X = velocity.Z = 0;
            return;
        }

        float control = MathF.Max(speed, StopSpeed);
        float drop = control * Friction * dt;
        float scale = MathF.Max(speed - drop, 0) / speed;
        velocity.X *= scale;
        velocity.Z *= scale;
    }

    private void ProbeGround(Vector3 position)
    {
        // Cast from the capsule centre down past the bottom cap.
        var self = new CollidableReference(CollidableMobility.Dynamic, _handle);
        var handler = new ClosestHitExcluding(self);
        float maximumT = CylinderHalfLength + Radius + 0.18f;
        _physics.Simulation.RayCast(position, -Vector3.UnitY, maximumT, _physics.BufferPool, ref handler, 0);

        OnGround = handler.Hit && handler.Normal.Y > MaxGroundSlopeCos;
        GroundNormal = OnGround ? Vector3.Normalize(handler.Normal) : Vector3.UnitY;
    }
}

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
    public CollidableReference Collidable;

    public readonly bool AllowTest(CollidableReference collidable) => collidable.Packed != self.Packed;
    public readonly bool AllowTest(CollidableReference collidable, int childIndex) => true;

    public void OnRayHit(in RayData ray, ref float maximumT, float t, Vector3 normal,
        CollidableReference collidable, int childIndex)
    {
        if (t >= T) return;
        T = t;
        Normal = normal;
        Hit = true;
        Collidable = collidable;
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
    public const float CylinderHalfLength = 0.6f;        // standing: total height = 2 * (Radius + this) = 2.0
    public const float CrouchCylinderHalfLength = 0.15f; // crouched: total height = 1.1
    public const float EyeHeight = 0.72f;                // above the capsule centre, standing
    public const float CrouchEyeHeight = 0.30f;
    public const float CrouchSpeed = 3.2f;

    // Tuned to feel close to HL2: 320 u/s ≈ 8 m/s at 40 units per metre.
    public const float MaxGroundSpeed = 8.0f;
    public const float GroundAcceleration = 12.0f;
    public const float AirAcceleration = 14.0f;
    public const float AirSpeedCap = 1.2f;          // classic bunny-hop knob
    public const float Friction = 8.0f;
    public const float StopSpeed = 1.5f;
    public const float JumpSpeed = 6.2f;
    public const float MaxGroundSlopeCos = 0.7f;    // ≈ 45°
    public const float MaxStepHeight = 0.55f;       // tallest ledge the player walks up
    public const float GroundSnapDistance = 0.6f;   // how far below the feet ground still counts
    public const float ViewSmoothTime = 0.12f;      // roughly how long the camera takes to catch up
    public const float ViewCatchUpMin = 1.0f;       // m/s, so tiny offsets still finish promptly
    public const float ViewCatchUpMax = 5.0f;       // m/s ceiling: this is what keeps it from snapping
    public const float MaxViewOffset = 1.2f;

    private readonly PhysicsWorld _physics;
    private BodyHandle _handle;
    private float _coyoteTimer;
    private float _jumpBufferTimer;
    private Vector3 _previousPosition;
    /// <summary>
    /// World-space vertical lag of the camera behind the body. Anything that moves the eye
    /// instantly — a step up or down, ducking, standing up — adds the jump to this offset,
    /// which then decays to zero. That is the whole of the view smoothing.
    /// </summary>
    private float _viewOffset;
    private bool _wasOnGround;
    private float _snapCooldown;   // suppresses ground snapping right after a jump
    private float _jumpTimer;      // keeps the player airborne while the jump clears the ground probe
    private Vector3 _flyPosition;
    private Vector3 _noclipVelocity;
    private float _halfLength = CylinderHalfLength;   // current cylinder half length
    private TypedIndex _standingShape, _crouchedShape;

    public bool OnGround { get; private set; }
    public Vector3 GroundNormal { get; private set; } = Vector3.UnitY;
    /// <summary>What the player is standing on, used to pick the footstep sound.</summary>
    public CollidableReference GroundCollidable { get; private set; }
    public float Yaw;      // radians, 0 = looking down -Z
    public float Pitch;

    public Character(PhysicsWorld physics, Vector3 spawnPosition)
    {
        _physics = physics;
        var capsule = new Capsule(Radius, CylinderHalfLength * 2f);
        _standingShape = physics.Simulation.Shapes.Add(capsule);
        _crouchedShape = physics.Simulation.Shapes.Add(new Capsule(Radius, CrouchCylinderHalfLength * 2f));
        var shape = _standingShape;

        var inertia = capsule.ComputeInertia(75f);
        inertia.InverseInertiaTensor = default;   // never let the capsule tip over

        var description = BodyDescription.CreateDynamic(spawnPosition, inertia, shape, 0.01f);
        description.Activity.SleepThreshold = -1f;   // never sleep: we drive it every frame
        _handle = physics.Simulation.Bodies.Add(description);
        _previousPosition = spawnPosition;
    }

    /// <summary>False while the player is riding in a vehicle: the capsule is out of the simulation.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>True while the capsule is in its short form.</summary>
    public bool IsCrouching { get; private set; }

    /// <summary>Half the capsule's total height, which changes with the crouch.</summary>
    public float HalfHeight => _halfLength + Radius;

    /// <summary>Free flight with no collision; the capsule leaves the simulation while it is on.</summary>
    public bool Noclip { get; private set; }
    public const float NoclipSpeed = 14f;

    public BodyReference Body => _physics.Simulation.Bodies[_handle];
    public Vector3 Position => Noclip ? _flyPosition : Body.Pose.Position;
    public Vector3 Velocity => Noclip ? _noclipVelocity : Body.Velocity.Linear;
    /// <summary>Eye height above the capsule centre for the current stance.</summary>
    public float EyeAboveCentre => IsCrouching ? CrouchEyeHeight : EyeHeight;

    public Vector3 EyePosition => Position + new Vector3(0, EyeAboveCentre, 0);

    /// <summary>
    /// Eye position for rendering: the pose is interpolated across the fixed timestep,
    /// and a step up is eased in rather than snapping the camera a whole stair up.
    /// </summary>
    public Vector3 InterpolatedEyePosition(float alpha)
        => Vector3.Lerp(_previousPosition, Position, alpha) + new Vector3(0, EyeAboveCentre - _viewOffset, 0);

    public Vector3 Forward => new(-MathF.Sin(Yaw) * MathF.Cos(Pitch), MathF.Sin(Pitch), -MathF.Cos(Yaw) * MathF.Cos(Pitch));
    public Vector3 FlatForward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    public Vector3 FlatRight => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

    public void Look(float deltaYaw, float deltaPitch)
    {
        Yaw += deltaYaw;
        Yaw = MathF.IEEERemainder(Yaw, MathF.Tau);
        Pitch = Math.Clamp(Pitch + deltaPitch, -1.55f, 1.55f);
    }

    /// <summary>
    /// Swaps the capsule between its standing and crouched shapes, keeping the feet planted.
    /// Standing back up is refused while there is something overhead, so the player stays
    /// crouched until they leave the low spot — the behaviour every shooter uses.
    /// </summary>
    private void UpdateCrouch(bool wantsCrouch)
    {
        bool target = wantsCrouch || (IsCrouching && !CanStandUp());
        if (target == IsCrouching) return;

        float delta = CylinderHalfLength - CrouchCylinderHalfLength;

        // On the ground the feet stay planted and the body shrinks downwards. In the air it is
        // the other way round: the head keeps its height and the feet come up, which is what
        // lets a crouch-jump clear a ledge a normal jump cannot. Source does exactly this.
        float shift = OnGround ? (target ? -delta : delta) : (target ? delta : -delta);

        if (!OnGround && target && Blocked(Vector3.UnitY, delta + 0.05f))
            shift = -delta;   // ceiling in the way: fall back to keeping the feet planted

        float eyeBefore = Body.Pose.Position.Y + EyeAboveCentre;

        var body = Body;
        body.Pose.Position += new Vector3(0, shift, 0);
        _previousPosition.Y += shift;   // the swap is instant; do not interpolate through it

        _physics.Simulation.Bodies.SetShape(_handle, target ? _crouchedShape : _standingShape);

        IsCrouching = target;
        _halfLength = target ? CrouchCylinderHalfLength : CylinderHalfLength;

        // Whatever the eye just did instantly, the camera undoes and then eases back in.
        float eyeAfter = body.Pose.Position.Y + EyeAboveCentre;
        _viewOffset = Math.Clamp(_viewOffset + (eyeAfter - eyeBefore), -MaxViewOffset, MaxViewOffset);
    }

    /// <summary>
    /// Room to return to full height. Standing on the ground grows the hull upwards; standing
    /// up in mid-air grows it downwards, because there the head is what stays put.
    /// </summary>
    private bool CanStandUp()
    {
        float needed = 2f * (CylinderHalfLength + Radius) - HalfHeight;
        return !Blocked(OnGround ? Vector3.UnitY : -Vector3.UnitY, needed);
    }

    /// <summary>True when something is in the way within <paramref name="distance"/>.</summary>
    private bool Blocked(Vector3 direction, float distance)
        => CastRay(Body.Pose.Position, direction, distance, out _, out _);

    /// <summary>Turns free flight on or off, taking the capsule in and out of the simulation.</summary>
    public void SetNoclip(bool enabled)
    {
        if (enabled == Noclip) return;

        if (enabled)
        {
            _flyPosition = Position;
            _previousPosition = _flyPosition;
            _noclipVelocity = Vector3.Zero;
            Disable();
            Noclip = true;
        }
        else
        {
            Noclip = false;
            Enable(_flyPosition);
        }
    }

    /// <param name="wish">Local move input: X = right, Y = forward, each in [-1, 1].</param>
    /// <param name="vertical">Up/down input, only used in noclip.</param>
    public void Move(Vector2 wish, bool jumpHeld, float dt, float vertical = 0f, bool fast = false, bool crouch = false)
    {
        if (Noclip)
        {
            MoveNoclip(wish, vertical, fast, dt);
            return;
        }

        UpdateCrouch(crouch);

        var body = Body;
        var velocity = body.Velocity.Linear;

        // Sampled before the solver integrates, so rendering can interpolate towards the new pose.
        _previousPosition = body.Pose.Position;
        // Catch up at a bounded speed rather than a fixed fraction per step: an exponential
        // decay fast enough for stairs moves a crouch's 0.87 m almost instantly, which reads
        // as a snap. Source clamps the rate for the same reason.
        float catchUp = Math.Clamp(MathF.Abs(_viewOffset) / ViewSmoothTime, ViewCatchUpMin, ViewCatchUpMax);
        float move = MathF.Min(MathF.Abs(_viewOffset), catchUp * dt);
        _viewOffset -= MathF.Sign(_viewOffset) * move;
        if (MathF.Abs(_viewOffset) < 0.001f) _viewOffset = 0;

        ProbeGround(body.Pose.Position);

        // The ground probe reaches 0.18 m past the feet, so for the first moments of a jump it
        // still reports ground - and ClipToGroundPlane would then wipe out the upward velocity.
        _jumpTimer = MathF.Max(0f, _jumpTimer - dt);
        if (_jumpTimer > 0f) OnGround = false;

        if (jumpHeld) _jumpBufferTimer = 0.12f;
        _coyoteTimer = OnGround ? 0.1f : MathF.Max(0, _coyoteTimer - dt);
        _jumpBufferTimer = MathF.Max(0, _jumpBufferTimer - dt);

        var wishDirection = FlatRight * wish.X + FlatForward * wish.Y;
        float wishLength = wishDirection.Length();
        if (wishLength > 1e-4f) wishDirection /= wishLength;
        float wishSpeed = MathF.Min(wishLength, 1f) * (IsCrouching ? CrouchSpeed : MaxGroundSpeed);

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
                _snapCooldown = 0.15f;   // do not glue the player back down mid-jump
                _jumpTimer = 0.12f;
            }
            else
            {
                ClipToGroundPlane(ref velocity);
            }
        }
        else
        {
            Accelerate(ref velocity, wishDirection, MathF.Min(wishSpeed, AirSpeedCap), AirAcceleration, dt);
        }

        _snapCooldown = MathF.Max(0, _snapCooldown - dt);
        if (!OnGround && _wasOnGround && _snapCooldown <= 0 && velocity.Y <= 0.1f)
            TryStepDown(ref velocity);

        if (_coyoteTimer > 0)
            TryStepUp(wishDirection, ref velocity);

        _wasOnGround = OnGround;

        body.Velocity.Linear = velocity;
        body.Awake = true;
    }

    /// <summary>Removes the capsule from the simulation (on entering a vehicle).</summary>
    public void Disable()
    {
        if (!IsActive) return;
        _physics.Simulation.Bodies.Remove(_handle);
        IsActive = false;
    }

    /// <summary>Puts the capsule back at <paramref name="position"/> (on leaving a vehicle).</summary>
    public void Enable(Vector3 position)
    {
        if (IsActive) return;

        var capsule = new Capsule(Radius, _halfLength * 2f);
        var shape = _physics.Simulation.Shapes.Add(capsule);
        var inertia = capsule.ComputeInertia(75f);
        inertia.InverseInertiaTensor = default;

        var description = BodyDescription.CreateDynamic(position, inertia, shape, 0.01f);
        description.Activity.SleepThreshold = -1f;
        _handle = _physics.Simulation.Bodies.Add(description);

        IsActive = true;
        _previousPosition = position;
        _viewOffset = 0;
        _wasOnGround = false;
        _snapCooldown = 0;
    }

    /// <summary>Free flight: the view direction drives movement, so looking up flies up.</summary>
    private void MoveNoclip(Vector2 wish, float vertical, bool fast, float dt)
    {
        _previousPosition = _flyPosition;

        var direction = Forward * wish.Y + FlatRight * wish.X + Vector3.UnitY * vertical;
        float length = direction.Length();
        if (length > 1e-4f) direction /= length;

        _noclipVelocity = direction * (NoclipSpeed * (fast ? 3f : 1f));
        _flyPosition += _noclipVelocity * dt;

        OnGround = false;
        _viewOffset = 0f;
        _coyoteTimer = 0f;
    }

    public void Teleport(Vector3 position)
    {
        if (Noclip)
        {
            _flyPosition = position;
            _previousPosition = position;
            return;
        }

        var body = Body;
        body.Pose.Position = position;
        _previousPosition = position;
        _viewOffset = 0;
        _wasOnGround = false;
        _snapCooldown = 0;
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

    /// <summary>
    /// Redirects velocity along the ground plane instead of flattening it to horizontal.
    /// Without this the player leaves a downhill slope every step, falls, lands, and repeats —
    /// the jitter Quake fixes with PM_ClipVelocity.
    /// </summary>
    private void ClipToGroundPlane(ref Vector3 velocity)
    {
        float speed = velocity.Length();
        if (speed < 1e-4f)
        {
            velocity = Vector3.Zero;
            return;
        }

        // Remove the component pushing into the ground, then restore the original speed so
        // that walking down a ramp is exactly as fast as walking along the flat.
        var clipped = velocity - GroundNormal * Vector3.Dot(velocity, GroundNormal);
        float clippedSpeed = clipped.Length();
        velocity = clippedSpeed < 1e-4f ? Vector3.Zero : clipped * (speed / clippedSpeed);
    }

    /// <summary>
    /// Ground snapping: the player was on the ground last substep and is only just off it,
    /// so pull the capsule back down to the surface instead of letting it go ballistic over
    /// the crest of a ramp or a stair edge. The camera eases the drop out like a step up.
    /// </summary>
    private void TryStepDown(ref Vector3 velocity)
    {
        var body = Body;
        var position = body.Pose.Position;
        float halfHeight = HalfHeight;

        if (!CastRay(position, -Vector3.UnitY, halfHeight + GroundSnapDistance, out float t, out var normal)) return;
        if (normal.Y <= MaxGroundSlopeCos) return;

        float targetY = position.Y - t + halfHeight + 0.01f;
        float drop = position.Y - targetY;
        if (drop <= 0.01f) return;

        body.Pose.Position = new Vector3(position.X, targetY, position.Z);
        _previousPosition.Y -= drop;
        _viewOffset = Math.Clamp(_viewOffset - drop, -MaxStepHeight, MaxStepHeight);

        OnGround = true;
        GroundNormal = normal;
        _coyoteTimer = 0.1f;
        velocity.Y = 0;
        ClipToGroundPlane(ref velocity);
    }

    /// <summary>
    /// Walks the capsule up a ledge the solver would otherwise refuse to climb.
    /// Three probes: a shin-height ray to find the obstacle, a downward ray to find the
    /// surface on top of it, and an upward ray to make sure the player fits there.
    /// </summary>
    private void TryStepUp(Vector3 wishDirection, ref Vector3 velocity)
    {
        if (wishDirection.LengthSquared() < 1e-6f) return;

        var body = Body;
        var position = body.Pose.Position;
        float feetY = position.Y - HalfHeight;

        // Is a wall-like surface in the way, just above the feet?
        var shin = new Vector3(position.X, feetY + 0.1f, position.Z);
        if (!CastRay(shin, wishDirection, Radius + 0.2f, out _, out var wallNormal)) return;
        if (wallNormal.Y > MaxGroundSlopeCos) return;   // walkable slope: the solver handles it

        // Find the surface on top of the obstacle, probing from above.
        float probeHeight = MaxStepHeight + 0.1f;
        var probe = position + wishDirection * (Radius + 0.08f);   // wishDirection is horizontal
        probe.Y = feetY + probeHeight;
        if (!CastRay(probe, -Vector3.UnitY, probeHeight, out float toSurface, out var surfaceNormal)) return;
        if (surfaceNormal.Y <= MaxGroundSlopeCos) return;

        float surfaceY = probe.Y - toSurface;
        float rise = surfaceY - feetY;
        if (rise <= 0.02f || rise > MaxStepHeight) return;

        // Refuse the step if the player would not fit standing on that surface.
        float height = 2f * HalfHeight;
        var surfacePoint = new Vector3(probe.X, surfaceY + 0.05f, probe.Z);
        if (CastRay(surfacePoint, Vector3.UnitY, height - 0.05f, out _, out _)) return;

        // Placed absolutely rather than by an increment: repeated attempts across substeps
        // then converge on standing exactly on the tread instead of stacking lift on lift.
        float targetY = surfaceY + HalfHeight + 0.01f;
        float lift = targetY - position.Y;
        if (lift <= 0.01f) return;
        body.Pose.Position = new Vector3(position.X, targetY, position.Z);
        _previousPosition.Y += lift;      // the lift is instant, so do not interpolate through it
        _viewOffset = Math.Clamp(_viewOffset + lift, -MaxStepHeight, MaxStepHeight);
        if (velocity.Y < 0) velocity.Y = 0;

        // Standing on the tread now: keep ground acceleration and friction for the next
        // substep, otherwise a flight of stairs would be climbed at air-control speed.
        OnGround = true;
        GroundNormal = surfaceNormal;
        _coyoteTimer = 0.1f;

        // Over the nose of a step the downward probe can find the tread below and snap the
        // player straight back off the stair, so hold ground snapping off for a moment.
        _snapCooldown = 0.1f;
    }

    private bool CastRay(Vector3 origin, Vector3 direction, float maximumT, out float t, out Vector3 normal)
        => CastRay(origin, direction, maximumT, out t, out normal, out _);

    private bool CastRay(Vector3 origin, Vector3 direction, float maximumT, out float t, out Vector3 normal,
        out CollidableReference collidable)
    {
        var self = new CollidableReference(CollidableMobility.Dynamic, _handle);
        var handler = new ClosestHitExcluding(self);
        _physics.Simulation.RayCast(origin, direction, maximumT, _physics.BufferPool, ref handler, 0);
        t = handler.T;
        normal = handler.Normal;
        collidable = handler.Collidable;
        return handler.Hit;
    }

    private void ProbeGround(Vector3 position)
    {
        // Cast from the capsule centre down past the bottom cap.
        float maximumT = HalfHeight + 0.18f;
        bool hit = CastRay(position, -Vector3.UnitY, maximumT, out _, out var normal, out var collidable);

        OnGround = hit && normal.Y > MaxGroundSlopeCos;
        if (OnGround) GroundCollidable = collidable;
        GroundNormal = OnGround ? Vector3.Normalize(normal) : Vector3.UnitY;
    }
}

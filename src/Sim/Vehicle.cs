using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace UnnamedGame.Sim;

/// <summary>
/// Arcade raycast car: a single rigid box for the chassis plus four spring-damper wheels that
/// are ray-probed against the world each substep. No wheel bodies and no joints — the classic
/// approach, and the one that stays stable at a 120 Hz fixed step.
/// </summary>
public sealed class Vehicle
{
    public const float Mass = 1150f;
    public const float WheelRadius = 0.29f;
    public const float SuspensionRest = 0.34f;    // anchor-to-hub distance with the car at rest
    public const float SuspensionStiffness = 62000f;
    public const float SuspensionDamping = 4200f;
    public const float DriveForce = 7000f;
    public const float ReverseForce = 6000f;
    public const float BrakeForce = 14000f;
    public const float MaxSteering = 0.55f;       // radians at the front wheels
    public const float SteeringSpeed = 3.2f;
    public const float TopSpeed = 28f;            // m/s, engine cuts out above this
    public const float ReverseSpeed = 8f;         // m/s cap when backing up
    public const float TyreFriction = 1.35f;      // peak grip as a multiple of the wheel's load
    public const float AntiRollStiffness = 9000f;
    /// <summary>
    /// Fraction of the contact patch's height below the centre of mass that friction actually
    /// levers against. At 1.0 a hard corner rolls the car onto its roof; halving it keeps
    /// visible body roll without the flip.
    /// </summary>
    private const float RollLeverage = 0.5f;

    public struct Wheel(Vector3 anchor, bool steered, bool powered)
    {
        public Vector3 Anchor = anchor;   // chassis-local suspension attachment
        public bool Steered = steered;
        public bool Powered = powered;
        public float Steer;               // current steering angle
        public float Spin;                // rolling angle, for the visual
        public float Compression;         // 0 = fully extended
        public float HubDistance = SuspensionRest;
        public bool OnGround;
    }

    private readonly PhysicsWorld _physics;
    private readonly BodyHandle _handle;
    private readonly Wheel[] _wheels;

    /// <summary>Height of the chassis centre above the ground when the car rests on its springs.</summary>
    public float CentreHeight { get; }

    public Vehicle(PhysicsWorld physics, Vector3 position, float yaw, Vector3[] wheelAnchors, float centreHeight)
    {
        _physics = physics;
        CentreHeight = centreHeight;

        var box = new Box(1.72f, 1.05f, 4.24f);
        var shape = physics.Simulation.Shapes.Add(box);
        var inertia = box.ComputeInertia(Mass);

        var description = BodyDescription.CreateDynamic(
            new RigidPose(position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw)),
            inertia, shape, 0.01f);
        description.Activity.SleepThreshold = 0.01f;
        _handle = physics.Simulation.Bodies.Add(description);
        PreviousPose = description.Pose;

        _wheels = new Wheel[4];
        for (int i = 0; i < 4; i++)
        {
            bool front = wheelAnchors[i].Z > 0;
            _wheels[i] = new Wheel(wheelAnchors[i], steered: front, powered: !front);
        }
    }

    /// <summary>Chassis pose at the start of the current physics step, for render interpolation.</summary>
    public RigidPose PreviousPose { get; private set; }

    public void SnapshotPose() => PreviousPose = Pose;

    public Matrix4x4 InterpolatedChassisMatrix(float alpha)
    {
        var pose = Pose;
        var position = Vector3.Lerp(PreviousPose.Position, pose.Position, alpha);
        var orientation = Quaternion.Slerp(PreviousPose.Orientation, pose.Orientation, alpha);
        return Matrix4x4.CreateFromQuaternion(orientation) * Matrix4x4.CreateTranslation(position);
    }

    public BodyReference Body => _physics.Simulation.Bodies[_handle];
    public RigidPose Pose => Body.Pose;
    public Vector3 Position => Body.Pose.Position;
    public Vector3 Velocity => Body.Velocity.Linear;
    public ReadOnlySpan<Wheel> Wheels => _wheels;

    // The model faces +Z and its left side is +X, so the car's frame follows the mesh.
    public Vector3 Forward => Vector3.Transform(Vector3.UnitZ, Pose.Orientation);
    public Vector3 Right => Vector3.Transform(-Vector3.UnitX, Pose.Orientation);
    public Vector3 Up => Vector3.Transform(Vector3.UnitY, Pose.Orientation);

    /// <summary>Signed speed along the car's own forward axis, in m/s.</summary>
    public float ForwardSpeed => Vector3.Dot(Velocity, Forward);

    /// <param name="throttle">-1 (reverse) to 1 (accelerate).</param>
    /// <param name="steering">-1 (left) to 1 (right).</param>
    public void Update(float throttle, float steering, bool handbrake, float dt)
    {
        var body = Body;
        body.Awake = true;

        var pose = body.Pose;
        var orientation = pose.Orientation;
        var up = Vector3.Transform(Vector3.UnitY, orientation);

        // Steering eases in, and tightens up less at speed so the car stays controllable.
        float speedFactor = 1f / (1f + MathF.Abs(ForwardSpeed) * 0.045f);
        float targetSteer = -steering * MaxSteering * speedFactor;

        int groundedCount = 0;
        for (int i = 0; i < _wheels.Length; i++)
        {
            ref var wheel = ref _wheels[i];
            if (wheel.Steered)
                wheel.Steer += Math.Clamp(targetSteer - wheel.Steer, -SteeringSpeed * dt, SteeringSpeed * dt);

            var anchor = pose.Position + Vector3.Transform(wheel.Anchor, orientation);
            float maxDistance = SuspensionRest + WheelRadius;

            if (!CastWheel(anchor, -up, maxDistance, out float hitDistance, out var groundNormal))
            {
                wheel.OnGround = false;
                wheel.Compression = 0f;
                wheel.HubDistance = SuspensionRest;
                wheel.Spin += ForwardSpeed / WheelRadius * dt;
                continue;
            }

            wheel.OnGround = true;
            groundedCount++;
            float hubDistance = MathF.Max(hitDistance - WheelRadius, 0f);
            float compression = SuspensionRest - hubDistance;

            var contact = anchor - up * hitDistance;
            var offset = contact - pose.Position;
            var contactVelocity = body.Velocity.Linear + Vector3.Cross(body.Velocity.Angular, offset);

            // Suspension: spring towards the rest length, damped by the compression rate.
            float compressionRate = (compression - wheel.Compression) / dt;
            float springForce = SuspensionStiffness * compression + SuspensionDamping * compressionRate;
            springForce = Math.Clamp(springForce, 0f, SuspensionStiffness * SuspensionRest * 2f);
            body.ApplyImpulse(up * (springForce * dt), offset);

            wheel.Compression = compression;
            wheel.HubDistance = hubDistance;

            // Wheel axes projected onto the contact plane.
            var steerRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, wheel.Steer);
            var forward = Vector3.Transform(Vector3.Transform(Vector3.UnitZ, steerRotation), orientation);
            var side = Vector3.Transform(Vector3.Transform(-Vector3.UnitX, steerRotation), orientation);
            forward = Flatten(forward, groundNormal);
            side = Flatten(side, groundNormal);

            float lateralSpeed = Vector3.Dot(contactVelocity, side);
            float longitudinalSpeed = Vector3.Dot(contactVelocity, forward);
            float massShare = Mass / _wheels.Length;

            // Drive, brake and rolling resistance, as a force at this contact patch.
            float force = 0f;
            if (wheel.Powered && !handbrake)
            {
                if (throttle >= 0 && ForwardSpeed < TopSpeed)
                    force = throttle * DriveForce / 2f;
                else if (throttle < 0 && ForwardSpeed > -ReverseSpeed)
                    force = throttle * ReverseForce / 2f;
            }

            bool braking = handbrake || (throttle > 0 && longitudinalSpeed < -0.5f) || (throttle < 0 && longitudinalSpeed > 0.5f);
            if (braking)
                force = -MathF.Sign(longitudinalSpeed) * BrakeForce / _wheels.Length;
            else if (MathF.Abs(throttle) < 0.01f)
                force = -longitudinalSpeed * 260f;

            // Friction circle: the tyre can only deliver so much before it slides, and the
            // lateral and longitudinal demands share that same budget.
            float maxImpulse = (handbrake ? 0.55f : TyreFriction) * springForce * dt;
            var desired = side * (-lateralSpeed * massShare) + forward * (force * dt);
            float magnitude = desired.Length();
            if (magnitude > maxImpulse && magnitude > 1e-4f)
                desired *= maxImpulse / magnitude;

            var frictionOffset = offset - up * (Vector3.Dot(offset, up) * (1f - RollLeverage));
            body.ApplyImpulse(desired, frictionOffset);

            wheel.Spin += longitudinalSpeed / WheelRadius * dt;
        }

        ApplyAntiRollBar(body, up, dt);

        // Downforce keeps the car planted; without it the light chassis skips over bumps.
        if (groundedCount > 0)
            body.ApplyLinearImpulse(-up * (MathF.Abs(ForwardSpeed) * 90f * dt));

        // Damp the yaw a little so the car does not spin forever once sideways.
        var angular = body.Velocity.Angular;
        body.Velocity.Angular = angular - Vector3.Dot(angular, up) * up * MathF.Min(1f, 2.2f * dt);
    }

    /// <summary>
    /// Couples the two wheels of each axle: the more one side compresses relative to the other,
    /// the harder the bar pushes them back level. This is what stops a hard corner from
    /// unloading the inside wheels entirely.
    /// </summary>
    private void ApplyAntiRollBar(BodyReference body, Vector3 up, float dt)
    {
        var pose = body.Pose;
        for (int axle = 0; axle < 2; axle++)
        {
            ref var left = ref _wheels[axle * 2];
            ref var right = ref _wheels[axle * 2 + 1];
            if (!left.OnGround && !right.OnGround) continue;

            float difference = left.Compression - right.Compression;
            float impulse = difference * AntiRollStiffness * dt;

            body.ApplyImpulse(-up * impulse, Vector3.Transform(left.Anchor, pose.Orientation));
            body.ApplyImpulse(up * impulse, Vector3.Transform(right.Anchor, pose.Orientation));
        }
    }

    private static Vector3 Flatten(Vector3 direction, Vector3 normal)
    {
        var flattened = direction - normal * Vector3.Dot(direction, normal);
        float length = flattened.Length();
        return length < 1e-4f ? direction : flattened / length;
    }

    private bool CastWheel(Vector3 origin, Vector3 direction, float maxDistance, out float distance, out Vector3 normal)
    {
        var self = new CollidableReference(CollidableMobility.Dynamic, _handle);
        var handler = new ClosestHitExcluding(self);
        _physics.Simulation.RayCast(origin, direction, maxDistance, _physics.BufferPool, ref handler, 0);
        distance = handler.T;
        normal = handler.Hit ? Vector3.Normalize(handler.Normal) : Vector3.UnitY;
        return handler.Hit;
    }

    /// <summary>World transform of one wheel, including suspension travel, steering and roll.</summary>
    public Matrix4x4 WheelTransform(int index, in Matrix4x4 chassis)
    {
        ref readonly var wheel = ref _wheels[index];
        var hub = wheel.Anchor - Vector3.UnitY * wheel.HubDistance;
        return Matrix4x4.CreateRotationX(wheel.Spin)
            * Matrix4x4.CreateRotationY(wheel.Steer)
            * Matrix4x4.CreateTranslation(hub)
            * chassis;
    }

    public Matrix4x4 ChassisMatrix()
    {
        var pose = Pose;
        return Matrix4x4.CreateFromQuaternion(pose.Orientation) * Matrix4x4.CreateTranslation(pose.Position);
    }

    /// <summary>Camera yaw that matches the car's heading, for the moment the player gets in.</summary>
    public float Yaw
    {
        get
        {
            var forward = Forward;
            return MathF.Atan2(-forward.X, -forward.Z);
        }
    }

    /// <summary>Chassis-local point to world.</summary>
    public Vector3 ToWorld(Vector3 local)
    {
        var pose = Pose;
        return pose.Position + Vector3.Transform(local, pose.Orientation);
    }
}

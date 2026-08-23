using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;

namespace UnnamedGame.Sim;

/// <summary>Material response for every pair. One global setting is enough for the MVP.</summary>
public struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
{
    public SpringSettings ContactSpringiness;
    public float MaximumRecoveryVelocity;
    public float FrictionCoefficient;

    public void Initialize(Simulation simulation)
    {
        if (ContactSpringiness.AngularFrequency == 0 && ContactSpringiness.TwiceDampingRatio == 0)
        {
            ContactSpringiness = new SpringSettings(30, 1);
            MaximumRecoveryVelocity = 2f;
            FrictionCoefficient = 1f;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => a.Mobility == CollidableMobility.Dynamic || b.Mobility == CollidableMobility.Dynamic;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties pairMaterial) where TManifold : unmanaged, IContactManifold<TManifold>
    {
        pairMaterial.FrictionCoefficient = FrictionCoefficient;
        pairMaterial.MaximumRecoveryVelocity = MaximumRecoveryVelocity;
        pairMaterial.SpringSettings = ContactSpringiness;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref ConvexContactManifold manifold) => true;

    public void Dispose() { }
}

/// <summary>Constant gravity plus a touch of linear/angular damping.</summary>
public struct PoseIntegratorCallbacks(Vector3 gravity, float linearDamping = 0.03f, float angularDamping = 0.03f)
    : IPoseIntegratorCallbacks
{
    private Vector3Wide _gravityWideDt;
    private Vector<float> _linearDampingDt;
    private Vector<float> _angularDampingDt;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        _gravityWideDt = Vector3Wide.Broadcast(gravity * dt);
        _linearDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1 - linearDamping, 0, 1), dt));
        _angularDampingDt = new Vector<float>(MathF.Pow(MathHelper.Clamp(1 - angularDamping, 0, 1), dt));
    }

    public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation,
        BodyInertiaWide localInertia, Vector<int> integrationMask, int workerIndex, Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        velocity.Linear = (velocity.Linear + _gravityWideDt) * _linearDampingDt;
        velocity.Angular *= _angularDampingDt;
    }
}

/// <summary>Owns the Bepu simulation, its buffer pool and its worker threads.</summary>
public sealed class PhysicsWorld : IDisposable
{
    public Simulation Simulation { get; }
    public BufferPool BufferPool { get; }
    private readonly ThreadDispatcher _dispatcher;

    public PhysicsWorld(Vector3 gravity)
    {
        BufferPool = new BufferPool();
        _dispatcher = new ThreadDispatcher(Math.Max(1, Environment.ProcessorCount - 1));
        Simulation = Simulation.Create(BufferPool,
            new NarrowPhaseCallbacks { ContactSpringiness = new SpringSettings(30, 1), MaximumRecoveryVelocity = 2f, FrictionCoefficient = 1f },
            new PoseIntegratorCallbacks(gravity),
            new SolveDescription(8, 1));
    }

    public void Step(float dt) => Simulation.Timestep(dt, _dispatcher);

    /// <summary>Adds an immovable box. Half-extents are half of <paramref name="size"/>.</summary>
    public StaticHandle AddStaticBox(Vector3 center, Vector3 size, Quaternion orientation)
    {
        var shape = Simulation.Shapes.Add(new Box(size.X, size.Y, size.Z));
        return Simulation.Statics.Add(new StaticDescription(center, orientation, shape));
    }

    public BodyHandle AddDynamicBox(Vector3 center, Vector3 size, float mass)
    {
        var box = new Box(size.X, size.Y, size.Z);
        var shape = Simulation.Shapes.Add(box);
        return Simulation.Bodies.Add(BodyDescription.CreateDynamic(
            center, box.ComputeInertia(mass), shape, 0.01f));
    }

    public BodyHandle AddDynamicSphere(Vector3 center, float radius, float mass, Vector3 velocity)
    {
        var sphere = new Sphere(radius);
        var shape = Simulation.Shapes.Add(sphere);
        var description = BodyDescription.CreateDynamic(
            center, sphere.ComputeInertia(mass), shape, 0.01f);
        description.Velocity.Linear = velocity;
        return Simulation.Bodies.Add(description);
    }

    /// <summary>True if anything at all is hit along the ray.</summary>
    public bool RayCastAny(Vector3 origin, Vector3 direction, float maximumT)
    {
        var handler = new ClosestHitExcluding(default);
        Simulation.RayCast(origin, direction, maximumT, BufferPool, ref handler, 0);
        return handler.Hit;
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _dispatcher.Dispose();
        BufferPool.Clear();
    }
}

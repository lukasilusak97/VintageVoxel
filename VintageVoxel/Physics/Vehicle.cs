using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using OpenTK.Windowing.GraphicsLibraryFramework;
using VintageVoxel.Rendering;

namespace VintageVoxel.Physics;

/// <summary>
/// High-level vehicle that owns the Bepu v2 <see cref="Simulation"/>,
/// <see cref="VehicleChassis"/>, <see cref="RaycastSuspension"/>,
/// <see cref="VehicleController"/>, and the visual <see cref="VehicleRenderer"/>.
///
/// Supports enter/leave via the E key with a proximity check.
/// While the player is inside, the camera follows the vehicle and WASD drives it.
/// </summary>
public sealed class Vehicle : IDisposable
{
    // Bepu v2 resources
    private readonly BufferPool _bufferPool;
    private readonly Simulation _simulation;

    // Vehicle subsystems
    private readonly VehicleChassis _chassis;
    private readonly RaycastSuspension _suspension;
    private readonly VehicleController _controller;
    private readonly IVoxelPhysicsQuery _query;

    // Rendering
    private readonly VehicleRenderer _renderer;

    /// <summary>True while the player is driving.</summary>
    public bool IsOccupied { get; private set; }

    /// <summary>Maximum distance from the vehicle centre to enter it.</summary>
    public float InteractRadius { get; set; } = 5f;

    /// <summary>Offset from the chassis centre where the player's camera sits while driving.</summary>
    public Vector3 CameraOffset { get; set; } = new(0f, 2.0f, 0f);

    /// <summary>Current world-space position of the chassis centre.</summary>
    public Vector3 Position => _chassis.Body.Pose.Position;

    /// <summary>Current chassis orientation.</summary>
    public Quaternion Orientation => _chassis.Body.Pose.Orientation;

    /// <summary>Chassis box half-extents for debug drawing.</summary>
    public Vector3 ChassisHalfExtents => _chassis.HalfExtents;

    /// <summary>Current linear velocity.</summary>
    public Vector3 LinearVelocity => _chassis.Body.Velocity.Linear;

    /// <summary>Suspension probe length for debug drawing.</summary>
    public float SuspensionLength => _suspension.SuspensionLength;

    /// <summary>Returns the world-space wheel attachment positions.</summary>
    public Vector3[] GetWheelOffsetsWorld()
    {
        var pose = _chassis.Body.Pose;
        var result = new Vector3[_suspension.WheelOffsets.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = pose.Position + Vector3.Transform(_suspension.WheelOffsets[i], pose.Orientation);
        return result;
    }

    /// <summary>Returns a copy of the per-wheel suspension states.</summary>
    public WheelState[] GetWheelStates()
    {
        var copy = new WheelState[_suspension.WheelStates.Length];
        Array.Copy(_suspension.WheelStates, copy, copy.Length);
        return copy;
    }

    public Vehicle(World world, Vector3 spawnPosition, VehicleRenderer renderer)
    {
        _renderer = renderer;

        _bufferPool = new BufferPool();
        _simulation = Simulation.Create(
            _bufferPool,
            new NarrowPhaseCallbacks(),
            new PoseIntegratorCallbacks(new Vector3(0, -9.81f, 0)),
            new SolveDescription(4, 1));

        _query = new VoxelPhysicsQuery(world);
        _chassis = new VehicleChassis(_simulation, spawnPosition);
        _suspension = new RaycastSuspension(_chassis, _query);
        _controller = new VehicleController(_chassis, _suspension);
    }

    /// <summary>
    /// Attempts to toggle enter/leave. Returns true if the state changed.
    /// </summary>
    /// <param name="playerPos">Player's current eye position (OpenTK).</param>
    public bool TryToggle(OpenTK.Mathematics.Vector3 playerPos)
    {
        if (IsOccupied)
        {
            IsOccupied = false;
            return true;
        }

        var vehiclePos = Position.ToOpenTK();
        float dist = (playerPos - vehiclePos).Length;
        if (dist <= InteractRadius)
        {
            IsOccupied = true;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Runs one physics tick. Only processes vehicle controls when occupied.
    /// </summary>
    public void Update(KeyboardState keyboard, float dt)
    {
        // Clamp dt to prevent physics explosions from frame spikes.
        dt = MathF.Min(dt, 1f / 30f);

        _suspension.Update(dt);

        if (IsOccupied)
            _controller.Update(keyboard, dt);

        _simulation.Timestep(dt);
    }

    /// <summary>
    /// When occupied, positions the camera at the vehicle's location + offset
    /// and locks horizontal look to the vehicle's forward direction.
    /// </summary>
    public void UpdateCamera(Camera camera)
    {
        if (!IsOccupied) return;

        var worldOffset = Vector3.Transform(CameraOffset, Orientation);
        var camPos = Position + worldOffset;
        camera.Position = camPos.ToOpenTK();
    }

    /// <summary>
    /// Gets the exit position — slightly to the left of the vehicle.
    /// </summary>
    public OpenTK.Mathematics.Vector3 GetExitPosition()
    {
        var left = Vector3.Transform(-Vector3.UnitX * 2.5f, Orientation);
        return (Position + left + new Vector3(0, 1.7f, 0)).ToOpenTK();
    }

    /// <summary>Draws the vehicle model.</summary>
    public void Render(Camera camera)
    {
        _renderer.Render(Position, Orientation, camera);
    }

    public void Dispose()
    {
        _chassis.Dispose();
        _simulation.Dispose();
        _bufferPool.Clear();
    }
}

/// <summary>
/// Minimal narrow-phase callbacks required by Bepu v2 Simulation.Create.
/// Allows all contacts to be processed with default material properties.
/// </summary>
internal struct NarrowPhaseCallbacks : BepuPhysics.CollisionDetection.INarrowPhaseCallbacks
{
    public void Initialize(Simulation simulation) { }

    public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin)
        => a.Mobility != CollidableMobility.Static || b.Mobility != CollidableMobility.Static;

    public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB)
        => true;

    public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold,
        out PairMaterialProperties material) where TManifold : unmanaged, BepuPhysics.CollisionDetection.IContactManifold<TManifold>
    {
        material.FrictionCoefficient = 1.0f;
        material.MaximumRecoveryVelocity = 0.5f;
        material.SpringSettings = new BepuPhysics.Constraints.SpringSettings(20f, 1f);
        return true;
    }

    public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB,
        ref BepuPhysics.CollisionDetection.ConvexContactManifold manifold)
        => true;

    public void Dispose() { }
}

/// <summary>
/// Pose integrator callbacks that apply gravity each substep.
/// Required by Bepu v2 Simulation.Create.
/// </summary>
internal struct PoseIntegratorCallbacks : BepuPhysics.IPoseIntegratorCallbacks
{
    private Vector3 _gravity;
    private Vector3Wide _gravityWideDt;
    private Vector3Wide _linearDampingWide;
    private Vector3Wide _angularDampingWide;

    public readonly AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
    public readonly bool AllowSubstepsForUnconstrainedBodies => false;
    public readonly bool IntegrateVelocityForKinematics => false;

    public PoseIntegratorCallbacks(Vector3 gravity) => _gravity = gravity;

    public void Initialize(Simulation simulation) { }

    public void PrepareForIntegration(float dt)
    {
        _gravityWideDt = Vector3Wide.Broadcast(_gravity * dt);
        // Damping factor per substep: velocity *= (1 - damping * dt)
        float linearDamping = MathF.Max(0f, 1f - 1.5f * dt);
        float angularDamping = MathF.Max(0f, 1f - 6.0f * dt);
        _linearDampingWide = Vector3Wide.Broadcast(new Vector3(linearDamping));
        _angularDampingWide = Vector3Wide.Broadcast(new Vector3(angularDamping));
    }

    public void IntegrateVelocity(
        System.Numerics.Vector<int> bodyIndices,
        Vector3Wide position,
        QuaternionWide orientation,
        BodyInertiaWide localInertia,
        System.Numerics.Vector<int> integrationMask,
        int workerIndex,
        System.Numerics.Vector<float> dt,
        ref BodyVelocityWide velocity)
    {
        velocity.Linear = velocity.Linear * _linearDampingWide + _gravityWideDt;
        velocity.Angular *= _angularDampingWide;
    }
}

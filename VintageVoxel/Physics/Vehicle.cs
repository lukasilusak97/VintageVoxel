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
/// <see cref="VehicleController"/>, and the visual <see cref="Rendering.EntityRenderer"/>.
///
/// Vehicles are built piece by piece: a body is placed first (no wheels),
/// then wheels are attached to predefined slots. Physics only runs when at
/// least one wheel is attached. The vehicle can be entered/left via E key.
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
    private readonly EntityRenderer _renderer;
    private readonly VehicleSetup? _setup;

    // Assembly tracking
    private readonly int[] _wheelEntityIds;
    private readonly WheelSetup?[] _wheelSetups;

    /// <summary>Entity ID of the body used to create this vehicle.</summary>
    public int BodyEntityId { get; }

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

    /// <summary>Number of wheel attachment slots defined by the body config.</summary>
    public int WheelSlotCount => _suspension.WheelOffsets.Length;

    /// <summary>Returns true if the given wheel slot has a wheel attached.</summary>
    public bool IsWheelAttached(int slot) => _suspension.ActiveMask[slot];

    /// <summary>Returns the entity ID of the wheel in the given slot, or 0 if empty.</summary>
    public int GetWheelEntityId(int slot) => _wheelEntityIds[slot];

    /// <summary>True when at least one wheel is attached.</summary>
    public bool HasAnyWheels
    {
        get
        {
            for (int i = 0; i < _suspension.ActiveMask.Length; i++)
                if (_suspension.ActiveMask[i]) return true;
            return false;
        }
    }

    /// <summary>Wheel model path from the first attached wheel, or null.</summary>
    public string? WheelModelPath
    {
        get
        {
            for (int i = 0; i < _wheelSetups.Length; i++)
                if (_wheelSetups[i] != null) return _wheelSetups[i]!.Model;
            return null;
        }
    }

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

    public Vehicle(World world, Vector3 spawnPosition, EntityRenderer renderer,
                   VehicleSetup? setup = null, int bodyEntityId = 0)
    {
        _renderer = renderer;
        _setup = setup;
        BodyEntityId = bodyEntityId;
        var s = setup ?? new VehicleSetup();

        InteractRadius = s.InteractRadius;
        CameraOffset = new Vector3(s.CameraOffset.X, s.CameraOffset.Y, s.CameraOffset.Z);

        _bufferPool = new BufferPool();
        _simulation = Simulation.Create(
            _bufferPool,
            new NarrowPhaseCallbacks(),
            new PoseIntegratorCallbacks(new Vector3(0, -9.81f, 0)),
            new SolveDescription(4, 1));

        // Convert wheel slot positions from body config.
        Vector3[]? slotOffsets = null;
        if (s.WheelSlots is { Length: > 0 })
        {
            slotOffsets = new Vector3[s.WheelSlots.Length];
            for (int i = 0; i < s.WheelSlots.Length; i++)
                slotOffsets[i] = new Vector3(s.WheelSlots[i].X, s.WheelSlots[i].Y, s.WheelSlots[i].Z);
        }

        _query = new VoxelPhysicsQuery(world);
        _chassis = new VehicleChassis(_simulation, spawnPosition,
            s.Chassis.Mass, s.Chassis.Width, s.Chassis.Height, s.Chassis.Length);
        _suspension = new RaycastSuspension(_chassis, _query, slotOffsets);
        // Suspension params are applied when wheels are attached.
        _controller = new VehicleController(_chassis, _suspension);
        _controller.DriveForce = s.Controller.DriveForce;
        _controller.BrakeForce = s.Controller.BrakeForce;
        _controller.SteerTorque = s.Controller.SteerTorque;
        _controller.LateralGrip = s.Controller.LateralGrip;

        _wheelEntityIds = new int[_suspension.WheelOffsets.Length];
        _wheelSetups = new WheelSetup[_suspension.WheelOffsets.Length];
    }

    /// <summary>Attaches a wheel to the given slot, activating it for physics.</summary>
    public void AttachWheel(int slotIndex, int entityId, WheelSetup wheelSetup)
    {
        _wheelEntityIds[slotIndex] = entityId;
        _wheelSetups[slotIndex] = wheelSetup;
        _suspension.ActiveMask[slotIndex] = true;

        // Apply suspension settings from the wheel config.
        var susp = wheelSetup.Suspension;
        _suspension.SuspensionLength = susp.SuspensionLength;
        _suspension.RestLength = susp.RestLength;
        _suspension.SpringStiffness = susp.SpringStiffness;
        _suspension.Damping = susp.Damping;
        _suspension.RightingTorque = susp.RightingTorque;
        _suspension.MinGroundClearance = susp.MinGroundClearance;
    }

    /// <summary>Detaches a wheel from the given slot.  Returns the entity ID (0 if empty).</summary>
    public int DetachWheel(int slotIndex)
    {
        if (!_suspension.ActiveMask[slotIndex]) return 0;

        int entityId = _wheelEntityIds[slotIndex];
        _wheelEntityIds[slotIndex] = 0;
        _wheelSetups[slotIndex] = null;
        _suspension.ActiveMask[slotIndex] = false;
        return entityId;
    }

    /// <summary>
    /// Attempts to toggle enter/leave. Returns true if the state changed.
    /// Cannot enter a vehicle with no wheels attached.
    /// </summary>
    /// <param name="playerPos">Player's current eye position (OpenTK).</param>
    public bool TryToggle(OpenTK.Mathematics.Vector3 playerPos)
    {
        if (IsOccupied)
        {
            IsOccupied = false;
            return true;
        }

        if (!HasAnyWheels) return false;

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
    /// Skips the simulation timestep entirely when no wheels are attached.
    /// </summary>
    public void Update(KeyboardState keyboard, float dt)
    {
        // No physics until at least one wheel is attached.
        if (!HasAnyWheels) return;

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

    /// <summary>Draws the vehicle model, advancing any keyframe animations by <paramref name="deltaTime"/>.</summary>
    public void Render(Shader shader, Camera camera, float deltaTime = 0f)
    {
        if (_setup?.BodyModel == null) return;

        var pos = MathConversions.ToOpenTK(Position);
        var rot = MathConversions.ToOpenTK(Orientation);
        var worldRot = OpenTK.Mathematics.Matrix4.CreateFromQuaternion(rot);
        var worldTrans = OpenTK.Mathematics.Matrix4.CreateTranslation(pos);

        // Body
        var bodyMatrix = worldRot * worldTrans;
        _renderer.RenderModel(shader, _setup.BodyModel, ref bodyMatrix, deltaTime);

        // Wheels
        string? wheelPath = WheelModelPath;
        if (wheelPath == null) return;

        var wheelOffsets = GetWheelOffsetsWorld();
        for (int i = 0; i < wheelOffsets.Length; i++)
        {
            if (!_suspension.ActiveMask[i]) continue;
            var wheelPos = MathConversions.ToOpenTK(wheelOffsets[i]);
            var wheelMatrix = worldRot * OpenTK.Mathematics.Matrix4.CreateTranslation(wheelPos);
            _renderer.RenderModel(shader, wheelPath, ref wheelMatrix, deltaTime);
        }
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

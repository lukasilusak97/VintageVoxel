using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace VintageVoxel.Physics;

/// <summary>
/// Bepu v2 dynamic rigid body representing the vehicle chassis.
///
/// Owns a single <see cref="BodyHandle"/> in the <see cref="Simulation"/>,
/// shaped as a <see cref="Box"/>. Ground contact is handled entirely by
/// <see cref="RaycastSuspension"/> (DDA rays + spring forces), so no
/// <see cref="VoxelCollisionWindow"/> statics are needed.
/// </summary>
public sealed class VehicleChassis : IDisposable
{
    private readonly Simulation _simulation;
    private readonly TypedIndex _shapeIndex;

    /// <summary>Handle to the chassis body inside the Bepu simulation.</summary>
    public BodyHandle Handle { get; }

    /// <summary>
    /// Live reference to the chassis body. Read <c>Pose</c> and <c>Velocity</c>
    /// from this, and call <c>ApplyImpulse</c> / <c>ApplyAngularImpulse</c> to
    /// drive the vehicle.
    /// </summary>
    public BodyReference Body => _simulation.Bodies.GetBodyReference(Handle);

    /// <summary>Half-extents of the chassis box (width, height, length).</summary>
    public Vector3 HalfExtents { get; }

    /// <param name="simulation">The Bepu v2 simulation that owns bodies and shapes.</param>
    /// <param name="spawnPosition">Initial world-space position of the chassis centre.</param>
    /// <param name="mass">Mass of the chassis in kg.</param>
    /// <param name="width">Full width of the chassis box (X axis).</param>
    /// <param name="height">Full height of the chassis box (Y axis).</param>
    /// <param name="length">Full length of the chassis box (Z axis).</param>
    public VehicleChassis(
        Simulation simulation,
        Vector3 spawnPosition,
        float mass = 500f,
        float width = 2f,
        float height = 1f,
        float length = 4f)
    {
        _simulation = simulation;

        HalfExtents = new Vector3(width * 0.5f, height * 0.5f, length * 0.5f);

        var box = new Box(width, height, length);
        _shapeIndex = simulation.Shapes.Add(box);

        var inertia = box.ComputeInertia(mass);

        var bodyDesc = BodyDescription.CreateDynamic(
            new RigidPose(spawnPosition),
            inertia,
            new CollidableDescription(_shapeIndex, 0.1f),
            new BodyActivityDescription(0.01f));

        Handle = simulation.Bodies.Add(bodyDesc);
    }

    /// <summary>
    /// Removes the chassis body and its shape from the simulation.
    /// </summary>
    public void Dispose()
    {
        _simulation.Bodies.Remove(Handle);
        _simulation.Shapes.Remove(_shapeIndex);
    }
}

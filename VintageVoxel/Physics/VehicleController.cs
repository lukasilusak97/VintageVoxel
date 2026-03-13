using System.Numerics;
using BepuPhysics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageVoxel.Physics;

/// <summary>
/// Reads OpenTK keyboard input and applies drive, steering, and friction impulses
/// to a <see cref="VehicleChassis"/> via its Bepu v2 <see cref="BodyReference"/>.
///
/// Wheel indices follow <see cref="RaycastSuspension"/>:
/// 0 = front-left, 1 = front-right, 2 = rear-left, 3 = rear-right.
/// </summary>
public sealed class VehicleController
{
    private readonly VehicleChassis _chassis;
    private readonly RaycastSuspension _suspension;

    /// <summary>Forward drive force applied per grounded wheel (Newtons).</summary>
    public float DriveForce { get; set; } = 1000f;

    /// <summary>Braking force applied per grounded wheel when reversing (Newtons).</summary>
    public float BrakeForce { get; set; } = 4000f;

    /// <summary>Angular impulse magnitude for steering (N·m·s per tick).</summary>
    public float SteerTorque { get; set; } = 1500f;

    /// <summary>
    /// Fraction of lateral velocity cancelled per tick [0, 1].
    /// 1 = perfect grip, 0 = ice.
    /// </summary>
    public float LateralGrip { get; set; } = 0.9f;

    public VehicleController(VehicleChassis chassis, RaycastSuspension suspension)
    {
        _chassis = chassis;
        _suspension = suspension;
    }

    /// <summary>
    /// Reads keyboard input and applies drive, steering, and friction impulses.
    /// Call once per physics tick, after <see cref="RaycastSuspension.Update"/>.
    /// </summary>
    public void Update(KeyboardState keyboard, float dt)
    {
        var body = _chassis.Body;
        var pose = body.Pose;
        var velocity = body.Velocity;

        // Body-local axes in world space.
        var worldForward = Vector3.Transform(-Vector3.UnitZ, pose.Orientation);
        var worldRight = Vector3.Transform(Vector3.UnitX, pose.Orientation);
        var worldUp = Vector3.Transform(Vector3.UnitY, pose.Orientation);

        bool anyWheelGrounded = false;
        for (int i = 0; i < _suspension.WheelStates.Length; i++)
        {
            if (_suspension.WheelStates[i].OnGround)
            {
                anyWheelGrounded = true;
                break;
            }
        }

        // --- Acceleration / Braking ---
        float throttle = 0f;
        if (keyboard.IsKeyDown(Keys.W)) throttle += 1f;
        if (keyboard.IsKeyDown(Keys.S)) throttle -= 1f;

        if (throttle != 0f && anyWheelGrounded)
        {
            float forceMag = throttle > 0f ? DriveForce : BrakeForce;
            int groundedCount = 0;
            for (int i = 0; i < _suspension.WheelStates.Length; i++)
                if (_suspension.WheelStates[i].OnGround) groundedCount++;

            var driveImpulse = worldForward * (throttle * forceMag * groundedCount * dt);
            body.ApplyImpulse(driveImpulse, Vector3.Zero);
        }

        // --- Steering ---
        float steer = 0f;
        if (keyboard.IsKeyDown(Keys.A)) steer += 1f;
        if (keyboard.IsKeyDown(Keys.D)) steer -= 1f;

        if (steer != 0f && anyWheelGrounded)
        {
            var angularImpulse = worldUp * (steer * SteerTorque * dt);
            body.ApplyAngularImpulse(angularImpulse);
        }

        // --- Lateral friction / grip ---
        // Cancel sideways sliding at the centre of mass to avoid pitch/roll torque.
        float lateralSpeed = Vector3.Dot(velocity.Linear, worldRight);
        if (anyWheelGrounded && MathF.Abs(lateralSpeed) > 0.001f)
        {
            var cancelImpulse = -worldRight * (lateralSpeed * LateralGrip);
            body.ApplyImpulse(cancelImpulse, Vector3.Zero);
        }
    }
}

using System.Numerics;
using BepuPhysics;

namespace VintageVoxel.Physics;

/// <summary>
/// Per-wheel state produced by <see cref="RaycastSuspension.Update"/>.
/// Stored as a struct to avoid GC pressure in the physics loop.
/// </summary>
public struct WheelState
{
    /// <summary>True when the ray hit ground within the suspension travel.</summary>
    public bool OnGround;

    /// <summary>World-space contact point on the voxel surface.</summary>
    public Vector3 HitPoint;

    /// <summary>Outward surface normal at the contact point.</summary>
    public Vector3 Normal;

    /// <summary>World-space distance from the wheel attachment to the hit point.</summary>
    public float HitDistance;
}

/// <summary>
/// Raycast-based spring suspension for a 4-wheel vehicle.
///
/// Each tick the system probes straight down from each wheel attachment point,
/// checking <see cref="IVoxelPhysicsQuery.IsSolid"/> at small vertical increments.
/// When ground is found within <see cref="SuspensionLength"/>, a spring-damper
/// impulse pushes the chassis upward.
///
/// A hard penetration correction prevents the chassis from ever sinking below
/// the ground surface.
/// </summary>
public sealed class RaycastSuspension
{
    private readonly VehicleChassis _chassis;
    private readonly IVoxelPhysicsQuery _query;

    /// <summary>Local-space wheel attachment points relative to the chassis centre.</summary>
    public readonly Vector3[] WheelOffsets;

    /// <summary>Per-wheel results from the last <see cref="Update"/> call.</summary>
    public readonly WheelState[] WheelStates;

    /// <summary>Per-wheel active mask. Only active (attached) wheels produce suspension forces.</summary>
    public readonly bool[] ActiveMask;

    /// <summary>Maximum downward probe distance from each wheel attachment (world units).</summary>
    public float SuspensionLength { get; set; } = 1.0f;

    /// <summary>Rest length of the spring — the distance at which no force is produced.</summary>
    public float RestLength { get; set; } = 0.6f;

    /// <summary>Spring stiffness coefficient (N/m). Higher = stiffer ride.</summary>
    public float SpringStiffness { get; set; } = 5000f;

    /// <summary>Damping coefficient (N·s/m). Only applied when the spring is extending (rebound).</summary>
    public float Damping { get; set; } = 1000f;

    /// <summary>Torque strength for auto-righting the chassis toward upright.</summary>
    public float RightingTorque { get; set; } = 800f;

    /// <summary>Minimum distance the chassis centre must stay above the ground surface.</summary>
    public float MinGroundClearance { get; set; } = 0.3f;

    /// <summary>Step size for the vertical ground probe (world units).</summary>
    private const float ProbeStep = 1f / 16f;

    public RaycastSuspension(VehicleChassis chassis, IVoxelPhysicsQuery query, Vector3[]? wheelOffsets = null)
    {
        _chassis = chassis;
        _query = query;

        if (wheelOffsets != null)
        {
            WheelOffsets = wheelOffsets;
        }
        else
        {
            var he = chassis.HalfExtents;
            WheelOffsets = new Vector3[]
            {
                new(-he.X, -he.Y, -he.Z), // front-left
                new( he.X, -he.Y, -he.Z), // front-right
                new(-he.X, -he.Y,  he.Z), // rear-left
                new( he.X, -he.Y,  he.Z), // rear-right
            };
        }

        WheelStates = new WheelState[WheelOffsets.Length];
        ActiveMask = new bool[WheelOffsets.Length];
    }

    /// <summary>
    /// Runs one suspension tick: probes for ground, computes spring-damper forces,
    /// applies impulses, and corrects hard penetration.
    /// </summary>
    public void Update(float dt)
    {
        var body = _chassis.Body;
        var pose = body.Pose;
        var velocity = body.Velocity;

        float closestGroundY = float.NegativeInfinity;

        for (int i = 0; i < WheelOffsets.Length; i++)
        {
            ref var state = ref WheelStates[i];

            if (!ActiveMask[i])
            {
                state.OnGround = false;
                continue;
            }

            // Transform the local wheel offset to world space.
            var worldOffset = Vector3.Transform(WheelOffsets[i], pose.Orientation);
            var probeX = pose.Position.X + worldOffset.X;
            var probeZ = pose.Position.Z + worldOffset.Z;
            var probeTopY = pose.Position.Y + worldOffset.Y;

            // Probe straight down from the wheel attachment point.
            bool hit = ProbeDown(probeX, probeTopY, probeZ, SuspensionLength, out float groundSurfaceY);

            if (!hit)
            {
                state.OnGround = false;
                continue;
            }

            float hitDistance = probeTopY - groundSurfaceY;
            if (hitDistance < 0f) hitDistance = 0f;

            state.OnGround = true;
            state.HitPoint = new Vector3(probeX, groundSurfaceY, probeZ);
            state.Normal = Vector3.UnitY;
            state.HitDistance = hitDistance;

            if (groundSurfaceY > closestGroundY)
                closestGroundY = groundSurfaceY;

            // Spring compression: positive when closer than rest length.
            float compression = RestLength - hitDistance;
            if (compression <= 0f) continue;

            // Only damp rebound (moving away from ground), not approach.
            // This lets the spring fully push back during initial contact.
            float verticalVel = velocity.Linear.Y;
            float dampingForce = 0f;
            if (verticalVel > 0f) // moving up = extending spring
                dampingForce = verticalVel * Damping;

            float force = compression * SpringStiffness - dampingForce;
            if (force <= 0f) continue;

            var impulse = Vector3.UnitY * (force * dt);
            body.ApplyImpulse(impulse, Vector3.Zero);
        }

        // Hard penetration correction: if the chassis centre is too close
        // to (or below) the ground, snap it up and kill downward velocity.
        if (closestGroundY > float.NegativeInfinity)
        {
            float minY = closestGroundY + MinGroundClearance;
            if (pose.Position.Y < minY)
            {
                body.Pose.Position = new Vector3(pose.Position.X, minY, pose.Position.Z);
                if (velocity.Linear.Y < 0f)
                    body.Velocity.Linear = new Vector3(velocity.Linear.X, 0f, velocity.Linear.Z);
            }
        }

        // Auto-righting torque: only apply when at least one wheel is active.
        bool anyActive = false;
        for (int i = 0; i < ActiveMask.Length; i++)
            if (ActiveMask[i]) { anyActive = true; break; }

        if (anyActive)
        {
            var bodyUp = Vector3.Transform(Vector3.UnitY, pose.Orientation);
            var rightingAxis = Vector3.Cross(bodyUp, Vector3.UnitY);
            float sinAngle = rightingAxis.Length();
            if (sinAngle > 0.001f)
            {
                rightingAxis /= sinAngle;
                body.ApplyAngularImpulse(rightingAxis * (sinAngle * RightingTorque * dt));
            }
        }
    }

    /// <summary>
    /// Probes straight down from (<paramref name="x"/>, <paramref name="topY"/>, <paramref name="z"/>)
    /// through the voxel grid, returning the Y coordinate of the top of the first solid
    /// voxel found within <paramref name="maxDepth"/>.
    /// </summary>
    private bool ProbeDown(float x, float topY, float z, float maxDepth, out float groundSurfaceY)
    {
        groundSurfaceY = 0f;
        float bottomY = topY - maxDepth;

        // Iterate downward in 1/16-block steps (one layer each).
        for (float y = topY; y >= bottomY; y -= ProbeStep)
        {
            var probe = new System.Numerics.Vector3(x, y, z);
            if (_query.IsSolid(probe))
            {
                // Found solid. The surface is at the top of this solid cell.
                // Walk back up to find the exact surface Y.
                int bx = (int)MathF.Floor(x);
                int by = (int)MathF.Floor(y);
                int bz = (int)MathF.Floor(z);

                // The surface is at by + topOffset of the block.
                // For a full block that's by + 1.0; for partial it's by + layer/16.
                // But we know the probe at y was solid, so the surface is above y.
                // Use the block's actual top.
                // Re-query the block to get its TopOffset.
                var blockProbe = new System.Numerics.Vector3(bx + 0.5f, by + 0.5f, bz + 0.5f);
                if (_query.IsSolid(blockProbe))
                {
                    // Surface is approximately at the top of the solid region.
                    // Walk up from y in fine steps to find the first non-solid point.
                    for (float sy = y; sy <= y + 1.0f; sy += ProbeStep)
                    {
                        if (!_query.IsSolid(new System.Numerics.Vector3(x, sy, z)))
                        {
                            groundSurfaceY = sy;
                            return true;
                        }
                    }
                    // Entire column above is solid (rare) — surface is 1 block up.
                    groundSurfaceY = y + 1.0f;
                    return true;
                }

                groundSurfaceY = y + ProbeStep;
                return true;
            }
        }

        return false;
    }
}

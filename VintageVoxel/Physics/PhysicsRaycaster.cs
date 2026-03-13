using SNVector3 = System.Numerics.Vector3;

namespace VintageVoxel.Physics;

/// <summary>
/// 3D DDA (Digital Differential Analyzer) raycaster for the vehicle physics system.
///
/// Uses an anisotropic voxel grid: X/Z cells are 1 world unit wide, Y cells are
/// 1/16 world units tall — matching the engine's 16-layer block subdivision.
/// Queries <see cref="IVoxelPhysicsQuery.IsSolid"/> directly rather than Bepu's
/// broadphase, preventing false collisions with the pooled statics from
/// <c>VoxelCollisionWindow</c>.
/// </summary>
public static class PhysicsRaycaster
{
    private const float LayerHeight = 1f / 16f;
    private const float InvLayerHeight = 16f;

    /// <summary>
    /// Casts a ray from <paramref name="origin"/> along <paramref name="direction"/>
    /// through the voxel grid, stepping at block resolution on X/Z and at 1/16-block
    /// (layer) resolution on Y.
    /// </summary>
    /// <param name="query">Voxel solidity oracle.</param>
    /// <param name="origin">Ray start in world space.</param>
    /// <param name="direction">Ray direction (need not be normalised).</param>
    /// <param name="maxDistance">Maximum ray travel in world units.</param>
    /// <param name="hitPoint">World-space point where the ray first enters solid geometry.</param>
    /// <param name="normal">Outward face normal of the voxel surface that was hit.</param>
    /// <returns>True if a solid voxel was hit within <paramref name="maxDistance"/>.</returns>
    public static bool Raycast(
        IVoxelPhysicsQuery query,
        SNVector3 origin,
        SNVector3 direction,
        float maxDistance,
        out SNVector3 hitPoint,
        out SNVector3 normal)
    {
        hitPoint = default;
        normal = default;

        float len = direction.Length();
        if (len < 1e-8f) return false;
        SNVector3 dir = direction / len;

        // Step direction per axis (+1 or -1; 0 when the ray is axis-parallel).
        int stepX = Math.Sign(dir.X);
        int stepY = Math.Sign(dir.Y);
        int stepZ = Math.Sign(dir.Z);

        // Current cell indices.
        // X/Z: one cell = 1 world unit.
        // Y:   one cell = 1/16 world unit (one layer).
        int ix = (int)MathF.Floor(origin.X);
        int iy = (int)MathF.Floor(origin.Y * InvLayerHeight);
        int iz = (int)MathF.Floor(origin.Z);

        // tDelta: ray parameter t needed to traverse one full cell in each axis.
        float tDeltaX = stepX != 0 ? 1f / MathF.Abs(dir.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? LayerHeight / MathF.Abs(dir.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? 1f / MathF.Abs(dir.Z) : float.PositiveInfinity;

        // Fractional position within the current cell [0, 1).
        float fractX = origin.X - MathF.Floor(origin.X);
        float fractY = origin.Y * InvLayerHeight - MathF.Floor(origin.Y * InvLayerHeight);
        float fractZ = origin.Z - MathF.Floor(origin.Z);

        // tMax: ray parameter at the first boundary crossing per axis.
        float tMaxX = stepX != 0
            ? (stepX > 0 ? 1f - fractX : fractX) / MathF.Abs(dir.X)
            : float.PositiveInfinity;
        float tMaxY = stepY != 0
            ? (stepY > 0 ? 1f - fractY : fractY) * LayerHeight / MathF.Abs(dir.Y)
            : float.PositiveInfinity;
        float tMaxZ = stepZ != 0
            ? (stepZ > 0 ? 1f - fractZ : fractZ) / MathF.Abs(dir.Z)
            : float.PositiveInfinity;

        // Check the origin cell — if the ray starts inside solid geometry, report
        // immediately with a zero-length hit.
        SNVector3 probe = CellCenter(ix, iy, iz);
        if (query.IsSolid(probe))
        {
            hitPoint = origin;
            normal = SNVector3.Zero;
            return true;
        }

        while (true)
        {
            // Pick the axis whose next boundary is nearest.
            float t;
            int axis;

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { t = tMaxX; axis = 0; }
            else if (tMaxY <= tMaxZ) { t = tMaxY; axis = 1; }
            else { t = tMaxZ; axis = 2; }

            if (t > maxDistance) break;

            // Advance into the next cell and record the face normal.
            switch (axis)
            {
                case 0:
                    ix += stepX;
                    tMaxX += tDeltaX;
                    normal = new SNVector3(-stepX, 0, 0);
                    break;
                case 1:
                    iy += stepY;
                    tMaxY += tDeltaY;
                    normal = new SNVector3(0, -stepY, 0);
                    break;
                default:
                    iz += stepZ;
                    tMaxZ += tDeltaZ;
                    normal = new SNVector3(0, 0, -stepZ);
                    break;
            }

            probe = CellCenter(ix, iy, iz);
            if (query.IsSolid(probe))
            {
                hitPoint = origin + dir * t;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns the world-space centre of the cell at the given indices.
    /// X/Z cells are 1 unit wide; Y cells are 1/16 unit tall.
    /// </summary>
    private static SNVector3 CellCenter(int ix, int iy, int iz)
        => new(ix + 0.5f, (iy + 0.5f) * LayerHeight, iz + 0.5f);
}

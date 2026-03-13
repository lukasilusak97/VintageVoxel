using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Voxel DDA (Digital Differential Analyzer) raycaster.
///
/// The DDA algorithm (Amanatides &amp; Woo, "A Fast Voxel Traversal Algorithm for Ray
/// Tracing", 1987) steps through the voxel grid one cell at a time along the ray,
/// always advancing to the nearest axis-aligned boundary next.  This guarantees no
/// voxels are skipped — unlike naive ray-marching with fixed step sizes — and is
/// O(distance) rather than O(distance²).
///
/// WHY not just march a small fixed step?
///   Fixed steps miss thin voxels when the step is too large, and waste time when
///   it is too small.  DDA is exact: it hits every voxel the ray passes through.
/// </summary>
public static class Raycaster
{
    /// <summary>
    /// Result returned by <see cref="Cast"/>.
    /// When <see cref="Hit"/> is false the other fields are zero-initialised.
    /// </summary>
    public readonly struct HitResult
    {
        /// <summary>Whether the ray hit a solid block within the max distance.</summary>
        public readonly bool Hit;

        /// <summary>World-space integer coordinates of the solid block that was hit.</summary>
        public readonly Vector3i BlockPos;

        /// <summary>
        /// Outward face normal of the voxel face the ray entered through.
        /// Each component is -1, 0, or +1.  Used to compute the adjacent position
        /// for block placement.
        /// </summary>
        public readonly Vector3i Normal;

        public HitResult(Vector3i blockPos, Vector3i normal)
        {
            Hit = true;
            BlockPos = blockPos;
            Normal = normal;
        }
    }

    /// <summary>
    /// Casts a ray from <paramref name="origin"/> along <paramref name="direction"/>
    /// through <paramref name="world"/>, returning the first solid block encountered
    /// within <paramref name="maxDistance"/> world units.
    /// </summary>
    /// <param name="origin">Ray start (camera eye position).</param>
    /// <param name="direction">Ray direction — does not need to be normalised.</param>
    /// <param name="world">World to query for block solidity.</param>
    /// <param name="maxDistance">Maximum reach in world units (default 8).</param>
    public static HitResult Cast(Vector3 origin, Vector3 direction, World world,
                                 float maxDistance = 8f)
    {
        Vector3 dir = Vector3.Normalize(direction);

        // Current voxel coordinates — start at the voxel that contains the origin.
        int ix = (int)MathF.Floor(origin.X);
        int iy = (int)MathF.Floor(origin.Y);
        int iz = (int)MathF.Floor(origin.Z);

        // Step direction: +1 or -1 per axis.  Zero means the ray is axis-aligned and
        // will never cross a boundary in that axis — we use Infinity for tMax/tDelta.
        int stepX = Math.Sign(dir.X);
        int stepY = Math.Sign(dir.Y);
        int stepZ = Math.Sign(dir.Z);

        // tMax: the ray parameter t at which the ray first crosses a boundary in each
        // axis, measured from the ray origin.
        // For a positive step the next X boundary is at floor(origin.X)+1;
        // for a negative step it is at floor(origin.X) (the boundary behind the origin).
        float tMaxX = stepX != 0
            ? (stepX > 0
                ? MathF.Floor(origin.X) + 1f - origin.X
                : origin.X - MathF.Floor(origin.X))
              / MathF.Abs(dir.X)
            : float.PositiveInfinity;

        float tMaxY = stepY != 0
            ? (stepY > 0
                ? MathF.Floor(origin.Y) + 1f - origin.Y
                : origin.Y - MathF.Floor(origin.Y))
              / MathF.Abs(dir.Y)
            : float.PositiveInfinity;

        float tMaxZ = stepZ != 0
            ? (stepZ > 0
                ? MathF.Floor(origin.Z) + 1f - origin.Z
                : origin.Z - MathF.Floor(origin.Z))
              / MathF.Abs(dir.Z)
            : float.PositiveInfinity;

        // tDelta: how far the ray must travel (in t) to cross one full voxel in each axis.
        float tDeltaX = stepX != 0 ? 1f / MathF.Abs(dir.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? 1f / MathF.Abs(dir.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? 1f / MathF.Abs(dir.Z) : float.PositiveInfinity;

        // The face normal is the inward normal of the face we just crossed — stored as the
        // OUTWARD normal of the entered voxel face (negated step direction).
        Vector3i normal = Vector3i.Zero;

        while (true)
        {
            // Pick the axis whose boundary is nearest along the ray.
            float t;
            int axis; // 0=X, 1=Y, 2=Z

            if (tMaxX <= tMaxY && tMaxX <= tMaxZ) { t = tMaxX; axis = 0; }
            else if (tMaxY <= tMaxZ) { t = tMaxY; axis = 1; }
            else { t = tMaxZ; axis = 2; }

            // Exceeded reach — no hit.
            if (t > maxDistance) break;

            // Advance into the next voxel and record which face we entered through.
            switch (axis)
            {
                case 0: ix += stepX; tMaxX += tDeltaX; normal = new Vector3i(-stepX, 0, 0); break;
                case 1: iy += stepY; tMaxY += tDeltaY; normal = new Vector3i(0, -stepY, 0); break;
                case 2: iz += stepZ; tMaxZ += tDeltaZ; normal = new Vector3i(0, 0, -stepZ); break;
            }

            if (!world.GetBlock(ix, iy, iz).IsEmpty)
            {
                return new HitResult(new Vector3i(ix, iy, iz), normal);
            }
        }

        return default; // No solid block found within maxDistance.
    }
}

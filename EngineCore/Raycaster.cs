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
        /// Outward face normal of the voxel (or sub-voxel) face the ray entered through.
        /// Each component is -1, 0, or +1.  Used to compute the adjacent position
        /// for block placement.
        /// </summary>
        public readonly Vector3i Normal;

        /// <summary>
        /// True when the hit block is a chiseled container (Block.ChiseledId).
        /// <see cref="SubVoxelPos"/> and <see cref="SubNormal"/> are valid only in this case.
        /// </summary>
        public readonly bool IsChiseled;

        /// <summary>
        /// Sub-voxel coordinates [0, 15]³ within the chiseled block that was hit.
        /// Zero-initialised when <see cref="IsChiseled"/> is false.
        /// </summary>
        public readonly Vector3i SubVoxelPos;

        /// <summary>
        /// Outward face normal of the specific sub-voxel face the ray entered.
        /// Equal to <see cref="Normal"/> for hits from outside the chiseled block.
        /// </summary>
        public readonly Vector3i SubNormal;

        /// <summary>Constructs a regular (non-chiseled) block hit.</summary>
        public HitResult(Vector3i blockPos, Vector3i normal)
        {
            Hit = true;
            BlockPos = blockPos;
            Normal = normal;
            IsChiseled = false;
            SubVoxelPos = Vector3i.Zero;
            SubNormal = normal;
        }

        /// <summary>Constructs a chiseled sub-voxel hit.</summary>
        public HitResult(Vector3i blockPos, Vector3i outerNormal,
                         Vector3i subVoxelPos, Vector3i subNormal)
        {
            Hit = true;
            BlockPos = blockPos;
            Normal = outerNormal;
            IsChiseled = true;
            SubVoxelPos = subVoxelPos;
            SubNormal = subNormal;
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
                var hitBlock = world.GetBlock(ix, iy, iz);
                if (hitBlock.Id == Block.ChiseledId)
                {
                    // Attempt a sub-voxel DDA inside the chiseled container.
                    // If no filled sub-voxel is hit (all chiseled away) we fall
                    // through and the outer loop continues past this block.
                    var subResult = CastSubVoxel(origin, dir, ix, iy, iz, normal, world);
                    if (subResult.Hit) return subResult;
                }
                else
                {
                    return new HitResult(new Vector3i(ix, iy, iz), normal);
                }
            }
        }

        return default; // No solid block found within maxDistance.
    }

    // -----------------------------------------------------------------------
    // Phase 13: Sub-voxel DDA for chiseled blocks
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs a DDA inside the 16×16×16 sub-voxel grid of the chiseled block at
    /// (<paramref name="blockX"/>, <paramref name="blockY"/>, <paramref name="blockZ"/>).
    ///
    /// Returns a <see cref="HitResult"/> whose <see cref="HitResult.IsChiseled"/>
    /// is true when a filled sub-voxel is hit.  Returns default (Hit = false)
    /// when every sub-voxel along the ray has been removed.
    ///
    /// The sub-voxel DDA parameters are derived by scaling the parent ray into
    /// the N×N×N integer lattice: one sub-voxel = 1/N world units, so
    /// tDelta_sub = tDelta_world / N.
    /// </summary>
    private static HitResult CastSubVoxel(
        Vector3 origin, Vector3 dir,
        int blockX, int blockY, int blockZ,
        Vector3i blockEntryNormal, World world)
    {
        var chiseled = world.GetChiselData(blockX, blockY, blockZ);
        if (chiseled == null) return default;

        const int N = ChiseledBlockData.SubSize;
        const float Nf = N;

        // ---- Compute the ray entry point into this block ----
        // Use the slab intersection method: entry time = max of per-axis entry times.
        float tEntryX = MathF.Abs(dir.X) > 1e-10f
            ? (dir.X > 0 ? (blockX - origin.X) : (blockX + 1f - origin.X)) / dir.X
            : float.NegativeInfinity;
        float tEntryY = MathF.Abs(dir.Y) > 1e-10f
            ? (dir.Y > 0 ? (blockY - origin.Y) : (blockY + 1f - origin.Y)) / dir.Y
            : float.NegativeInfinity;
        float tEntryZ = MathF.Abs(dir.Z) > 1e-10f
            ? (dir.Z > 0 ? (blockZ - origin.Z) : (blockZ + 1f - origin.Z)) / dir.Z
            : float.NegativeInfinity;

        // clamp to 0 when the origin is already inside the block
        float tEntry = Math.Max(0f, Math.Max(tEntryX, Math.Max(tEntryY, tEntryZ)));

        // Tiny nudge pushes the sample point just inside the block face.
        float epx = origin.X + dir.X * (tEntry + 1e-4f);
        float epy = origin.Y + dir.Y * (tEntry + 1e-4f);
        float epz = origin.Z + dir.Z * (tEntry + 1e-4f);

        // Map to sub-voxel floating-point coordinates and clamp to [0, N).
        float sox = Math.Clamp((epx - blockX) * Nf, 0f, Nf - 1e-5f);
        float soy = Math.Clamp((epy - blockY) * Nf, 0f, Nf - 1e-5f);
        float soz = Math.Clamp((epz - blockZ) * Nf, 0f, Nf - 1e-5f);

        int six = (int)MathF.Floor(sox);
        int siy = (int)MathF.Floor(soy);
        int siz = (int)MathF.Floor(soz);

        // Check the starting sub-voxel (handles origin-inside-block case).
        if (chiseled.Get(six, siy, siz))
            return new HitResult(
                new Vector3i(blockX, blockY, blockZ), blockEntryNormal,
                new Vector3i(six, siy, siz), blockEntryNormal);

        // ---- Standard DDA in sub-voxel integer space ----
        int stepX = Math.Sign(dir.X);
        int stepY = Math.Sign(dir.Y);
        int stepZ = Math.Sign(dir.Z);

        // tDelta: t-distance to cross one sub-voxel along each axis.
        float tDeltaSubX = stepX != 0 ? 1f / (Nf * MathF.Abs(dir.X)) : float.PositiveInfinity;
        float tDeltaSubY = stepY != 0 ? 1f / (Nf * MathF.Abs(dir.Y)) : float.PositiveInfinity;
        float tDeltaSubZ = stepZ != 0 ? 1f / (Nf * MathF.Abs(dir.Z)) : float.PositiveInfinity;

        // tMax: t at the first sub-voxel boundary from the entry point.
        float tMaxSubX = stepX != 0
            ? (stepX > 0 ? MathF.Floor(sox) + 1f - sox : sox - MathF.Floor(sox))
              / (Nf * MathF.Abs(dir.X))
            : float.PositiveInfinity;
        float tMaxSubY = stepY != 0
            ? (stepY > 0 ? MathF.Floor(soy) + 1f - soy : soy - MathF.Floor(soy))
              / (Nf * MathF.Abs(dir.Y))
            : float.PositiveInfinity;
        float tMaxSubZ = stepZ != 0
            ? (stepZ > 0 ? MathF.Floor(soz) + 1f - soz : soz - MathF.Floor(soz))
              / (Nf * MathF.Abs(dir.Z))
            : float.PositiveInfinity;

        Vector3i subNormal = Vector3i.Zero;
        int maxSteps = N * 3; // worst-case diagonal traversal

        for (int step = 0; step < maxSteps; step++)
        {
            int axis;
            if (tMaxSubX <= tMaxSubY && tMaxSubX <= tMaxSubZ) axis = 0;
            else if (tMaxSubY <= tMaxSubZ) axis = 1;
            else axis = 2;

            switch (axis)
            {
                case 0: six += stepX; tMaxSubX += tDeltaSubX; subNormal = new Vector3i(-stepX, 0, 0); break;
                case 1: siy += stepY; tMaxSubY += tDeltaSubY; subNormal = new Vector3i(0, -stepY, 0); break;
                case 2: siz += stepZ; tMaxSubZ += tDeltaSubZ; subNormal = new Vector3i(0, 0, -stepZ); break;
            }

            // Exited the sub-voxel grid — no hit inside this chiseled block.
            if (six < 0 || six >= N || siy < 0 || siy >= N || siz < 0 || siz >= N)
                break;

            if (chiseled.Get(six, siy, siz))
                return new HitResult(
                    new Vector3i(blockX, blockY, blockZ), subNormal,
                    new Vector3i(six, siy, siz), subNormal);
        }

        return default; // All sub-voxels along the ray are empty.
    }
}

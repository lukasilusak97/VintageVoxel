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

        var dda = DdaTraversal.Initialize(origin, dir);

        while (true)
        {
            float t = dda.Step(ref ix, ref iy, ref iz);

            // Exceeded reach — no hit.
            if (t > maxDistance) break;

            if (!world.GetBlock(ix, iy, iz).IsEmpty)
            {
                var hitBlock = world.GetBlock(ix, iy, iz);
                if (hitBlock.Id == Block.ChiseledId)
                {
                    // Attempt a sub-voxel DDA inside the chiseled container.
                    // If no filled sub-voxel is hit (all chiseled away) we fall
                    // through and the outer loop continues past this block.
                    var subResult = CastSubVoxel(origin, dir, ix, iy, iz, dda.Normal, world);
                    if (subResult.Hit) return subResult;
                }
                else
                {
                    return new HitResult(new Vector3i(ix, iy, iz), dda.Normal);
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
        // cellSize = 1/N so that t-values remain in world space.
        var dda = DdaTraversal.Initialize(new Vector3(sox, soy, soz), dir, 1f / Nf);
        int maxSteps = N * 3; // worst-case diagonal traversal

        for (int step = 0; step < maxSteps; step++)
        {
            dda.Step(ref six, ref siy, ref siz);

            // Exited the sub-voxel grid — no hit inside this chiseled block.
            if (six < 0 || six >= N || siy < 0 || siy >= N || siz < 0 || siz >= N)
                break;

            if (chiseled.Get(six, siy, siz))
                return new HitResult(
                    new Vector3i(blockX, blockY, blockZ), dda.Normal,
                    new Vector3i(six, siy, siz), dda.Normal);
        }

        return default; // All sub-voxels along the ray are empty.
    }
}

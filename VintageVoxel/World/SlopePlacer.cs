using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Post-generation pass that examines height transitions between adjacent surface
/// blocks and replaces full cubes with the appropriate slope shape.
///
/// Must be called only after all 8 horizontal neighbours of the target chunk are
/// loaded — it reads surface heights across chunk boundaries via World.GetBlock.
///
/// Classification rules (1-block drops only; 2+ drops remain as cliffs):
///   single  cardinal  drop (one  neighbour exactly 1 lower) → directional ramp
///   two adjacent cardinal drops (L-shape)                   → outer corner
///   all 4 cardinals same height but one diagonal is 1 lower  → inner corner
///   anything else                                            → leave as Cube
/// </summary>
public static class SlopePlacer
{
    /// <summary>
    /// Scans the centre chunk (identified by <paramref name="chunkPos"/>) and writes
    /// slope shapes to every surface block whose height-transition pattern matches
    /// a ramp, outer corner, or inner corner.
    ///
    /// Requires: all 8 horizontal neighbour chunks are present in <paramref name="world"/>.
    /// </summary>
    public static void PlaceSlopes(World world, Vector3i chunkPos)
    {
        if (!world.Chunks.TryGetValue(chunkPos, out Chunk? chunk)) return;

        int startWx = chunkPos.X * Chunk.Size;
        int startWz = chunkPos.Z * Chunk.Size;

        for (int z = 0; z < Chunk.Size; z++)
            for (int x = 0; x < Chunk.Size; x++)
            {
                int wx = startWx + x;
                int wz = startWz + z;

                // Find the surface block Y for this column inside the chunk.
                int surfaceY = -1;
                for (int y = Chunk.Size - 1; y >= 0; y--)
                {
                    ref Block b = ref chunk.GetBlock(x, y, z);
                    if (!b.IsEmpty && !b.IsTransparent)
                    {
                        surfaceY = y;
                        break;
                    }
                }
                if (surfaceY < 0) continue;

                // Sample the surface heights of the 8 neighbours using world coords
                // (this automatically crosses chunk boundaries).
                int hN = GetSurfaceY(world, wx, wz - 1); // North (-Z)
                int hS = GetSurfaceY(world, wx, wz + 1); // South (+Z)
                int hE = GetSurfaceY(world, wx + 1, wz); // East  (+X)
                int hW = GetSurfaceY(world, wx - 1, wz); // West  (-X)
                int hNE = GetSurfaceY(world, wx + 1, wz - 1);
                int hNW = GetSurfaceY(world, wx - 1, wz - 1);
                int hSE = GetSurfaceY(world, wx + 1, wz + 1);
                int hSW = GetSurfaceY(world, wx - 1, wz + 1);

                SlopeShape shape = ClassifyShape(surfaceY, hN, hS, hE, hW, hNE, hNW, hSE, hSW);
                if (shape != SlopeShape.Cube)
                    chunk.SetShape(x, surfaceY, z, shape);
            }
    }

    // -------------------------------------------------------------------------
    // Shape classification
    // -------------------------------------------------------------------------

    private static SlopeShape ClassifyShape(
        int h,
        int hN, int hS, int hE, int hW,
        int hNE, int hNW, int hSE, int hSW)
    {
        // Drops: true when the neighbour is exactly 1 block lower.
        bool dropN = hN == h - 1;
        bool dropS = hS == h - 1;
        bool dropE = hE == h - 1;
        bool dropW = hW == h - 1;

        int cardinalDrops = (dropN ? 1 : 0) + (dropS ? 1 : 0)
                          + (dropE ? 1 : 0) + (dropW ? 1 : 0);

        // --- Single cardinal drop → ramp toward the low side ---
        if (cardinalDrops == 1)
        {
            if (dropN) return SlopeShape.RampN;  // high N side faces the drop
            if (dropS) return SlopeShape.RampS;
            if (dropE) return SlopeShape.RampE;
            if (dropW) return SlopeShape.RampW;
        }

        // --- Two adjacent (L-shaped) cardinal drops → outer corner ---
        if (cardinalDrops == 2)
        {
            if (dropN && dropE) return SlopeShape.OuterCornerNE;
            if (dropN && dropW) return SlopeShape.OuterCornerNW;
            if (dropS && dropE) return SlopeShape.OuterCornerSE;
            if (dropS && dropW) return SlopeShape.OuterCornerSW;
            // Opposite cardinal drops (N+S or E+W) = ridge, not a corner.
        }

        // --- All cardinals same, one diagonal exactly 1 lower → inner corner ---
        if (cardinalDrops == 0)
        {
            bool dropNE = hNE == h - 1;
            bool dropNW = hNW == h - 1;
            bool dropSE = hSE == h - 1;
            bool dropSW = hSW == h - 1;

            int diagDrops = (dropNE ? 1 : 0) + (dropNW ? 1 : 0)
                          + (dropSE ? 1 : 0) + (dropSW ? 1 : 0);

            if (diagDrops == 1)
            {
                if (dropNE) return SlopeShape.InnerCornerNE;
                if (dropNW) return SlopeShape.InnerCornerNW;
                if (dropSE) return SlopeShape.InnerCornerSE;
                if (dropSW) return SlopeShape.InnerCornerSW;
            }
        }

        return SlopeShape.Cube;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the Y of the highest solid, non-transparent block in the column at (wx, wz).
    /// Returns -1 if the column is all-air in the loaded range.
    /// </summary>
    private static int GetSurfaceY(World world, int wx, int wz)
    {
        for (int y = Chunk.Size - 1; y >= 0; y--)
        {
            Block b = world.GetBlock(wx, y, wz);
            if (!b.IsEmpty && !b.IsTransparent)
                return y;
        }
        return -1;
    }
}

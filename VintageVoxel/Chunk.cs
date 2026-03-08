namespace VintageVoxel;

/// <summary>
/// A 32 x 32 x 32 region of blocks stored in a single flat array.
///
/// WHY flat over 3-D array?
///   A 3-D array (Block[,,]) allocates a managed object with three-level indirection.
///   A 1-D array is one contiguous block of memory; sequential access (as the mesher
///   does) stays in CPU cache, which is critical when iterating 32 768 blocks per frame.
///
/// Index formula:  index = x + Size * (y + Size * z)
///   X varies fastest (innermost), Z slowest (outermost).
///   This layout means a row of X values for fixed (Y, Z) is contiguous, matching
///   the order the mesher's inner loop walks.
/// </summary>
public class Chunk
{
    public const int Size = 32;
    public const int Volume = Size * Size * Size; // 32 768

    /// <summary>World-space origin of this chunk (in chunk units, not block units).</summary>
    public readonly OpenTK.Mathematics.Vector3i Position;

    private readonly Block[] _blocks = new Block[Volume];

    public Chunk(OpenTK.Mathematics.Vector3i position)
    {
        Position = position;
        Generate();
    }

    // ------------------------------------------------------------------
    // Public block access
    // ------------------------------------------------------------------

    /// <summary>Returns the block at local coordinates (x, y, z).</summary>
    public ref Block GetBlock(int x, int y, int z) => ref _blocks[Index(x, y, z)];

    /// <summary>Returns true when (x, y, z) is within [0, Size).</summary>
    public static bool InBounds(int x, int y, int z) =>
        (uint)x < Size && (uint)y < Size && (uint)z < Size;

    // ------------------------------------------------------------------
    // World generation
    // ------------------------------------------------------------------

    /// <summary>
    /// Generates terrain using fractional Brownian motion (Perlin noise).
    ///
    /// Block stacking from bottom to surface:
    ///   y &lt; surfaceY - 3  →  Stone  (ID 2)
    ///   y &lt; surfaceY      →  Dirt   (ID 1)
    ///   y == surfaceY     →  Grass  (ID 3, top tile = grass, sides/bottom = dirt)
    ///   y &gt; surfaceY      →  Air
    ///
    /// noiseScale controls feature width: smaller = broader hills.
    /// minHeight / maxHeight clamp the surface within the chunk's Y range.
    /// </summary>
    private void Generate()
    {
        const float noiseScale = 0.035f;
        const int minHeight = 6;
        const int maxHeight = 22;

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                // Convert local block coords to continuous world-space coords so that
                // adjacent chunks sample the same noise field without seams.
                float wx = Position.X * Size + x;
                float wz = Position.Z * Size + z;

                float sample = NoiseGenerator.Octave(wx * noiseScale, wz * noiseScale, octaves: 4);
                int surfaceY = minHeight + (int)(sample * (maxHeight - minHeight));
                surfaceY = Math.Clamp(surfaceY, 1, Size - 1);

                for (int y = 0; y < Size; y++)
                {
                    Block b;
                    if (y > surfaceY) b = Block.Air;
                    else if (y == surfaceY) b = new Block { Id = 3, IsTransparent = false }; // Grass
                    else if (y >= surfaceY - 3) b = new Block { Id = 1, IsTransparent = false }; // Dirt
                    else b = new Block { Id = 2, IsTransparent = false }; // Stone

                    _blocks[Index(x, y, z)] = b;
                }
            }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Converts local (x, y, z) to a flat array index.</summary>
    private static int Index(int x, int y, int z) => x + Size * (y + Size * z);
}

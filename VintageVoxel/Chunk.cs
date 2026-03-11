using System.Collections.Generic;

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

    // Light levels stored per-voxel, range [0, 15].
    // SunLight  — propagated downward from the sky (max 15) then BFS-spread horizontally.
    // BlockLight — emitted by torches / light-source blocks (max 14 in current design).
    // Stored as separate byte arrays (1 byte = 8-bit, only 4 bits used) so the mesher
    // can sample them without any bitpacking overhead.
    public readonly byte[] SunLight = new byte[Volume];
    public readonly byte[] BlockLight = new byte[Volume];

    /// <summary>
    /// Per-block chiseled data keyed by the flat array index of the block.
    /// Only populated for blocks whose Id == Block.ChiseledId.
    /// </summary>
    public Dictionary<int, ChiseledBlockData> ChiseledBlocks { get; } = new();

    public Chunk(OpenTK.Mathematics.Vector3i position)
    {
        Position = position;
        Generate();
    }

    // ------------------------------------------------------------------
    // Serialization support
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a chunk whose block array is left as all-Air (skips Generate).
    /// Used exclusively by <see cref="WorldPersistence"/> — the caller is
    /// responsible for filling <see cref="_blocks"/> via <see cref="LoadBlocksFromSave"/>.
    /// </summary>
    internal static Chunk CreateForDeserialization(OpenTK.Mathematics.Vector3i position)
        => new(position, skipGenerate: true);

    /// Constructor overload that optionally skips terrain generation.
    private Chunk(OpenTK.Mathematics.Vector3i position, bool skipGenerate)
    {
        Position = position;
        if (!skipGenerate) Generate();
    }

    /// <summary>Returns the raw block ID at the given flat array index. Used by WorldPersistence.</summary>
    internal ushort GetRawBlockId(int flatIndex) => _blocks[flatIndex].Id;

    /// <summary>
    /// Overwrites the entire block array from a saved ID list produced by
    /// <see cref="WorldPersistence"/>.  Transparency is derived directly from
    /// the block ID (Air = transparent, everything else = opaque).
    /// </summary>
    internal void LoadBlocksFromSave(ushort[] savedIds)
    {
        for (int i = 0; i < Volume; i++)
            _blocks[i] = new Block { Id = savedIds[i], IsTransparent = savedIds[i] == 0 };
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
        if (WorldGenConfig.FlatWorld) { GenerateFlat(); return; }

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

    /// <summary>
    /// Generates a perfectly flat world: Stone below <c>grassY-3</c>, Dirt in
    /// the three layers below the surface, Grass on top, Air above.
    /// The surface height is constant regardless of chunk position.
    /// </summary>
    private void GenerateFlat()
    {
        const int grassY = 5;
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                for (int y = 0; y < Size; y++)
                {
                    Block b;
                    if (y > grassY) b = Block.Air;
                    else if (y == grassY) b = new Block { Id = 3, IsTransparent = false }; // Grass
                    else if (y >= grassY - 3) b = new Block { Id = 1, IsTransparent = false }; // Dirt
                    else b = new Block { Id = 2, IsTransparent = false }; // Stone
                    _blocks[Index(x, y, z)] = b;
                }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Converts local (x, y, z) to a flat array index.</summary>
    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);

    /// <summary>
    /// Returns the <see cref="ChiseledBlockData"/> for the block at (x, y, z),
    /// creating a fully-filled entry with <paramref name="sourceId"/> textures
    /// if one does not yet exist.
    /// </summary>
    public ChiseledBlockData GetOrCreateChiseled(int x, int y, int z, ushort sourceId = 1)
    {
        int idx = Index(x, y, z);
        if (!ChiseledBlocks.TryGetValue(idx, out var data))
        {
            data = new ChiseledBlockData(sourceId);
            ChiseledBlocks[idx] = data;
        }
        return data;
    }
}

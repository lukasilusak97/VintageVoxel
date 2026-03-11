namespace VintageVoxel;

/// <summary>
/// A 16×16×16 grid of boolean sub-voxels stored inside a single "chiseled"
/// block container (Block.ChiseledId = 999).
///
/// When a regular block is chiseled it becomes a ChiseledId block and a
/// ChiseledBlockData object is stored in Chunk.ChiseledBlocks at the block's
/// flat array index.
///
/// Default state: all 4096 sub-voxels are filled (= a completely solid block).
/// Each sub-voxel occupies 1/16 of a world unit (0.0625 m).
///
/// Index formula: x + SubSize * (y + SubSize * z)
///   X varies fastest, Z slowest — matches the parent Chunk convention.
/// </summary>
public class ChiseledBlockData
{
    public const int SubSize = 16;
    public const int SubVolume = SubSize * SubSize * SubSize; // 4096

    /// <summary>
    /// Block ID whose per-face atlas tiles are used when rendering this chiseled
    /// block.  Set to the original block type at the moment of chiseling so the
    /// chiseled fragments look like the source material.
    /// </summary>
    public ushort SourceBlockId { get; set; }

    private readonly bool[] _subVoxels = new bool[SubVolume];

    /// <param name="sourceBlockId">Original block ID; used for texture lookup.</param>
    public ChiseledBlockData(ushort sourceBlockId)
    {
        SourceBlockId = sourceBlockId;
        Array.Fill(_subVoxels, true); // Start fully solid.
    }

    // ------------------------------------------------------------------
    // Coordinate helpers
    // ------------------------------------------------------------------

    /// <summary>Flat array index for sub-voxel (x, y, z).</summary>
    public static int Index(int x, int y, int z) =>
        x + SubSize * (y + SubSize * z);

    /// <summary>True when (x, y, z) is within [0, SubSize).</summary>
    public static bool InBounds(int x, int y, int z) =>
        (uint)x < SubSize && (uint)y < SubSize && (uint)z < SubSize;

    // ------------------------------------------------------------------
    // Sub-voxel access
    // ------------------------------------------------------------------

    /// <returns>True if the sub-voxel at (x, y, z) is filled (solid).</returns>
    public bool Get(int x, int y, int z) => _subVoxels[Index(x, y, z)];

    /// <summary>Sets the fill state of the sub-voxel at (x, y, z).</summary>
    public void Set(int x, int y, int z, bool filled) =>
        _subVoxels[Index(x, y, z)] = filled;

    /// <returns>True if at least one sub-voxel is still filled.</returns>
    public bool HasAnyFilled()
    {
        for (int i = 0; i < SubVolume; i++)
            if (_subVoxels[i]) return true;
        return false;
    }

    // ------------------------------------------------------------------
    // Serialization support (WorldPersistence only)
    // ------------------------------------------------------------------

    /// <summary>Returns the raw bool at flat <paramref name="index"/>. Used by WorldPersistence.</summary>
    internal bool GetRaw(int index) => _subVoxels[index];

    /// <summary>Sets the raw bool at flat <paramref name="index"/>. Used by WorldPersistence.</summary>
    internal void SetRaw(int index, bool value) => _subVoxels[index] = value;
}

namespace VintageVoxel;

/// <summary>
/// Represents a single voxel block in the world.
/// Kept as a struct so that a Chunk's flat array is a single contiguous allocation —
/// no per-element heap objects, no GC pressure, cache-friendly iteration.
/// </summary>
public struct Block
{
    /// <summary>
    /// Type identifier. 0 = Air (empty). All other values are solid block types.
    /// ushort gives us 65 535 distinct block types — far more than we'll ever need,
    /// at only 2 bytes per block (vs 4 for int).
    /// </summary>
    public ushort Id;

    /// <summary>
    /// True if light (and therefore neighbouring-face visibility) can pass through
    /// this block. Air is always transparent; water/glass would also be transparent.
    /// The mesher uses this flag: a face is only emitted when its neighbour is transparent.
    /// </summary>
    public bool IsTransparent;

    /// <summary>Air — the absence of a block.</summary>
    public static readonly Block Air = new Block { Id = 0, IsTransparent = true };

    /// <summary>Convenience: returns true when this block occupies no space.</summary>
    public readonly bool IsEmpty => Id == 0;
}

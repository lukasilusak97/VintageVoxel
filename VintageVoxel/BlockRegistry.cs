namespace VintageVoxel;

/// <summary>
/// Maps block IDs to their texture atlas tile indices per face.
///
/// WHY keep this outside the Block struct?
///   Block is stored 32 768 times per chunk.  Adding tile-index fields would blow
///   up the struct size and trash the CPU cache during meshing.  The registry is a
///   tiny static table — one lookup per emitted face is negligible overhead.
/// </summary>
public static class BlockRegistry
{
    /// <summary>Atlas tile indices for each face group of a block type.</summary>
    public readonly struct FaceTiles
    {
        public readonly int Top;    // +Y face
        public readonly int Bottom; // -Y face
        public readonly int Side;   // All four side faces (N / S / E / W)

        public FaceTiles(int top, int bottom, int side)
        {
            Top = top;
            Bottom = bottom;
            Side = side;
        }
    }

    // Index = block ID.
    // Tile indices match TextureAtlas: 0 = Dirt, 1 = Stone, 2 = Grass top.
    private static readonly FaceTiles[] Definitions =
    {
        new FaceTiles(0, 0, 0), // ID 0  Air         (never rendered)
        new FaceTiles(0, 0, 0), // ID 1  Dirt        (all faces = dirt tile)
        new FaceTiles(1, 1, 1), // ID 2  Stone       (all faces = stone tile)
        new FaceTiles(2, 0, 0), // ID 3  Grass       (top = grass, rest = dirt)
    };

    /// <summary>Returns the tile index for a specific face of the given block ID.
    /// face 0 = Top (+Y), face 1 = Bottom (-Y), faces 2-5 = Sides.</summary>
    public static int TileForFace(ushort blockId, int face)
    {
        var def = blockId < Definitions.Length
            ? Definitions[blockId]
            : Definitions[1]; // Unknown ID falls back to Dirt

        return face switch
        {
            0 => def.Top,
            1 => def.Bottom,
            _ => def.Side,
        };
    }
}

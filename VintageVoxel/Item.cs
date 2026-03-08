namespace VintageVoxel;

/// <summary>
/// Defines the static (shared) properties of a single item type.
/// All instances with the same ID are the same logical item.
/// </summary>
public class Item
{
    /// <summary>Unique numeric identifier.  Block-derived items share the block ID.</summary>
    public int Id { get; }

    /// <summary>Human-readable display name shown in the hotbar tooltip.</summary>
    public string Name { get; }

    /// <summary>Maximum number of items that can occupy one inventory slot.</summary>
    public int MaxStackSize { get; }

    /// <summary>
    /// Index of the atlas tile used to render this item's 2-D icon.
    /// Matches the tile indices produced by <see cref="BlockRegistry"/>.
    /// </summary>
    public int TextureId { get; }

    public Item(int id, string name, int maxStackSize, int textureId)
    {
        Id = id;
        Name = name;
        MaxStackSize = maxStackSize;
        TextureId = textureId;
    }

    // -------------------------------------------------------------------------
    // Pre-defined block items — created once and shared by reference.
    // -------------------------------------------------------------------------

    /// <summary>A Dirt block as a holdable/placeable item.</summary>
    public static readonly Item Dirt = new(1, "Dirt", 64, 0);   // atlas tile 0

    /// <summary>A Stone block as a holdable/placeable item.</summary>
    public static readonly Item Stone = new(2, "Stone", 64, 3);  // atlas tile 3

    /// <summary>A Grass block as a holdable/placeable item.</summary>
    public static readonly Item Grass = new(3, "Grass", 64, 6);  // atlas tile 6 (top face)
}

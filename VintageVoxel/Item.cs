namespace VintageVoxel;

/// <summary>Determines how an item is represented in the world and inventory.</summary>
public enum ItemType
{
    /// <summary>Placed as a full voxel block; icon comes from the texture atlas.</summary>
    Block,
    /// <summary>Uses a voxel JSON model for both in-world and inventory rendering.</summary>
    Model,
}

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
    /// Only meaningful when <see cref="Type"/> is <see cref="ItemType.Block"/>.
    /// </summary>
    public int TextureId { get; }

    /// <summary>Whether this item is a placed block or a stand-alone model.</summary>
    public ItemType Type { get; }

    /// <summary>
    /// Loaded voxel model for <see cref="ItemType.Model"/> items (legacy VoxelModel format).
    /// <see langword="null"/> for <see cref="ItemType.Block"/> items.
    /// </summary>
    public VoxelModel? Model { get; }

    /// <summary>
    /// Loaded Minecraft-element mesh for <see cref="ItemType.Model"/> items.
    /// <see langword="null"/> for <see cref="ItemType.Block"/> items or models
    /// that could not be parsed as a Minecraft-format JSON.
    /// </summary>
    public ModelMesh? Mesh { get; }

    public Item(int id, string name, int maxStackSize, int textureId,
                ItemType type = ItemType.Block, VoxelModel? model = null,
                ModelMesh? mesh = null)
    {
        Id = id;
        Name = name;
        MaxStackSize = maxStackSize;
        TextureId = textureId;
        Type = type;
        Model = model;
        Mesh = mesh;
    }
}

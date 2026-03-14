namespace VintageVoxel;

/// <summary>Determines how an item is represented in the world and inventory.</summary>
public enum ItemType
{
    /// <summary>Placed as a full voxel block; icon comes from the texture atlas.</summary>
    Block,
    /// <summary>Uses a voxel JSON model for both in-world and inventory rendering.</summary>
    Model,
    /// <summary>Spawns an entity (e.g. vehicle) when placed in the world.</summary>
    Entity,
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
    /// The block ID this item places, or 0 for non-block items.
    /// Used to look up per-face atlas tiles via <see cref="BlockRegistry.TileForFace"/>.
    /// </summary>
    public int BlockId { get; }

    /// <summary>
    /// The entity ID this item spawns when placed, or 0 for non-entity items.
    /// Used to look up entity definitions via <see cref="EntityRegistry.Get"/>.
    /// </summary>
    public int EntityId { get; }

    /// <summary>Whether this item is a placed block, a stand-alone model, or an entity spawner.</summary>
    public ItemType Type { get; }

    /// <summary>
    /// Loaded model mesh for <see cref="ItemType.Model"/> items.
    /// <see langword="null"/> for <see cref="ItemType.Block"/> items.
    /// </summary>
    public ModelMesh? Mesh { get; }

    /// <summary>
    /// Relative model path (under <c>Assets/Models/</c>, without extension) for
    /// <see cref="ItemType.Model"/> items. Used by <see cref="Rendering.EntityRenderer"/>
    /// to look up models in its path-based cache.
    /// </summary>
    public string? ModelPath { get; }

    public Item(int id, string name, int maxStackSize, int blockId = 0,
                ItemType type = ItemType.Block,
                ModelMesh? mesh = null, int entityId = 0,
                string? modelPath = null)
    {
        Id = id;
        Name = name;
        MaxStackSize = maxStackSize;
        BlockId = blockId;
        Type = type;
        Mesh = mesh;
        EntityId = entityId;
        ModelPath = modelPath;
    }
}

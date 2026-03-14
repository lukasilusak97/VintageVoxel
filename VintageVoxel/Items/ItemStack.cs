namespace VintageVoxel;

/// <summary>
/// A pairing of an <see cref="Item"/> type and a quantity.
/// An empty slot is represented by <c>Item == null</c> (the <see langword="default"/> value).
/// </summary>
public struct ItemStack
{
    /// <summary>The item type, or <see langword="null"/> for an empty slot.</summary>
    public Item? Item;

    /// <summary>
    /// Number of items in this stack.  Range: 1–<see cref="Item.MaxStackSize"/>.
    /// Zero (or negative) means the slot is logically empty.
    /// </summary>
    public int Count;

    /// <summary>
    /// Mutable runtime state for tool-type items (e.g. layers carried by a shovel).
    /// <see langword="null"/> for non-tool items and empty slots.
    /// Created lazily when a tool first picks up material.
    /// </summary>
    public ToolData? ToolState;

    /// <summary><see langword="true"/> when this slot holds no items.</summary>
    public readonly bool IsEmpty => Item == null || Count <= 0;

    /// <summary>Convenience constructor for non-empty stacks.</summary>
    public ItemStack(Item item, int count)
    {
        Item = item;
        Count = count;
        ToolState = item.IsTool ? new ToolData() : null;
    }

    /// <summary>Canonical empty-slot sentinel (same as <see langword="default"/>).</summary>
    public static readonly ItemStack Empty = default;
}

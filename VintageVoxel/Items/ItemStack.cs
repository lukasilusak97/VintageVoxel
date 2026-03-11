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

    /// <summary><see langword="true"/> when this slot holds no items.</summary>
    public readonly bool IsEmpty => Item == null || Count <= 0;

    /// <summary>Convenience constructor for non-empty stacks.</summary>
    public ItemStack(Item item, int count)
    {
        Item = item;
        Count = count;
    }

    /// <summary>Canonical empty-slot sentinel (same as <see langword="default"/>).</summary>
    public static readonly ItemStack Empty = default;
}

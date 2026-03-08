namespace VintageVoxel;

/// <summary>
/// Holds a fixed-size array of <see cref="ItemStack"/> slots and tracks the
/// currently selected hotbar slot.
///
/// Slots 0–<see cref="HotbarSize"/>-1 form the hotbar; additional slots (if
/// <paramref name="slotCount"/> is larger) are the backpack grid and are
/// exposed through the same <see cref="Slots"/> collection.
/// </summary>
public class Inventory
{
    /// <summary>Number of slots visible in the hotbar strip.</summary>
    public const int HotbarSize = 10;

    private readonly ItemStack[] _slots;

    /// <summary>Index of the currently selected hotbar slot (0–<see cref="HotbarSize"/>-1).</summary>
    public int SelectedSlot { get; private set; }

    /// <summary>Creates an inventory with <paramref name="slotCount"/> total slots.</summary>
    public Inventory(int slotCount = HotbarSize)
    {
        _slots = new ItemStack[slotCount];
    }

    /// <summary>Read-only view across all slots (hotbar + backpack).</summary>
    public IReadOnlyList<ItemStack> Slots => _slots;

    /// <summary>Direct reference to the stack in the currently selected hotbar slot.</summary>
    public ref ItemStack HeldStack => ref _slots[SelectedSlot];

    // -------------------------------------------------------------------------
    // Hotbar navigation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Advances the selected slot by <paramref name="delta"/> steps (positive = next,
    /// negative = previous), wrapping around at both ends.
    /// Intended to be called from the mouse-scroll event (delta = ±1).
    /// </summary>
    public void ScrollHotbar(int delta)
    {
        // Double-modulo trick ensures the result is always positive.
        SelectedSlot = ((SelectedSlot + delta) % HotbarSize + HotbarSize) % HotbarSize;
    }

    /// <summary>Directly selects slot <paramref name="index"/> (clamped to valid range).</summary>
    public void SelectSlot(int index)
    {
        SelectedSlot = Math.Clamp(index, 0, HotbarSize - 1);
    }

    // -------------------------------------------------------------------------
    // Item management
    // -------------------------------------------------------------------------

    /// <summary>
    /// Adds up to <paramref name="count"/> of <paramref name="item"/> to this inventory.
    /// Merges into existing partial stacks of the same type first, then fills
    /// empty slots.  Returns the number of items that could NOT be placed (overflow).
    /// </summary>
    public int AddItem(Item item, int count = 1)
    {
        // Pass 1: top up existing partial stacks.
        for (int i = 0; i < _slots.Length && count > 0; i++)
        {
            if (_slots[i].Item == item && _slots[i].Count < item.MaxStackSize)
            {
                int space = item.MaxStackSize - _slots[i].Count;
                int add = Math.Min(space, count);
                _slots[i].Count += add;
                count -= add;
            }
        }

        // Pass 2: fill empty slots.
        for (int i = 0; i < _slots.Length && count > 0; i++)
        {
            if (_slots[i].IsEmpty)
            {
                int add = Math.Min(item.MaxStackSize, count);
                _slots[i] = new ItemStack(item, add);
                count -= add;
            }
        }

        return count; // Items that didn't fit.
    }

    /// <summary>
    /// Removes up to <paramref name="count"/> of <paramref name="item"/> from this
    /// inventory (scanning from slot 0).  Returns the count actually removed.
    /// </summary>
    public int RemoveItem(Item item, int count = 1)
    {
        int removed = 0;
        for (int i = 0; i < _slots.Length && count > 0; i++)
        {
            if (_slots[i].Item != item) continue;

            int take = Math.Min(_slots[i].Count, count);
            _slots[i].Count -= take;
            if (_slots[i].Count == 0)
                _slots[i] = ItemStack.Empty;
            removed += take;
            count -= take;
        }
        return removed;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the inventory contains at least
    /// <paramref name="count"/> of <paramref name="item"/>.
    /// </summary>
    public bool HasItem(Item item, int count = 1)
    {
        int total = 0;
        foreach (ref readonly var slot in _slots.AsSpan())
            if (slot.Item == item) total += slot.Count;
        return total >= count;
    }
}

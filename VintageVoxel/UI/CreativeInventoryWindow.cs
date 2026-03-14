using ImGuiNET;
using System.Numerics;

namespace VintageVoxel;

/// <summary>
/// Draws a creative-mode item catalogue alongside the player inventory.
/// Lists every item from <see cref="ItemRegistry"/> in a scrollable grid.
/// Left-click picks up a full stack; right-click picks up a single item.
/// The picked-up stack is shared with <see cref="InventoryWindow"/> so the
/// player can drag items straight into their inventory.
/// </summary>
public sealed class CreativeInventoryWindow
{
    // ── Layout ────────────────────────────────────────────────────────────────
    private const float SlotSize = 52f;
    private const float SlotPadding = 4f;
    private const int Columns = 8;

    // ── Colours ───────────────────────────────────────────────────────────────
    private static readonly uint ColSlotBg = Pack(0.10f, 0.10f, 0.10f, 0.30f);
    private static readonly uint ColSlotBorder = Pack(0.50f, 0.50f, 0.50f, 1.00f);
    private static readonly uint ColSlotHover = Pack(0.35f, 0.35f, 0.35f, 0.45f);

    // Slot positions for 3-D item rendering.
    private readonly List<(ItemStack Stack, float DispX, float DispY, float Size)> _slotRenderTargets = new();

    /// <summary>
    /// Slot positions populated during <see cref="Draw"/>.  Each entry describes a
    /// non-empty slot the caller should render as a 3-D item preview.
    /// </summary>
    public IReadOnlyList<(ItemStack Stack, float DispX, float DispY, float Size)> SlotRenderTargets => _slotRenderTargets;

    private static uint Pack(float r, float g, float b, float a) =>
        ((uint)(a * 255) << 24) | ((uint)(b * 255) << 16) |
        ((uint)(g * 255) << 8) | (uint)(r * 255);

    // Cached sorted list of all items (rebuilt when count changes).
    private List<Item>? _allItems;
    private int _lastItemCount;

    private List<Item> GetAllItems()
    {
        int count = ItemRegistry.All.Count;
        if (_allItems == null || _lastItemCount != count)
        {
            _allItems = ItemRegistry.All.Values.OrderBy(i => i.Id).ToList();
            _lastItemCount = count;
        }
        return _allItems;
    }

    /// <summary>
    /// Draws the creative catalogue panel.  Must be called while the inventory
    /// window is open.  The <paramref name="inventoryWindow"/> is used to share
    /// the cursor stack so drag-and-drop works across both panels.
    /// </summary>
    public void Draw(InventoryWindow inventoryWindow)
    {
        _slotRenderTargets.Clear();

        var io = ImGui.GetIO();
        var ds = io.DisplaySize;
        var items = GetAllItems();
        int rows = (items.Count + Columns - 1) / Columns;

        // Compute content size for the grid.
        float gridW = Columns * (SlotSize + SlotPadding) - SlotPadding + 16f; // +padding
        float maxGridH = ds.Y * 0.6f;

        // Position to the left of centre (the inventory window is centred).
        ImGui.SetNextWindowPos(
            new Vector2(ds.X * 0.5f - gridW - 10f, ds.Y * 0.5f),
            ImGuiCond.Always,
            new Vector2(1.0f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.93f);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(gridW, 150f),
            new Vector2(gridW, maxGridH));

        const ImGuiWindowFlags Flags =
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.Begin("Creative##vv_creative", Flags);

        ImGui.TextColored(new Vector4(0.518f, 0.773f, 0.784f, 1f), "Creative Items");
        ImGui.Separator();
        ImGui.Spacing();

        // Scrollable child region for the grid.
        float childH = Math.Min(rows * (SlotSize + SlotPadding) + SlotPadding, maxGridH - 60f);
        ImGui.BeginChild("##creative_grid", new Vector2(0, childH), false, ImGuiWindowFlags.None);

        for (int i = 0; i < items.Count; i++)
        {
            int col = i % Columns;
            if (col > 0) ImGui.SameLine(0f, SlotPadding);
            DrawCreativeSlot(inventoryWindow, items[i], $"##cr{items[i].Id}");
        }

        ImGui.EndChild();
        ImGui.End();
    }

    private void DrawCreativeSlot(InventoryWindow inventoryWindow, Item item, string id)
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(SlotSize, SlotSize));
        bool hovered = ImGui.IsItemHovered();
        bool lClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        bool rClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        uint bg = hovered ? ColSlotHover : ColSlotBg;
        drawList.AddRectFilled(origin, origin + new Vector2(SlotSize, SlotSize), bg, 4f);
        drawList.AddRect(origin, origin + new Vector2(SlotSize, SlotSize),
                         ColSlotBorder, 4f, ImDrawFlags.None, 1.5f);

        // Show the item for 3-D rendering.
        var displayStack = new ItemStack(item, item.MaxStackSize);
        _slotRenderTargets.Add((displayStack, origin.X, origin.Y, SlotSize));

        // Tooltip.
        if (hovered)
        {
            ImGui.BeginTooltip();
            ImGui.Text(item.Name);
            ImGui.TextDisabled(item.Type == ItemType.Block ? "Block"
                             : item.Type == ItemType.Entity ? "Entity"
                             : "Item");
            ImGui.EndTooltip();
        }

        // Click: give player a stack (creative = infinite source).
        if (lClicked)
        {
            var cursor = inventoryWindow.CursorStack;
            if (cursor.IsEmpty)
            {
                inventoryWindow.CursorStack = new ItemStack(item, item.MaxStackSize);
            }
            else if (cursor.Item == item)
            {
                // Top up to max.
                inventoryWindow.CursorStack = new ItemStack(item, item.MaxStackSize);
            }
            else
            {
                // Replace with new item.
                inventoryWindow.CursorStack = new ItemStack(item, item.MaxStackSize);
            }
        }
        else if (rClicked)
        {
            var cursor = inventoryWindow.CursorStack;
            if (cursor.IsEmpty)
            {
                inventoryWindow.CursorStack = new ItemStack(item, 1);
            }
            else if (cursor.Item == item && cursor.Count < item.MaxStackSize)
            {
                inventoryWindow.CursorStack = new ItemStack(item, cursor.Count + 1);
            }
            else if (cursor.Item != item)
            {
                inventoryWindow.CursorStack = new ItemStack(item, 1);
            }
        }
    }
}

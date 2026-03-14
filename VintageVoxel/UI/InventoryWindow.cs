using ImGuiNET;
using System.Numerics;

namespace VintageVoxel;

/// <summary>
/// Renders the full inventory grid as a centred ImGui overlay window.
///
/// Interaction model (drag-and-drop):
///   Left-click           – pick up entire stack / place held stack / swap with slot
///   Right-click          – pick up half stack / place one item
///   Shift + Left-click   – move entire stack to the first available slot on the
///                           opposite side (hotbar ↔ backpack)
///
/// Call <see cref="Draw"/> every frame after ImGuiController.Update and before
/// ImGuiController.Render.  Call <see cref="OnClose"/> when the window is
/// dismissed so any cursor-held items are returned to the inventory.
/// </summary>
public sealed class InventoryWindow
{
    // The item stack currently "held" on the mouse cursor during a drag.
    private ItemStack _cursorStack = ItemStack.Empty;

    /// <summary>
    /// The stack currently held on the mouse cursor.  Exposed so that external
    /// panels (e.g. <see cref="CreativeInventoryWindow"/>) can share the same
    /// drag-and-drop cursor as the inventory grid.
    /// </summary>
    public ItemStack CursorStack
    {
        get => _cursorStack;
        set => _cursorStack = value;
    }

    /// <summary>Whether the inventory window is currently visible.</summary>
    public bool IsOpen { get; set; }

    // Slot positions collected each frame for 3-D item rendering (display coords, Y=0 at top).
    private readonly List<(ItemStack Stack, float DispX, float DispY, float Size)> _slotRenderTargets = new();

    /// <summary>
    /// Slot positions populated during <see cref="Draw"/>.  Each entry describes a
    /// non-empty slot the caller should render as a 3-D item preview.
    /// Coordinates are in ImGui display space (Y=0 at top).
    /// </summary>
    public IReadOnlyList<(ItemStack Stack, float DispX, float DispY, float Size)> SlotRenderTargets => _slotRenderTargets;

    // ── Layout ────────────────────────────────────────────────────────────────
    private const float SlotSize = 52f;
    private const float SlotPadding = 4f;

    // ── Colours (ImGui packed ABGR) ───────────────────────────────────────────
    // Backgrounds are kept semi-transparent so 3-D items rendered underneath show through.
    private static readonly uint ColSlotBg = Pack(0.10f, 0.10f, 0.10f, 0.30f);
    private static readonly uint ColSlotBorder = Pack(0.50f, 0.50f, 0.50f, 1.00f);
    private static readonly uint ColSlotHover = Pack(0.35f, 0.35f, 0.35f, 0.45f);
    private static readonly uint ColSlotSelected = Pack(0.25f, 0.55f, 0.85f, 0.45f);
    private static readonly uint ColTextShadow = 0xAA000000;
    private static readonly uint ColCount = Pack(1.00f, 0.92f, 0.20f, 1.00f);

    // ImGui packed colour: IM_COL32 = A<<24 | B<<16 | G<<8 | R
    private static uint Pack(float r, float g, float b, float a) =>
        ((uint)(a * 255) << 24) | ((uint)(b * 255) << 16) |
        ((uint)(g * 255) << 8) | (uint)(r * 255);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws the inventory window.  Call between ImGuiController.Update and
    /// ImGuiController.Render while <see cref="GameState.Playing"/> is active.
    /// After this call, <see cref="SlotRenderTargets"/> contains the display-space
    /// positions of all non-empty slots for 3-D item rendering.
    /// </summary>
    public void Draw(Inventory inventory)
    {
        _slotRenderTargets.Clear();
        if (!IsOpen) return;

        var io = ImGui.GetIO();
        var ds = io.DisplaySize;
        int cols = Inventory.BackpackCols;
        int rows = Inventory.BackpackRows;

        ImGui.SetNextWindowPos(
            new Vector2(ds.X * 0.5f, ds.Y * 0.5f),
            ImGuiCond.Always,
            new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowBgAlpha(0.93f);

        const ImGuiWindowFlags Flags =
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.Begin("Inventory##vv_inv", Flags);

        // ── Title ─────────────────────────────────────────────────────────────
        ImGui.TextColored(new Vector4(0.518f, 0.773f, 0.784f, 1f), "Inventory");
        ImGui.Separator();
        ImGui.Spacing();

        // ── Backpack grid ──────────────────────────────────────────────────────
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                int slotIndex = Inventory.HotbarSize + row * cols + col;
                if (col > 0) ImGui.SameLine(0f, SlotPadding);
                DrawSlot(inventory, slotIndex, $"##bp{slotIndex}", hotbar: false);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.70f, 0.70f, 0.70f, 1f), "Hotbar");
        ImGui.Spacing();

        // ── Hotbar row ─────────────────────────────────────────────────────────
        for (int col = 0; col < Inventory.HotbarSize; col++)
        {
            if (col > 0) ImGui.SameLine(0f, SlotPadding);
            DrawSlot(inventory, col, $"##hb{col}",
                     hotbar: true,
                     isSelectedHotbar: col == inventory.SelectedSlot);
        }

        ImGui.Spacing();
        ImGui.End();

        // ── Floating cursor-stack ──────────────────────────────────────────────
        if (!_cursorStack.IsEmpty)
            DrawFloatingStack(ImGui.GetForegroundDrawList(), io.MousePos, _cursorStack);
    }

    /// <summary>
    /// Called when the inventory is closed.  Returns any cursor-held item to
    /// the inventory (or drops overflow silently if the inventory is full).
    /// </summary>
    public void OnClose(Inventory inventory)
    {
        if (_cursorStack.IsEmpty) return;
        inventory.AddItem(_cursorStack.Item!, _cursorStack.Count);
        _cursorStack = ItemStack.Empty;
    }

    // ── Slot rendering + interaction ──────────────────────────────────────────

    private void DrawSlot(Inventory inventory, int slotIndex,
                          string id, bool hotbar, bool isSelectedHotbar = false)
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetCursorScreenPos();

        // Reserve interactive area.
        ImGui.InvisibleButton(id, new Vector2(SlotSize, SlotSize));
        bool hovered = ImGui.IsItemHovered();
        bool lClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        bool rClicked = hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right);

        // Slot background.
        uint bg = isSelectedHotbar ? ColSlotSelected
                : hovered ? ColSlotHover
                                   : ColSlotBg;
        drawList.AddRectFilled(origin, origin + new Vector2(SlotSize, SlotSize), bg, 4f);
        drawList.AddRect(origin, origin + new Vector2(SlotSize, SlotSize),
                         ColSlotBorder, 4f, ImDrawFlags.None, 1.5f);

        // Item contents.
        ref ItemStack stack = ref inventory.GetSlotRef(slotIndex);

        // Record position for 3-D rendering (before clicks can mutate the slot).
        ItemStack renderSnapshot = stack;
        if (!renderSnapshot.IsEmpty)
            _slotRenderTargets.Add((renderSnapshot, origin.X, origin.Y, SlotSize));

        if (!stack.IsEmpty)
        {
            // Stack count (bottom-right corner, only when > 1).
            // Name is shown by the 3-D item + tooltip; count label stays on top.
            if (stack.Count > 1)
            {
                string countStr = stack.Count.ToString();
                Vector2 ts = ImGui.CalcTextSize(countStr);
                Vector2 countPos = origin + new Vector2(SlotSize - ts.X - 4f, SlotSize - ts.Y - 3f);
                drawList.AddText(countPos + new Vector2(1, 1), ColTextShadow, countStr);
                drawList.AddText(countPos, ColCount, countStr);
            }
        }

        // Tooltip on hover.
        if (hovered && !stack.IsEmpty)
        {
            ImGui.BeginTooltip();
            ImGui.Text(stack.Item!.Name);
            if (stack.Count > 1)
                ImGui.TextDisabled($"x{stack.Count}");
            ImGui.TextDisabled(stack.Item.Type == ItemType.Block ? "Block" : "Item");
            ImGui.EndTooltip();
        }

        // ── Click handling ────────────────────────────────────────────────────
        if (lClicked)
        {
            bool shift = ImGui.GetIO().KeyShift;
            if (shift && _cursorStack.IsEmpty && !stack.IsEmpty)
            {
                // Shift+click: move entire stack to the opposite section.
                MoveToFirstAvailable(inventory, slotIndex, hotbar);
            }
            else if (_cursorStack.IsEmpty)
            {
                // Pick up.
                _cursorStack = stack;
                stack = ItemStack.Empty;
            }
            else if (stack.IsEmpty)
            {
                // Place into empty slot.
                stack = _cursorStack;
                _cursorStack = ItemStack.Empty;
            }
            else if (stack.Item == _cursorStack.Item)
            {
                // Merge held stack into this slot.
                int space = stack.Item!.MaxStackSize - stack.Count;
                int add = Math.Min(space, _cursorStack.Count);
                stack.Count += add;
                _cursorStack.Count -= add;
                if (_cursorStack.Count <= 0)
                    _cursorStack = ItemStack.Empty;
            }
            else
            {
                // Swap cursor ↔ slot.
                (stack, _cursorStack) = (_cursorStack, stack);
            }
        }
        else if (rClicked)
        {
            if (_cursorStack.IsEmpty && !stack.IsEmpty)
            {
                // Pick up half the stack.
                int half = (stack.Count + 1) / 2;
                _cursorStack = new ItemStack(stack.Item!, half);
                stack.Count -= half;
                if (stack.Count <= 0)
                    stack = ItemStack.Empty;
            }
            else if (!_cursorStack.IsEmpty)
            {
                if (stack.IsEmpty || stack.Item == _cursorStack.Item)
                {
                    // Place one item.
                    if (stack.IsEmpty)
                        stack = new ItemStack(_cursorStack.Item!, 0);
                    if (stack.Count < _cursorStack.Item!.MaxStackSize)
                    {
                        stack.Count++;
                        _cursorStack.Count--;
                        if (_cursorStack.Count <= 0)
                            _cursorStack = ItemStack.Empty;
                    }
                }
                else
                {
                    // Swap.
                    (stack, _cursorStack) = (_cursorStack, stack);
                }
            }
        }
    }

    // Moves an entire stack from <paramref name="sourceSlot"/> to the first
    // available slot in the opposite section (backpack ↔ hotbar).
    private static void MoveToFirstAvailable(Inventory inventory, int sourceSlot, bool fromHotbar)
    {
        ref ItemStack src = ref inventory.GetSlotRef(sourceSlot);
        if (src.IsEmpty) return;

        int destStart = fromHotbar ? Inventory.HotbarSize : 0;
        int destEnd = fromHotbar ? Inventory.TotalSlots : Inventory.HotbarSize;

        // Pass 1: merge into partial stacks of the same type.
        for (int i = destStart; i < destEnd && src.Count > 0; i++)
        {
            ref ItemStack dst = ref inventory.GetSlotRef(i);
            if (dst.Item == src.Item && dst.Count < src.Item!.MaxStackSize)
            {
                int space = src.Item.MaxStackSize - dst.Count;
                int move = Math.Min(space, src.Count);
                dst.Count += move;
                src.Count -= move;
            }
        }
        if (src.Count <= 0) { src = ItemStack.Empty; return; }

        // Pass 2: fill empty slots.
        for (int i = destStart; i < destEnd; i++)
        {
            ref ItemStack dst = ref inventory.GetSlotRef(i);
            if (dst.IsEmpty)
            {
                dst = src;
                src = ItemStack.Empty;
                return;
            }
        }
    }

    // Draws the floating ghost stack that follows the cursor during drag.
    // Semi-transparent background; the 3-D item is queued via SlotRenderTargets.
    private void DrawFloatingStack(ImDrawListPtr drawList, Vector2 pos, ItemStack stack)
    {
        const float Half = SlotSize * 0.5f;
        Vector2 tl = pos - new Vector2(Half, Half);
        Vector2 br = pos + new Vector2(Half, Half);

        drawList.AddRectFilled(tl, br, Pack(0.10f, 0.10f, 0.10f, 0.30f), 4f);
        drawList.AddRect(tl, br, 0xFFFFFFFF, 4f, ImDrawFlags.None, 1.8f);

        if (stack.Count > 1)
        {
            string c = stack.Count.ToString();
            Vector2 ts = ImGui.CalcTextSize(c);
            Vector2 countPos = tl + new Vector2(SlotSize - ts.X - 4f, SlotSize - ts.Y - 3f);
            drawList.AddText(countPos + new Vector2(1, 1), ColTextShadow, c);
            drawList.AddText(countPos, ColCount, c);
        }

        // Queue cursor item for 3-D rendering at the mouse position.
        _slotRenderTargets.Add((stack, tl.X, tl.Y, SlotSize));
    }
}

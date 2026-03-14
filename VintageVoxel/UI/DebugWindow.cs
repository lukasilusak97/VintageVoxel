using ImGuiNET;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// The in-game developer dashboard rendered with ImGui.
///
/// Call <see cref="Render"/> every frame (after ImGui.NewFrame and before
/// ImGuiController.Render) to draw the overlay window and update toggle states.
///
/// Exposed properties are read by Game.cs to apply the corresponding GL state
/// changes each frame.
/// </summary>
public class DebugWindow
{

    // Exponential moving average for a stable FPS display.
    private float _smoothFps;
    private const float FpsSmoothAlpha = 0.05f;

    /// <summary>
    /// Renders the debug overlay window. Call each frame between
    /// ImGuiController.Update() and ImGuiController.Render().
    /// </summary>
    public void Render(float fps, float frameTimeMs, Vector3 playerPos, int chunksLoaded, bool creativeMode,
                     ItemStack heldItem, int hotbarSlot,
                     DebugState debugState, World? world = null, Vector3? cameraPos = null, Vector3? cameraFront = null,
                     string? saveStatus = null)
    {
        // Smooth FPS to stop the number flickering.
        _smoothFps = _smoothFps < 1f
            ? fps
            : MathHelper.Lerp(_smoothFps, fps, FpsSmoothAlpha);

        // Pin window to the top-left corner — let ImGui size it automatically.
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10f, 10f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.80f);

        ImGui.Begin("Debug##vv",
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings);

        // ---- Metrics ----
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Metrics");
        ImGui.Separator();
        ImGui.Text($"FPS        : {_smoothFps,6:F1}");
        ImGui.Text($"Frame Time : {frameTimeMs,6:F2} ms");
        ImGui.Text($"Pos        : {playerPos.X,7:F1}, {playerPos.Y,6:F1}, {playerPos.Z,7:F1}");
        ImGui.Text($"Chunks     : {chunksLoaded}");
        ImGui.Text($"Mode       : {(creativeMode ? "Creative" : "Survival")}");
        string itemLabel = heldItem.IsEmpty ? "(empty)" : $"{heldItem.Item!.Name} x{heldItem.Count}";
        ImGui.Text($"Held [{hotbarSlot}]  : {itemLabel}");
        if (saveStatus != null)
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f), saveStatus);

        ImGui.Spacing();

        // ---- FPS & Frame Time graphs ----
        RenderPerformanceGraphs();

        ImGui.Spacing();

        // ---- Toggles ----
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Toggles");
        ImGui.Separator();

        ImGui.Checkbox("Wireframe Mode", ref debugState.WireframeMode);
        ImGui.Checkbox("Show Chunk Borders", ref debugState.ShowChunkBorders);
        ImGui.Checkbox("No Textures (White)", ref debugState.NoTextures);
        ImGui.Checkbox("Lighting Debug (AO+Light)", ref debugState.LightingDebug);
        ImGui.Checkbox("Show Vehicle Debug", ref debugState.ShowVehicleDebug);

        ImGui.Spacing();

        // ---- Profiler section timing graphs ----
        if (Profiler.Sections.Count > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Timings");
            ImGui.Separator();

            RenderTimingsGraphs();

            ImGui.Spacing();
        }

        // ---- Crosshair Block Info ----
        if (world != null && cameraPos.HasValue && cameraFront.HasValue)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Crosshair Block");
            ImGui.Separator();

            var hit = Raycaster.Cast(cameraPos.Value, cameraFront.Value, world);
            if (hit.Hit)
            {
                var block = world.GetBlock(hit.BlockPos.X, hit.BlockPos.Y, hit.BlockPos.Z);
                string name = BlockRegistry.GetName(block.Id);
                ImGui.Text($"Block      : {name} (id={block.Id})");
                ImGui.Text($"Position   : {hit.BlockPos.X}, {hit.BlockPos.Y}, {hit.BlockPos.Z}");
                ImGui.Text($"Layer      : {block.Layer}/16  ({block.TopOffset:F3})");
                ImGui.Text($"Partial    : {block.IsPartial}");
                ImGui.Text($"Transparent: {block.IsTransparent}");
                ImGui.Text($"WaterLevel : {block.WaterLevel}");
                ImGui.Text($"Normal     : {hit.Normal.X}, {hit.Normal.Y}, {hit.Normal.Z}");
            }
            else
            {
                ImGui.TextDisabled("(no block in range)");
            }

            ImGui.Spacing();
        }

        // ---- Tool State ----
        if (!heldItem.IsEmpty && heldItem.Item!.IsTool)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Tool State");
            ImGui.Separator();
            ImGui.Text($"Tool Type  : {heldItem.Item.Tool!.Type}");
            ImGui.Text($"Capacity   : {heldItem.Item.Tool.Capacity}");
            if (heldItem.ToolState != null && !heldItem.ToolState.IsEmpty)
            {
                string carriedName = BlockRegistry.GetName(heldItem.ToolState.CarriedBlockId);
                ImGui.Text($"Carrying   : {carriedName} (id={heldItem.ToolState.CarriedBlockId})");
                ImGui.Text($"Layers     : {heldItem.ToolState.CarriedLayers}/{heldItem.Item.Tool.Capacity}");
            }
            else
            {
                ImGui.TextDisabled("(empty)");
            }
            ImGui.Spacing();
        }

        ImGui.TextDisabled("[F3] Toggle debug UI  [F] Toggle Creative/Survival");
        ImGui.TextDisabled("[Ctrl+S] Save world  [Scroll] Cycle hotbar");

        ImGui.End();
    }

    /// <summary>Renders FPS and frame-time scrolling line graphs using ImGui.PlotLines.</summary>
    private void RenderPerformanceGraphs()
    {
        var fpsHistory = Profiler.FpsHistory;
        var ftHistory = Profiler.FrameTimeHistory;
        int offset = Profiler.GlobalOffset;
        int len = Profiler.HistoryLength;

        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Performance");
        ImGui.Separator();

        // FPS graph — green overlay text
        string fpsOverlay = $"{_smoothFps:F0} FPS";
        ImGui.PushStyleColor(ImGuiCol.PlotLines, new System.Numerics.Vector4(0.2f, 0.85f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.1f, 0.1f, 0.1f, 0.8f));
        ImGui.PlotLines("##FPS", ref fpsHistory[0], len, offset, fpsOverlay, 0f, 240f, new System.Numerics.Vector2(340, 80));
        ImGui.PopStyleColor(2);

        // Frame time graph — yellow
        string ftOverlay = $"{Profiler.FrameTimeHistory[(offset + len - 1) % len]:F1} ms";
        ImGui.PushStyleColor(ImGuiCol.PlotLines, new System.Numerics.Vector4(0.95f, 0.8f, 0.1f, 1f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.1f, 0.1f, 0.1f, 0.8f));
        ImGui.PlotLines("##FrameTime", ref ftHistory[0], len, offset, ftOverlay, 0f, 33f, new System.Numerics.Vector2(340, 80));
        ImGui.PopStyleColor(2);
    }

    /// <summary>Renders per-section timing graphs plus color-coded summary text.</summary>
    private static void RenderTimingsGraphs()
    {
        int len = Profiler.HistoryLength;

        foreach (var name in Profiler.Sections)
        {
            var history = Profiler.GetHistory(name);
            if (history.Length == 0) continue;
            int offset = Profiler.GetHistoryOffset(name);

            double ms = Profiler.GetMs(name);
            double peakMs = Profiler.GetPeakMs(name);

            // Color: green < 1 ms, yellow < 5 ms, red >= 5 ms.
            var lineColor = ms < 1.0
                ? new System.Numerics.Vector4(0.2f, 0.85f, 0.2f, 1f)
                : ms < 5.0
                    ? new System.Numerics.Vector4(0.95f, 0.8f, 0.1f, 1f)
                    : new System.Numerics.Vector4(0.95f, 0.25f, 0.25f, 1f);

            string overlay = $"{name}  {ms:F2} ms  peak {peakMs:F2}";
            ImGui.PushStyleColor(ImGuiCol.PlotLines, lineColor);
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new System.Numerics.Vector4(0.1f, 0.1f, 0.1f, 0.8f));
            ImGui.PlotLines($"##{name}", ref history[0], len, offset, overlay, 0f, 16f, new System.Numerics.Vector2(340, 50));
            ImGui.PopStyleColor(2);
        }

        ImGui.TextDisabled("16 ms = 60 fps budget");
    }
}

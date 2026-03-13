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
    /// <param name="fps">Raw frames-per-second this frame.</param>
    /// <param name="frameTimeMs">Raw frame time in milliseconds.</param>
    /// <param name="playerPos">Current camera / player world position.</param>
    /// <param name="chunksLoaded">Number of chunks currently in GPU memory.</param>
    /// <param name="creativeMode">Whether creative / fly mode is active.</param>
    /// <param name="heldItem">The <see cref="ItemStack"/> in the selected hotbar slot.</param>
    /// <param name="hotbarSlot">Index of the currently selected hotbar slot (0-based).</param>
    /// <param name="saveStatus">Optional last save / load status line shown in the overlay.</param>
    public void Render(float fps, float frameTimeMs, Vector3 playerPos, int chunksLoaded, bool creativeMode,
                     ItemStack heldItem, int hotbarSlot,
                     DebugState debugState, string? saveStatus = null)
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

        // ---- Toggles ----
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Toggles");
        ImGui.Separator();

        ImGui.Checkbox("Wireframe Mode", ref debugState.WireframeMode);
        ImGui.Checkbox("Show Chunk Borders", ref debugState.ShowChunkBorders);
        ImGui.Checkbox("No Textures (White)", ref debugState.NoTextures);
        ImGui.Checkbox("Lighting Debug (AO+Light)", ref debugState.LightingDebug);

        ImGui.Spacing();

        // ---- Profiler timings bar chart ----
        if (Profiler.Sections.Count > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Timings");
            ImGui.Separator();

            // Scale: bars fill at 16 ms (one 60 fps frame budget).
            const double barBudgetMs = 16.0;
            float barWidth = 180f;
            float barHeight = 14f;

            var drawList = ImGui.GetWindowDrawList();

            foreach (var name in Profiler.Sections)
            {
                double ms = Profiler.GetMs(name);
                double rawMs = Profiler.GetRawMs(name);
                double peakMs = Profiler.GetPeakMs(name);

                float fraction = (float)Math.Min(ms / barBudgetMs, 1.0);
                float rawFraction = (float)Math.Min(rawMs / barBudgetMs, 1.0);
                float peakFraction = (float)Math.Min(peakMs / barBudgetMs, 1.0);

                // Smoothed bar colour: green < 1 ms, yellow < 5 ms, red >= 5 ms.
                uint barColour = ms < 1.0
                    ? ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.85f, 0.2f, 0.9f))
                    : ms < 5.0
                        ? ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.95f, 0.8f, 0.1f, 0.9f))
                        : ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.95f, 0.25f, 0.25f, 0.9f));

                // Raw bar: slightly transparent version of the same colour
                uint rawColour = ms < 1.0
                    ? ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.2f, 0.85f, 0.2f, 0.35f))
                    : ms < 5.0
                        ? ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.95f, 0.8f, 0.1f, 0.35f))
                        : ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.95f, 0.25f, 0.25f, 0.35f));

                var cursor = ImGui.GetCursorScreenPos();

                // Background track
                drawList.AddRectFilled(
                    cursor,
                    new System.Numerics.Vector2(cursor.X + barWidth, cursor.Y + barHeight),
                    ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(0.15f, 0.15f, 0.15f, 0.8f)));

                // Raw last-frame bar (dim, behind smoothed bar — reveals spikes)
                if (rawFraction > 0f)
                    drawList.AddRectFilled(
                        cursor,
                        new System.Numerics.Vector2(cursor.X + barWidth * rawFraction, cursor.Y + barHeight),
                        rawColour);

                // Smoothed bar (solid, on top)
                if (fraction > 0f)
                    drawList.AddRectFilled(
                        cursor,
                        new System.Numerics.Vector2(cursor.X + barWidth * fraction, cursor.Y + barHeight),
                        barColour);

                // Peak tick: white vertical line that sticks at the highest seen value
                if (peakFraction > 0f)
                {
                    float px = cursor.X + barWidth * peakFraction;
                    drawList.AddLine(
                        new System.Numerics.Vector2(px, cursor.Y),
                        new System.Numerics.Vector2(px, cursor.Y + barHeight),
                        0xFFFFFFFF, 2f);
                }

                // Label: name + smoothed + peak
                string label = $"{name}  {ms:F2} ms  peak {peakMs:F2}";
                drawList.AddText(
                    new System.Numerics.Vector2(cursor.X + 4f, cursor.Y + 1f),
                    0xFFFFFFFF,
                    label);

                // Advance cursor past the bar
                ImGui.Dummy(new System.Numerics.Vector2(barWidth, barHeight));
                ImGui.Spacing();
            }

            // Legend: show what 100% bar width represents
            ImGui.TextDisabled($"Bar = {barBudgetMs} ms (60 fps budget)");
            ImGui.Spacing();
        }

        ImGui.TextDisabled("[F3] Toggle debug UI  [F] Toggle Creative/Survival");
        ImGui.TextDisabled("[Ctrl+S] Save world  [Scroll] Cycle hotbar");

        ImGui.End();
    }
}

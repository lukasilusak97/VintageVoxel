using ImGuiNET;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// The in-game developer dashboard rendered with ImGui.
///
/// Call <see cref="Draw"/> every frame (after ImGui.NewFrame and before
/// ImGuiController.Render) to draw the overlay window and update toggle states.
///
/// Exposed properties are read by Game.cs to apply the corresponding GL state
/// changes each frame.
/// </summary>
public class DebugWindow
{
    // --- Toggle states (read by Game.cs) ---
    public bool WireframeMode { get; private set; }
    public bool ShowChunkBorders { get; private set; }
    public bool NoTextures { get; private set; }
    /// <summary>
    /// When true the shader shows AO+light as greyscale (uNoTexture = 2).
    /// Shows the combined effect of ambient occlusion and light levels without
    /// texture colour, making both effects easy to inspect.
    /// </summary>
    public bool LightingDebug { get; private set; }

    // Exponential moving average for a stable FPS display.
    private float _smoothFps;
    private const float FpsSmoothAlpha = 0.05f;

    /// <summary>
    /// Draws the debug overlay window. Call each frame between
    /// ImGuiController.Update() and ImGuiController.Render().
    /// </summary>
    /// <param name="fps">Raw frames-per-second this frame.</param>
    /// <param name="frameTimeMs">Raw frame time in milliseconds.</param>
    /// <param name="playerPos">Current camera / player world position.</param>
    /// <param name="chunksLoaded">Number of chunks currently in GPU memory.</param>
    /// <param name="creativeMode">Whether creative / fly mode is active.</param>
    /// <param name="saveStatus">Optional last save / load status line shown in the overlay.</param>
    public void Draw(float fps, float frameTimeMs, Vector3 playerPos, int chunksLoaded, bool creativeMode,
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
        if (saveStatus != null)
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.4f, 1f), saveStatus);

        ImGui.Spacing();

        // ---- Toggles ----
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0f, 1f), "Toggles");
        ImGui.Separator();

        bool wf = WireframeMode;
        ImGui.Checkbox("Wireframe Mode", ref wf);
        WireframeMode = wf;

        bool cb = ShowChunkBorders;
        ImGui.Checkbox("Show Chunk Borders", ref cb);
        ShowChunkBorders = cb;

        bool nt = NoTextures;
        ImGui.Checkbox("No Textures (White)", ref nt);
        NoTextures = nt;

        bool ld = LightingDebug;
        ImGui.Checkbox("Lighting Debug (AO+Light)", ref ld);
        LightingDebug = ld;

        ImGui.Spacing();
        ImGui.TextDisabled("[F3] Toggle debug UI  [F] Toggle Creative/Survival");
        ImGui.TextDisabled("[Ctrl+S] Save world  [Middle Click] Chisel block");
        ImGui.TextDisabled("[LClick] Remove sub-voxel  [RClick] Add sub-voxel");

        ImGui.End();
    }
}

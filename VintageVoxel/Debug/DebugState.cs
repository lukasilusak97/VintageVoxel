namespace VintageVoxel;

/// <summary>
/// Shared mutable bag of debug render flags.
/// Written by <see cref="DebugWindow"/> each frame; read by
/// <see cref="VintageVoxel.Rendering.WorldRenderer"/> to adjust GL state.
/// </summary>
public sealed class DebugState
{
    public bool WireframeMode;
    public bool ShowChunkBorders;
    public bool NoTextures;
    public bool LightingDebug;
    public bool ShowVehicleDebug;
}

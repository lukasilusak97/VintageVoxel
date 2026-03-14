namespace VintageVoxel;

/// <summary>
/// Mutable runtime state attached to a tool-type <see cref="ItemStack"/>.
/// Tracks what material the tool is currently carrying (e.g. scooped layers).
/// <para>
/// Created lazily the first time a tool picks up material, and cleared
/// when all carried material is placed back into the world.
/// </para>
/// </summary>
public sealed class ToolData
{
    /// <summary>Block type currently loaded on the tool (0 = empty).</summary>
    public ushort CarriedBlockId;

    /// <summary>Number of layers currently loaded (0 = empty, max depends on <see cref="ToolDef.Capacity"/>).</summary>
    public byte CarriedLayers;

    /// <summary>True when the tool is not carrying any material.</summary>
    public bool IsEmpty => CarriedBlockId == 0 || CarriedLayers == 0;

    /// <summary>Resets the tool to its empty state.</summary>
    public void Clear()
    {
        CarriedBlockId = 0;
        CarriedLayers = 0;
    }
}

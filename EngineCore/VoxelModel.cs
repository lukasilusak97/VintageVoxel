using System.Text.Json.Serialization;

namespace VintageVoxel;

/// <summary>
/// Root data model for a JSON-exported microblock asset.
/// Matches the export format described in asset_editor.md §2.
/// </summary>
public sealed class VoxelModel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "unnamed";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "MicroBlock";

    /// <summary>Number of voxel cells along each axis (e.g. 16 → 16×16×16 grid).</summary>
    [JsonPropertyName("gridSize")]
    public int GridSize { get; set; } = 16;

    [JsonPropertyName("voxels")]
    public List<VoxelEntry> Voxels { get; set; } = new();
}

/// <summary>
/// A single filled cell in the voxel grid.
/// </summary>
public sealed class VoxelEntry
{
    [JsonPropertyName("x")]
    public int X { get; set; }

    [JsonPropertyName("y")]
    public int Y { get; set; }

    [JsonPropertyName("z")]
    public int Z { get; set; }

    /// <summary>Hex color string, e.g. "#FF0000".</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "#FFFFFF";
}

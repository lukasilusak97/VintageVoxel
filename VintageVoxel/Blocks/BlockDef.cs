using System.Text.Json.Serialization;

namespace VintageVoxel;

/// <summary>
/// Immutable definition of a block type as loaded from blocks.json.
/// </summary>
public sealed record BlockDef(
    int Id,
    string Name,

    /// <summary>Face texture names keyed by direction: up/down/north/south/east/west.
    /// The special key "all" is a shorthand that applies to every face.
    /// Null for model-only blocks (e.g. Torch) that have no cube faces.</summary>
    [property: JsonPropertyName("textures")]
    Dictionary<string, string>? Textures,

    bool Transparent = false,
    string? Model = null,
    string? Tint = null,

    /// <summary>Per-texture tint overrides. Maps texture name to a hex color string (e.g. "#79C05A").
    /// Takes precedence over the block-level <see cref="Tint"/> for the named texture.</summary>
    [property: JsonPropertyName("textureTints")]
    Dictionary<string, string>? TextureTints = null)
{
    /// <summary>
    /// Returns the texture name for the given face index.
    /// face 0=up, 1=down, 2=north, 3=south, 4=west, 5=east.
    /// Falls back to "all" key, then to an empty string.
    /// </summary>
    public string TextureForFace(int face)
    {
        string faceName = face switch
        {
            0 => "up",
            1 => "down",
            2 => "north",
            3 => "south",
            4 => "west",
            5 => "east",
            _ => "up"
        };

        if (Textures is null) return string.Empty;
        if (Textures.TryGetValue(faceName, out var tex)) return tex;
        if (Textures.TryGetValue("all", out var all)) return all;
        return string.Empty;
    }
}

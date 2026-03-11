using System.Text.Json.Serialization;

namespace VintageVoxel;

// ---------------------------------------------------------------------------
// Deserialization types for the Minecraft Java Edition block model JSON format.
// Reference: https://minecraft.wiki/w/Tutorials/Models
// ---------------------------------------------------------------------------

/// <summary>Root object of a Minecraft Java Edition block model JSON file.</summary>
public sealed class MinecraftJavaModel
{
    /// <summary>Optional Blockbench export version tag (not used by vanilla Minecraft).</summary>
    [JsonPropertyName("format_version")]
    public string? FormatVersion { get; set; }

    [JsonPropertyName("credit")]
    public string? Credit { get; set; }

    /// <summary>Optional parent model path (e.g. "block/cube_all").</summary>
    [JsonPropertyName("parent")]
    public string? Parent { get; set; }

    [JsonPropertyName("ambientocclusion")]
    public bool AmbientOcclusion { get; set; } = true;

    /// <summary>
    /// Maps texture variable names (e.g. "0", "all", "particle") to resource-pack
    /// paths or bare file names (e.g. "block/stone", "torch").
    /// </summary>
    [JsonPropertyName("textures")]
    public Dictionary<string, string> Textures { get; set; } = new();

    [JsonPropertyName("elements")]
    public List<MinecraftJavaElement> Elements { get; set; } = new();
}

/// <summary>A cube element defined by an axis-aligned bounding box.</summary>
public sealed class MinecraftJavaElement
{
    /// <summary>Minimum corner [x, y, z] in 0–16 block space.</summary>
    [JsonPropertyName("from")]
    public float[] From { get; set; } = new float[3];

    /// <summary>Maximum corner [x, y, z] in 0–16 block space.</summary>
    [JsonPropertyName("to")]
    public float[] To { get; set; } = new float[3];

    /// <summary>Optional rotation applied to the element around a pivot point.</summary>
    [JsonPropertyName("rotation")]
    public MinecraftJavaElementRotation? Rotation { get; set; }

    [JsonPropertyName("shade")]
    public bool Shade { get; set; } = true;

    /// <summary>Per-face data keyed by direction: "north", "south", "east", "west", "up", "down".</summary>
    [JsonPropertyName("faces")]
    public Dictionary<string, MinecraftJavaFace> Faces { get; set; } = new();
}

/// <summary>Rotation around a pivot point for a cube element.</summary>
public sealed class MinecraftJavaElementRotation
{
    /// <summary>Rotation angle in degrees. Vanilla supports −45, −22.5, 0, 22.5, 45.</summary>
    [JsonPropertyName("angle")]
    public float Angle { get; set; }

    /// <summary>Rotation axis: "x", "y", or "z".</summary>
    [JsonPropertyName("axis")]
    public string Axis { get; set; } = "y";

    /// <summary>Pivot point [x, y, z] in 0–16 space.</summary>
    [JsonPropertyName("origin")]
    public float[] Origin { get; set; } = new float[3];

    /// <summary>When true the element is scaled to compensate for the rotation.</summary>
    [JsonPropertyName("rescale")]
    public bool Rescale { get; set; }
}

/// <summary>A single face of a cube element.</summary>
public sealed class MinecraftJavaFace
{
    /// <summary>
    /// UV rectangle [u1, v1, u2, v2] in 0–16 texture-pixel space.
    /// When null the loader derives sensible defaults from the element AABB.
    /// Swapped u1/u2 or v1/v2 causes the texture to mirror on that axis.
    /// </summary>
    [JsonPropertyName("uv")]
    public float[]? Uv { get; set; }

    /// <summary>
    /// Texture variable reference, e.g. "#0", "#all".
    /// The "#" prefix is stripped and the name is looked up in <see cref="MinecraftJavaModel.Textures"/>.
    /// </summary>
    [JsonPropertyName("texture")]
    public string Texture { get; set; } = "#0";

    /// <summary>Optional face to cull against when the adjacent block is opaque.</summary>
    [JsonPropertyName("cullface")]
    public string? Cullface { get; set; }

    /// <summary>UV rotation in degrees clockwise: 0, 90, 180, or 270.</summary>
    [JsonPropertyName("rotation")]
    public int Rotation { get; set; }

    /// <summary>Biome tint index (−1 = no tint).</summary>
    [JsonPropertyName("tintindex")]
    public int TintIndex { get; set; } = -1;
}

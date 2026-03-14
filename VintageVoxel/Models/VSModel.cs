using System.Text.Json.Serialization;

namespace VintageVoxel;

/// <summary>
/// Root data model for a Vintage Story shape JSON file.
/// Contains texture mappings, a tree of <see cref="VSElement"/>s, and optional animations.
/// </summary>
public sealed class VSModel
{
    [JsonPropertyName("textureWidth")]
    public int TextureWidth { get; set; } = 16;

    [JsonPropertyName("textureHeight")]
    public int TextureHeight { get; set; } = 16;

    /// <summary>
    /// Maps texture variable names (e.g. "0", "1") to texture file names.
    /// Faces reference these via "#0", "#1", etc.
    /// </summary>
    [JsonPropertyName("textures")]
    public Dictionary<string, string> Textures { get; set; } = new();

    /// <summary>Root-level elements. Each may contain nested <see cref="VSElement.Children"/>.</summary>
    [JsonPropertyName("elements")]
    public List<VSElement> Elements { get; set; } = new();

    [JsonPropertyName("animations")]
    public List<VSAnimation> Animations { get; set; } = new();
}

/// <summary>
/// A box element in a VS shape. Supports hierarchical nesting via <see cref="Children"/>.
/// </summary>
public sealed class VSElement
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Minimum corner [x, y, z].</summary>
    [JsonPropertyName("from")]
    public float[] From { get; set; } = new float[3];

    /// <summary>Maximum corner [x, y, z].</summary>
    [JsonPropertyName("to")]
    public float[] To { get; set; } = new float[3];

    /// <summary>Pivot point [x, y, z] for rotations. Defaults to origin.</summary>
    [JsonPropertyName("rotationOrigin")]
    public float[] RotationOrigin { get; set; } = new float[3];

    [JsonPropertyName("rotationX")]
    public float RotationX { get; set; }

    [JsonPropertyName("rotationY")]
    public float RotationY { get; set; }

    [JsonPropertyName("rotationZ")]
    public float RotationZ { get; set; }

    /// <summary>Per-face data keyed by direction: "north", "south", "east", "west", "up", "down".</summary>
    [JsonPropertyName("faces")]
    public Dictionary<string, VSFace> Faces { get; set; } = new();

    /// <summary>Child elements that inherit this element's transform.</summary>
    [JsonPropertyName("children")]
    public List<VSElement> Children { get; set; } = new();
}

/// <summary>A single face of a VS box element.</summary>
public sealed class VSFace
{
    /// <summary>UV rectangle [u1, v1, u2, v2]. Null means auto-derive from element bounds.</summary>
    [JsonPropertyName("uv")]
    public float[]? Uv { get; set; }

    /// <summary>Texture variable reference, e.g. "#0".</summary>
    [JsonPropertyName("texture")]
    public string Texture { get; set; } = "#0";
}

/// <summary>A named keyframe animation for a VS model.</summary>
public sealed class VSAnimation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("keyframes")]
    public List<VSKeyFrame> KeyFrames { get; set; } = new();
}

/// <summary>A single keyframe in a VS animation, containing per-element transforms.</summary>
public sealed class VSKeyFrame
{
    /// <summary>Frame number (or time value) for this keyframe.</summary>
    [JsonPropertyName("frame")]
    public float Frame { get; set; }

    /// <summary>Per-element transform offsets, keyed by element name.</summary>
    [JsonPropertyName("elements")]
    public Dictionary<string, ElementTransform> Elements { get; set; } = new();
}

/// <summary>Position and rotation offsets applied to an element during animation.</summary>
public sealed class ElementTransform
{
    /// <summary>Translation offset [x, y, z].</summary>
    [JsonPropertyName("offsetX")]
    public float OffsetX { get; set; }

    [JsonPropertyName("offsetY")]
    public float OffsetY { get; set; }

    [JsonPropertyName("offsetZ")]
    public float OffsetZ { get; set; }

    [JsonPropertyName("rotationX")]
    public float RotationX { get; set; }

    [JsonPropertyName("rotationY")]
    public float RotationY { get; set; }

    [JsonPropertyName("rotationZ")]
    public float RotationZ { get; set; }
}

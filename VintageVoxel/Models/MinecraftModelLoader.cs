using System.Text.Json;

namespace VintageVoxel;

/// <summary>
/// Loads a Minecraft Java Edition block model JSON file and converts it to a
/// <see cref="ModelMesh"/> compatible with the project's chunk shader.
/// </summary>
/// <remarks>
/// Supported features:
/// <list type="bullet">
///   <item>Cube elements with <c>from</c>/<c>to</c> coordinates in 0–16 block space.</item>
///   <item>Per-face UV rectangles ([u1,v1,u2,v2] in 0–16 pixel space, including mirror via swapped coords).</item>
///   <item>Per-face UV rotation (0 / 90 / 180 / 270 degrees clockwise).</item>
///   <item>Per-element pivot rotation (angle, axis, origin).</item>
///   <item>Automatic default UVs when the <c>uv</c> field is absent.</item>
///   <item>Texture variable resolution and PNG lookup next to the model file.</item>
/// </list>
/// </remarks>
public static class MinecraftModelLoader
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses <paramref name="filePath"/> and returns a GPU-ready <see cref="ModelMesh"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">When the file does not exist.</exception>
    /// <exception cref="JsonException">When the JSON is malformed.</exception>
    public static ModelMesh Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Minecraft model file not found: {filePath}", filePath);

        string json = File.ReadAllText(filePath);
        MinecraftJavaModel? model = JsonSerializer.Deserialize<MinecraftJavaModel>(json, s_options);

        if (model is null)
            throw new JsonException($"Failed to deserialize Minecraft model from: {filePath}");

        return BuildMesh(model, filePath);
    }

    /// <summary>
    /// Attempts to load without throwing. Returns <c>false</c> on any failure.
    /// </summary>
    public static bool TryLoad(string filePath, out ModelMesh? mesh)
    {
        try { mesh = Load(filePath); return true; }
        catch { mesh = null; return false; }
    }

    // -----------------------------------------------------------------------
    // Mesh building
    // -----------------------------------------------------------------------

    private static ModelMesh BuildMesh(MinecraftJavaModel model, string filePath)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();

        // Minecraft coordinates and UVs are on a 0–16 grid.
        const float uvSize = 16f;

        foreach (MinecraftJavaElement elem in model.Elements)
            AppendElement(elem, vertices, indices, uvSize);

        byte[]? texPng = TryLoadTexture(model, filePath);
        string name = Path.GetFileNameWithoutExtension(filePath);

        return new ModelMesh
        {
            Name = name,
            Vertices = vertices.ToArray(),
            Indices = indices.ToArray(),
            TexturePng = texPng
        };
    }

    private static void AppendElement(
        MinecraftJavaElement elem,
        List<float> vertices,
        List<uint> indices,
        float uvSize)
    {
        float x1 = elem.From[0], y1 = elem.From[1], z1 = elem.From[2];
        float x2 = elem.To[0], y2 = elem.To[1], z2 = elem.To[2];

        // Six faces with CCW corner order viewed from outside (matches OpenGL default).
        // Minecraft: north = -Z, south = +Z, east = +X, west = -X.
        var cubeFaces = new (string key, (float x, float y, float z)[] corners)[]
        {
            ("north", [(x1,y2,z1), (x2,y2,z1), (x2,y1,z1), (x1,y1,z1)]),
            ("south", [(x2,y2,z2), (x1,y2,z2), (x1,y1,z2), (x2,y1,z2)]),
            ("east",  [(x2,y2,z1), (x2,y2,z2), (x2,y1,z2), (x2,y1,z1)]),
            ("west",  [(x1,y2,z2), (x1,y2,z1), (x1,y1,z1), (x1,y1,z2)]),
            ("up",    [(x1,y2,z2), (x2,y2,z2), (x2,y2,z1), (x1,y2,z1)]),
            ("down",  [(x1,y1,z1), (x2,y1,z1), (x2,y1,z2), (x1,y1,z2)]),
        };

        MinecraftJavaElementRotation? rot = elem.Rotation;

        foreach ((string key, (float x, float y, float z)[] corners) in cubeFaces)
        {
            if (!elem.Faces.TryGetValue(key, out MinecraftJavaFace? face)) continue;

            // Resolve UV rectangle — use explicit values or derive defaults from element AABB.
            float u1, v1f, u2, v2f;
            if (face.Uv is { Length: 4 })
            {
                u1 = face.Uv[0]; v1f = face.Uv[1];
                u2 = face.Uv[2]; v2f = face.Uv[3];
            }
            else
            {
                (u1, v1f, u2, v2f) = DefaultUv(key, x1, y1, z1, x2, y2, z2);
            }

            // Normalise from 0–16 pixel space to 0–1 UV space.
            u1 /= uvSize; v1f /= uvSize;
            u2 /= uvSize; v2f /= uvSize;

            // Build 4 UV pairs (TL, TR, BR, BL) with optional UV rotation.
            (float u, float v)[] uvs = ApplyUvRotation(u1, v1f, u2, v2f, face.Rotation);

            uint baseIdx = (uint)(vertices.Count / 7);

            for (int i = 0; i < 4; i++)
            {
                (float px, float py, float pz) = ApplyElementRotation(corners[i], rot);

                vertices.Add(px);
                vertices.Add(py);
                vertices.Add(pz);
                vertices.Add(uvs[i].u);
                vertices.Add(uvs[i].v);
                vertices.Add(1.0f); // light — fully lit
                vertices.Add(1.0f); // ao   — no ambient occlusion
            }

            // Two CCW triangles per quad.
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
        }
    }

    /// <summary>
    /// Derives default UV coordinates from the face direction and element AABB,
    /// following the Minecraft wiki's default UV specification.
    /// </summary>
    private static (float u1, float v1, float u2, float v2) DefaultUv(
        string face, float x1, float y1, float z1, float x2, float y2, float z2) =>
        face switch
        {
            "north" or "south" => (x1, 16f - y2, x2, 16f - y1),
            "east" or "west" => (z1, 16f - y2, z2, 16f - y1),
            "up" or "down" => (x1, z1, x2, z2),
            _ => (0f, 0f, 16f, 16f)
        };

    /// <summary>
    /// Returns 4 UV pairs for the quad corners (TL, TR, BR, BL) after applying
    /// the per-face UV rotation (0, 90, 180, or 270 degrees clockwise).
    /// </summary>
    private static (float u, float v)[] ApplyUvRotation(
        float u1, float v1, float u2, float v2, int rotation)
    {
        // Base mapping: index 0 = TL, 1 = TR, 2 = BR, 3 = BL.
        (float u, float v)[] uvs =
        [
            (u1, v1),   // TL
            (u2, v1),   // TR
            (u2, v2),   // BR
            (u1, v2),   // BL
        ];

        // Each 90° CW step cycles the array: the BL UV moves to TL, shifting all others right.
        int steps = ((rotation / 90) % 4 + 4) % 4;
        for (int i = 0; i < steps; i++)
        {
            (float u, float v) last = uvs[3];
            uvs[3] = uvs[2];
            uvs[2] = uvs[1];
            uvs[1] = uvs[0];
            uvs[0] = last;
        }

        return uvs;
    }

    /// <summary>
    /// Applies the element-level rotation (pivot/axis/angle) to a single corner position.
    /// </summary>
    private static (float x, float y, float z) ApplyElementRotation(
        (float x, float y, float z) pt, MinecraftJavaElementRotation? rot)
    {
        if (rot is null || rot.Angle == 0f) return pt;

        float ox = rot.Origin[0], oy = rot.Origin[1], oz = rot.Origin[2];
        float rad = rot.Angle * MathF.PI / 180f;
        float cos = MathF.Cos(rad), sin = MathF.Sin(rad);

        float dx = pt.x - ox, dy = pt.y - oy, dz = pt.z - oz;
        float rx, ry, rz;

        switch (rot.Axis.ToLowerInvariant())
        {
            case "x":
                rx = dx;
                ry = dy * cos - dz * sin;
                rz = dy * sin + dz * cos;
                break;
            case "y":
                rx = dx * cos + dz * sin;
                ry = dy;
                rz = -dx * sin + dz * cos;
                break;
            case "z":
                rx = dx * cos - dy * sin;
                ry = dx * sin + dy * cos;
                rz = dz;
                break;
            default:
                return pt;
        }

        return (rx + ox, ry + oy, rz + oz);
    }

    // -----------------------------------------------------------------------
    // Texture loading
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attempts to locate and load the first referenced texture as raw PNG bytes.
    /// Checks the model's directory, a sibling "textures" folder, and a few common
    /// parent-level "Textures" folders. Returns <c>null</c> when nothing is found.
    /// </summary>
    private static byte[]? TryLoadTexture(MinecraftJavaModel model, string modelFilePath)
    {
        if (model.Textures.Count == 0) return null;

        // Resolve: prefer common variable names, fall back to any non-reference value.
        string? textureName =
            ResolveTextureVar(model, "0") ??
            ResolveTextureVar(model, "all") ??
            FirstTextureValue(model);

        if (textureName is null) return null;

        // Strip "namespace:path/to/" prefix, keep only the bare file name.
        textureName = StripNamespacePath(textureName);

        string dir = Path.GetDirectoryName(modelFilePath) ?? ".";

        string[] candidates =
        [
            Path.Combine(dir, textureName + ".png"),
            Path.Combine(dir, "textures", textureName + ".png"),
            Path.Combine(dir, "..", "textures", textureName + ".png"),
            Path.Combine(dir, "..", "..", "Textures", textureName + ".png"),
            Path.Combine(dir, "..", "..", "textures", textureName + ".png"),
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);
            }
            catch { /* skip unreadable paths */ }
        }

        return null;
    }

    /// <summary>
    /// Resolves a texture variable, following indirection chains (e.g. "#other" → look up "other").
    /// Returns <c>null</c> if the key is absent or the chain leads to an unresolved reference.
    /// </summary>
    private static string? ResolveTextureVar(MinecraftJavaModel model, string key)
    {
        if (!model.Textures.TryGetValue(key, out string? val)) return null;

        const int maxChainDepth = 8;
        for (int i = 0; i < maxChainDepth && val is { Length: > 1 } && val[0] == '#'; i++)
        {
            string next = val[1..];
            if (!model.Textures.TryGetValue(next, out string? next2)) break;
            val = next2;
        }

        return val is { Length: > 0 } && val[0] != '#' ? val : null;
    }

    private static string? FirstTextureValue(MinecraftJavaModel model)
    {
        foreach (string v in model.Textures.Values)
            if (v is { Length: > 0 } && v[0] != '#')
                return v;
        return null;
    }

    /// <summary>
    /// Strips an optional "namespace:" prefix and any path segments, returning just
    /// the bare texture name for local file lookup.
    /// Example: "minecraft:block/torch" → "torch"
    /// </summary>
    private static string StripNamespacePath(string path)
    {
        int colon = path.IndexOf(':');
        if (colon >= 0) path = path[(colon + 1)..];

        int slash = path.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) path = path[(slash + 1)..];

        return path;
    }
}

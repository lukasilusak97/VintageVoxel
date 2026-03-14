using System.Text.Json;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Loads a Vintage Story shape JSON file and converts it to a
/// <see cref="ModelMesh"/> compatible with the project's chunk shader.
/// </summary>
/// <remarks>
/// Supported features:
/// <list type="bullet">
///   <item>Box elements with <c>from</c>/<c>to</c> coordinates.</item>
///   <item>Per-face UV rectangles ([u1,v1,u2,v2]).</item>
///   <item>Per-element rotation via <c>rotationX/Y/Z</c> around <c>rotationOrigin</c>.</item>
///   <item>Automatic default UVs when the <c>uv</c> field is absent.</item>
///   <item>Texture variable resolution and PNG lookup next to the model file.</item>
/// </list>
/// </remarks>
public static class VSModelLoader
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses <paramref name="filePath"/> and returns a GPU-ready <see cref="ModelMesh"/>.
    /// </summary>
    public static ModelMesh Load(string filePath, Dictionary<string, ElementTransform>? animOffsets = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}", filePath);

        string json = File.ReadAllText(filePath);
        VSModel? model = JsonSerializer.Deserialize<VSModel>(json, s_options);

        if (model is null)
            throw new JsonException($"Failed to deserialize model from: {filePath}");

        return BuildMesh(model, filePath, animOffsets);
    }

    /// <summary>
    /// Attempts to load without throwing. Returns <c>false</c> on any failure.
    /// </summary>
    public static bool TryLoad(string filePath, out ModelMesh? mesh)
    {
        try { mesh = Load(filePath); return true; }
        catch { mesh = null; return false; }
    }

    /// <summary>
    /// Deserialises a VS shape JSON file into a raw <see cref="VSModel"/> for
    /// repeated mesh rebuilds (e.g. animation).
    /// </summary>
    public static VSModel LoadModel(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}", filePath);

        string json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<VSModel>(json, s_options)
            ?? throw new JsonException($"Failed to deserialize model from: {filePath}");
    }

    /// <summary>
    /// Rebuilds a <see cref="ModelMesh"/> from an already-loaded <see cref="VSModel"/>
    /// with the given animation offsets. Texture lookup uses <paramref name="filePath"/>.
    /// </summary>
    public static ModelMesh BuildAnimatedMesh(
        VSModel model, string filePath, Dictionary<string, ElementTransform>? animOffsets)
    {
        return BuildMesh(model, filePath, animOffsets);
    }

    // -----------------------------------------------------------------------
    // Mesh building
    // -----------------------------------------------------------------------

    private static ModelMesh BuildMesh(VSModel model, string filePath, Dictionary<string, ElementTransform>? animOffsets = null)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();

        float uvWidth = model.TextureWidth;
        float uvHeight = model.TextureHeight;

        foreach (VSElement elem in model.Elements)
            AppendElementRecursive(elem, Matrix4.Identity, vertices, indices, uvWidth, uvHeight, animOffsets);

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

    private static void AppendElementRecursive(
        VSElement elem,
        Matrix4 parentTransform,
        List<float> vertices,
        List<uint> indices,
        float uvWidth,
        float uvHeight,
        Dictionary<string, ElementTransform>? animOffsets = null)
    {
        ElementTransform? anim = null;
        animOffsets?.TryGetValue(elem.Name, out anim);

        Matrix4 worldTransform = CalculateTransform(elem, parentTransform, anim);

        // Raw coordinates for UV derivation (pixel space).
        float rx1 = elem.From[0], ry1 = elem.From[1], rz1 = elem.From[2];
        float rx2 = elem.To[0], ry2 = elem.To[1], rz2 = elem.To[2];

        // Normalise to block-unit space (1 block = 1.0).
        float x1 = rx1 / 16f, y1 = ry1 / 16f, z1 = rz1 / 16f;
        float x2 = rx2 / 16f, y2 = ry2 / 16f, z2 = rz2 / 16f;

        // Six faces with CCW corner order viewed from outside (matches OpenGL default).
        // north = -Z, south = +Z, east = +X, west = -X.
        var cubeFaces = new (string key, (float x, float y, float z)[] corners)[]
        {
            ("north", [(x1,y2,z1), (x2,y2,z1), (x2,y1,z1), (x1,y1,z1)]),
            ("south", [(x2,y2,z2), (x1,y2,z2), (x1,y1,z2), (x2,y1,z2)]),
            ("east",  [(x2,y2,z1), (x2,y2,z2), (x2,y1,z2), (x2,y1,z1)]),
            ("west",  [(x1,y2,z2), (x1,y2,z1), (x1,y1,z1), (x1,y1,z2)]),
            ("up",    [(x1,y2,z2), (x2,y2,z2), (x2,y2,z1), (x1,y2,z1)]),
            ("down",  [(x1,y1,z1), (x2,y1,z1), (x2,y1,z2), (x1,y1,z2)]),
        };

        foreach ((string key, (float x, float y, float z)[] corners) in cubeFaces)
        {
            elem.Faces.TryGetValue(key, out VSFace? face);

            // Resolve UV rectangle -- use explicit values or derive defaults from element AABB.
            float u1, v1f, u2, v2f;
            if (face?.Uv is { Length: 4 })
            {
                u1 = face.Uv[0]; v1f = face.Uv[1];
                u2 = face.Uv[2]; v2f = face.Uv[3];
            }
            else
            {
                (u1, v1f, u2, v2f) = DefaultUv(key, rx1, ry1, rz1, rx2, ry2, rz2);
            }

            // Normalise from pixel space to 0-1 UV space.
            u1 /= uvWidth; v1f /= uvHeight;
            u2 /= uvWidth; v2f /= uvHeight;

            // Simple TL, TR, BR, BL UV mapping (no per-face UV rotation in VS format).
            (float u, float v)[] uvs =
            [
                (u1, v1f),
                (u2, v1f),
                (u2, v2f),
                (u1, v2f),
            ];

            uint baseIdx = (uint)(vertices.Count / 7);

            for (int i = 0; i < 4; i++)
            {
                var corner = new Vector4(corners[i].x, corners[i].y, corners[i].z, 1f);
                var transformed = corner * worldTransform;

                vertices.Add(transformed.X);
                vertices.Add(transformed.Y);
                vertices.Add(transformed.Z);
                vertices.Add(uvs[i].u);
                vertices.Add(uvs[i].v);
                vertices.Add(1.0f); // light
                vertices.Add(1.0f); // ao
            }

            // Two CCW triangles per quad.
            indices.Add(baseIdx); indices.Add(baseIdx + 1); indices.Add(baseIdx + 2);
            indices.Add(baseIdx); indices.Add(baseIdx + 2); indices.Add(baseIdx + 3);
        }

        // Recurse into child elements, passing this element's world transform.
        foreach (VSElement child in elem.Children)
            AppendElementRecursive(child, worldTransform, vertices, indices, uvWidth, uvHeight, animOffsets);
    }

    /// <summary>
    /// Derives default UV coordinates from the face direction and element AABB.
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
    /// Builds the local transform matrix for an element and multiplies it by the
    /// parent transform to produce the world-space matrix.
    /// When <paramref name="anim"/> is provided, its rotation/translation offsets are
    /// applied relative to the rotation origin, before the parent matrix multiply.
    /// </summary>
    internal static Matrix4 CalculateTransform(VSElement elem, Matrix4 parentTransform, ElementTransform? anim = null)
    {
        float ox = elem.RotationOrigin[0] / 16f;
        float oy = elem.RotationOrigin[1] / 16f;
        float oz = elem.RotationOrigin[2] / 16f;

        float rx = elem.RotationX + (anim?.RotationX ?? 0f);
        float ry = elem.RotationY + (anim?.RotationY ?? 0f);
        float rz = elem.RotationZ + (anim?.RotationZ ?? 0f);

        // Build local matrix: T(-origin) * Rz * Ry * Rx * T(+origin)
        var local = Matrix4.CreateTranslation(-ox, -oy, -oz) *
                    Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(rz)) *
                    Matrix4.CreateRotationY(MathHelper.DegreesToRadians(ry)) *
                    Matrix4.CreateRotationX(MathHelper.DegreesToRadians(rx)) *
                    Matrix4.CreateTranslation(ox, oy, oz);

        // Apply animated translation offset (already in block-units: /16 is done here).
        if (anim is not null && (anim.OffsetX != 0f || anim.OffsetY != 0f || anim.OffsetZ != 0f))
            local *= Matrix4.CreateTranslation(anim.OffsetX / 16f, anim.OffsetY / 16f, anim.OffsetZ / 16f);

        return local * parentTransform;
    }

    // -----------------------------------------------------------------------
    // Texture loading
    // -----------------------------------------------------------------------

    /// <summary>
    /// Attempts to locate and load the first referenced texture as raw PNG bytes.
    /// </summary>
    private static byte[]? TryLoadTexture(VSModel model, string modelFilePath)
    {
        if (model.Textures.Count == 0) return null;

        string? textureName =
            ResolveTextureVar(model, "0") ??
            ResolveTextureVar(model, "all") ??
            FirstTextureValue(model) ??
            FirstTextureKey(model);

        if (textureName is null) return null;

        textureName = StripNamespacePath(textureName);

        // Strip .png extension if present — we add it back when building candidates.
        if (textureName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            textureName = textureName[..^4];

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

    private static string? ResolveTextureVar(VSModel model, string key)
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

    private static string? FirstTextureValue(VSModel model)
    {
        foreach (string v in model.Textures.Values)
            if (v is { Length: > 0 } && v[0] != '#')
                return v;
        return null;
    }

    /// <summary>
    /// When all texture values are empty (meaning the texture file name IS the key),
    /// return the first key as the texture name.
    /// </summary>
    private static string? FirstTextureKey(VSModel model)
    {
        foreach (string k in model.Textures.Keys)
            if (k is { Length: > 0 })
                return k;
        return null;
    }

    private static string StripNamespacePath(string path)
    {
        int colon = path.IndexOf(':');
        if (colon >= 0) path = path[(colon + 1)..];

        int slash = path.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) path = path[(slash + 1)..];

        return path;
    }
}

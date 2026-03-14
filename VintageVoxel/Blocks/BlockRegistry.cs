using System.Text.Json;

namespace VintageVoxel;

/// <summary>
/// Maps block IDs to texture atlas tile indices and block properties.
/// Must be initialised at startup via <see cref="Load"/> then <see cref="Initialize"/>.
///
/// WHY keep this outside the Block struct?
///   Block is stored 32 768 times per chunk.  Adding tile-index fields would blow
///   up the struct size and trash the CPU cache during meshing.  The registry is a
///   tiny static table — one lookup per emitted face is negligible overhead.
/// </summary>
public static class BlockRegistry
{
    // face index → int[6] atlas tile index, indexed by block ID
    private static int[][] _faceTiles = Array.Empty<int[]>();
    private static bool[] _hasModel = Array.Empty<bool>();
    private static string?[] _modelNames = Array.Empty<string?>();
    private static BlockDef[] _defs = Array.Empty<BlockDef>();

    // ------------------------------------------------------------------
    // Loading
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses <paramref name="blocksJsonPath"/> and stores block definitions.
    /// Call this before <see cref="GetAllTextureNames"/> and <see cref="Initialize"/>.
    /// </summary>
    public static void Load(string blocksJsonPath)
    {
        string json = File.ReadAllText(blocksJsonPath);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };
        _defs = JsonSerializer.Deserialize<BlockDef[]>(json, options)
            ?? throw new InvalidDataException($"Failed to parse {blocksJsonPath}");
    }

    /// <summary>
    /// Returns the ordered, deduplicated list of texture names referenced by all loaded
    /// block definitions.  Pass this to <see cref="TextureAtlas.Build"/> to get the
    /// <c>nameToIndex</c> map required by <see cref="Initialize"/>.
    /// </summary>
    public static IReadOnlyList<string> GetAllTextureNames()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>();

        foreach (var def in _defs)
        {
            for (int face = 0; face < 6; face++)
            {
                string tex = def.TextureForFace(face);
                if (!string.IsNullOrEmpty(tex) && seen.Add(tex))
                    names.Add(tex);
            }
        }
        return names;
    }

    /// <summary>
    /// Returns a map from texture name to tint color (R,G,B bytes) for every
    /// block definition that declares a "tint" hex color.  Used by
    /// <see cref="TextureAtlas.Build"/> to multiply pixels at atlas-build time.
    /// </summary>
    public static Dictionary<string, (byte R, byte G, byte B)> GetTextureTints()
    {
        var result = new Dictionary<string, (byte, byte, byte)>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in _defs)
        {
            // Block-level tint applies to all face textures (used for uniform-texture blocks like leaves).
            if (!string.IsNullOrEmpty(def.Tint))
            {
                string hex = def.Tint!.TrimStart('#');
                if (hex.Length >= 6)
                {
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    for (int face = 0; face < 6; face++)
                    {
                        string tex = def.TextureForFace(face);
                        if (!string.IsNullOrEmpty(tex))
                            result[tex] = (r, g, b);
                    }
                }
            }

            // Per-texture tints override specific texture names without affecting shared textures.
            if (def.TextureTints != null)
            {
                foreach (var (texName, hexColor) in def.TextureTints)
                {
                    string hex = hexColor.TrimStart('#');
                    if (hex.Length < 6) continue;
                    byte r = Convert.ToByte(hex.Substring(0, 2), 16);
                    byte g = Convert.ToByte(hex.Substring(2, 2), 16);
                    byte b = Convert.ToByte(hex.Substring(4, 2), 16);
                    result[texName] = (r, g, b);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Builds the internal face-tile lookup table.  Must be called after
    /// <see cref="TextureAtlas.Build"/> has produced the <paramref name="nameToIndex"/> map.
    /// </summary>
    public static void Initialize(Dictionary<string, int> nameToIndex)
    {
        int maxId = 0;
        foreach (var def in _defs)
            if (def.Id > maxId) maxId = def.Id;

        // Allocate slots 0..maxId.  ID 0 = Air (never rendered, all tiles 0).
        _faceTiles = new int[maxId + 1][];
        for (int i = 0; i <= maxId; i++)
            _faceTiles[i] = new int[6]; // default: tile 0 for every face

        _hasModel = new bool[maxId + 1];
        _modelNames = new string?[maxId + 1];
        foreach (var def in _defs)
        {
            var tiles = new int[6];
            for (int face = 0; face < 6; face++)
            {
                string tex = def.TextureForFace(face);
                tiles[face] = !string.IsNullOrEmpty(tex) && nameToIndex.TryGetValue(tex, out int idx) ? idx : 0;
            }
            _faceTiles[def.Id] = tiles;
            _hasModel[def.Id] = !string.IsNullOrEmpty(def.Model);
            _modelNames[def.Id] = def.Model;
        }
    }

    // ------------------------------------------------------------------
    // Runtime lookups (called every frame by ChunkMeshBuilder)
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the atlas tile index for a specific face of the given block.
    /// face 0=up(+Y), 1=down(-Y), 2=north(-Z), 3=south(+Z), 4=west(-X), 5=east(+X).
    /// </summary>
    public static int TileForFace(ushort blockId, int face)
    {
        if (blockId == 0 || blockId >= _faceTiles.Length) return 0;
        return _faceTiles[blockId][face & 7];
    }

    /// <summary>Returns <see langword="true"/> if the block has a 3-D model and should not be meshed as a cube.</summary>
    public static bool HasModel(ushort blockId)
    {
        return blockId > 0 && blockId < _hasModel.Length && _hasModel[blockId];
    }

    /// <summary>Returns <see langword="true"/> if the block uses the X-face (cross) model rendered inline in the chunk mesh.</summary>
    public static bool IsCrossModel(ushort blockId)
    {
        if (blockId == 0 || blockId >= _modelNames.Length) return false;
        return string.Equals(_modelNames[blockId], "x_face", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns <see langword="true"/> if the block with the given ID is transparent.</summary>
    public static bool IsTransparent(ushort blockId)
    {
        if (blockId == 0) return true;
        foreach (var def in _defs)
            if (def.Id == blockId) return def.Transparent;
        return false;
    }

    /// <summary>Returns the display name of the block, or an empty string if not registered.</summary>
    public static string GetName(ushort blockId)
    {
        foreach (var def in _defs)
            if (def.Id == blockId) return def.Name;
        return string.Empty;
    }
}

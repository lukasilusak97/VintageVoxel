using System.Text.Json;

namespace VintageVoxel;

/// <summary>
/// Loads item definitions from items.json and provides fast lookup by ID.
/// Serves as the single source of truth for all item types in the game.
/// </summary>
public static class ItemRegistry
{
    private static readonly Dictionary<int, Item> _items = new();

    /// <summary>All loaded items keyed by their ID.</summary>
    public static IReadOnlyDictionary<int, Item> All => _items;

    /// <summary>
    /// Reads <paramref name="path"/> (a JSON array of item definitions),
    /// constructs <see cref="Item"/> instances, and stores them for lookup.
    /// For items with type "MODEL" the model JSON is resolved relative to
    /// the <c>Assets/Models</c> folder (name must match the item name, case-insensitive).
    /// </summary>
    public static void Load(string path)
    {
        _items.Clear();

        string modelsDir = Path.Combine(Path.GetDirectoryName(path) ?? ".", "Models");

        string json = File.ReadAllText(path);
        var defs = JsonSerializer.Deserialize<ItemDef[]>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException($"Failed to parse {path}");

        foreach (var def in defs)
        {
            ItemType itemType = def.Type.Equals("MODEL", StringComparison.OrdinalIgnoreCase)
                ? ItemType.Model
                : ItemType.Block;

            VoxelModel? model = null;
            ModelMesh? mesh = null;
            if (itemType == ItemType.Model)
            {
                string modelPath = Path.Combine(modelsDir, def.Name.ToLowerInvariant() + ".json");
                // Try Minecraft/Blockbench element format first; fall back to VoxelModel format.
                if (!MinecraftModelLoader.TryLoad(modelPath, out mesh))
                    ModelLoader.TryLoad(modelPath, out model);
            }

            _items[def.Id] = new Item(def.Id, def.Name, def.MaxStackSize, def.TextureId,
                                      itemType, model, mesh);
        }
    }

    /// <summary>Returns the item with the given ID, or <see langword="null"/> if not found.</summary>
    public static Item? Get(int id) => _items.TryGetValue(id, out var item) ? item : null;

    // -------------------------------------------------------------------------
    // Private DTO — matches the JSON schema in items.json
    // -------------------------------------------------------------------------
    private sealed class ItemDef
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int MaxStackSize { get; set; }
        public int TextureId { get; set; }
        public string Type { get; set; } = "BLOCK";
    }
}

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
            ModelMesh? mesh = null;
            ItemType itemType = ItemType.Block;
            string? modelRelPath = null;

            if (def.EntityId > 0)
            {
                itemType = ItemType.Entity;
                var entityDef = EntityRegistry.Get(def.EntityId);
                if (entityDef?.Model != null)
                {
                    // Convert entity model path (e.g. "Models/Entities/X/x.json")
                    // to renderer-relative path (e.g. "Entities/X/x") under Assets/Models/.
                    string raw = entityDef.Model;
                    if (raw.StartsWith("Models/", StringComparison.OrdinalIgnoreCase))
                        raw = raw["Models/".Length..];
                    if (raw.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        raw = raw[..^5];
                    modelRelPath = raw.ToLowerInvariant();
                    string modelPath = Path.Combine(modelsDir, modelRelPath + ".json");
                    VSModelLoader.TryLoad(modelPath, out mesh);
                }
            }
            else if (!string.IsNullOrEmpty(def.Model))
            {
                itemType = ItemType.Model;
                modelRelPath = def.Model.ToLowerInvariant();
                string modelPath = Path.Combine(modelsDir, modelRelPath + ".json");
                VSModelLoader.TryLoad(modelPath, out mesh);
            }

            ToolDef? toolDef = null;
            if (def.Tool != null)
            {
                toolDef = new ToolDef(
                    def.Tool.Type ?? string.Empty,
                    def.Tool.Capacity,
                    def.Tool.TargetBlocks ?? Array.Empty<int>());
            }

            _items[def.Id] = new Item(def.Id, def.Name, def.MaxStackSize, def.BlockId,
                                      itemType, mesh, def.EntityId, modelRelPath, toolDef);
        }
    }

    /// <summary>Returns the item with the given ID, or <see langword="null"/> if not found.</summary>
    public static Item? Get(int id) => _items.TryGetValue(id, out var item) ? item : null;

    /// <summary>Returns the first item whose <see cref="Item.EntityId"/> matches, or null.</summary>
    public static Item? GetByEntityId(int entityId)
    {
        foreach (var item in _items.Values)
            if (item.EntityId == entityId) return item;
        return null;
    }

    // -------------------------------------------------------------------------
    // Private DTO — matches the JSON schema in items.json
    // -------------------------------------------------------------------------
    private sealed class ItemDef
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int MaxStackSize { get; set; }
        public int BlockId { get; set; }
        /// <summary>Optional model path (relative to Assets/Models/, without extension).</summary>
        public string? Model { get; set; }
        /// <summary>Entity ID to spawn when this item is placed (0 = not an entity item).</summary>
        public int EntityId { get; set; }
        /// <summary>Optional tool definition for tool-type items.</summary>
        public ToolDefDto? Tool { get; set; }
    }

    private sealed class ToolDefDto
    {
        public string? Type { get; set; }
        public int Capacity { get; set; }
        public int[]? TargetBlocks { get; set; }
    }
}

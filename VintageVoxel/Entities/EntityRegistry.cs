using System.Text.Json;

namespace VintageVoxel;

/// <summary>
/// Loads entity definitions from entities.json and their associated setup files.
/// Provides fast lookup by entity ID.
/// </summary>
public static class EntityRegistry
{
    private static readonly Dictionary<int, EntityDef> _defs = new();
    private static readonly Dictionary<int, VehicleSetup> _vehicleSetups = new();

    public static IReadOnlyDictionary<int, EntityDef> All => _defs;

    /// <summary>
    /// Loads entities.json and parses each entity's setup file from Assets/Entities/.
    /// </summary>
    public static void Load(string entitiesJsonPath)
    {
        _defs.Clear();
        _vehicleSetups.Clear();

        string entitiesDir = Path.GetDirectoryName(entitiesJsonPath) ?? ".";
        string setupDir = Path.Combine(entitiesDir, "Entities");

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
        };

        string json = File.ReadAllText(entitiesJsonPath);
        var defs = JsonSerializer.Deserialize<EntityDef[]>(json, options)
            ?? throw new InvalidDataException($"Failed to parse {entitiesJsonPath}");

        foreach (var def in defs)
        {
            _defs[def.Id] = def;

            if (string.Equals(def.Type, "vehicle", StringComparison.OrdinalIgnoreCase))
            {
                string setupPath = Path.Combine(setupDir, def.Setup.ToLowerInvariant() + ".json");
                if (File.Exists(setupPath))
                {
                    string setupJson = File.ReadAllText(setupPath);
                    var setup = JsonSerializer.Deserialize<VehicleSetup>(setupJson, options)
                        ?? new VehicleSetup();
                    _vehicleSetups[def.Id] = setup;
                }
            }
        }
    }

    /// <summary>Returns the entity definition with the given ID, or null if not found.</summary>
    public static EntityDef? Get(int id) => _defs.TryGetValue(id, out var def) ? def : null;

    /// <summary>Returns the vehicle setup for the given entity ID, or null if not a vehicle.</summary>
    public static VehicleSetup? GetVehicleSetup(int id) =>
        _vehicleSetups.TryGetValue(id, out var setup) ? setup : null;
}

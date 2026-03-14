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
    private static readonly Dictionary<int, WheelSetup> _wheelSetups = new();

    public static IReadOnlyDictionary<int, EntityDef> All => _defs;

    /// <summary>
    /// Loads entities.json and parses each entity's setup file from Assets/Vehicles/.
    /// </summary>
    public static void Load(string entitiesJsonPath)
    {
        _defs.Clear();
        _vehicleSetups.Clear();
        _wheelSetups.Clear();

        string entitiesDir = Path.GetDirectoryName(entitiesJsonPath) ?? ".";
        string setupDir = Path.Combine(entitiesDir, "Vehicles");

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

            string setupPath = Path.Combine(setupDir, def.Setup.ToLowerInvariant() + ".json");
            if (!File.Exists(setupPath)) continue;

            string setupJson = File.ReadAllText(setupPath);

            if (string.Equals(def.Type, "vehicleBody", StringComparison.OrdinalIgnoreCase))
            {
                var setup = JsonSerializer.Deserialize<VehicleSetup>(setupJson, options)
                    ?? new VehicleSetup();
                _vehicleSetups[def.Id] = setup;
            }
            else if (string.Equals(def.Type, "vehicleWheel", StringComparison.OrdinalIgnoreCase))
            {
                var setup = JsonSerializer.Deserialize<WheelSetup>(setupJson, options)
                    ?? new WheelSetup();
                _wheelSetups[def.Id] = setup;
            }
        }
    }

    /// <summary>Returns the entity definition with the given ID, or null if not found.</summary>
    public static EntityDef? Get(int id) => _defs.TryGetValue(id, out var def) ? def : null;

    /// <summary>Returns the vehicle setup for the given entity ID, or null if not a vehicle body.</summary>
    public static VehicleSetup? GetVehicleSetup(int id) =>
        _vehicleSetups.TryGetValue(id, out var setup) ? setup : null;

    /// <summary>Returns the wheel setup for the given entity ID, or null if not a vehicle wheel.</summary>
    public static WheelSetup? GetWheelSetup(int id) =>
        _wheelSetups.TryGetValue(id, out var setup) ? setup : null;
}

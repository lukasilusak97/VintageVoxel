using System.Text.Json.Serialization;

namespace VintageVoxel;

/// <summary>
/// Immutable definition of an entity type as loaded from entities.json.
/// </summary>
public sealed record EntityDef(
    int Id,
    string Name,

    /// <summary>Entity category: "vehicle", etc.</summary>
    string Type,

    /// <summary>Setup file name (without extension) inside Assets/Entities/.</summary>
    string Setup);

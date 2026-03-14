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

    /// <summary>Model JSON path relative to Assets/, e.g. "Models/Entities/BuggieBody/buggie_body.json".</summary>
    string? Model,

    /// <summary>Setup JSON path relative to Assets/, e.g. "Vehicles/buggie.json".</summary>
    string Setup);

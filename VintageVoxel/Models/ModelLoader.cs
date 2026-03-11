using System.Text.Json;

namespace VintageVoxel;

/// <summary>
/// Loads a <see cref="VoxelModel"/> from a JSON file on disk.
/// Used by both the GameClient and the AssetEditor to import microblock assets.
/// </summary>
public static class ModelLoader
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Parses <paramref name="filePath"/> and returns the deserialized <see cref="VoxelModel"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">When the file does not exist.</exception>
    /// <exception cref="JsonException">When the JSON is malformed.</exception>
    public static VoxelModel Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Model file not found: {filePath}", filePath);

        string json = File.ReadAllText(filePath);
        VoxelModel? model = JsonSerializer.Deserialize<VoxelModel>(json, s_options);

        if (model is null)
            throw new JsonException($"Failed to deserialize model from: {filePath}");

        return model;
    }

    /// <summary>
    /// Attempts to parse the file without throwing. Returns <c>false</c> on failure.
    /// </summary>
    public static bool TryLoad(string filePath, out VoxelModel? model)
    {
        try
        {
            model = Load(filePath);
            return true;
        }
        catch
        {
            model = null;
            return false;
        }
    }
}

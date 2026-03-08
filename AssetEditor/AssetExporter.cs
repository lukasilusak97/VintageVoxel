using System.Text.Json;

namespace VintageVoxel.Editor;

/// <summary>
/// Serializes a <see cref="VintageVoxel.VoxelModel"/> to a formatted JSON file.
/// Output is written to the shared <c>SharedData/Models/</c> folder beside the solution.
/// </summary>
public static class AssetExporter
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Saves <paramref name="model"/> to <c>{outputDir}/{model.Name}.json</c>.
    /// Creates the directory if it does not exist.
    /// </summary>
    /// <returns>The full path of the written file.</returns>
    public static string Export(VintageVoxel.VoxelModel model, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        string filePath = Path.Combine(outputDir, $"{model.Name}.json");
        string json = JsonSerializer.Serialize(model, s_options);
        File.WriteAllText(filePath, json);
        return filePath;
    }

    /// <summary>
    /// Convenience overload that writes to the solution-relative default path:
    /// <c>../../SharedData/Models/</c> (two levels up from the binary output folder).
    /// </summary>
    public static string ExportToSharedData(VintageVoxel.VoxelModel model)
    {
        // Resolve relative to the running executable so it works from any cwd.
        string baseDir = AppContext.BaseDirectory;
        string sharedDir = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "SharedData", "Models"));
        return Export(model, sharedDir);
    }
}

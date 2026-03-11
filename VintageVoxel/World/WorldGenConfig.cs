namespace VintageVoxel;

/// <summary>
/// Global world-generation parameters, set once before a world is created or loaded.
/// Applied by <see cref="Chunk"/> during terrain generation and by
/// <see cref="NoiseGenerator"/> for seeded permutation.
/// </summary>
public static class WorldGenConfig
{
    /// <summary>
    /// When <c>true</c>, chunks are generated as a flat plane instead of
    /// procedural terrain.  Flat worlds ignore the current seed.
    /// </summary>
    public static bool FlatWorld { get; set; } = false;
}

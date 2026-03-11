namespace VintageVoxel;

/// <summary>
/// GPU-ready mesh data produced by <see cref="MinecraftModelLoader"/>.
/// </summary>
/// <remarks>
/// Vertex layout matches the world chunk shader stride (7 floats = 28 bytes):
///   [0-2] position  (x, y, z)
///   [3-4] tex coord (u, v)   — normalised 0..1
///   [5]   light     — always 1.0 (fully lit)
///   [6]   ao        — always 1.0 (no ambient occlusion)
/// </remarks>
public sealed class ModelMesh
{
    /// <summary>Model name taken from the source file name.</summary>
    public string Name { get; init; } = "unnamed";

    /// <summary>
    /// Interleaved vertex data: 7 floats per vertex [x, y, z, u, v, light, ao].
    /// </summary>
    public float[] Vertices { get; init; } = Array.Empty<float>();

    /// <summary>Triangle indices referencing <see cref="Vertices"/>.</summary>
    public uint[] Indices { get; init; } = Array.Empty<uint>();

    /// <summary>
    /// Raw PNG bytes for the first resolved texture.
    /// <c>null</c> when no texture file was found.
    /// </summary>
    public byte[]? TexturePng { get; init; }
}

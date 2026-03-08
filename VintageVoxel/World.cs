using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Manages the collection of loaded chunks in an infinite procedural world.
///
/// Chunks are keyed by 2-D chunk-space coordinates (X, Z), where one unit equals
/// Chunk.Size (32) world blocks.  The world is infinite horizontally; vertically
/// there is a single chunk layer (block Y in [0, Chunk.Size)).
///
/// Call <see cref="Update"/> every frame with the camera/player position.  The
/// method generates missing chunks within <see cref="RenderDistance"/> and unloads
/// those that have drifted beyond <see cref="UnloadDistance"/>, returning the
/// change lists so the caller can create or destroy matching GPU resources.
/// </summary>
public class World
{
    /// <summary>
    /// Half-width of the loaded region: chunks are generated from
    /// (center - RenderDistance) to (center + RenderDistance) in each axis,
    /// producing a (2*RenderDistance+1)^2 grid.  The roadmap specifies a 5×5
    /// grid, which corresponds to RenderDistance = 2.
    /// </summary>
    public const int RenderDistance = 4; // 9x9 grid — comfortable view distance

    // Extra buffer before a chunk is unloaded.  Prevents rapid thrashing when
    // the player walks back and forth across a chunk boundary.
    private const int UnloadDistance = RenderDistance + 2;

    private readonly Dictionary<Vector2i, Chunk> _chunks = new();

    /// <summary>Read-only view of the currently active chunks.</summary>
    public IReadOnlyDictionary<Vector2i, Chunk> Chunks => _chunks;

    // -------------------------------------------------------------------------
    // Coordinate utilities
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a world-space position to the chunk-space (X, Z) tile that
    /// contains it.  Uses Floor so negative world coordinates round toward
    /// negative infinity rather than toward zero.
    /// </summary>
    public static Vector2i WorldToChunk(Vector3 worldPos) => new(
        (int)MathF.Floor(worldPos.X / Chunk.Size),
        (int)MathF.Floor(worldPos.Z / Chunk.Size));

    // -------------------------------------------------------------------------
    // Block query (used by ChunkMeshBuilder for cross-chunk face culling)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the block at the given world-space integer coordinates.
    ///
    /// Returns <see cref="Block.Air"/> when:
    ///   • worldY is outside the single vertical chunk layer [0, Chunk.Size), or
    ///   • the owning chunk has not been loaded yet.
    ///
    /// The second case intentionally exposes chunk boundary faces toward unloaded
    /// neighbours so newly appearing chunks mesh correctly on arrival.
    /// </summary>
    public Block GetBlock(int worldX, int worldY, int worldZ)
    {
        if ((uint)worldY >= (uint)Chunk.Size)
            return Block.Air;

        // Fast floor for negative world coordinates.
        int cx = (int)MathF.Floor((float)worldX / Chunk.Size);
        int cz = (int)MathF.Floor((float)worldZ / Chunk.Size);

        if (!_chunks.TryGetValue(new Vector2i(cx, cz), out Chunk? chunk))
            return Block.Air;

        int lx = worldX - cx * Chunk.Size;
        int lz = worldZ - cz * Chunk.Size;
        return chunk.GetBlock(lx, worldY, lz);
    }

    // -------------------------------------------------------------------------
    // Chunk streaming
    // -------------------------------------------------------------------------

    /// <summary>
    /// Loads chunks within <see cref="RenderDistance"/> of <paramref name="playerPos"/>
    /// and unloads those beyond <see cref="UnloadDistance"/>.
    ///
    /// <paramref name="added"/>   — chunk keys that were created this call.
    /// <paramref name="removed"/> — chunk keys that were discarded this call.
    ///
    /// The caller uses these lists to allocate or release GPU resources.
    /// </summary>
    public void Update(Vector3 playerPos,
                       out List<Vector2i> added,
                       out List<Vector2i> removed)
    {
        added = new List<Vector2i>();
        removed = new List<Vector2i>();

        Vector2i center = WorldToChunk(playerPos);

        // Generate missing chunks within the render square.
        for (int dz = -RenderDistance; dz <= RenderDistance; dz++)
            for (int dx = -RenderDistance; dx <= RenderDistance; dx++)
            {
                var key = new Vector2i(center.X + dx, center.Y + dz);
                if (!_chunks.ContainsKey(key))
                {
                    // Position.Y = 0: single vertical chunk layer.
                    _chunks[key] = new Chunk(new Vector3i(key.X, 0, key.Y));
                    added.Add(key);
                }
            }

        // Unload chunks that moved outside the buffer zone.
        // Iterating over a copy allows safe removal during the loop.
        foreach (var key in new List<Vector2i>(_chunks.Keys))
        {
            if (Math.Abs(key.X - center.X) > UnloadDistance ||
                Math.Abs(key.Y - center.Y) > UnloadDistance)
            {
                _chunks.Remove(key);
                removed.Add(key);
            }
        }
    }
}

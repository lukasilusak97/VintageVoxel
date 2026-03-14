using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Manages the collection of loaded chunks in an infinite procedural world.
///
/// Chunks are keyed by 3-D chunk-space coordinates (X, Y, Z), where one unit equals
/// Chunk.Size (32) world blocks.  The world is infinite horizontally; vertically
/// chunks span from Y=0 to Y=MaxChunkY-1, giving MaxChunkY*Chunk.Size world blocks
/// of build height.
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

    /// <summary>Number of vertical chunk layers (Y=0 to MaxChunkY-1). Total build height = MaxChunkY * Chunk.Size.</summary>
    public const int MaxChunkY = 8; // 256 blocks max build height

    // Extra buffer before a chunk is unloaded.  Prevents rapid thrashing when
    // the player walks back and forth across a chunk boundary.
    private const int UnloadDistance = RenderDistance + 2;

    private readonly Dictionary<Vector3i, Chunk> _chunks = new();

    /// <summary>Read-only view of the currently active chunks.</summary>
    public IReadOnlyDictionary<Vector3i, Chunk> Chunks => _chunks;

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
    /// Returns <see cref="Block.Air"/> when the owning chunk has not been loaded.
    /// </summary>
    public Block GetBlock(int worldX, int worldY, int worldZ)
    {
        int cx = (int)MathF.Floor((float)worldX / Chunk.Size);
        int cy = (int)MathF.Floor((float)worldY / Chunk.Size);
        int cz = (int)MathF.Floor((float)worldZ / Chunk.Size);

        if (!_chunks.TryGetValue(new Vector3i(cx, cy, cz), out Chunk? chunk))
            return Block.Air;

        int lx = worldX - cx * Chunk.Size;
        int ly = worldY - cy * Chunk.Size;
        int lz = worldZ - cz * Chunk.Size;
        return chunk.GetBlock(lx, ly, lz);
    }

    // -------------------------------------------------------------------------
    // Chunk streaming
    // -------------------------------------------------------------------------

    /// <summary>Max chunks to generate (terrain noise) per frame to avoid spikes.</summary>
    private const int MaxChunkGenPerFrame = 4;

    /// <summary>
    /// Optional GPU terrain generator. When set, chunk creation uses the GPU
    /// compute shader for heightmap/biome computation instead of CPU noise.
    /// </summary>
    public GpuTerrainGenerator? GpuTerrain { get; set; }

    // Caches GPU terrain + settlement data for partially-completed columns so
    // a column split across two frames doesn't re-dispatch the GPU or re-run
    // settlement queries.
    private readonly Dictionary<Vector2i, ColumnData> _columnCache = new();

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
                       out List<Vector3i> added,
                       out List<Vector3i> removed,
                       out List<(Vector3i key, Chunk chunk)> removedDirty)
    {
        added = new List<Vector3i>();
        removed = new List<Vector3i>();
        removedDirty = new List<(Vector3i, Chunk)>();

        Vector2i center = WorldToChunk(playerPos);

        // Generate missing chunks within the render square, across all vertical layers.
        // Rate-limited to avoid frame spikes from terrain generation.
        // Nearby columns are generated first (ring-by-ring distance order).
        int generated = 0;
        for (int dist = 0; dist <= RenderDistance && generated < MaxChunkGenPerFrame; dist++)
            for (int dz = -dist; dz <= dist && generated < MaxChunkGenPerFrame; dz++)
                for (int dx = -dist; dx <= dist && generated < MaxChunkGenPerFrame; dx++)
                {
                    // Only process the ring at this distance, skip interior (already handled).
                    if (Math.Abs(dx) != dist && Math.Abs(dz) != dist) continue;

                    // Check if any Y layer in this column is missing.
                    bool anyMissing = false;
                    for (int cy = 0; cy < MaxChunkY; cy++)
                    {
                        if (!_chunks.ContainsKey(new Vector3i(center.X + dx, cy, center.Y + dz)))
                        { anyMissing = true; break; }
                    }
                    if (!anyMissing) continue;

                    var colXZ = new Vector2i(center.X + dx, center.Y + dz);

                    // Reuse cached column data if this column was partially completed last frame.
                    if (!_columnCache.TryGetValue(colXZ, out ColumnData? colData))
                    {
                        // Compute heightmap + biome once per XZ column (GPU or CPU).
                        if (GpuTerrain != null && !WorldGenConfig.FlatWorld)
                        {
                            GpuTerrain.Generate(colXZ, out float[] gpuHeights, out int[] gpuBiomes);
                            colData = Chunk.BuildColumnData(colXZ, gpuHeights, gpuBiomes);
                        }
                        else
                        {
                            colData = null;
                        }

                        // Cache for subsequent frames if we can't finish the whole column now.
                        if (colData != null)
                            _columnCache[colXZ] = colData;
                    }

                    bool columnComplete = true;
                    for (int cy = 0; cy < MaxChunkY && generated < MaxChunkGenPerFrame; cy++)
                    {
                        var key = new Vector3i(center.X + dx, cy, center.Y + dz);
                        if (!_chunks.ContainsKey(key))
                        {
                            _chunks[key] = colData != null
                                ? new Chunk(key, colData)
                                : new Chunk(key);
                            added.Add(key);
                            generated++;
                        }
                    }

                    // Check if we finished all Y layers for this column.
                    for (int cy = 0; cy < MaxChunkY; cy++)
                    {
                        if (!_chunks.ContainsKey(new Vector3i(colXZ.X, cy, colXZ.Y)))
                        { columnComplete = false; break; }
                    }
                    if (columnComplete)
                        _columnCache.Remove(colXZ);
                }

        // Unload chunks whose XZ column moved outside the buffer zone.
        foreach (var key in new List<Vector3i>(_chunks.Keys))
        {
            if (Math.Abs(key.X - center.X) > UnloadDistance ||
                Math.Abs(key.Z - center.Y) > UnloadDistance)
            {
                var chunk = _chunks[key];
                if (chunk.IsDirty)
                    removedDirty.Add((key, chunk));
                _chunks.Remove(key);
                removed.Add(key);
            }
        }

        // Evict stale column cache entries for columns that moved out of range.
        if (_columnCache.Count > 0)
        {
            foreach (var colKey in new List<Vector2i>(_columnCache.Keys))
            {
                if (Math.Abs(colKey.X - center.X) > UnloadDistance ||
                    Math.Abs(colKey.Y - center.Y) > UnloadDistance)
                    _columnCache.Remove(colKey);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Light query (used by ChunkMeshBuilder for cross-chunk light sampling)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the combined light level [0,1] at the given world coordinate.
    /// Returns 1.0 for unloaded positions (full-bright at boundaries).
    /// </summary>
    public float GetLight(int worldX, int worldY, int worldZ)
    {
        int cx = (int)MathF.Floor((float)worldX / Chunk.Size);
        int cy = (int)MathF.Floor((float)worldY / Chunk.Size);
        int cz = (int)MathF.Floor((float)worldZ / Chunk.Size);

        if (!_chunks.TryGetValue(new Vector3i(cx, cy, cz), out Chunk? chunk))
            return 1.0f;

        int lx = worldX - cx * Chunk.Size;
        int ly = worldY - cy * Chunk.Size;
        int lz = worldZ - cz * Chunk.Size;
        int idx = Chunk.Index(lx, ly, lz);
        return Math.Max(chunk.SunLight[idx], chunk.BlockLight[idx]) / 15f;
    }

    /// <summary>
    /// Returns the separate sunlight and block-light levels [0,1] at the given
    /// world coordinate.  Returns (1,0) for unloaded positions.
    /// </summary>
    public (float sun, float block) GetSunAndBlockLight(int worldX, int worldY, int worldZ)
    {
        int cx = (int)MathF.Floor((float)worldX / Chunk.Size);
        int cy = (int)MathF.Floor((float)worldY / Chunk.Size);
        int cz = (int)MathF.Floor((float)worldZ / Chunk.Size);

        if (!_chunks.TryGetValue(new Vector3i(cx, cy, cz), out Chunk? chunk))
            return (1.0f, 0f);

        int lx = worldX - cx * Chunk.Size;
        int ly = worldY - cy * Chunk.Size;
        int lz = worldZ - cz * Chunk.Size;
        int idx = Chunk.Index(lx, ly, lz);
        return (chunk.SunLight[idx] / 15f, chunk.BlockLight[idx] / 15f);
    }

    // -------------------------------------------------------------------------
    // Block mutation (used by interaction / raycasting)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="block"/> to the given world-space integer coordinates.
    /// Returns <c>false</c> if the owning chunk is not currently loaded.
    /// </summary>
    public bool SetBlock(int worldX, int worldY, int worldZ, Block block)
    {
        int cx = (int)MathF.Floor((float)worldX / Chunk.Size);
        int cy = (int)MathF.Floor((float)worldY / Chunk.Size);
        int cz = (int)MathF.Floor((float)worldZ / Chunk.Size);

        if (!_chunks.TryGetValue(new Vector3i(cx, cy, cz), out Chunk? chunk)) return false;

        int lx = worldX - cx * Chunk.Size;
        int ly = worldY - cy * Chunk.Size;
        int lz = worldZ - cz * Chunk.Size;
        ref Block b = ref chunk.GetBlock(lx, ly, lz);
        b = block;
        chunk.IsDirty = true;
        return true;
    }

    // -------------------------------------------------------------------------
    // Phase 14: persistence support
    // -------------------------------------------------------------------------

    /// <summary>
    /// Replaces the chunk stored at <paramref name="key"/> with
    /// <paramref name="chunk"/>.  Used by <see cref="WorldPersistence"/> to swap
    /// the freshly-generated chunk for the player's saved version immediately
    /// after <see cref="Update"/> creates the slot.
    /// </summary>
    public void ReplaceChunk(Vector3i key, Chunk chunk) => _chunks[key] = chunk;

    /// <summary>
    /// Ensures every vertical chunk in the column at (<paramref name="chunkX"/>,
    /// <paramref name="chunkZ"/>) is loaded, generating any that are missing.
    /// Returns the keys of newly created chunks so the caller can include them
    /// in lighting and mesh-build passes.
    /// </summary>
    public List<Vector3i> EnsureColumnLoaded(int chunkX, int chunkZ)
    {
        var added = new List<Vector3i>();
        for (int cy = 0; cy < MaxChunkY; cy++)
        {
            var key = new Vector3i(chunkX, cy, chunkZ);
            if (!_chunks.ContainsKey(key))
            {
                _chunks[key] = new Chunk(key);
                added.Add(key);
            }
        }
        return added;
    }
}

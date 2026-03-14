using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Drives chunk streaming each frame: runs the world update, unloads chunks that
/// left the render radius, and loads + lights + meshes chunks that entered it.
///
/// Heavy work (disk load, lighting, meshing) is rate-limited: only a fixed number
/// of chunks are fully processed per frame so the game stays responsive while new
/// terrain streams in progressively.
/// </summary>
public sealed class WorldStreamer
{
    private readonly World _world;
    private readonly WorldRenderer _renderer;
    private readonly string _savePath;

    /// <summary>Max chunks to fully process (disk load + light + mesh) per frame.</summary>
    private const int MaxChunksPerFrame = 1;

    /// <summary>Max total mesh rebuilds per frame (new chunks + their neighbors).</summary>
    private const int MaxMeshRebuildsPerFrame = 4;

    // Chunks waiting to be disk-loaded, lit, and meshed (sorted before draining).
    private readonly List<Vector3i> _pendingLoad = new();
    private readonly HashSet<Vector3i> _pendingSet = new();
    // Neighbor meshes deferred to subsequent frames.
    private readonly List<Vector3i> _pendingMeshRebuild = new();

    public WorldStreamer(World world, WorldRenderer renderer, string savePath)
    {
        _world = world;
        _renderer = renderer;
        _savePath = savePath;
    }

    /// <summary>
    /// Advances chunk streaming for the given player position.
    /// Mutates the world's chunk dictionary and the renderer's GPU cache.
    /// </summary>
    public void Update(Vector3 playerPos)
    {
        // Flush any incremental light updates queued by block interactions this frame,
        // then rebuild the affected chunk meshes with the corrected light data.
        Profiler.Begin("Light: Flush Dirty");
        var relit = LightEngine.FlushDirty(_world);
        Profiler.End("Light: Flush Dirty");
        foreach (var key in relit)
            _renderer.RebuildChunk(key);

        Profiler.Begin("Chunk Stream: World Update");
        _world.Update(playerPos, out var added, out var removed);
        Profiler.End("Chunk Stream: World Update");

        Profiler.Begin("Chunk Stream: Unload");
        foreach (var key in removed)
        {
            _renderer.EvictChunkPlacedModels(key);
            _renderer.TryFreeChunkGpu(key);
        }
        if (removed.Count > 0) _renderer.BordersDirty = true;
        Profiler.End("Chunk Stream: Unload");

        // Enqueue newly generated chunks for progressive processing.
        if (added.Count > 0)
        {
            _pendingLoad.AddRange(added);
            foreach (var key in added)
                _pendingSet.Add(key);
        }

        if (_pendingLoad.Count > 0)
        {
            // Sort pending chunks: process chunks closest to the player first,
            // with higher Y (top-to-bottom) as tiebreaker for correct sunlight seeding.
            Vector2i playerChunk = World.WorldToChunk(playerPos);
            _pendingLoad.Sort((a, b) =>
            {
                int distA = Math.Abs(a.X - playerChunk.X) + Math.Abs(a.Z - playerChunk.Y);
                int distB = Math.Abs(b.X - playerChunk.X) + Math.Abs(b.Z - playerChunk.Y);
                if (distA != distB) return distA.CompareTo(distB);
                return b.Y.CompareTo(a.Y); // higher Y first for lighting
            });

            // Drain at most MaxChunksPerFrame from the queue.
            int count = Math.Min(_pendingLoad.Count, MaxChunksPerFrame);
            var batch = _pendingLoad.GetRange(0, count);
            _pendingLoad.RemoveRange(0, count);
            foreach (var key in batch)
                _pendingSet.Remove(key);

            // Replace freshly-generated streaming chunks with any saved counterparts.
            Profiler.Begin("Chunk Stream: Disk Load");
            foreach (var key in batch)
            {
                if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                    _world.ReplaceChunk(key, saved);
            }
            Profiler.End("Chunk Stream: Disk Load");

            // Compute lighting top-to-bottom within the batch.
            Profiler.Begin("Chunk Stream: Lighting");
            foreach (var key in batch.OrderByDescending(k => k.Y))
            {
                if (_world.Chunks.TryGetValue(key, out var newChunk))
                    LightEngine.ComputeChunk(newChunk, _world);
            }
            Profiler.End("Chunk Stream: Lighting");

            // Queue the batch chunk and its face-adjacent neighbours for meshing,
            // skipping neighbours still pending load — they'll be meshed when processed.
            foreach (var key in batch)
            {
                if (!_pendingMeshRebuild.Contains(key))
                    _pendingMeshRebuild.Insert(0, key);

                var neighbors = new Vector3i[]
                {
                    new(key.X - 1, key.Y, key.Z), new(key.X + 1, key.Y, key.Z),
                    new(key.X, key.Y - 1, key.Z), new(key.X, key.Y + 1, key.Z),
                    new(key.X, key.Y, key.Z - 1), new(key.X, key.Y, key.Z + 1),
                };
                foreach (var nb in neighbors)
                {
                    if (!_pendingSet.Contains(nb) && !_pendingMeshRebuild.Contains(nb))
                        _pendingMeshRebuild.Add(nb);
                }
            }

            foreach (var key in batch)
                if (_world.Chunks.TryGetValue(key, out var sc)) _renderer.ScanChunkForPlacedModels(sc);

            _renderer.BordersDirty = true;
        }

        // Drain deferred mesh rebuilds every frame, capped to keep frame time low.
        if (_pendingMeshRebuild.Count > 0)
        {
            Profiler.Begin("Chunk Stream: Mesh Upload");
            int meshCount = Math.Min(_pendingMeshRebuild.Count, MaxMeshRebuildsPerFrame);
            for (int i = 0; i < meshCount; i++)
                _renderer.RebuildChunk(_pendingMeshRebuild[i]);
            _pendingMeshRebuild.RemoveRange(0, meshCount);
            Profiler.End("Chunk Stream: Mesh Upload");
        }
    }
}

using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel;

/// <summary>
/// Drives chunk streaming each frame: runs the world update, unloads chunks that
/// left the render radius, and loads + lights + meshes chunks that entered it.
/// Also triggers the slope-placement pass once a chunk and all 8 horizontal
/// neighbours are loaded.  When a new chunk fills in a missing neighbour slot,
/// the already-loaded neighbours are re-queued so their edge blocks get correct slopes.
/// </summary>
public sealed class WorldStreamer
{
    private readonly World _world;
    private readonly WorldRenderer _renderer;
    private readonly string _savePath;

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

        if (added.Count == 0) return;

        // Replace freshly-generated streaming chunks with any saved counterparts.
        Profiler.Begin("Chunk Stream: Disk Load");
        foreach (var key in added)
        {
            if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                _world.ReplaceChunk(key, saved);
        }
        Profiler.End("Chunk Stream: Disk Load");

        // Compute lighting for the new chunks top-to-bottom: upper layers must be lit
        // before lower layers so the sky-open column check in seeding works correctly.
        Profiler.Begin("Chunk Stream: Lighting");
        foreach (var key in added.OrderByDescending(k => k.Y))
        {
            if (_world.Chunks.TryGetValue(key, out var newChunk))
                LightEngine.ComputeChunk(newChunk, _world);
        }
        Profiler.End("Chunk Stream: Lighting");

        // Include all six face-adjacent neighbours so their boundary faces get re-culled.
        var toRebuild = new HashSet<Vector3i>(added);
        foreach (var key in added)
        {
            toRebuild.Add(new Vector3i(key.X - 1, key.Y, key.Z));
            toRebuild.Add(new Vector3i(key.X + 1, key.Y, key.Z));
            toRebuild.Add(new Vector3i(key.X, key.Y - 1, key.Z));
            toRebuild.Add(new Vector3i(key.X, key.Y + 1, key.Z));
            toRebuild.Add(new Vector3i(key.X, key.Y, key.Z - 1));
            toRebuild.Add(new Vector3i(key.X, key.Y, key.Z + 1));
        }

        Profiler.Begin("Chunk Stream: Mesh Upload");
        foreach (var key in toRebuild)
            _renderer.RebuildChunk(key);
        Profiler.End("Chunk Stream: Mesh Upload");

        foreach (var key in added)
            if (_world.Chunks.TryGetValue(key, out var sc)) _renderer.ScanChunkForPlacedModels(sc);

        _renderer.BordersDirty = true;
    }
}

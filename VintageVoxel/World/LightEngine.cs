using System.Collections.Generic;
using System.Runtime.CompilerServices;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Fast BFS-based voxel lighting engine with sky-based photometric behavior.
///
/// SUNLIGHT MODEL (sky channel)
///   Sky columns receive sunlight level 15 from the top of the world downward.
///   Sunlight propagates VERTICALLY without intensity decay — a column open to
///   the sky is fully bright at every depth, just like real outdoor light.
///   When spreading HORIZONTALLY (XZ) the level decrements by 1 per step,
///   casting a soft penumbra under overhangs and into cave mouths.
///
/// BLOCK LIGHT MODEL (emitter channel)
///   Light-emitting blocks (torches etc.) seed the BFS at a fixed emission
///   level (≤14).  Propagation decays by 1 per step in all six axes,
///   giving a warm radius of illumination that fades into shadow.
///
/// INCREMENTAL UPDATES
///   <see cref="UpdateAtBlock"/> marks affected chunks dirty.
///   <see cref="FlushDirty"/> re-floods all dirty chunks each frame,
///   batching multiple block edits into a single propagation pass.
///
/// PERFORMANCE NOTES
///   - A lightweight <see cref="LightNode"/> struct keeps the BFS queue
///     allocation minimal and cache-friendly.
///   - Coordinates are kept as world-space integers throughout; chunk
///     lookups use an arithmetic right-shift floor-divide (no MathF.Floor).
///   - Sun and block channels share the same BFS loop but write to separate
///     arrays, so the mesher can apply different color temperatures.
/// </summary>
public static class LightEngine
{
    public const byte MaxSunLight = 15;
    public const byte MaxBlockLight = 14; // Torches

    private static readonly HashSet<Vector3i> _pendingDirty = new();

    // Six face-adjacent offsets.  Down (index 1) is special for sunlight.
    private static readonly (int dx, int dy, int dz)[] Faces6 =
    {
        ( 0, +1,  0), // 0 Up
        ( 0, -1,  0), // 1 Down  ← sunlight propagates without decay in this direction
        ( 0,  0, -1), // 2 North
        ( 0,  0, +1), // 3 South
        (-1,  0,  0), // 4 West
        (+1,  0,  0), // 5 East
    };

    // BFS node — 12 bytes, stays on the stack inside Queue<T>.
    private readonly struct LightNode
    {
        public readonly int Wx, Wy, Wz;
        public readonly byte Level;
        public readonly bool IsSun;
        public LightNode(int wx, int wy, int wz, byte level, bool isSun)
        { Wx = wx; Wy = wy; Wz = wz; Level = level; IsSun = isSun; }
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes full lighting for a single newly-loaded chunk, then lets light
    /// bleed into its already-lit loaded neighbours.
    /// </summary>
    public static void ComputeChunk(Chunk chunk, World world)
    {
        Array.Clear(chunk.SunLight, 0, Chunk.Volume);
        Array.Clear(chunk.BlockLight, 0, Chunk.Volume);

        var queue = new Queue<LightNode>(Chunk.Volume);
        SeedSunlightChunk(chunk, world, queue);
        SeedBlockLightChunk(chunk, queue);
        BfsPropagate(world, queue);
    }

    /// <summary>
    /// Full recompute of both sunlight and block-light across all loaded chunks.
    /// Useful after world load or large terrain mutations.
    /// </summary>
    public static void PropagateSunlight(World world)
    {
        foreach (var chunk in world.Chunks.Values)
        {
            Array.Clear(chunk.SunLight, 0, Chunk.Volume);
            Array.Clear(chunk.BlockLight, 0, Chunk.Volume);
        }

        var queue = new Queue<LightNode>(Chunk.Volume * world.Chunks.Count);
        // Seed top-to-bottom: upper chunks must be seeded first so lower-chunk
        // sky-open checks see valid block data above them.
        foreach (var chunk in world.Chunks.Values.OrderByDescending(c => c.Position.Y))
        {
            SeedSunlightChunk(chunk, world, queue);
            SeedBlockLightChunk(chunk, queue);
        }
        BfsPropagate(world, queue);
    }

    /// <summary>
    /// Marks the chunk containing <paramref name="blockPos"/> and all six
    /// face-neighbouring chunks dirty for a lighting recompute on the next
    /// <see cref="FlushDirty"/> call, batching multiple edits per frame.
    /// </summary>
    public static void UpdateAtBlock(Vector3i blockPos, World world)
    {
        int cx = ChunkCoord(blockPos.X);
        int cy = ChunkCoord(blockPos.Y);
        int cz = ChunkCoord(blockPos.Z);

        _pendingDirty.Add(new Vector3i(cx, cy, cz));
        _pendingDirty.Add(new Vector3i(cx - 1, cy, cz));
        _pendingDirty.Add(new Vector3i(cx + 1, cy, cz));
        _pendingDirty.Add(new Vector3i(cx, cy - 1, cz));
        _pendingDirty.Add(new Vector3i(cx, cy + 1, cz));
        _pendingDirty.Add(new Vector3i(cx, cy, cz - 1));
        _pendingDirty.Add(new Vector3i(cx, cy, cz + 1));
    }

    /// <summary>
    /// Re-floods all dirty chunks accumulated since the last call.
    /// Returns the set of chunk keys whose data changed so the caller can
    /// schedule mesh rebuilds for them.
    /// </summary>
    public static HashSet<Vector3i> FlushDirty(World world)
    {
        if (_pendingDirty.Count == 0) return new HashSet<Vector3i>();

        var queue = new Queue<LightNode>(Chunk.Volume * 2);
        foreach (var key in _pendingDirty.OrderByDescending(k => k.Y))
        {
            if (!world.Chunks.TryGetValue(key, out var chunk)) continue;
            Array.Clear(chunk.SunLight, 0, Chunk.Volume);
            Array.Clear(chunk.BlockLight, 0, Chunk.Volume);
            SeedSunlightChunk(chunk, world, queue);
            SeedBlockLightChunk(chunk, queue);
        }
        BfsPropagate(world, queue);

        var relit = new HashSet<Vector3i>(_pendingDirty);
        _pendingDirty.Clear();
        return relit;
    }

    // -------------------------------------------------------------------------
    // Seeding helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Seeds the BFS with sunlight entries for every sky-open (x,z) column in
    /// <paramref name="chunk"/>.  All transparent voxels in a sky-open column are
    /// assigned MaxSunLight == 15 — sunlight does not decay going straight down.
    /// </summary>
    private static void SeedSunlightChunk(Chunk chunk, World world, Queue<LightNode> queue)
    {
        int baseWx = chunk.Position.X * Chunk.Size;
        int baseWy = chunk.Position.Y * Chunk.Size;
        int baseWz = chunk.Position.Z * Chunk.Size;
        bool isTop = chunk.Position.Y == World.MaxChunkY - 1;

        for (int z = 0; z < Chunk.Size; z++)
            for (int x = 0; x < Chunk.Size; x++)
            {
                if (!isTop && IsColumnBlockedAbove(chunk, world, x, z)) continue;

                for (int y = Chunk.Size - 1; y >= 0; y--)
                {
                    if (chunk.GetBlock(x, y, z).IsFullBlock) break;

                    int idx = Chunk.Index(x, y, z);
                    chunk.SunLight[idx] = MaxSunLight;
                    queue.Enqueue(new LightNode(baseWx + x, baseWy + y, baseWz + z, MaxSunLight, isSun: true));
                }
            }
    }

    /// <summary>
    /// Returns true when any loaded chunk above this column contains a full block
    /// (opaque OR transparent-but-solid, e.g. leaves), meaning direct sky sunlight
    /// cannot reach this (x,z) column.  Light may still leak through via BFS with
    /// per-step decay.
    /// </summary>
    private static bool IsColumnBlockedAbove(Chunk chunk, World world, int x, int z)
    {
        for (int cy = chunk.Position.Y + 1; cy < World.MaxChunkY; cy++)
        {
            var key = new Vector3i(chunk.Position.X, cy, chunk.Position.Z);
            if (!world.Chunks.TryGetValue(key, out var above)) continue;
            for (int ay = 0; ay < Chunk.Size; ay++)
                if (above.GetBlock(x, ay, z).IsFullBlock) return true;
        }
        return false;
    }

    /// <summary>
    /// Seeds the BFS queue with every block-light emitter inside
    /// <paramref name="chunk"/> (e.g. torches at emission level 14).
    /// </summary>
    private static void SeedBlockLightChunk(Chunk chunk, Queue<LightNode> queue)
    {
        int bwx = chunk.Position.X * Chunk.Size;
        int bwy = chunk.Position.Y * Chunk.Size;
        int bwz = chunk.Position.Z * Chunk.Size;

        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    byte emit = BlockLightEmission(chunk.GetBlock(x, y, z).Id);
                    if (emit == 0) continue;

                    int idx = Chunk.Index(x, y, z);
                    chunk.BlockLight[idx] = emit;
                    queue.Enqueue(new LightNode(bwx + x, bwy + y, bwz + z, emit, isSun: false));
                }
    }

    /// <summary>Returns the block-light emission strength for the given block ID.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte BlockLightEmission(ushort id) => id switch
    {
        4 => MaxBlockLight, // Torch
        _ => 0,
    };

    // -------------------------------------------------------------------------
    // BFS flood-fill propagation
    // -------------------------------------------------------------------------

    // Persistent queue for amortized chunk-streaming BFS.
    private static readonly Queue<LightNode> _streamQueue = new();

    /// <summary>Whether the streaming BFS queue still has nodes to process.</summary>
    public static bool HasPendingStreamLight => _streamQueue.Count > 0;

    /// <summary>
    /// Seeds lighting for a newly-streamed chunk without propagating.
    /// Call <see cref="ContinueStreamBfs"/> each subsequent frame to drain
    /// the BFS incrementally, keeping frame time low.
    /// </summary>
    public static void SeedChunkStreaming(Chunk chunk, World world)
    {
        Array.Clear(chunk.SunLight, 0, Chunk.Volume);
        Array.Clear(chunk.BlockLight, 0, Chunk.Volume);
        SeedSunlightChunk(chunk, world, _streamQueue);
        SeedBlockLightChunk(chunk, _streamQueue);
    }

    /// <summary>
    /// Drains up to <paramref name="maxNodes"/> from the streaming BFS queue.
    /// Returns true when the queue is fully drained (lighting complete).
    /// </summary>
    public static bool ContinueStreamBfs(World world, int maxNodes)
    {
        return BfsPropagate(world, _streamQueue, maxNodes);
    }

    /// <summary>
    /// Spreads light from every seed in <paramref name="queue"/>.
    ///
    /// When <paramref name="maxNodes"/> is 0 the queue is drained completely
    /// (used by FlushDirty / ComputeChunk).  A positive value caps the number
    /// of nodes processed, spreading the work across multiple frames.
    ///
    /// Returns true when the queue is empty after this call.
    ///
    /// SUNLIGHT RULE: when propagating downward (-Y) from a fully-bright sky voxel
    ///   (level == MaxSunLight), pass the SAME level to the voxel below — no decay.
    ///   This means an open sky column stays at 15 regardless of depth.  Any other
    ///   direction (sideways, upward, or attenuated downward) decrements by 1.
    ///
    /// BLOCK LIGHT RULE: always decrement by 1 in all six directions.
    /// </summary>
    private static bool BfsPropagate(World world, Queue<LightNode> queue, int maxNodes = 0)
    {
        int processed = 0;
        while (queue.Count > 0 && (maxNodes == 0 || processed < maxNodes))
        {
            var node = queue.Dequeue();
            processed++;
            if (node.Level <= 1) continue;

            for (int f = 0; f < 6; f++)
            {
                var (dx, dy, dz) = Faces6[f];
                int nx = node.Wx + dx;
                int ny = node.Wy + dy;
                int nz = node.Wz + dz;

                int ncx = ChunkCoord(nx);
                int ncy = ChunkCoord(ny);
                int ncz = ChunkCoord(nz);

                if (!world.Chunks.TryGetValue(new Vector3i(ncx, ncy, ncz), out var nChunk))
                    continue;

                int lx = nx - ncx * Chunk.Size;
                int ly = ny - ncy * Chunk.Size;
                int lz = nz - ncz * Chunk.Size;

                if (!nChunk.GetBlock(lx, ly, lz).IsTransparent) continue;

                // Sunlight going straight down keeps full intensity;
                // everything else (sideways, up, or attenuated descent) loses 1.
                byte nextLevel = (node.IsSun && f == 1 && node.Level == MaxSunLight)
                    ? MaxSunLight
                    : (byte)(node.Level - 1);

                int idx = Chunk.Index(lx, ly, lz);
                if (node.IsSun)
                {
                    if (nChunk.SunLight[idx] >= nextLevel) continue;
                    nChunk.SunLight[idx] = nextLevel;
                }
                else
                {
                    if (nChunk.BlockLight[idx] >= nextLevel) continue;
                    nChunk.BlockLight[idx] = nextLevel;
                }

                if (nextLevel > 1)
                    queue.Enqueue(new LightNode(nx, ny, nz, nextLevel, node.IsSun));
            }
        }
        return queue.Count == 0;
    }

    /// <summary>
    /// Seeds the streaming BFS queue with border voxels from a GPU-lit chunk
    /// and its loaded neighbours so that cross-chunk light bleeding is resolved
    /// by subsequent <see cref="ContinueStreamBfs"/> calls.
    /// </summary>
    public static void SeedBorderBleeding(Chunk chunk, World world)
    {
        int bwx = chunk.Position.X * Chunk.Size;
        int bwy = chunk.Position.Y * Chunk.Size;
        int bwz = chunk.Position.Z * Chunk.Size;

        // Seed border voxels of the newly lit chunk (bleeding OUT).
        for (int face = 0; face < 6; face++)
            SeedSingleFace(chunk, bwx, bwy, bwz, face);

        // Seed border voxels of loaded neighbour chunks (bleeding IN).
        for (int f = 0; f < 6; f++)
        {
            var (dx, dy, dz) = Faces6[f];
            var nk = new Vector3i(
                chunk.Position.X + dx, chunk.Position.Y + dy, chunk.Position.Z + dz);
            if (!world.Chunks.TryGetValue(nk, out var neighbor)) continue;

            int nbwx = neighbor.Position.X * Chunk.Size;
            int nbwy = neighbor.Position.Y * Chunk.Size;
            int nbwz = neighbor.Position.Z * Chunk.Size;
            SeedSingleFace(neighbor, nbwx, nbwy, nbwz, f ^ 1); // opposite face
        }
    }

    private static void SeedSingleFace(Chunk chunk, int bwx, int bwy, int bwz, int face)
    {
        for (int a = 0; a < Chunk.Size; a++)
            for (int b = 0; b < Chunk.Size; b++)
            {
                int x, y, z;
                switch (face)
                {
                    case 0: x = a; y = Chunk.Size - 1; z = b; break;
                    case 1: x = a; y = 0; z = b; break;
                    case 2: x = a; y = b; z = 0; break;
                    case 3: x = a; y = b; z = Chunk.Size - 1; break;
                    case 4: x = 0; y = a; z = b; break;
                    case 5: x = Chunk.Size - 1; y = a; z = b; break;
                    default: continue;
                }

                int idx = Chunk.Index(x, y, z);
                byte sun = chunk.SunLight[idx];
                byte blk = chunk.BlockLight[idx];

                if (sun > 1)
                    _streamQueue.Enqueue(new LightNode(bwx + x, bwy + y, bwz + z, sun, isSun: true));
                if (blk > 1)
                    _streamQueue.Enqueue(new LightNode(bwx + x, bwy + y, bwz + z, blk, isSun: false));
            }
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fast floor-division by <see cref="Chunk.Size"/> (32).
    /// Correct for all integers, positive and negative.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ChunkCoord(int world) => world >> 5; // arithmetic right-shift = floor div by 32
}

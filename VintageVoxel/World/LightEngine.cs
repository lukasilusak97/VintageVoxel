using System.Collections.Generic;
using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Propagates sunlight and block-light through the voxel world using a
/// BFS (Breadth-First Search) flood-fill algorithm.
///
/// SUNLIGHT MODEL
///   Columns that are open to the sky receive sunlight level 15 from the top
///   of the chunk downward.  Once a sky-lit voxel is known, BFS spreads the
///   light horizontally through neighbouring air blocks, decaying by 1 per step.
///   This produces a soft penumbra inside caves and under overhangs.
///
/// BLOCK LIGHT MODEL
///   Any block with a non-zero emitted level is seeded into the same BFS queue.
///   Currently this is a scaffold — no block type emits light yet, but the
///   plumbing is in place for torches (ID 4, level 14).
///
/// SCOPE
///   This engine operates on <see cref="World"/> coordinates. For an initial
///   full-world compute after chunk load, call
///   <see cref="ComputeChunk(Chunk, World)"/>.
///   For incremental updates after block changes call
///   <see cref="PropagateSunlight(World)"/> (full recompute) or the targeted
///   <see cref="UpdateBlockLight"/> path (not yet implemented).
///
/// TECHNICAL NOTES
///   - Light is stored in <see cref="Chunk.SunLight"/> and
///     <see cref="Chunk.BlockLight"/> as <c>byte</c> values [0, 15].
///   - The BFS is bounded by the loaded chunk set; unloaded neighbours are
///     treated as opaque (no propagation across the load boundary).
///   - Full recompute is O(loaded voxels) which is fast enough for the chunk
///     streaming frequency of this engine.
/// </summary>
public static class LightEngine
{
    private const byte MaxSunLight = 15;
    private const byte MaxBlockLight = 14; // Torches (when added)

    // Chunk keys that need a light recompute on the next FlushDirty call.
    private static readonly HashSet<Vector2i> _pendingDirty = new();

    // Axis-aligned neighbour offsets (6-connected face adjacency).
    private static readonly (int dx, int dy, int dz)[] Neighbors6 =
    {
        ( 0, +1,  0),
        ( 0, -1,  0),
        ( 0,  0, -1),
        ( 0,  0, +1),
        (-1,  0,  0),
        (+1,  0,  0),
    };

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes lighting for a single newly-added chunk, then lets light bleed
    /// into (and from) its already-lit neighbours.  Call this after a chunk is
    /// generated and added to the world.
    /// </summary>
    public static void ComputeChunk(Chunk chunk, World world)
    {
        // Clear previous light data for this chunk.
        System.Array.Clear(chunk.SunLight, 0, Chunk.Volume);
        System.Array.Clear(chunk.BlockLight, 0, Chunk.Volume);

        // Seed the BFS with sky-column entries for this chunk.
        var queue = new Queue<(int wx, int wy, int wz, byte level, bool isSun)>();
        SeedSunlightChunk(chunk, world, queue);
        SeedBlockLightChunk(chunk, queue);

        // Run BFS to spread light through the world.
        BfsPropagate(world, queue);
    }

    /// <summary>
    /// Full recompute of sunlight across all currently loaded chunks.
    /// Clears all existing sun-light data, then floods from the sky.
    /// Useful for initial world load or after major terrain edits.
    /// </summary>
    public static void PropagateSunlight(World world)
    {
        // Clear sun light across every loaded chunk.
        foreach (var chunk in world.Chunks.Values)
            System.Array.Clear(chunk.SunLight, 0, Chunk.Volume);

        var queue = new Queue<(int wx, int wy, int wz, byte level, bool isSun)>();

        foreach (var chunk in world.Chunks.Values)
            SeedSunlightChunk(chunk, world, queue);

        BfsPropagate(world, queue);
    }

    /// <summary>
    /// Schedules a lighting recompute for the chunk containing
    /// <paramref name="blockPos"/> and its four cardinal XZ neighbours.
    /// The actual BFS runs the next time <see cref="FlushDirty"/> is called
    /// (once per frame from <see cref="WorldStreamer"/>), so multiple block
    /// changes within the same frame are batched into a single propagation pass.
    /// </summary>
    public static void UpdateAtBlock(Vector3i blockPos, World world)
    {
        int cx = (int)MathF.Floor((float)blockPos.X / Chunk.Size);
        int cz = (int)MathF.Floor((float)blockPos.Z / Chunk.Size);

        _pendingDirty.Add(new Vector2i(cx, cz));
        _pendingDirty.Add(new Vector2i(cx - 1, cz));
        _pendingDirty.Add(new Vector2i(cx + 1, cz));
        _pendingDirty.Add(new Vector2i(cx, cz - 1));
        _pendingDirty.Add(new Vector2i(cx, cz + 1));
    }

    /// <summary>
    /// Recomputes lighting for every chunk that was marked dirty by
    /// <see cref="UpdateAtBlock"/> since the last flush, then clears the
    /// dirty set.  Returns the set of chunk keys whose light data changed
    /// so the caller can schedule mesh rebuilds for them.
    /// </summary>
    public static HashSet<Vector2i> FlushDirty(World world)
    {
        if (_pendingDirty.Count == 0)
            return new HashSet<Vector2i>();

        var queue = new Queue<(int, int, int, byte, bool)>();

        foreach (var key in _pendingDirty)
        {
            if (!world.Chunks.TryGetValue(key, out var chunk)) continue;
            System.Array.Clear(chunk.SunLight, 0, Chunk.Volume);
            System.Array.Clear(chunk.BlockLight, 0, Chunk.Volume);
            SeedSunlightChunk(chunk, world, queue);
            SeedBlockLightChunk(chunk, queue);
        }

        BfsPropagate(world, queue);

        var relit = new HashSet<Vector2i>(_pendingDirty);
        _pendingDirty.Clear();
        return relit;
    }

    // -------------------------------------------------------------------------
    // Seeding
    // -------------------------------------------------------------------------

    /// <summary>
    /// Seeds the BFS queue with sunlight entries for the given chunk.
    ///
    /// Column-fill strategy:
    ///   Walk each (x, z) column from the top of the chunk downward.
    ///   As long as the block is transparent, assign level 15 and continue.
    ///   When we hit a solid block, stop — the face underneath is in shadow.
    ///   After column-fill, any sky-lit voxel at the edge of the chunk (or any
    ///   that is directly sky-exposed) is enqueued for BFS spread.
    /// </summary>
    private static void SeedSunlightChunk(
        Chunk chunk,
        World world,
        Queue<(int, int, int, byte, bool)> queue)
    {
        int baseWx = chunk.Position.X * Chunk.Size;
        int baseWz = chunk.Position.Z * Chunk.Size;

        for (int z = 0; z < Chunk.Size; z++)
            for (int x = 0; x < Chunk.Size; x++)
            {
                // Check if the block directly above the chunk top is air
                // (i.e. this column has sky above — which it always does in our
                // single-vertical-layer world).
                for (int y = Chunk.Size - 1; y >= 0; y--)
                {
                    ref Block b = ref chunk.GetBlock(x, y, z);
                    if (!b.IsTransparent) break;

                    int idx = Chunk.Index(x, y, z);
                    chunk.SunLight[idx] = MaxSunLight;

                    // Enqueue for horizontal spread only — vertical already filled above.
                    int wx = baseWx + x;
                    int wy = y;
                    int wz = baseWz + z;
                    queue.Enqueue((wx, wy, wz, MaxSunLight, true));
                }
            }
    }

    /// <summary>
    /// Seeds the BFS queue with block-light emitters inside <paramref name="chunk"/>.
    /// Currently no block type emits light, so this is a no-op scaffold.
    /// Torches (ID 4) would emit MaxBlockLight here.
    /// </summary>
    private static void SeedBlockLightChunk(
        Chunk chunk,
        Queue<(int, int, int, byte, bool)> queue)
    {
        int bwx = chunk.Position.X * Chunk.Size;
        int bwz = chunk.Position.Z * Chunk.Size;

        for (int z = 0; z < Chunk.Size; z++)
            for (int y = 0; y < Chunk.Size; y++)
                for (int x = 0; x < Chunk.Size; x++)
                {
                    ref Block b = ref chunk.GetBlock(x, y, z);
                    byte emit = EmittedBlockLight(b.Id);
                    if (emit == 0) continue;

                    int idx = Chunk.Index(x, y, z);
                    chunk.BlockLight[idx] = emit;
                    queue.Enqueue((bwx + x, y, bwz + z, emit, false));
                }
    }

    /// <summary>Returns the block-light emission level for the given block ID.</summary>
    private static byte EmittedBlockLight(ushort id) => id switch
    {
        4 => MaxBlockLight,  // Torch
        _ => 0,
    };

    // -------------------------------------------------------------------------
    // BFS flood-fill
    // -------------------------------------------------------------------------

    /// <summary>
    /// Processes the BFS queue, spreading light through transparent voxels in
    /// the loaded world.  Each step decrements the level by 1; propagation stops
    /// when level reaches 1 (level 0 = no light).
    /// </summary>
    private static void BfsPropagate(
        World world,
        Queue<(int wx, int wy, int wz, byte level, bool isSun)> queue)
    {
        while (queue.Count > 0)
        {
            var (wx, wy, wz, level, isSun) = queue.Dequeue();

            if (level <= 1) continue;
            byte nextLevel = (byte)(level - 1);

            foreach (var (dx, dy, dz) in Neighbors6)
            {
                int nx = wx + dx;
                int ny = wy + dy;
                int nz = wz + dz;

                // Resolve the chunk and local coordinates for the neighbour.
                int ncx = (int)MathF.Floor((float)nx / Chunk.Size);
                int ncz = (int)MathF.Floor((float)nz / Chunk.Size);

                if (!world.Chunks.TryGetValue(new Vector2i(ncx, ncz), out var nChunk))
                    continue; // Outside loaded world.

                if ((uint)ny >= (uint)Chunk.Size)
                    continue; // Outside vertical chunk bounds.

                int lx = nx - ncx * Chunk.Size;
                int lz = nz - ncz * Chunk.Size;

                ref Block nb = ref nChunk.GetBlock(lx, ny, lz);
                if (!nb.IsTransparent) continue; // Light can't enter solid blocks.

                int idx = Chunk.Index(lx, ny, lz);

                if (isSun)
                {
                    if (nChunk.SunLight[idx] >= nextLevel) continue; // Already brighter.
                    nChunk.SunLight[idx] = nextLevel;
                }
                else
                {
                    if (nChunk.BlockLight[idx] >= nextLevel) continue;
                    nChunk.BlockLight[idx] = nextLevel;
                }

                queue.Enqueue((nx, ny, nz, nextLevel, isSun));
            }
        }
    }
}

using System.Collections.Generic;

namespace VintageVoxel;

/// <summary>
/// A 32 x 32 x 32 region of blocks stored in a single flat array.
///
/// WHY flat over 3-D array?
///   A 3-D array (Block[,,]) allocates a managed object with three-level indirection.
///   A 1-D array is one contiguous block of memory; sequential access (as the mesher
///   does) stays in CPU cache, which is critical when iterating 32 768 blocks per frame.
///
/// Index formula:  index = x + Size * (y + Size * z)
///   X varies fastest (innermost), Z slowest (outermost).
///   This layout means a row of X values for fixed (Y, Z) is contiguous, matching
///   the order the mesher's inner loop walks.
/// </summary>
public class Chunk
{
    public const int Size = 32;
    public const int Volume = Size * Size * Size; // 32 768

    /// <summary>World-space origin of this chunk (in chunk units, not block units).</summary>
    public readonly OpenTK.Mathematics.Vector3i Position;

    private readonly Block[] _blocks = new Block[Volume];

    // Light levels stored per-voxel, range [0, 15].
    // SunLight  — propagated downward from the sky (max 15) then BFS-spread horizontally.
    // BlockLight — emitted by torches / light-source blocks (max 14 in current design).
    // Stored as separate byte arrays (1 byte = 8-bit, only 4 bits used) so the mesher
    // can sample them without any bitpacking overhead.
    public readonly byte[] SunLight = new byte[Volume];
    public readonly byte[] BlockLight = new byte[Volume];

    public Chunk(OpenTK.Mathematics.Vector3i position)
    {
        Position = position;
        // Ensure every block starts as transparent air before terrain generation
        // fills in the solid blocks.  Without this, default-initialized blocks have
        // IsTransparent = false (the bool zero value), which the light engine treats
        // as solid — causing upper air chunks (Y>0) to block all sunlight.
        Array.Fill(_blocks, Block.Air);
        Generate();
    }

    // ------------------------------------------------------------------
    // Serialization support
    // ------------------------------------------------------------------

    /// <summary>
    /// Creates a chunk whose block array is left as all-Air (skips Generate).
    /// Used exclusively by <see cref="WorldPersistence"/> — the caller is
    /// responsible for filling <see cref="_blocks"/> via <see cref="LoadBlocksFromSave"/>.
    /// </summary>
    internal static Chunk CreateForDeserialization(OpenTK.Mathematics.Vector3i position)
        => new(position, skipGenerate: true);

    /// Constructor overload that optionally skips terrain generation.
    private Chunk(OpenTK.Mathematics.Vector3i position, bool skipGenerate)
    {
        Position = position;
        if (!skipGenerate) Generate();
    }

    /// <summary>Returns the raw block ID at the given flat array index. Used by WorldPersistence.</summary>
    internal ushort GetRawBlockId(int flatIndex) => _blocks[flatIndex].Id;

    /// <summary>Returns the raw block Layer at the given flat array index. Used by WorldPersistence.</summary>
    internal byte GetRawBlockLayer(int flatIndex) => _blocks[flatIndex].Layer;

    /// <summary>
    /// Overwrites the entire block array from a saved ID (and optional layer) list produced by
    /// <see cref="WorldPersistence"/>.  Transparency is resolved through the
    /// block registry so water, leaves, and other transparent blocks render correctly.
    /// <paramref name="savedLayers"/> may be null for older saves, which default to Layer=16 (full) for solid, 0 for air.
    /// </summary>
    internal void LoadBlocksFromSave(ushort[] savedIds, byte[]? savedLayers = null)
    {
        for (int i = 0; i < Volume; i++)
        {
            bool transparent = BlockRegistry.IsTransparent(savedIds[i]);
            _blocks[i] = new Block
            {
                Id = savedIds[i],
                IsTransparent = transparent,
                Layer = savedLayers != null ? savedLayers[i] : (byte)(savedIds[i] == 0 ? 0 : 16),
            };
        }
    }

    // ------------------------------------------------------------------
    // Public block access
    // ------------------------------------------------------------------

    /// <summary>Returns the block at local coordinates (x, y, z).</summary>
    public ref Block GetBlock(int x, int y, int z) => ref _blocks[Index(x, y, z)];

    /// <summary>Returns true when (x, y, z) is within [0, Size).</summary>
    public static bool InBounds(int x, int y, int z) =>
        (uint)x < Size && (uint)y < Size && (uint)z < Size;

    // ------------------------------------------------------------------
    // World generation
    // ------------------------------------------------------------------

    // Y level at which still water fills any air column below the surface.
    private const int SeaLevel = 12;

    // Biome identifiers.
    private const int BiomePlains = 0;
    private const int BiomeForest = 1;
    private const int BiomeDesert = 2;
    private const int BiomeMountains = 3;
    private const int BiomeSnowy = 4;

    /// <summary>
    /// Computes the terrain surface height as a float at the given world-space XZ
    /// position using multi-octave Perlin noise. The integer part determines the
    /// block Y, and the fractional part maps to 1–16 layers within the surface block.
    /// </summary>
    private static float ComputeSurfaceHeight(float wx, float wz)
    {
        // Base continent shape — 6 octaves for rich detail, coarse scale for wide features.
        float base_ = NoiseGenerator.Octave(wx * 0.018f, wz * 0.018f, octaves: 6);
        // Map [0,1] → [4,28]
        float h = 4f + base_ * 24f;

        // Mountain ridge amplifier: only kicks in above mid-height.
        if (h > 16f)
        {
            float ridge = NoiseGenerator.Octave(wx * 0.03f, wz * 0.03f, octaves: 4);
            h += ridge * 6f;
        }

        return Math.Clamp(h, 2f, Size - 2f);
    }

    /// <summary>
    /// Determines the biome at the given world-space XZ position using two
    /// decorrelated noise channels (temperature and humidity).
    /// </summary>
    private static int ComputeBiome(float wx, float wz)
    {
        float temp = NoiseGenerator.Octave(wx * 0.007f, wz * 0.007f, octaves: 3);
        float humidity = NoiseGenerator.Octave(wx * 0.009f + 500f, wz * 0.009f + 500f, octaves: 3);

        if (temp > 0.65f) return BiomeDesert;
        if (temp < 0.30f) return BiomeSnowy;
        if (humidity < 0.35f && temp < 0.55f) return BiomeMountains;
        if (humidity > 0.60f) return BiomeForest;
        return BiomePlains;
    }

    /// <summary>
    /// Generates realistic terrain: biome-based surface blocks with sub-block layer
    /// precision, sea-level water, sandy beaches, mountain peaks, trees, and ores.
    /// Surface blocks get a partial layer (1–16) based on noise for smooth terrain.
    /// </summary>
    private void Generate()
    {
        if (WorldGenConfig.FlatWorld) { GenerateFlat(); return; }

        // Only the Y=0 chunk layer contains terrain; higher layers start as all-air.
        if (Position.Y != 0) return;

        // Pre-compute heightmap (float for sub-block precision) and biome map.
        var surfaceHeights = new float[Size * Size];
        var biomeMap = new int[Size * Size];

        int startWx = Position.X * Size;
        int startWz = Position.Z * Size;

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                float wx = startWx + x;
                float wz = startWz + z;
                surfaceHeights[x + z * Size] = ComputeSurfaceHeight(wx, wz);
                biomeMap[x + z * Size] = ComputeBiome(wx, wz);
            }

        // Fill blocks column by column.
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                float surfaceH = surfaceHeights[x + z * Size];
                // Ceiling − 1 so an exact integer height (e.g. 6.0) places a
                // full block at y=5 (Layer 16) instead of a tiny sliver at y=6.
                int surfaceY = (int)MathF.Ceiling(surfaceH) - 1;
                float frac = surfaceH - surfaceY;
                byte surfaceLayer = (byte)Math.Clamp((int)MathF.Ceiling(frac * 16f), 1, 16);

                int biome = biomeMap[x + z * Size];

                // Beach override: within 2 blocks of sea level replace surface with sand.
                bool isBeach = Math.Abs(surfaceY - SeaLevel) <= 2 && biome != BiomeMountains;
                if (isBeach) biome = BiomeDesert; // reuse sand placement logic

                for (int y = 0; y < Size; y++)
                {
                    Block b;

                    if (y > surfaceY)
                    {
                        // Fill air below sea level with water.
                        b = y <= SeaLevel
                            ? new Block { Id = 15, IsTransparent = true, Layer = 16 }
                            : Block.Air;
                    }
                    else if (y == surfaceY)
                    {
                        b = biome switch
                        {
                            BiomeDesert => new Block { Id = 5, IsTransparent = false, Layer = surfaceLayer },
                            BiomeMountains => surfaceY > 24
                                ? new Block { Id = 16, IsTransparent = false, Layer = surfaceLayer }
                                : new Block { Id = 2, IsTransparent = false, Layer = surfaceLayer },
                            BiomeSnowy => new Block { Id = 16, IsTransparent = false, Layer = surfaceLayer },
                            _ => new Block { Id = 3, IsTransparent = false, Layer = surfaceLayer },
                        };
                    }
                    else if (y >= surfaceY - 3)
                    {
                        // Sub-surface layer (3 blocks of fill beneath the top).
                        b = biome switch
                        {
                            BiomeDesert => new Block { Id = 5, IsTransparent = false, Layer = 16 },
                            BiomeMountains => new Block { Id = 2, IsTransparent = false, Layer = 16 },
                            _ => new Block { Id = 1, IsTransparent = false, Layer = 16 },
                        };
                    }
                    else
                    {
                        // Stone base — inline ore replacement using a fast integer hash.
                        float wx = startWx + x;
                        float wz = startWz + z;
                        uint h = (uint)((int)(wx * 374761393) ^ (y * 1274126177) ^ (int)(wz * 668265263));
                        h = (h ^ (h >> 13)) * 1274126177u;
                        h ^= h >> 16;

                        if (y <= 12 && h % 130 == 0)
                            b = new Block { Id = 14, IsTransparent = false, Layer = 16 };
                        else if (y <= 18 && h % 70 == 0)
                            b = new Block { Id = 13, IsTransparent = false, Layer = 16 };
                        else
                            b = new Block { Id = 2, IsTransparent = false, Layer = 16 };
                    }

                    _blocks[Index(x, y, z)] = b;
                }
            }

        // Place trees — scan an extended border region so trees whose trunks land
        // outside this chunk still deposit leaves inside it (no cross-chunk seams).
        for (int tz = startWz - 2; tz < startWz + Size + 2; tz++)
            for (int tx = startWx - 2; tx < startWx + Size + 2; tx++)
            {
                int biome = ComputeBiome(tx, tz);
                if (biome == BiomeDesert || biome == BiomeMountains) continue;

                int treeDensity = biome == BiomeForest ? 8 : 20;
                if (!ShouldPlaceTree(tx, tz, treeDensity)) continue;

                int sy = (int)MathF.Ceiling(ComputeSurfaceHeight(tx, tz)) - 1;
                if (sy <= SeaLevel) continue; // no aquatic trees

                PlaceTreeInChunk(tx, tz, sy);
            }
    }

    /// <summary>
    /// Returns true when a tree trunk should be placed at the given world XZ position.
    /// Deterministic and independent of chunk boundaries — the same world position
    /// always returns the same answer regardless of which chunk queries it.
    /// </summary>
    private static bool ShouldPlaceTree(int wx, int wz, int modulo)
    {
        uint h = (uint)(wx * 374761393 ^ wz * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h % (uint)modulo == 0;
    }

    /// <summary>
    /// Writes a small oak tree (4-block trunk + leaf crown) centred on the world XZ
    /// position, writing only the blocks that fall inside this chunk's [0, Size)³ bounds.
    /// </summary>
    private void PlaceTreeInChunk(int treeWx, int treeWz, int surfaceY)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;

        // Trunk: 4 log blocks starting at the surface block (one lower than above it)
        // so partial-layer surfaces don't leave a visible gap.
        for (int dy = 0; dy <= 3; dy++)
        {
            int ly = surfaceY + dy;
            if (InBounds(lx, ly, lz))
                _blocks[Index(lx, ly, lz)] = new Block { Id = 7, IsTransparent = false, Layer = 16 };
        }

        // Leaf crown: 3×3 at trunk top − 1 and trunk top, plus single block above.
        for (int layer = 0; layer <= 2; layer++)
        {
            int ly = surfaceY + 2 + layer;
            int radius = layer == 2 ? 0 : 1; // top layer is 1×1, lower two are 3×3

            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = lx + dx;
                    int nz = lz + dz;
                    if (!InBounds(nx, ly, nz)) continue;

                    ref Block b = ref _blocks[Index(nx, ly, nz)];
                    if (b.Id == 0) // don't overwrite solid blocks
                        b = new Block { Id = 8, IsTransparent = true, Layer = 16 };
                }
        }
    }

    /// <summary>
    /// Generates a perfectly flat world: Stone below <c>grassY-3</c>, Dirt in
    /// the three layers below the surface, Grass on top, Air above.
    /// </summary>
    private void GenerateFlat()
    {
        const int grassY = 5;
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                for (int y = 0; y < Size; y++)
                {
                    Block b;
                    if (y > grassY) b = Block.Air;
                    else if (y == grassY) b = new Block { Id = 3, IsTransparent = false, Layer = 16 };
                    else if (y >= grassY - 3) b = new Block { Id = 1, IsTransparent = false, Layer = 16 };
                    else b = new Block { Id = 2, IsTransparent = false, Layer = 16 };
                    _blocks[Index(x, y, z)] = b;
                }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Converts local (x, y, z) to a flat array index.</summary>
    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);
}

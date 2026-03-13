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

    /// <summary>Returns the raw block Shape at the given flat array index. Used by WorldPersistence.</summary>
    internal byte GetRawBlockShape(int flatIndex) => _blocks[flatIndex].Shape;

    /// <summary>
    /// Overwrites the entire block array from a saved ID (and optional shape) list produced by
    /// <see cref="WorldPersistence"/>.  Transparency is resolved through the
    /// block registry so water, leaves, and other transparent blocks render correctly.
    /// <paramref name="savedShapes"/> may be null for older saves (v2), which default to Shape=0.
    /// </summary>
    internal void LoadBlocksFromSave(ushort[] savedIds, byte[]? savedShapes = null)
    {
        for (int i = 0; i < Volume; i++)
            _blocks[i] = new Block
            {
                Id = savedIds[i],
                IsTransparent = BlockRegistry.IsTransparent(savedIds[i]),
                Shape = savedShapes != null ? savedShapes[i] : (byte)0,
            };
    }

    // ------------------------------------------------------------------
    // Public block access
    // ------------------------------------------------------------------

    /// <summary>Returns the block at local coordinates (x, y, z).</summary>
    public ref Block GetBlock(int x, int y, int z) => ref _blocks[Index(x, y, z)];

    /// <summary>Sets the Shape field on the block at local coordinates.  Used by SlopePlacer.</summary>
    public void SetShape(int x, int y, int z, SlopeShape shape)
        => _blocks[Index(x, y, z)].Shape = (byte)shape;

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
    /// Computes the terrain surface height (in block Y) at the given world-space XZ
    /// position using multi-octave Perlin noise.  Mountains receive an additional
    /// ridge-boost when the base height exceeds mid-range.
    /// </summary>
    private static int ComputeSurfaceY(float wx, float wz)
    {
        // Base continent shape — 6 octaves for rich detail, coarse scale for wide features.
        float base_ = NoiseGenerator.Octave(wx * 0.018f, wz * 0.018f, octaves: 6);
        // Map [0,1] → [4,28]
        int h = 4 + (int)(base_ * 24f);

        // Mountain ridge amplifier: only kicks in above mid-height.
        if (h > 16)
        {
            float ridge = NoiseGenerator.Octave(wx * 0.03f, wz * 0.03f, octaves: 4);
            h += (int)(ridge * 6f);
        }

        return Math.Clamp(h, 2, Size - 2);
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
    /// Generates realistic terrain: biome-based surface blocks, sea-level water,
    /// sandy beaches, mountain peaks, cross-chunk oak trees, and ore clusters.
    /// </summary>
    private void Generate()
    {
        if (WorldGenConfig.FlatWorld) { GenerateFlat(); return; }

        // Only the Y=0 chunk layer contains terrain; higher layers start as all-air.
        if (Position.Y != 0) return;

        // Pre-compute heightmap and biome map for all 32×32 columns.
        var surfaceMap = new int[Size * Size];
        var biomeMap = new int[Size * Size];

        int startWx = Position.X * Size;
        int startWz = Position.Z * Size;

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                float wx = startWx + x;
                float wz = startWz + z;
                surfaceMap[x + z * Size] = ComputeSurfaceY(wx, wz);
                biomeMap[x + z * Size] = ComputeBiome(wx, wz);
            }

        // Fill blocks column by column.
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int surfaceY = surfaceMap[x + z * Size];
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
                            ? new Block { Id = 15, IsTransparent = true }   // Water
                            : Block.Air;
                    }
                    else if (y == surfaceY)
                    {
                        b = biome switch
                        {
                            BiomeDesert => new Block { Id = 5, IsTransparent = false }, // Sand
                            BiomeMountains => surfaceY > 24
                                ? new Block { Id = 16, IsTransparent = false }              // Snow cap
                                : new Block { Id = 2, IsTransparent = false },             // Bare stone
                            BiomeSnowy => new Block { Id = 16, IsTransparent = false }, // Snow
                            _ => new Block { Id = 3, IsTransparent = false }, // Grass
                        };
                    }
                    else if (y >= surfaceY - 3)
                    {
                        // Sub-surface layer (3 blocks of fill beneath the top).
                        b = biome switch
                        {
                            BiomeDesert => new Block { Id = 5, IsTransparent = false },  // Sand
                            BiomeMountains => new Block { Id = 2, IsTransparent = false },  // Stone
                            _ => new Block { Id = 1, IsTransparent = false },  // Dirt
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
                            b = new Block { Id = 14, IsTransparent = false }; // Iron Ore
                        else if (y <= 18 && h % 70 == 0)
                            b = new Block { Id = 13, IsTransparent = false }; // Coal Ore
                        else
                            b = new Block { Id = 2, IsTransparent = false }; // Stone
                    }

                    _blocks[Index(x, y, z)] = b;
                }
            }

        // Slope pass: classify surface blocks using ComputeSurfaceY for neighbors.
        // ComputeSurfaceY is a pure noise function, so neighbor chunks never need to
        // be loaded — slopes are fully determined at generation time from the same
        // noise that produced the heightmap.
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int surfaceY = surfaceMap[x + z * Size];
                float wx = startWx + x;
                float wz = startWz + z;

                int hN = ComputeSurfaceY(wx, wz - 1);
                int hS = ComputeSurfaceY(wx, wz + 1);
                int hE = ComputeSurfaceY(wx + 1, wz);
                int hW = ComputeSurfaceY(wx - 1, wz);
                int hNE = ComputeSurfaceY(wx + 1, wz - 1);
                int hNW = ComputeSurfaceY(wx - 1, wz - 1);
                int hSE = ComputeSurfaceY(wx + 1, wz + 1);
                int hSW = ComputeSurfaceY(wx - 1, wz + 1);

                SlopeShape shape = ClassifySlope(surfaceY, hN, hS, hE, hW, hNE, hNW, hSE, hSW);
                if (shape != SlopeShape.Cube)
                    _blocks[Index(x, surfaceY, z)].Shape = (byte)shape;
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

                int sy = ComputeSurfaceY(tx, tz);
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

        // Trunk: 4 log blocks directly above the surface.
        for (int dy = 1; dy <= 4; dy++)
        {
            int ly = surfaceY + dy;
            if (InBounds(lx, ly, lz))
                _blocks[Index(lx, ly, lz)] = new Block { Id = 7, IsTransparent = false }; // Oak Log
        }

        // Leaf crown: 3×3 at trunk top − 1 and trunk top, plus single block above.
        for (int layer = 0; layer <= 2; layer++)
        {
            int ly = surfaceY + 3 + layer;
            int radius = layer == 2 ? 0 : 1; // top layer is 1×1, lower two are 3×3

            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = lx + dx;
                    int nz = lz + dz;
                    if (!InBounds(nx, ly, nz)) continue;

                    ref Block b = ref _blocks[Index(nx, ly, nz)];
                    if (b.Id == 0) // don't overwrite solid blocks
                        b = new Block { Id = 8, IsTransparent = true }; // Oak Leaves
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
                    else if (y == grassY) b = new Block { Id = 3, IsTransparent = false }; // Grass
                    else if (y >= grassY - 3) b = new Block { Id = 1, IsTransparent = false }; // Dirt
                    else b = new Block { Id = 2, IsTransparent = false }; // Stone
                    _blocks[Index(x, y, z)] = b;
                }
    }

    // ------------------------------------------------------------------
    // Slope classification
    // ------------------------------------------------------------------

    /// <summary>
    /// Classifies the slope shape for a surface block at height <paramref name="h"/>
    /// given the surface heights of its 8 neighbours.  Only 1-block drops are
    /// considered; larger drops remain as Cube (cliff).
    /// </summary>
    private static SlopeShape ClassifySlope(
        int h,
        int hN, int hS, int hE, int hW,
        int hNE, int hNW, int hSE, int hSW)
    {
        bool dropN = hN == h - 1;
        bool dropS = hS == h - 1;
        bool dropE = hE == h - 1;
        bool dropW = hW == h - 1;

        int cardinalDrops = (dropN ? 1 : 0) + (dropS ? 1 : 0)
                          + (dropE ? 1 : 0) + (dropW ? 1 : 0);

        if (cardinalDrops == 1)
        {
            if (dropN) return SlopeShape.RampN;
            if (dropS) return SlopeShape.RampS;
            if (dropE) return SlopeShape.RampE;
            if (dropW) return SlopeShape.RampW;
        }

        if (cardinalDrops == 2)
        {
            if (dropN && dropE) return SlopeShape.OuterCornerNE;
            if (dropN && dropW) return SlopeShape.OuterCornerNW;
            if (dropS && dropE) return SlopeShape.OuterCornerSE;
            if (dropS && dropW) return SlopeShape.OuterCornerSW;
        }

        if (cardinalDrops == 0)
        {
            bool dropNE = hNE == h - 1;
            bool dropNW = hNW == h - 1;
            bool dropSE = hSE == h - 1;
            bool dropSW = hSW == h - 1;

            int diagDrops = (dropNE ? 1 : 0) + (dropNW ? 1 : 0)
                          + (dropSE ? 1 : 0) + (dropSW ? 1 : 0);

            if (diagDrops == 1)
            {
                if (dropNE) return SlopeShape.InnerCornerNE;
                if (dropNW) return SlopeShape.InnerCornerNW;
                if (dropSE) return SlopeShape.InnerCornerSE;
                if (dropSW) return SlopeShape.InnerCornerSW;
            }
        }

        return SlopeShape.Cube;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Converts local (x, y, z) to a flat array index.</summary>
    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);
}

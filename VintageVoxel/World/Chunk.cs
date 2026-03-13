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
    private const int BiomeTaiga = 5;
    private const int BiomeDarkForest = 6;
    private const int BiomeBirchForest = 7;
    private const int BiomeSavanna = 8;
    private const int BiomeCherryGrove = 9;
    private const int BiomeJungle = 10;

    /// <summary>
    /// Computes the terrain surface height as a float at the given world-space XZ
    /// position.  Uses continent noise for base shape, detail noise for small hills,
    /// and an erosion channel that drives mountain ridges and valley flattening.
    /// </summary>
    private static float ComputeSurfaceHeight(float wx, float wz)
    {
        // Base continent shape — wide smooth features.
        float continent = NoiseGenerator.Octave(wx * 0.010f, wz * 0.010f, octaves: 6);

        // Detail hills — finer frequency for small undulations.
        float detail = NoiseGenerator.Octave(wx * 0.035f, wz * 0.035f, octaves: 4);

        // Erosion noise — controls how mountainous an area is.
        float erosion = NoiseGenerator.Octave(wx * 0.005f + 1000f, wz * 0.005f + 1000f, octaves: 3);

        // Base height: [4, 20] from continent noise.
        float h = 4f + continent * 16f;

        // Small detail bumps: ±2 blocks.
        h += (detail - 0.5f) * 4f;

        // Mountain ridges — dramatically raise terrain where erosion noise is high.
        if (erosion > 0.52f)
        {
            float strength = (erosion - 0.52f) / 0.48f;
            float ridge = NoiseGenerator.Octave(wx * 0.025f, wz * 0.025f, octaves: 5);
            float ridged = 1f - MathF.Abs(ridge * 2f - 1f);
            ridged *= ridged; // sharpen peaks
            h += ridged * strength * 10f;
        }

        // Valleys — flatten and lower terrain where erosion is very low.
        if (erosion < 0.35f)
        {
            float valleyStr = (0.35f - erosion) / 0.35f;
            h = MathF.Max(h * (1f - valleyStr * 0.25f), 4f);
        }

        return Math.Clamp(h, 2f, Size - 2f);
    }

    /// <summary>
    /// Determines the biome at the given world-space XZ position using temperature
    /// and humidity noise channels.  Returns one of the Biome* constants.
    /// </summary>
    private static int ComputeBiome(float wx, float wz)
    {
        float temp = NoiseGenerator.Octave(wx * 0.006f, wz * 0.006f, octaves: 3);
        float humidity = NoiseGenerator.Octave(wx * 0.008f + 500f, wz * 0.008f + 500f, octaves: 3);

        // Hot biomes.
        if (temp > 0.72f)
            return humidity > 0.55f ? BiomeJungle : BiomeDesert;

        // Warm biomes.
        if (temp > 0.58f)
            return humidity > 0.55f ? BiomeCherryGrove : BiomeSavanna;

        // Cold biomes.
        if (temp < 0.28f)
            return BiomeSnowy;

        if (temp < 0.40f)
            return humidity < 0.40f ? BiomeMountains : BiomeTaiga;

        // Temperate band (0.40–0.58).
        if (humidity > 0.65f) return BiomeDarkForest;
        if (humidity > 0.50f) return BiomeForest;
        if (humidity < 0.35f) return BiomePlains;
        return BiomeBirchForest;
    }

    /// <summary>
    /// Generates realistic terrain: biome-based surface blocks with sub-block layer
    /// precision, sea-level water, sandy beaches, mountain peaks, varied trees, and ores.
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
                int surfaceY = (int)MathF.Ceiling(surfaceH) - 1;
                float frac = surfaceH - surfaceY;
                byte surfaceLayer = (byte)Math.Clamp((int)MathF.Ceiling(frac * 16f), 1, 16);

                int biome = biomeMap[x + z * Size];

                // Beach override: within 2 blocks of sea level replace surface with sand.
                bool isBeach = Math.Abs(surfaceY - SeaLevel) <= 2 && biome != BiomeMountains;
                if (isBeach) biome = BiomeDesert;

                // When terrain is partial and underwater, compute the water layer
                // that fills the remaining space above the terrain in the same cell.
                bool underwaterPartial = surfaceY < SeaLevel && surfaceLayer < 16;

                for (int y = 0; y < Size; y++)
                {
                    Block b;

                    if (underwaterPartial && y == surfaceY)
                    {
                        // Terrain surface — keep its partial layer.
                        b = BiomeSurfaceBlock(biome, surfaceY, surfaceLayer);
                    }
                    else if (y > surfaceY && y <= SeaLevel)
                    {
                        // Water column above terrain.
                        byte waterLayer;
                        if (y == SeaLevel)
                            waterLayer = 14; // slightly recessed water surface
                        else
                            waterLayer = 16;
                        b = new Block { Id = 15, IsTransparent = true, Layer = waterLayer };
                    }
                    else if (y > surfaceY)
                    {
                        b = Block.Air;
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
                            BiomeTaiga or BiomeDarkForest => new Block { Id = 29, IsTransparent = false, Layer = surfaceLayer },
                            _ => new Block { Id = 3, IsTransparent = false, Layer = surfaceLayer },
                        };
                    }
                    else if (y >= surfaceY - 3)
                    {
                        b = biome switch
                        {
                            BiomeDesert => new Block { Id = 5, IsTransparent = false, Layer = 16 },
                            BiomeMountains => new Block { Id = 2, IsTransparent = false, Layer = 16 },
                            _ => new Block { Id = 1, IsTransparent = false, Layer = 16 },
                        };
                    }
                    else
                    {
                        // Stone base with ore pockets.
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
        const int border = 4;
        for (int tz = startWz - border; tz < startWz + Size + border; tz++)
            for (int tx = startWx - border; tx < startWx + Size + border; tx++)
            {
                int biome = ComputeBiome(tx, tz);
                if (biome == BiomeDesert) continue;

                int treeDensity = GetTreeDensity(biome);
                if (treeDensity <= 0 || !ShouldPlaceTree(tx, tz, treeDensity)) continue;

                int sy = (int)MathF.Ceiling(ComputeSurfaceHeight(tx, tz)) - 1;
                if (sy <= SeaLevel) continue;

                // Mountains: no trees above the snow line.
                if (biome == BiomeMountains && sy > 24) continue;

                PlaceTreeForBiome(tx, tz, sy, biome);
            }
    }

    // ------------------------------------------------------------------
    // Biome surface block helper
    // ------------------------------------------------------------------

    private static Block BiomeSurfaceBlock(int biome, int surfaceY, byte layer) => biome switch
    {
        BiomeDesert => new Block { Id = 5, IsTransparent = false, Layer = layer },
        BiomeMountains => surfaceY > 24
            ? new Block { Id = 16, IsTransparent = false, Layer = layer }
            : new Block { Id = 2, IsTransparent = false, Layer = layer },
        BiomeSnowy => new Block { Id = 16, IsTransparent = false, Layer = layer },
        BiomeTaiga or BiomeDarkForest => new Block { Id = 29, IsTransparent = false, Layer = layer },
        _ => new Block { Id = 3, IsTransparent = false, Layer = layer },
    };

    // ------------------------------------------------------------------
    // Tree density per biome (lower = denser)
    // ------------------------------------------------------------------

    private static int GetTreeDensity(int biome) => biome switch
    {
        BiomePlains => 25,
        BiomeForest => 6,
        BiomeBirchForest => 7,
        BiomeDarkForest => 5,
        BiomeTaiga => 7,
        BiomeSnowy => 9,
        BiomeMountains => 18,
        BiomeSavanna => 14,
        BiomeCherryGrove => 8,
        BiomeJungle => 5,
        _ => 0,
    };

    // ------------------------------------------------------------------
    // Deterministic helpers
    // ------------------------------------------------------------------

    private static uint HashWorldPos(int wx, int wz)
    {
        uint h = (uint)(wx * 374761393 ^ wz * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h;
    }

    private static bool ShouldPlaceTree(int wx, int wz, int modulo)
    {
        return HashWorldPos(wx, wz) % (uint)modulo == 0;
    }

    // ------------------------------------------------------------------
    // Block-placement helpers (bounds-checked)
    // ------------------------------------------------------------------

    private void SetLog(int lx, int ly, int lz, ushort logId)
    {
        if (InBounds(lx, ly, lz))
            _blocks[Index(lx, ly, lz)] = new Block { Id = logId, IsTransparent = false, Layer = 16 };
    }

    private void SetLeaf(int lx, int ly, int lz, ushort leafId)
    {
        if (InBounds(lx, ly, lz))
        {
            ref Block b = ref _blocks[Index(lx, ly, lz)];
            if (b.Id == 0)
                b = new Block { Id = leafId, IsTransparent = true, Layer = 16 };
        }
    }

    // ------------------------------------------------------------------
    // Biome → tree dispatcher
    // ------------------------------------------------------------------

    private void PlaceTreeForBiome(int wx, int wz, int surfaceY, int biome)
    {
        uint hash = HashWorldPos(wx, wz);
        switch (biome)
        {
            case BiomePlains:
                if (hash % 5 == 0) PlaceLargeOakTree(wx, wz, surfaceY, hash);
                else PlaceOakTree(wx, wz, surfaceY, hash);
                break;
            case BiomeForest:
                if (hash % 3 == 0) PlaceBirchTree(wx, wz, surfaceY, hash);
                else if (hash % 7 == 0) PlaceLargeOakTree(wx, wz, surfaceY, hash);
                else PlaceOakTree(wx, wz, surfaceY, hash);
                break;
            case BiomeBirchForest:
                PlaceBirchTree(wx, wz, surfaceY, hash);
                break;
            case BiomeDarkForest:
                if (hash % 4 == 0) PlaceOakTree(wx, wz, surfaceY, hash);
                else PlaceDarkOakTree(wx, wz, surfaceY, hash);
                break;
            case BiomeTaiga:
            case BiomeSnowy:
            case BiomeMountains:
                PlaceSpruceTree(wx, wz, surfaceY, hash);
                break;
            case BiomeSavanna:
                PlaceAcaciaTree(wx, wz, surfaceY, hash);
                break;
            case BiomeCherryGrove:
                PlaceCherryTree(wx, wz, surfaceY, hash);
                break;
            case BiomeJungle:
                if (hash % 3 == 0) PlaceJungleTree(wx, wz, surfaceY, hash);
                else PlaceOakTree(wx, wz, surfaceY, hash);
                break;
        }
    }

    // ------------------------------------------------------------------
    // Tree generators — each writes only blocks inside this chunk
    // ------------------------------------------------------------------

    /// <summary>Small oak: 4–5 block trunk, 3×3 crown + 1×1 cap.</summary>
    private void PlaceOakTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 4 + (int)(hash % 2);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 7);

        for (int layer = 0; layer < 3; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            int radius = layer == 2 ? 0 : 1;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    SetLeaf(lx + dx, ly, lz + dz, 8);
        }
    }

    /// <summary>Large oak: 6–7 block trunk, 5×5 rounded crown.</summary>
    private void PlaceLargeOakTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 6 + (int)(hash % 2);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 7);

        for (int layer = 0; layer < 4; layer++)
        {
            int ly = surfaceY + trunkH - 3 + layer;
            int radius = layer < 2 ? 2 : layer == 2 ? 1 : 0;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 8);
                }
        }
    }

    /// <summary>Birch: 5–6 block trunk, narrow 3×3 crown + 1×1 cap.</summary>
    private void PlaceBirchTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 5 + (int)(hash % 2);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 17);

        for (int layer = 0; layer < 3; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            int radius = layer == 2 ? 0 : 1;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                    SetLeaf(lx + dx, ly, lz + dz, 18);
        }
    }

    /// <summary>Spruce: 7–9 block trunk, conical crown widening toward the base.</summary>
    private void PlaceSpruceTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 7 + (int)(hash % 3);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 19);

        // Leaf cap above the trunk.
        SetLeaf(lx, surfaceY + trunkH, lz, 20);

        // Conical leaf layers — widening from top to bottom.
        int leafLayers = trunkH - 2;
        for (int i = 0; i < leafLayers; i++)
        {
            int ly = surfaceY + trunkH - 1 - i;
            int radius = 1 + i / 2;
            if (radius > 3) radius = 3;

            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0) continue; // trunk
                    if (Math.Abs(dx) + Math.Abs(dz) > radius + 1) continue; // diamond trim
                    SetLeaf(lx + dx, ly, lz + dz, 20);
                }
        }
    }

    /// <summary>Dark oak: 2×2 trunk 5–6 blocks tall, wide dome canopy.</summary>
    private void PlaceDarkOakTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 5 + (int)(hash % 2);

        for (int dy = 0; dy < trunkH; dy++)
        {
            SetLog(lx, surfaceY + dy, lz, 21);
            SetLog(lx + 1, surfaceY + dy, lz, 21);
            SetLog(lx, surfaceY + dy, lz + 1, 21);
            SetLog(lx + 1, surfaceY + dy, lz + 1, 21);
        }

        // Dome canopy — 3 layers, widest at the bottom.
        float cx = lx + 0.5f, cz = lz + 0.5f;
        for (int layer = 0; layer < 3; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            float maxDist = layer == 2 ? 1.5f : 3.0f;
            for (int dz = -3; dz <= 3; dz++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    float dist = MathF.Sqrt((lx + dx - cx) * (lx + dx - cx)
                                          + (lz + dz - cz) * (lz + dz - cz));
                    if (dist > maxDist) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 22);
                }
        }
    }

    /// <summary>Acacia: 5–6 block trunk, flat wide canopy (2 layers of 5×5).</summary>
    private void PlaceAcaciaTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 5 + (int)(hash % 2);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 23);

        for (int layer = 0; layer < 2; layer++)
        {
            int ly = surfaceY + trunkH - 1 + layer;
            for (int dz = -2; dz <= 2; dz++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 24);
                }
        }
    }

    /// <summary>Cherry: 4–5 block trunk, round 5×5 canopy with pink leaves.</summary>
    private void PlaceCherryTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 4 + (int)(hash % 2);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 25);

        for (int layer = 0; layer < 3; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            int radius = layer == 2 ? 1 : 2;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 26);
                }
        }
    }

    /// <summary>Jungle: 8–11 block tall trunk, large rounded canopy at the top.</summary>
    private void PlaceJungleTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 8 + (int)(hash % 4);

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 27);

        for (int layer = 0; layer < 4; layer++)
        {
            int ly = surfaceY + trunkH - 3 + layer;
            int radius = layer < 2 ? 2 : layer == 2 ? 1 : 0;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius == 2 && Math.Abs(dx) == 2 && Math.Abs(dz) == 2) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 28);
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

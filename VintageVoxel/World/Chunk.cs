using System.Collections.Generic;

namespace VintageVoxel;

/// <summary>
/// Pre-computed per-column data that is identical across all Y layers of a chunk column.
/// Computed once in <see cref="World.Update"/> and shared by all 8 vertical chunks.
/// </summary>
public sealed class ColumnData
{
    public readonly float[] SurfaceHeights;      // [1024] flattened heightmap
    public readonly int[] BiomeMap;            // [1024] biome IDs
    public readonly SettlementMap.ZoneType[] Zones; // [1024]
    public readonly float[] Dist;                // [1024] distance to settlement center
    public readonly float MinSurface;
    public readonly float MaxSurface;

    public ColumnData(float[] surfaceHeights, int[] biomeMap,
                      SettlementMap.ZoneType[] zones, float[] dist,
                      float minSurface, float maxSurface)
    {
        SurfaceHeights = surfaceHeights;
        BiomeMap = biomeMap;
        Zones = zones;
        Dist = dist;
        MinSurface = minSurface;
        MaxSurface = maxSurface;
    }
}

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

    /// <summary>True when block data has been modified since the last save/load.</summary>
    public bool IsDirty { get; set; }

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

    /// <summary>
    /// Creates a chunk using pre-computed heightmap and biome arrays from the GPU
    /// terrain noise compute shader, skipping CPU noise evaluation entirely.
    /// Settlement flattening is still applied CPU-side as a post-pass.
    /// </summary>
    public Chunk(OpenTK.Mathematics.Vector3i position, float[] gpuHeights, int[] gpuBiomes)
    {
        Position = position;
        Array.Fill(_blocks, Block.Air);
        GenerateFromHeightmap(gpuHeights, gpuBiomes);
    }

    /// <summary>
    /// Creates a chunk using pre-computed column data (heightmap, biomes, settlement
    /// zones) that was already computed once for the entire XZ column.  This avoids
    /// redundant settlement queries across the 8 vertical layers.
    /// </summary>
    public Chunk(OpenTK.Mathematics.Vector3i position, ColumnData col)
    {
        Position = position;
        Array.Fill(_blocks, Block.Air);
        GenerateFromColumnData(col);
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

    /// <summary>Returns the raw block WaterLevel at the given flat array index. Used by WorldPersistence.</summary>
    internal byte GetRawWaterLevel(int flatIndex) => _blocks[flatIndex].WaterLevel;

    /// <summary>
    /// Overwrites the entire block array from a saved ID (and optional layer) list produced by
    /// <see cref="WorldPersistence"/>.  Transparency is resolved through the
    /// block registry so water, leaves, and other transparent blocks render correctly.
    /// <paramref name="savedLayers"/> may be null for older saves, which default to Layer=16 (full) for solid, 0 for air.
    /// </summary>
    internal void LoadBlocksFromSave(ushort[] savedIds, byte[]? savedLayers = null, byte[]? savedWaterLevels = null)
    {
        for (int i = 0; i < Volume; i++)
        {
            bool transparent = BlockRegistry.IsTransparent(savedIds[i]);
            _blocks[i] = new Block
            {
                Id = savedIds[i],
                IsTransparent = transparent,
                Layer = savedLayers != null ? savedLayers[i] : (byte)(savedIds[i] == 0 ? 0 : 16),
                WaterLevel = savedWaterLevels != null ? savedWaterLevels[i] : (byte)0,
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

    // World-space Y level at which still water fills any air column below the surface.
    private const int SeaLevel = 64;

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
    /// <summary>Maximum world-space build height (MaxChunkY * Size).</summary>
    private const int MaxWorldHeight = World.MaxChunkY * Size; // 256

    private static float ComputeSurfaceHeight(float wx, float wz)
    {
        // Continentalness — very low-frequency noise that separates ocean from land.
        // Produces large, coherent land masses with distinct ocean basins.
        float continentalness = NoiseGenerator.Octave(wx * 0.0008f + 3000f, wz * 0.0008f + 3000f, octaves: 4);

        // Land factor: 0 = ocean, 1 = land.  Threshold chosen so ~65-70% of
        // the world is dry land, the rest is ocean.
        float landFactor;
        if (continentalness < 0.33f)
            landFactor = 0f;
        else if (continentalness < 0.43f)
            landFactor = (continentalness - 0.33f) * 10f;
        else
            landFactor = 1f;

        // Hermite smoothstep for natural coastlines.
        landFactor = landFactor * landFactor * (3f - 2f * landFactor);

        // Base continent shape — wide smooth features.
        float continent = NoiseGenerator.Octave(wx * 0.010f, wz * 0.010f, octaves: 6);

        // Detail hills — finer frequency for small undulations.
        float detail = NoiseGenerator.Octave(wx * 0.035f, wz * 0.035f, octaves: 4);

        // Erosion noise — controls how mountainous an area is.
        float erosion = NoiseGenerator.Octave(wx * 0.0006f + 1000f, wz * 0.0006f + 1000f, octaves: 3);

        // Ocean floor: 28–48 (well below sea level 64).
        float oceanH = 28f + continent * 20f;

        // Land base: 68–92 (safely above sea level 64).
        float landH = 68f + continent * 24f;

        // Blend between ocean and land based on continentalness.
        float h = oceanH + (landH - oceanH) * landFactor;

        // Detail bumps: reduced on ocean floor to keep it flatter.
        h += (detail - 0.5f) * 8f * (0.3f + 0.7f * landFactor);

        // Mountain ridges — only on land.
        if (landFactor > 0.5f && erosion > 0.45f)
        {
            float strength = (erosion - 0.45f) / 0.55f;
            strength *= strength; // gradual ramp
            float ridge = NoiseGenerator.Octave(wx * 0.02f, wz * 0.02f, octaves: 5);
            float ridged = 1f - MathF.Abs(ridge * 2f - 1f);
            ridged *= ridged; // sharpen peaks
            h += ridged * strength * 120f * landFactor;
        }

        // Valleys — flatten and lower terrain where erosion is very low (land only).
        if (landFactor > 0.5f && erosion < 0.30f)
        {
            float valleyStr = (0.30f - erosion) / 0.30f;
            h = MathF.Max(h * (1f - valleyStr * 0.25f), 8f);
        }

        // Inland lakes — rare medium-frequency depressions deep inside land masses.
        if (landFactor > 0.85f)
        {
            float lakeNoise = NoiseGenerator.Octave(wx * 0.005f + 7000f, wz * 0.005f + 7000f, octaves: 3);
            if (lakeNoise < 0.25f)
            {
                float lakeDepth = (0.25f - lakeNoise) / 0.25f;
                lakeDepth *= lakeDepth; // soften edges
                h -= lakeDepth * 24f;
            }
        }

        return Math.Clamp(h, 4f, MaxWorldHeight - 4f);
    }

    /// <summary>
    /// Determines the biome at the given world-space XZ position using temperature
    /// and humidity noise channels.  Returns one of the Biome* constants.
    /// </summary>
    private static int ComputeBiome(float wx, float wz)
    {
        float temp = NoiseGenerator.Octave(wx * 0.00012f, wz * 0.00012f, octaves: 3);
        float humidity = NoiseGenerator.Octave(wx * 0.00016f + 500f, wz * 0.00016f + 500f, octaves: 3);

        // Hot biomes.
        if (temp > 0.72f)
            return humidity > 0.55f ? BiomeJungle : BiomeDesert;

        // Warm biomes.
        if (temp > 0.58f)
            return humidity > 0.55f ? BiomeCherryGrove : BiomeSavanna;

        // Cold biomes.
        if (temp < 0.25f)
            return BiomeSnowy;

        if (temp < 0.45f)
            return humidity < 0.45f ? BiomeMountains : BiomeTaiga;

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

        // World-space Y range for this chunk.
        int chunkWorldYMin = Position.Y * Size;
        int chunkWorldYMax = chunkWorldYMin + Size - 1;

        // Pre-compute heightmap (float for sub-block precision) and biome map.
        var surfaceHeights = new float[Size * Size];
        var biomeMap = new int[Size * Size];

        int startWx = Position.X * Size;
        int startWz = Position.Z * Size;

        // Settlement zone map — tracks which columns are inside roads / settlements.
        var settlementZones = new SettlementMap.ZoneType[Size * Size];
        var settlementDist = new float[Size * Size];

        // Quick check: if the lowest possible terrain is above this chunk, skip filling.
        float minSurface = float.MaxValue;
        float maxSurface = float.MinValue;

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                float wx = startWx + x;
                float wz = startWz + z;
                float sh = ComputeSurfaceHeight(wx, wz);

                // Apply settlement terrain flattening.
                float flatFactor = SettlementMap.GetFlatteningFactor(wx, wz);
                if (flatFactor > 0f)
                {
                    float targetH = SettlementMap.GetSettlementTargetHeight(wx, wz);
                    if (targetH > 0f)
                        sh = sh + (targetH - sh) * flatFactor;
                }

                surfaceHeights[x + z * Size] = sh;
                biomeMap[x + z * Size] = ComputeBiome(wx, wz);

                // Cache settlement zone for this column.
                settlementZones[x + z * Size] = SettlementMap.Query(wx, wz, out float dist);
                settlementDist[x + z * Size] = dist;

                if (sh < minSurface) minSurface = sh;
                if (sh > maxSurface) maxSurface = sh;
            }

        // If the entire chunk is above all terrain AND above sea level, check buildings too.
        // Tall apartments can reach ~24 blocks above surface, so add headroom.
        bool entirelyAboveTerrain = chunkWorldYMin > (int)MathF.Ceiling(maxSurface) + 24
                                   && chunkWorldYMin > SeaLevel;
        if (entirelyAboveTerrain) return;

        FillTerrain(surfaceHeights, biomeMap, settlementZones, settlementDist, minSurface, maxSurface);
    }

    /// <summary>
    /// Shared block-fill logic used by both <see cref="Generate"/> and
    /// <see cref="GenerateFromHeightmap"/>.  Fills blocks column by column,
    /// stamps settlement overlays, and places trees.
    /// </summary>
    private void FillTerrain(float[] surfaceHeights, int[] biomeMap,
                             SettlementMap.ZoneType[] settlementZones, float[] settlementDist,
                             float minSurface, float maxSurface)
    {
        int chunkWorldYMin = Position.Y * Size;
        int startWx = Position.X * Size;
        int startWz = Position.Z * Size;

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

                bool underwater = surfaceY < SeaLevel;
                bool partiallySubmerged = surfaceY == SeaLevel && surfaceLayer < 14;

                for (int ly = 0; ly < Size; ly++)
                {
                    int wy = chunkWorldYMin + ly;
                    Block b;

                    if (wy > surfaceY && wy <= SeaLevel)
                    {
                        byte wl = (wy == SeaLevel) ? (byte)14 : (byte)16;
                        b = new Block { Id = 0, IsTransparent = true, Layer = 0, WaterLevel = wl };
                    }
                    else if (wy > surfaceY)
                    {
                        b = Block.Air;
                    }
                    else if (wy == surfaceY)
                    {
                        b = BiomeSurfaceBlock(biome, surfaceY, surfaceLayer);
                        if (underwater)
                            b.WaterLevel = 16;
                        else if (partiallySubmerged)
                            b.WaterLevel = 14;
                    }
                    else if (wy >= surfaceY - 3)
                    {
                        b = biome switch
                        {
                            BiomeDesert => new Block { Id = 5, IsTransparent = false, Layer = 16 },
                            BiomeMountains => new Block { Id = 2, IsTransparent = false, Layer = 16 },
                            _ => new Block { Id = 1, IsTransparent = false, Layer = 16 },
                        };
                        if (underwater)
                            b.WaterLevel = 16;
                    }
                    else
                    {
                        // Stone base with ore pockets.
                        float wx = startWx + x;
                        float wz = startWz + z;
                        uint h = (uint)((int)(wx * 374761393) ^ (wy * 1274126177) ^ (int)(wz * 668265263));
                        h = (h ^ (h >> 13)) * 1274126177u;
                        h ^= h >> 16;

                        if (wy <= 24 && h % 130 == 0)
                            b = new Block { Id = 14, IsTransparent = false, Layer = 16 };
                        else if (wy <= 48 && h % 70 == 0)
                            b = new Block { Id = 13, IsTransparent = false, Layer = 16 };
                        else
                            b = new Block { Id = 2, IsTransparent = false, Layer = 16 };
                    }

                    _blocks[Index(x, ly, z)] = b;
                }
            }

        // Settlement overlay: stamp roads and buildings on top.
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int colIdx = x + z * Size;
                var zone = settlementZones[colIdx];
                if (zone == SettlementMap.ZoneType.None) continue;

                float surfaceH = surfaceHeights[colIdx];
                int surfaceY = (int)MathF.Ceiling(surfaceH) - 1;
                float frac = surfaceH - surfaceY;
                byte surfaceLayer = (byte)Math.Clamp((int)MathF.Ceiling(frac * 16f), 1, 16);
                float dist = settlementDist[colIdx];
                float fwx = startWx + x;
                float fwz = startWz + z;

                if (zone == SettlementMap.ZoneType.MainRoad || zone == SettlementMap.ZoneType.SecondaryRoad)
                {
                    for (int ly = 0; ly < Size; ly++)
                    {
                        int wy = chunkWorldYMin + ly;
                        Block? rb = BuildingGenerator.GetInterRoadBlock(
                            (int)fwx, wy, (int)fwz, surfaceY, surfaceLayer,
                            zone == SettlementMap.ZoneType.MainRoad);
                        if (rb.HasValue)
                            _blocks[Index(x, ly, z)] = rb.Value;
                    }
                }
                else
                {
                    var structure = SettlementMap.GetStructureAt(fwx, fwz, zone, dist);
                    if (structure == SettlementMap.StructureType.None) continue;

                    for (int ly = 0; ly < Size; ly++)
                    {
                        int wy = chunkWorldYMin + ly;
                        Block? sb = BuildingGenerator.GetStructureBlock(
                            (int)fwx, wy, (int)fwz, structure, surfaceY, zone);
                        if (sb.HasValue)
                            _blocks[Index(x, ly, z)] = sb.Value;
                    }
                }
            }

        // Place trees.
        const int border = 4;
        // Skip tree placement entirely for chunks whose Y range cannot contain any
        // tree blocks.  Trees grow upward from the surface by at most ~25 blocks
        // (jungle trees).  The border region (±4 blocks) may have slightly different
        // heights, so we add a conservative margin.
        const int treeMaxHeight = 25;
        const int heightMargin = 20; // for border-region height variance
        int chunkWorldYMax = chunkWorldYMin + Size - 1;
        bool canContainTrees = chunkWorldYMax >= (int)minSurface - heightMargin
                            && chunkWorldYMin <= (int)maxSurface + treeMaxHeight + heightMargin;

        if (canContainTrees)
            for (int tz = startWz - border; tz < startWz + Size + border; tz++)
                for (int tx = startWx - border; tx < startWx + Size + border; tx++)
                {
                    // Cheap deterministic hash check first — skip most positions before
                    // doing any noise evaluations.
                    int biome = ComputeBiome(tx, tz);
                    if (biome == BiomeDesert) continue;

                    int treeDensity = GetTreeDensity(biome);
                    if (treeDensity <= 0) continue;

                    if (!ShouldPlaceTree(tx, tz, treeDensity)) continue;

                    float vegNoise = ComputeVegetationDensity(tx, tz);
                    if (vegNoise < 0.01f) continue;

                    float densityScale = 0.15f + 6.0f * (1f - vegNoise);
                    int adjustedDensity = Math.Max(4, (int)(treeDensity * densityScale));

                    // Re-check with adjusted density (original treeDensity was a quick pre-filter).
                    if (adjustedDensity > treeDensity && !ShouldPlaceTree(tx, tz, adjustedDensity)) continue;

                    // Compute surface height — expensive, so only done for surviving candidates.
                    int sy = (int)MathF.Ceiling(ComputeSurfaceHeight(tx, tz)) - 1;
                    if (sy <= SeaLevel) continue;

                    if (biome == BiomeMountains && sy > 140) continue;

                    // Settlement/road check last — most expensive due to road network queries.
                    var zone = SettlementMap.Query(tx, tz, out _);
                    if (zone == SettlementMap.ZoneType.Village || zone == SettlementMap.ZoneType.City) continue;
                    if (zone == SettlementMap.ZoneType.MainRoad || zone == SettlementMap.ZoneType.SecondaryRoad) continue;

                    PlaceTreeForBiome(tx, tz, sy, biome);
                }
    }

    // ------------------------------------------------------------------
    // Biome surface block helper
    // ------------------------------------------------------------------

    private static Block BiomeSurfaceBlock(int biome, int surfaceY, byte layer) => biome switch
    {
        BiomeDesert => new Block { Id = 5, IsTransparent = false, Layer = layer },
        BiomeMountains => surfaceY > 140
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
        BiomePlains => 200,
        BiomeForest => 64,
        BiomeBirchForest => 72,
        BiomeDarkForest => 48,
        BiomeTaiga => 64,
        BiomeSnowy => 80,
        BiomeMountains => 140,
        BiomeSavanna => 120,
        BiomeCherryGrove => 72,
        BiomeJungle => 40,
        _ => 0,
    };

    // ------------------------------------------------------------------
    // Vegetation density noise — separates forests from open grassland
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns a value in [0, 1] where 1 = dense forest and 0 = open grassland/veldt.
    /// Uses low-frequency noise with a sharp threshold to create large, distinct patches.
    /// Roughly 40-50% of land is open grassland/veldt, the rest is forested.
    /// </summary>
    private static float ComputeVegetationDensity(float wx, float wz)
    {
        // Low frequency for large patches (~600-700 blocks across).
        float raw = NoiseGenerator.Octave(wx * 0.0015f + 5000f, wz * 0.0015f + 5000f, octaves: 3);
        // Threshold at 0.50 so roughly half the land is open.
        // Below 0.45 = fully open veldt, above 0.55 = fully forested.
        if (raw < 0.45f) return 0f;
        if (raw > 0.55f) return 1f;
        float t = (raw - 0.45f) * 10f; // remap [0.45, 0.55] → [0, 1]
        return t * t * (3f - 2f * t); // smoothstep
    }

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

    /// <summary>Converts world-space Y to local Y for this chunk, or -1 if out of range.</summary>
    private int WorldToLocalY(int worldY)
    {
        int ly = worldY - Position.Y * Size;
        return (uint)ly < Size ? ly : -1;
    }

    private void SetLog(int lx, int wy, int lz, ushort logId)
    {
        int ly = WorldToLocalY(wy);
        if (ly >= 0 && InBounds(lx, ly, lz))
            _blocks[Index(lx, ly, lz)] = new Block { Id = logId, IsTransparent = false, Layer = 16 };
    }

    private void SetLeaf(int lx, int wy, int lz, ushort leafId)
    {
        int ly = WorldToLocalY(wy);
        if (ly >= 0 && InBounds(lx, ly, lz))
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

    /// <summary>Small oak: 4–7 block trunk, varied crown shape.</summary>
    private void PlaceOakTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 4 + (int)(hash % 4); // 4-7

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 7);

        int crownLayers = 3 + (int)((hash >> 4) % 2); // 3-4 layers
        for (int layer = 0; layer < crownLayers; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            int radius = layer >= crownLayers - 1 ? 0 : (layer == 0 && crownLayers > 3 ? 2 : 1);
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Randomly trim corners for organic shape
                    if (radius >= 2 && Math.Abs(dx) == radius && Math.Abs(dz) == radius
                        && ((hash >> 8) + (uint)(dx + dz)) % 3 != 0) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 8);
                }
        }
    }

    /// <summary>Large oak: 6–10 block trunk, wide rounded crown with variation.</summary>
    private void PlaceLargeOakTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 6 + (int)(hash % 5); // 6-10

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 7);

        int crownLayers = 4 + (int)((hash >> 3) % 3); // 4-6 layers
        for (int layer = 0; layer < crownLayers; layer++)
        {
            int ly = surfaceY + trunkH - (crownLayers / 2) + layer;
            int radius;
            if (layer < crownLayers / 2) radius = 2 + (int)((hash >> 6) % 2); // 2-3
            else if (layer < crownLayers - 1) radius = 1 + (int)((hash >> 8) % 2); // 1-2
            else radius = 0;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Trim corners with hash-based variation
                    if (radius >= 2 && Math.Abs(dx) == radius && Math.Abs(dz) == radius
                        && ((hash >> 10) + (uint)(dx * 3 + dz)) % 3 == 0) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 8);
                }
        }
    }

    /// <summary>Birch: 5–9 block trunk, narrow crown with variation.</summary>
    private void PlaceBirchTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 5 + (int)(hash % 5); // 5-9

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 17);

        int crownLayers = 3 + (int)((hash >> 4) % 3); // 3-5 layers
        for (int layer = 0; layer < crownLayers; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            int radius = layer >= crownLayers - 1 ? 0 : (layer == 0 && crownLayers > 3 ? 2 : 1);
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius >= 2 && Math.Abs(dx) == radius && Math.Abs(dz) == radius
                        && ((hash >> 7) + (uint)(dx + dz * 2)) % 3 != 0) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 18);
                }
        }
    }

    /// <summary>Spruce: 7–14 block trunk, conical crown with varied width.</summary>
    private void PlaceSpruceTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 7 + (int)(hash % 8); // 7-14

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 19);

        // Leaf cap above the trunk.
        SetLeaf(lx, surfaceY + trunkH, lz, 20);

        // Conical leaf layers — widening from top to bottom.
        int leafLayers = trunkH - 2;
        int maxRadius = 2 + (int)((hash >> 5) % 3); // 2-4
        for (int i = 0; i < leafLayers; i++)
        {
            int ly = surfaceY + trunkH - 1 - i;
            int radius = 1 + i / 2;
            if (radius > maxRadius) radius = maxRadius;

            // Alternate between full and trimmed layers for layered look
            bool trimLayer = (i % 2 == 0) && ((hash >> 8) % 3 != 0);

            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0) continue; // trunk
                    int manhattan = Math.Abs(dx) + Math.Abs(dz);
                    if (manhattan > radius + 1) continue; // diamond trim
                    if (trimLayer && manhattan == radius + 1) continue; // extra trim
                    SetLeaf(lx + dx, ly, lz + dz, 20);
                }
        }
    }

    /// <summary>Dark oak: 2×2 trunk 5–9 blocks tall, wide dome canopy with variation.</summary>
    private void PlaceDarkOakTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 5 + (int)(hash % 5); // 5-9

        for (int dy = 0; dy < trunkH; dy++)
        {
            SetLog(lx, surfaceY + dy, lz, 21);
            SetLog(lx + 1, surfaceY + dy, lz, 21);
            SetLog(lx, surfaceY + dy, lz + 1, 21);
            SetLog(lx + 1, surfaceY + dy, lz + 1, 21);
        }

        // Dome canopy — varied layers, widest at the bottom.
        float cx = lx + 0.5f, cz = lz + 0.5f;
        int domeLayers = 3 + (int)((hash >> 4) % 3); // 3-5
        float baseRadius = 2.5f + ((hash >> 7) % 3) * 0.5f; // 2.5-3.5
        for (int layer = 0; layer < domeLayers; layer++)
        {
            int ly = surfaceY + trunkH - (domeLayers / 2) + layer;
            float frac = (float)layer / (domeLayers - 1);
            float maxDist = layer >= domeLayers - 1 ? 1.5f : baseRadius * (1f - frac * 0.4f);
            int extent = (int)MathF.Ceiling(maxDist);
            for (int dz = -extent; dz <= extent; dz++)
                for (int dx = -extent; dx <= extent; dx++)
                {
                    float dist = MathF.Sqrt((lx + dx - cx) * (lx + dx - cx)
                                          + (lz + dz - cz) * (lz + dz - cz));
                    if (dist > maxDist) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 22);
                }
        }
    }

    /// <summary>Acacia: 5–9 block trunk, flat wide canopy with offset and variation.</summary>
    private void PlaceAcaciaTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 5 + (int)(hash % 5); // 5-9

        // Acacia trunk leans — offset the top
        int leanX = (int)((hash >> 3) % 3) - 1; // -1, 0, or 1
        int leanZ = (int)((hash >> 5) % 3) - 1;
        for (int dy = 0; dy < trunkH; dy++)
        {
            int ox = dy >= trunkH / 2 ? leanX : 0;
            int oz = dy >= trunkH / 2 ? leanZ : 0;
            SetLog(lx + ox, surfaceY + dy, lz + oz, 23);
        }

        int canopyLayers = 2 + (int)((hash >> 8) % 2); // 2-3
        int canopyRadius = 2 + (int)((hash >> 10) % 2); // 2-3
        for (int layer = 0; layer < canopyLayers; layer++)
        {
            int ly = surfaceY + trunkH - 1 + layer;
            int r = layer == canopyLayers - 1 ? canopyRadius - 1 : canopyRadius;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    if (Math.Abs(dx) == r && Math.Abs(dz) == r
                        && ((hash >> 12) + (uint)(dx + dz)) % 2 == 0) continue;
                    SetLeaf(lx + leanX + dx, ly, lz + leanZ + dz, 24);
                }
        }
    }

    /// <summary>Cherry: 4–8 block trunk, round canopy with varied shape.</summary>
    private void PlaceCherryTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 4 + (int)(hash % 5); // 4-8

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 25);

        int crownLayers = 3 + (int)((hash >> 4) % 3); // 3-5
        for (int layer = 0; layer < crownLayers; layer++)
        {
            int ly = surfaceY + trunkH - 2 + layer;
            int radius = layer >= crownLayers - 1 ? 1 : (layer == 0 ? 2 + (int)((hash >> 7) % 2) : 2);
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius >= 2 && Math.Abs(dx) == radius && Math.Abs(dz) == radius
                        && ((hash >> 9) + (uint)(dx + dz)) % 3 == 0) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 26);
                }
        }
    }

    /// <summary>Jungle: 8–18 block tall trunk, large rounded canopy with variation.</summary>
    private void PlaceJungleTree(int treeWx, int treeWz, int surfaceY, uint hash)
    {
        int lx = treeWx - Position.X * Size;
        int lz = treeWz - Position.Z * Size;
        int trunkH = 8 + (int)(hash % 11); // 8-18

        for (int dy = 0; dy < trunkH; dy++)
            SetLog(lx, surfaceY + dy, lz, 27);

        int crownLayers = 4 + (int)((hash >> 4) % 3); // 4-6
        int maxRadius = 2 + (int)((hash >> 7) % 2); // 2-3
        for (int layer = 0; layer < crownLayers; layer++)
        {
            int ly = surfaceY + trunkH - (crownLayers / 2) + layer;
            int radius;
            if (layer < crownLayers / 2) radius = maxRadius;
            else if (layer < crownLayers - 1) radius = maxRadius - 1;
            else radius = 0;
            for (int dz = -radius; dz <= radius; dz++)
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius >= 2 && Math.Abs(dx) == radius && Math.Abs(dz) == radius
                        && ((hash >> 10) + (uint)(dx * 2 + dz)) % 3 == 0) continue;
                    SetLeaf(lx + dx, ly, lz + dz, 28);
                }
        }
    }

    /// <summary>
    /// Fills terrain using pre-computed heightmap and biome arrays from the GPU
    /// compute shader.  Settlement flattening, block fill, overlays and tree
    /// placement all remain CPU-side (identical logic to <see cref="Generate"/>).
    /// </summary>
    private void GenerateFromHeightmap(float[] gpuHeights, int[] gpuBiomes)
    {
        if (WorldGenConfig.FlatWorld) { GenerateFlat(); return; }

        int chunkWorldYMin = Position.Y * Size;

        var surfaceHeights = new float[Size * Size];
        var biomeMap = new int[Size * Size];

        int startWx = Position.X * Size;
        int startWz = Position.Z * Size;

        var settlementZones = new SettlementMap.ZoneType[Size * Size];
        var settlementDist = new float[Size * Size];

        float minSurface = float.MaxValue;
        float maxSurface = float.MinValue;

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int idx = x + z * Size;
                float wx = startWx + x;
                float wz = startWz + z;

                float sh = gpuHeights[idx];

                // Apply settlement terrain flattening (CPU post-pass).
                float flatFactor = SettlementMap.GetFlatteningFactor(wx, wz);
                if (flatFactor > 0f)
                {
                    float targetH = SettlementMap.GetSettlementTargetHeight(wx, wz);
                    if (targetH > 0f)
                        sh = sh + (targetH - sh) * flatFactor;
                }

                surfaceHeights[idx] = sh;
                biomeMap[idx] = gpuBiomes[idx];

                settlementZones[idx] = SettlementMap.Query(wx, wz, out float dist);
                settlementDist[idx] = dist;

                if (sh < minSurface) minSurface = sh;
                if (sh > maxSurface) maxSurface = sh;
            }

        bool entirelyAboveTerrain = chunkWorldYMin > (int)MathF.Ceiling(maxSurface) + 24
                                   && chunkWorldYMin > SeaLevel;
        if (entirelyAboveTerrain) return;

        FillTerrain(surfaceHeights, biomeMap, settlementZones, settlementDist, minSurface, maxSurface);
    }

    /// <summary>
    /// Fast path: uses fully pre-computed <see cref="ColumnData"/> so no settlement
    /// queries run inside the chunk constructor at all.
    /// </summary>
    private void GenerateFromColumnData(ColumnData col)
    {
        if (WorldGenConfig.FlatWorld) { GenerateFlat(); return; }

        int chunkWorldYMin = Position.Y * Size;
        bool entirelyAboveTerrain = chunkWorldYMin > (int)MathF.Ceiling(col.MaxSurface) + 24
                                   && chunkWorldYMin > SeaLevel;
        if (entirelyAboveTerrain) return;

        FillTerrain(col.SurfaceHeights, col.BiomeMap, col.Zones, col.Dist,
                    col.MinSurface, col.MaxSurface);
    }

    /// <summary>
    /// Builds <see cref="ColumnData"/> for an XZ column using GPU heightmap/biome
    /// arrays.  Settlement queries run once here and are shared by all Y-layer chunks.
    /// </summary>
    public static ColumnData BuildColumnData(OpenTK.Mathematics.Vector2i colXZ,
                                             float[] gpuHeights, int[] gpuBiomes)
    {
        var surfaceHeights = new float[Size * Size];
        var biomeMap = new int[Size * Size];
        var zones = new SettlementMap.ZoneType[Size * Size];
        var dist = new float[Size * Size];

        int startWx = colXZ.X * Size;
        int startWz = colXZ.Y * Size;
        float minSurface = float.MaxValue;
        float maxSurface = float.MinValue;

        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
            {
                int idx = x + z * Size;
                float wx = startWx + x;
                float wz = startWz + z;

                float sh = gpuHeights[idx];

                // Single settlement query per column position (replaces 3 redundant calls).
                var zone = SettlementMap.Query(wx, wz, out float d);
                zones[idx] = zone;
                dist[idx] = d;

                // Apply settlement terrain flattening using the already-queried zone.
                float flatFactor = zone switch
                {
                    SettlementMap.ZoneType.City => SettlementMap.SmoothFalloff(d, 60, 0.95f),
                    SettlementMap.ZoneType.Village => SettlementMap.SmoothFalloff(d, 40, 0.5f),
                    SettlementMap.ZoneType.MainRoad => 0.3f,
                    SettlementMap.ZoneType.SecondaryRoad => 0.2f,
                    _ => 0f,
                };
                if (flatFactor > 0f)
                {
                    float targetH = SettlementMap.GetSettlementTargetHeight(wx, wz);
                    if (targetH > 0f)
                        sh += (targetH - sh) * flatFactor;
                }

                surfaceHeights[idx] = sh;
                biomeMap[idx] = gpuBiomes[idx];

                if (sh < minSurface) minSurface = sh;
                if (sh > maxSurface) maxSurface = sh;
            }

        return new ColumnData(surfaceHeights, biomeMap, zones, dist, minSurface, maxSurface);
    }

    /// <summary>
    /// Generates a perfectly flat world: Stone below <c>grassY-3</c>, Dirt in
    /// the three layers below the surface, Grass on top, Air above.
    /// </summary>
    private void GenerateFlat()
    {
        const int grassY = 64; // world-space grass level
        int chunkWorldYMin = Position.Y * Size;
        for (int z = 0; z < Size; z++)
            for (int x = 0; x < Size; x++)
                for (int ly = 0; ly < Size; ly++)
                {
                    int wy = chunkWorldYMin + ly;
                    Block b;
                    if (wy > grassY) b = Block.Air;
                    else if (wy == grassY) b = new Block { Id = 3, IsTransparent = false, Layer = 16 };
                    else if (wy >= grassY - 3) b = new Block { Id = 1, IsTransparent = false, Layer = 16 };
                    else b = new Block { Id = 2, IsTransparent = false, Layer = 16 };
                    _blocks[Index(x, ly, z)] = b;
                }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Converts local (x, y, z) to a flat array index.</summary>
    public static int Index(int x, int y, int z) => x + Size * (y + Size * z);
}

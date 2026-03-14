namespace VintageVoxel;

/// <summary>
/// Deterministic noise-based settlement placement system.
/// Identifies village zones, city zones, and road networks at any world XZ position
/// using only seeded hash / noise — no global data structures needed.
///
/// Settlement centres are placed on a coarse grid with jitter so every chunk can
/// independently query "am I inside a settlement?" without cross-chunk communication.
///
/// Grid layout:
///   - City grid   : 512-block spacing, rare (hash threshold ~8%)
///   - Village grid : 256-block spacing, moderate (~18%)
///   - Roads connect each settlement to its nearest grid-axis neighbour.
/// </summary>
public static class SettlementMap
{
    // ------------------------------------------------------------------
    // Settlement types
    // ------------------------------------------------------------------

    public enum ZoneType
    {
        None,
        Village,
        City,
        SecondaryRoad,
        MainRoad,
    }

    // ------------------------------------------------------------------
    // Grid parameters
    // ------------------------------------------------------------------

    private const int SettlementSpacing = 384;  // grid cell size in blocks
    private const int MaxJitter = 80;           // max offset from grid centre
    private const int VillageRadius = 40;       // blocks from centre
    private const int CityRadius = 60;
    private const float CityThreshold = 0.08f;  // ~8% of cells become cities
    private const float VillageThreshold = 0.26f; // ~18% villages (0.26 - 0.08)

    // Road parameters
    private const float MainRoadHalfWidth = 4.0f;
    private const float SecondaryRoadHalfWidth = 2.5f;

    // Must match Chunk.SeaLevel — settlements below this are rejected.
    private const int SeaLevel = 64;

    // Max grid cells to search in each direction to find a connecting settlement.
    private const int RoadSearchRange = 3;

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the settlement zone type at world-space position (wx, wz).
    /// Also outputs the nearest settlement centre if inside one.
    /// </summary>
    public static ZoneType Query(float wx, float wz, out float distToCenter)
    {
        distToCenter = float.MaxValue;

        // Check nearby grid cells (3x3 neighbourhood) for settlement centres.
        int cellX = FloorDiv((int)wx, SettlementSpacing);
        int cellZ = FloorDiv((int)wz, SettlementSpacing);

        ZoneType best = ZoneType.None;

        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = cellX + dx;
                int cz = cellZ + dz;

                if (!TryGetSettlement(cx, cz, out var type, out float sx, out float sz))
                    continue;

                float dist = MathF.Sqrt((wx - sx) * (wx - sx) + (wz - sz) * (wz - sz));
                float radius = type == ZoneType.City ? CityRadius : VillageRadius;

                if (dist < radius && dist < distToCenter)
                {
                    distToCenter = dist;
                    best = type;
                }
            }

        if (best != ZoneType.None)
            return best;

        // Check roads — connect settlements to nearest neighbour in each direction.
        float roadDist = QueryRoad(wx, wz, out bool isMain);
        if (isMain && roadDist < MainRoadHalfWidth)
        {
            distToCenter = roadDist;
            return ZoneType.MainRoad;
        }
        if (!isMain && roadDist < SecondaryRoadHalfWidth)
        {
            distToCenter = roadDist;
            return ZoneType.SecondaryRoad;
        }
        // Main roads have a secondary-road edge band for smooth transition.
        if (isMain && roadDist < MainRoadHalfWidth + 1.0f)
        {
            distToCenter = roadDist;
            return ZoneType.SecondaryRoad;
        }

        return ZoneType.None;
    }

    /// <summary>
    /// Returns the terrain flattening factor at (wx, wz).
    /// 0 = no flattening, 1 = fully flat.
    /// Cities get strong flattening, villages get mild, roads get slight.
    /// </summary>
    public static float GetFlatteningFactor(float wx, float wz)
    {
        var zone = Query(wx, wz, out float dist);
        return zone switch
        {
            ZoneType.City => SmoothFalloff(dist, CityRadius, 0.95f),
            ZoneType.Village => SmoothFalloff(dist, VillageRadius, 0.5f),
            ZoneType.MainRoad => 0.3f,
            ZoneType.SecondaryRoad => 0.2f,
            _ => 0f,
        };
    }

    /// <summary>
    /// Returns the target flat height for the settlement area at (wx, wz).
    /// This is the average surface height sampled at the settlement centre.
    /// Returns -1 if not in a settlement area.
    /// </summary>
    public static float GetSettlementTargetHeight(float wx, float wz)
    {
        int cellX = FloorDiv((int)wx, SettlementSpacing);
        int cellZ = FloorDiv((int)wz, SettlementSpacing);

        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = cellX + dx;
                int cz = cellZ + dz;

                if (!TryGetSettlement(cx, cz, out var type, out float sx, out float sz))
                    continue;

                float radius = type == ZoneType.City ? CityRadius : VillageRadius;
                float dist = MathF.Sqrt((wx - sx) * (wx - sx) + (wz - sz) * (wz - sz));

                // Extended radius for smooth transition
                if (dist < radius + 20f)
                    return GetSettlementBaseHeight(cx, cz);
            }

        return -1f;
    }

    /// <summary>
    /// Checks if the given world position is inside a settlement zone (village or city).
    /// </summary>
    public static bool IsInSettlement(float wx, float wz)
    {
        var zone = Query(wx, wz, out _);
        return zone == ZoneType.Village || zone == ZoneType.City;
    }

    // ------------------------------------------------------------------
    // Settlement grid
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns true if grid cell (cx, cz) contains a settlement.
    /// Outputs the settlement type, and its world-space XZ centre.
    /// </summary>
    public static bool TryGetSettlement(int cx, int cz, out ZoneType type, out float wx, out float wz)
    {
        uint h = CellHash(cx, cz, 0);
        float chance = (h & 0xFFFF) / 65535f;

        if (chance >= VillageThreshold)
        {
            type = ZoneType.None;
            wx = wz = 0;
            return false;
        }

        type = chance < CityThreshold ? ZoneType.City : ZoneType.Village;

        // Jitter position within cell.
        uint hx = CellHash(cx, cz, 1);
        uint hz = CellHash(cx, cz, 2);
        int jx = (int)(hx % (uint)(MaxJitter * 2)) - MaxJitter;
        int jz = (int)(hz % (uint)(MaxJitter * 2)) - MaxJitter;

        wx = cx * SettlementSpacing + SettlementSpacing / 2 + jx;
        wz = cz * SettlementSpacing + SettlementSpacing / 2 + jz;

        // Reject settlements whose terrain is at or below sea level (ocean/beach).
        float baseH = ComputeRawSurfaceHeight(wx, wz);
        if (baseH <= SeaLevel + 3)
        {
            type = ZoneType.None;
            wx = wz = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns the pre-computed base height for a settlement centre.
    /// Cached via hash — always returns the same value for the same cell.
    /// </summary>
    // Cache for settlement base heights — same grid cell always returns the same value
    // and involves 25 × ComputeRawSurfaceHeight calls (~350 noise samples).
    private static readonly Dictionary<long, float> _baseHeightCache = new();

    private static float GetSettlementBaseHeight(int cx, int cz)
    {
        long key = ((long)cx << 32) | (uint)cz;
        if (_baseHeightCache.TryGetValue(key, out float cached))
            return cached;

        if (!TryGetSettlement(cx, cz, out _, out float sx, out float sz))
        {
            _baseHeightCache[key] = 70f;
            return 70f;
        }

        // Sample terrain height at the settlement centre.
        // We use a small average around the centre for stability.
        float sum = 0f;
        int count = 0;
        for (int dz = -8; dz <= 8; dz += 4)
            for (int dx = -8; dx <= 8; dx += 4)
            {
                sum += ComputeRawSurfaceHeight(sx + dx, sz + dz);
                count++;
            }
        float result = MathF.Floor(sum / count);
        _baseHeightCache[key] = result;
        return result;
    }

    /// <summary>
    /// Approximation of the base terrain height without settlement flattening.
    /// Mirrors the continent + detail logic in Chunk.ComputeSurfaceHeight but
    /// without mountain amplification — settlements avoid extreme terrain.
    /// </summary>
    private static float ComputeRawSurfaceHeight(float wx, float wz)
    {
        float continentalness = NoiseGenerator.Octave(wx * 0.0008f + 3000f, wz * 0.0008f + 3000f, octaves: 4);
        float landFactor;
        if (continentalness < 0.33f) landFactor = 0f;
        else if (continentalness < 0.43f) landFactor = (continentalness - 0.33f) * 10f;
        else landFactor = 1f;
        landFactor = landFactor * landFactor * (3f - 2f * landFactor);

        float continent = NoiseGenerator.Octave(wx * 0.010f, wz * 0.010f, octaves: 6);
        float detail = NoiseGenerator.Octave(wx * 0.035f, wz * 0.035f, octaves: 4);

        float oceanH = 28f + continent * 20f;
        float landH = 68f + continent * 24f;
        float h = oceanH + (landH - oceanH) * landFactor;
        h += (detail - 0.5f) * 8f * (0.3f + 0.7f * landFactor);

        return Math.Clamp(h, 4f, 252f);
    }

    // ------------------------------------------------------------------
    // Road network
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the minimum distance from (wx, wz) to any road segment.
    /// Roads connect each settlement to the nearest settlement found in each of
    /// 4 cardinal directions (up to <see cref="RoadSearchRange"/> cells away).
    /// If no nearby road, returns float.MaxValue.
    /// </summary>
    private static float QueryRoad(float wx, float wz, out bool isMainRoad)
    {
        isMainRoad = false;
        float minDist = float.MaxValue;

        int cellX = FloorDiv((int)wx, SettlementSpacing);
        int cellZ = FloorDiv((int)wz, SettlementSpacing);

        // For each cell in a 3x3 neighbourhood that has a settlement, check
        // outgoing road connections in 4 cardinal directions.
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int cx = cellX + dx;
                int cz = cellZ + dz;

                if (!TryGetSettlement(cx, cz, out var srcType, out float sx, out float sz))
                    continue;

                // Search +X direction for nearest settlement
                FindAndCheckRoad(cx, cz, 1, 0, sx, sz, srcType, wx, wz, ref minDist, ref isMainRoad);
                // Search +Z direction
                FindAndCheckRoad(cx, cz, 0, 1, sx, sz, srcType, wx, wz, ref minDist, ref isMainRoad);
                // Search -X direction
                FindAndCheckRoad(cx, cz, -1, 0, sx, sz, srcType, wx, wz, ref minDist, ref isMainRoad);
                // Search -Z direction
                FindAndCheckRoad(cx, cz, 0, -1, sx, sz, srcType, wx, wz, ref minDist, ref isMainRoad);
            }

        return minDist;
    }

    /// <summary>
    /// From settlement at cell (cx, cz), searches in direction (stepX, stepZ) for
    /// the nearest settlement within <see cref="RoadSearchRange"/> cells and checks
    /// whether the query point is near that road segment.
    /// </summary>
    private static void FindAndCheckRoad(int cx, int cz, int stepX, int stepZ,
        float sx, float sz, ZoneType srcType,
        float wx, float wz, ref float minDist, ref bool isMain)
    {
        for (int i = 1; i <= RoadSearchRange; i++)
        {
            int nx = cx + stepX * i;
            int nz = cz + stepZ * i;

            if (!TryGetSettlement(nx, nz, out var dstType, out float dx, out float dz))
                continue;

            // Found nearest settlement in this direction — check the road segment.
            float dist = DistToSegment(wx, wz, sx, sz, dx, dz);
            if (dist < minDist)
            {
                minDist = dist;
                isMain = srcType == ZoneType.City || dstType == ZoneType.City;
            }
            break; // Only connect to the nearest settlement in each direction.
        }
    }

    // ------------------------------------------------------------------
    // Building layout within settlements
    // ------------------------------------------------------------------

    /// <summary>
    /// For a given world position inside a settlement, returns what structure
    /// should be placed there. Uses a deterministic sub-grid within the settlement.
    /// </summary>
    public static StructureType GetStructureAt(float wx, float wz, ZoneType zone, float distToCenter)
    {
        if (zone != ZoneType.Village && zone != ZoneType.City)
            return StructureType.None;

        float radius = zone == ZoneType.City ? CityRadius : VillageRadius;

        // Internal road grid within settlements — every 12 blocks
        const int internalGrid = 12;
        int gx = FloorMod((int)MathF.Floor(wx), internalGrid);
        int gz = FloorMod((int)MathF.Floor(wz), internalGrid);

        // Internal roads at grid edges (first 2 blocks of each grid cell)
        if (gx < 3 || gz < 3)
            return StructureType.InternalRoad;

        // Building plots occupy the 9x9 interior of each 12x12 cell
        // Determine building type from hash of the grid cell
        int plotX = FloorDiv((int)MathF.Floor(wx), internalGrid);
        int plotZ = FloorDiv((int)MathF.Floor(wz), internalGrid);
        uint plotHash = CellHash(plotX, plotZ, 42);

        // Outer ring of settlement = fields/gardens, inner = buildings
        float normalizedDist = distToCenter / radius;

        if (zone == ZoneType.Village)
        {
            if (normalizedDist > 0.7f)
                return (plotHash % 3 == 0) ? StructureType.Field : StructureType.None;
            if (normalizedDist > 0.4f)
                return (plotHash % 4 < 3) ? StructureType.VillageHouse : StructureType.Field;
            return (plotHash % 3 == 0) ? StructureType.VillageHouse : StructureType.VillageLargeHouse;
        }

        // City
        if (normalizedDist > 0.8f)
            return (plotHash % 3 == 0) ? StructureType.CityHouse : StructureType.Park;
        if (normalizedDist > 0.4f)
            return (plotHash % 4 < 2) ? StructureType.Apartment : StructureType.Shop;
        return (plotHash % 3 == 0) ? StructureType.TallApartment : StructureType.Apartment;
    }

    public enum StructureType
    {
        None,
        InternalRoad,
        Field,
        VillageHouse,
        VillageLargeHouse,
        CityHouse,
        Shop,
        Apartment,
        TallApartment,
        Park,
    }

    // ------------------------------------------------------------------
    // Math helpers
    // ------------------------------------------------------------------

    private static int FloorDiv(int a, int b)
    {
        return a >= 0 ? a / b : (a - b + 1) / b;
    }

    private static int FloorMod(int a, int b)
    {
        int r = a % b;
        return r < 0 ? r + b : r;
    }

    private static uint CellHash(int x, int z, int salt)
    {
        uint h = (uint)(x * 374761393 ^ z * 668265263 ^ salt * 1274126177);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h;
    }

    /// <summary>Distance from point (px, pz) to line segment (ax, az)→(bx, bz).</summary>
    private static float DistToSegment(float px, float pz, float ax, float az, float bx, float bz)
    {
        float dx = bx - ax, dz = bz - az;
        float len2 = dx * dx + dz * dz;
        if (len2 < 0.001f) return MathF.Sqrt((px - ax) * (px - ax) + (pz - az) * (pz - az));

        float t = Math.Clamp(((px - ax) * dx + (pz - az) * dz) / len2, 0f, 1f);
        float cx = ax + t * dx;
        float cz = az + t * dz;
        return MathF.Sqrt((px - cx) * (px - cx) + (pz - cz) * (pz - cz));
    }

    public static float SmoothFalloff(float dist, float radius, float maxStrength)
    {
        if (dist >= radius) return 0f;
        float t = 1f - dist / radius;
        return t * t * (3f - 2f * t) * maxStrength;
    }
}

namespace VintageVoxel;

/// <summary>
/// Generates buildings and infrastructure blocks for settlements.
/// All methods are deterministic — given a world position plus the settlement
/// context from <see cref="SettlementMap"/>, they always produce the same blocks.
///
/// Block IDs used:
///   9  = Oak Planks        30 = Dirt Path        37 = Terracotta
///  10  = Cobblestone       31 = Stone Bricks     38 = Red Terracotta
///  11  = Glass             32 = Bricks           39 = Brown Terracotta
///  33  = Spruce Planks     34 = Farmland         40 = Smooth Stone
///  35  = Hay Block         36 = White Wool       41 = Bookshelf
///  42  = Birch Planks      43 = Dark Oak Planks  44 = Andesite
///  45  = Polished Andesite
/// </summary>
public static class BuildingGenerator
{
    // ------------------------------------------------------------------
    // Block IDs (matching blocks.json)
    // ------------------------------------------------------------------

    private const ushort Air = 0;
    private const ushort Dirt = 1;
    private const ushort Stone = 2;
    private const ushort OakPlanks = 9;
    private const ushort Cobblestone = 10;
    private const ushort Glass = 11;
    private const ushort DirtPath = 30;
    private const ushort StoneBricks = 31;
    private const ushort Bricks = 32;
    private const ushort SprucePlanks = 33;
    private const ushort Farmland = 34;
    private const ushort HayBlock = 35;
    private const ushort WhiteWool = 36;
    private const ushort Terracotta = 37;
    private const ushort RedTerracotta = 38;
    private const ushort BrownTerracotta = 39;
    private const ushort SmoothStone = 40;
    private const ushort Bookshelf = 41;
    private const ushort BirchPlanks = 42;
    private const ushort DarkOakPlanks = 43;
    private const ushort Andesite = 44;
    private const ushort PolishedAndesite = 45;
    private const ushort OakLog = 7;

    // ------------------------------------------------------------------
    // Public API — called per-column during chunk generation
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the block that should be placed at world position (wx, wy, wz)
    /// for the given structure type. Returns null if no structure block is needed
    /// (the caller should keep the terrain block).
    /// <paramref name="surfaceY"/> is the (flattened) terrain surface height.
    /// </summary>
    public static Block? GetStructureBlock(int wx, int wy, int wz,
        SettlementMap.StructureType structure, int surfaceY, SettlementMap.ZoneType zone)
    {
        return structure switch
        {
            SettlementMap.StructureType.InternalRoad => GetRoadBlock(wx, wy, wz, surfaceY, zone),
            SettlementMap.StructureType.Field => GetFieldBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.VillageHouse => GetVillageHouseBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.VillageLargeHouse => GetVillageLargeHouseBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.CityHouse => GetCityHouseBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.Shop => GetShopBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.Apartment => GetApartmentBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.TallApartment => GetTallApartmentBlock(wx, wy, wz, surfaceY),
            SettlementMap.StructureType.Park => GetParkBlock(wx, wy, wz, surfaceY),
            _ => null,
        };
    }

    /// <summary>
    /// Returns the block for inter-settlement roads (main and secondary).
    /// Roads follow terrain with smooth layering for a polished look.
    /// </summary>
    public static Block? GetInterRoadBlock(int wx, int wy, int wz,
        int surfaceY, byte surfaceLayer, bool isMainRoad)
    {
        // Road surface: replace the surface block
        if (wy == surfaceY)
        {
            ushort id = isMainRoad ? Cobblestone : DirtPath;
            return new Block { Id = id, IsTransparent = false, Layer = surfaceLayer };
        }

        // Clear above road
        if (wy > surfaceY && wy <= surfaceY + 4)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Internal roads within settlements
    // ------------------------------------------------------------------

    private static Block? GetRoadBlock(int wx, int wy, int wz, int surfaceY, SettlementMap.ZoneType zone)
    {
        if (wy == surfaceY)
        {
            ushort id = zone == SettlementMap.ZoneType.City ? StoneBricks : Cobblestone;
            return new Block { Id = id, IsTransparent = false, Layer = 16 };
        }

        // Clear 4 blocks above road for walkability
        if (wy > surfaceY && wy <= surfaceY + 4)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Fields (village outskirts)
    // ------------------------------------------------------------------

    private static Block? GetFieldBlock(int wx, int wy, int wz, int surfaceY)
    {
        if (wy != surfaceY) return wy > surfaceY && wy <= surfaceY + 3 ? Block.Air : (Block?)null;

        // Alternating rows of farmland and hay bales
        int row = FloorMod(wz, 4);
        if (row < 3)
            return new Block { Id = Farmland, IsTransparent = false, Layer = 16 };

        // Every 4th row is a path between fields
        return new Block { Id = DirtPath, IsTransparent = false, Layer = 14 };
    }

    // ------------------------------------------------------------------
    // Village house — small 5x5 wooden house with peaked roof
    // ------------------------------------------------------------------

    private static Block? GetVillageHouseBlock(int wx, int wy, int wz, int surfaceY)
    {
        // Local coordinates within the 9x9 building plot (offset by 3 from grid edge)
        int lx = FloorMod(wx, 12) - 3;
        int lz = FloorMod(wz, 12) - 3;
        int ly = wy - surfaceY; // height above surface

        // Only generate within 7x7 footprint centred in plot
        if (lx < 1 || lx > 7 || lz < 1 || lz > 7)
            return ly > 0 && ly <= 8 ? Block.Air : (Block?)null;

        // Use hash for material variation
        int plotX = FloorDiv(wx, 12);
        int plotZ = FloorDiv(wz, 12);
        uint h = PlotHash(plotX, plotZ);
        ushort wallBlock = (h % 3) switch { 0 => OakPlanks, 1 => BirchPlanks, _ => SprucePlanks };
        ushort roofBlock = (h % 2) == 0 ? BrownTerracotta : RedTerracotta;

        bool isWall = lx == 1 || lx == 7 || lz == 1 || lz == 7;
        bool isCorner = (lx == 1 || lx == 7) && (lz == 1 || lz == 7);
        bool isInterior = lx > 1 && lx < 7 && lz > 1 && lz < 7;

        // Foundation
        if (ly == 0)
            return new Block { Id = Cobblestone, IsTransparent = false, Layer = 16 };

        // Walls (3 blocks high)
        if (ly >= 1 && ly <= 3 && isWall)
        {
            // Windows on the 2nd layer, not on corners
            if (ly == 2 && !isCorner && ((lx == 1 || lx == 7) ? (lz == 4) : (lx == 4)))
                return new Block { Id = Glass, IsTransparent = true, Layer = 16 };

            // Door opening on south wall
            if (lz == 1 && lx == 4 && ly <= 2)
                return Block.Air;

            if (isCorner)
                return new Block { Id = OakLog, IsTransparent = false, Layer = 16 };

            return new Block { Id = wallBlock, IsTransparent = false, Layer = 16 };
        }

        // Floor (inside at ground level already handled by foundation)

        // Ceiling / flat roof base at ly == 4
        if (ly == 4 && lx >= 1 && lx <= 7 && lz >= 1 && lz <= 7)
            return new Block { Id = wallBlock, IsTransparent = false, Layer = 16 };

        // Peaked roof: ly 5-6
        if (ly == 5)
        {
            int roofInset = 1;
            if (lx >= 1 + roofInset && lx <= 7 - roofInset && lz >= 1 + roofInset && lz <= 7 - roofInset)
                return new Block { Id = roofBlock, IsTransparent = false, Layer = 16 };
        }
        if (ly == 6)
        {
            int roofInset = 2;
            if (lx >= 1 + roofInset && lx <= 7 - roofInset && lz >= 1 + roofInset && lz <= 7 - roofInset)
                return new Block { Id = roofBlock, IsTransparent = false, Layer = 16 };
        }

        // Clear above
        if (ly > 0 && ly <= 8)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Village large house — 9x9 two-storey
    // ------------------------------------------------------------------

    private static Block? GetVillageLargeHouseBlock(int wx, int wy, int wz, int surfaceY)
    {
        int lx = FloorMod(wx, 12) - 3;
        int lz = FloorMod(wz, 12) - 3;
        int ly = wy - surfaceY;

        if (lx < 0 || lx > 8 || lz < 0 || lz > 8)
            return ly > 0 && ly <= 12 ? Block.Air : (Block?)null;

        int plotX = FloorDiv(wx, 12);
        int plotZ = FloorDiv(wz, 12);
        uint h = PlotHash(plotX, plotZ);
        ushort wallBlock = (h % 3) switch { 0 => OakPlanks, 1 => SprucePlanks, _ => DarkOakPlanks };
        ushort roofBlock = (h % 2) == 0 ? Terracotta : RedTerracotta;

        bool isWall = lx == 0 || lx == 8 || lz == 0 || lz == 8;
        bool isCorner = (lx == 0 || lx == 8) && (lz == 0 || lz == 8);
        bool isInterior = lx > 0 && lx < 8 && lz > 0 && lz < 8;

        // Foundation
        if (ly == 0)
            return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

        // Walls (6 blocks high — two storeys)
        if (ly >= 1 && ly <= 6 && isWall)
        {
            // Windows
            if ((ly == 2 || ly == 5) && !isCorner)
            {
                bool isXWall = lx == 0 || lx == 8;
                int pos = isXWall ? lz : lx;
                if (pos == 3 || pos == 5)
                    return new Block { Id = Glass, IsTransparent = true, Layer = 16 };
            }

            // Door
            if (lz == 0 && lx == 4 && ly <= 2)
                return Block.Air;

            if (isCorner)
                return new Block { Id = OakLog, IsTransparent = false, Layer = 16 };

            return new Block { Id = wallBlock, IsTransparent = false, Layer = 16 };
        }

        // Second floor divider at ly == 4
        if (ly == 4 && isInterior)
            return new Block { Id = wallBlock, IsTransparent = false, Layer = 16 };

        // Ceiling at ly == 7
        if (ly == 7 && lx >= 0 && lx <= 8 && lz >= 0 && lz <= 8)
            return new Block { Id = wallBlock, IsTransparent = false, Layer = 16 };

        // Roof pyramid
        for (int roofLayer = 0; roofLayer < 4; roofLayer++)
        {
            if (ly == 8 + roofLayer)
            {
                int inset = roofLayer + 1;
                if (lx >= inset && lx <= 8 - inset && lz >= inset && lz <= 8 - inset)
                    return new Block { Id = roofBlock, IsTransparent = false, Layer = 16 };
            }
        }

        if (ly > 0 && ly <= 12)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // City house — brick with stone trim
    // ------------------------------------------------------------------

    private static Block? GetCityHouseBlock(int wx, int wy, int wz, int surfaceY)
    {
        int lx = FloorMod(wx, 12) - 3;
        int lz = FloorMod(wz, 12) - 3;
        int ly = wy - surfaceY;

        if (lx < 1 || lx > 7 || lz < 1 || lz > 7)
            return ly > 0 && ly <= 10 ? Block.Air : (Block?)null;

        int plotX = FloorDiv(wx, 12);
        int plotZ = FloorDiv(wz, 12);
        uint h = PlotHash(plotX, plotZ);

        // Foundation
        if (ly == 0)
            return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

        bool isWall = lx == 1 || lx == 7 || lz == 1 || lz == 7;
        bool isCorner = (lx == 1 || lx == 7) && (lz == 1 || lz == 7);

        // Walls (4 blocks)
        if (ly >= 1 && ly <= 4 && isWall)
        {
            if (ly == 2 && !isCorner)
            {
                bool isXWall = lx == 1 || lx == 7;
                int pos = isXWall ? lz : lx;
                if (pos == 3 || pos == 5)
                    return new Block { Id = Glass, IsTransparent = true, Layer = 16 };
            }

            if (lz == 1 && lx == 4 && ly <= 2)
                return Block.Air;

            if (isCorner)
                return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

            return new Block { Id = Bricks, IsTransparent = false, Layer = 16 };
        }

        // Flat roof
        if (ly == 5 && lx >= 1 && lx <= 7 && lz >= 1 && lz <= 7)
            return new Block { Id = SmoothStone, IsTransparent = false, Layer = 16 };

        if (ly > 0 && ly <= 10)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Shop — ground floor with large windows
    // ------------------------------------------------------------------

    private static Block? GetShopBlock(int wx, int wy, int wz, int surfaceY)
    {
        int lx = FloorMod(wx, 12) - 3;
        int lz = FloorMod(wz, 12) - 3;
        int ly = wy - surfaceY;

        if (lx < 1 || lx > 7 || lz < 1 || lz > 7)
            return ly > 0 && ly <= 8 ? Block.Air : (Block?)null;

        int plotX = FloorDiv(wx, 12);
        int plotZ = FloorDiv(wz, 12);
        uint h = PlotHash(plotX, plotZ);

        // Foundation
        if (ly == 0)
            return new Block { Id = PolishedAndesite, IsTransparent = false, Layer = 16 };

        bool isWall = lx == 1 || lx == 7 || lz == 1 || lz == 7;
        bool isCorner = (lx == 1 || lx == 7) && (lz == 1 || lz == 7);

        // Shop walls — 4 blocks with large front windows
        if (ly >= 1 && ly <= 4 && isWall)
        {
            // Large display windows on front (south) wall
            if (lz == 1 && lx >= 3 && lx <= 5 && ly <= 3)
                return new Block { Id = Glass, IsTransparent = true, Layer = 16 };

            // Door
            if (lz == 1 && lx == 4 && ly <= 2)
                return Block.Air;

            // Side windows
            if (ly == 2 && !isCorner && lz != 1)
            {
                bool isXWall = lx == 1 || lx == 7;
                int pos = isXWall ? lz : lx;
                if (pos == 4)
                    return new Block { Id = Glass, IsTransparent = true, Layer = 16 };
            }

            if (isCorner)
                return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

            return new Block { Id = Bricks, IsTransparent = false, Layer = 16 };
        }

        // Interior: counter at back
        if (ly == 1 && lz == 6 && lx >= 3 && lx <= 5)
            return new Block { Id = SmoothStone, IsTransparent = false, Layer = 8 };

        // Flat roof
        if (ly == 5 && lx >= 1 && lx <= 7 && lz >= 1 && lz <= 7)
            return new Block { Id = SmoothStone, IsTransparent = false, Layer = 16 };

        if (ly > 0 && ly <= 8)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Apartment — 3 storey brick building
    // ------------------------------------------------------------------

    private static Block? GetApartmentBlock(int wx, int wy, int wz, int surfaceY)
    {
        int lx = FloorMod(wx, 12) - 3;
        int lz = FloorMod(wz, 12) - 3;
        int ly = wy - surfaceY;

        if (lx < 0 || lx > 8 || lz < 0 || lz > 8)
            return ly > 0 && ly <= 14 ? Block.Air : (Block?)null;

        int plotX = FloorDiv(wx, 12);
        int plotZ = FloorDiv(wz, 12);
        uint h = PlotHash(plotX, plotZ);

        const int storeys = 3;
        const int storeyHeight = 4;
        int totalHeight = storeys * storeyHeight;

        // Foundation
        if (ly == 0)
            return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

        bool isWall = lx == 0 || lx == 8 || lz == 0 || lz == 8;
        bool isCorner = (lx == 0 || lx == 8) && (lz == 0 || lz == 8);
        bool isInterior = lx > 0 && lx < 8 && lz > 0 && lz < 8;

        // Walls
        if (ly >= 1 && ly <= totalHeight && isWall)
        {
            int inStorey = ((ly - 1) % storeyHeight) + 1;

            // Windows on 2nd and 3rd block of each storey
            if ((inStorey == 2 || inStorey == 3) && !isCorner)
            {
                bool isXWall = lx == 0 || lx == 8;
                int pos = isXWall ? lz : lx;
                if (pos == 2 || pos == 4 || pos == 6)
                    return new Block { Id = Glass, IsTransparent = true, Layer = 16 };
            }

            // Ground floor door
            if (ly <= 2 && lz == 0 && lx == 4)
                return Block.Air;

            if (isCorner)
                return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

            return new Block { Id = Bricks, IsTransparent = false, Layer = 16 };
        }

        // Floor dividers
        for (int s = 1; s < storeys; s++)
        {
            if (ly == s * storeyHeight + 1 && isInterior)
                return new Block { Id = SmoothStone, IsTransparent = false, Layer = 16 };
        }

        // Flat roof
        if (ly == totalHeight + 1 && lx >= 0 && lx <= 8 && lz >= 0 && lz <= 8)
            return new Block { Id = Andesite, IsTransparent = false, Layer = 16 };

        if (ly > 0 && ly <= totalHeight + 2)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Tall apartment — 5 storey
    // ------------------------------------------------------------------

    private static Block? GetTallApartmentBlock(int wx, int wy, int wz, int surfaceY)
    {
        int lx = FloorMod(wx, 12) - 3;
        int lz = FloorMod(wz, 12) - 3;
        int ly = wy - surfaceY;

        if (lx < 0 || lx > 8 || lz < 0 || lz > 8)
            return ly > 0 && ly <= 24 ? Block.Air : (Block?)null;

        int plotX = FloorDiv(wx, 12);
        int plotZ = FloorDiv(wz, 12);
        uint h = PlotHash(plotX, plotZ);

        const int storeys = 5;
        const int storeyHeight = 4;
        int totalHeight = storeys * storeyHeight;

        // Foundation
        if (ly == 0)
            return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

        bool isWall = lx == 0 || lx == 8 || lz == 0 || lz == 8;
        bool isCorner = (lx == 0 || lx == 8) && (lz == 0 || lz == 8);
        bool isInterior = lx > 0 && lx < 8 && lz > 0 && lz < 8;

        if (ly >= 1 && ly <= totalHeight && isWall)
        {
            int inStorey = ((ly - 1) % storeyHeight) + 1;

            if ((inStorey == 2 || inStorey == 3) && !isCorner)
            {
                bool isXWall = lx == 0 || lx == 8;
                int pos = isXWall ? lz : lx;
                if (pos == 2 || pos == 4 || pos == 6)
                    return new Block { Id = Glass, IsTransparent = true, Layer = 16 };
            }

            if (ly <= 2 && lz == 0 && lx == 4)
                return Block.Air;

            if (isCorner)
                return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

            // First floor: stone bricks, upper floors: bricks
            if (ly <= storeyHeight)
                return new Block { Id = StoneBricks, IsTransparent = false, Layer = 16 };

            return new Block { Id = Bricks, IsTransparent = false, Layer = 16 };
        }

        // Floor dividers
        for (int s = 1; s < storeys; s++)
        {
            if (ly == s * storeyHeight + 1 && isInterior)
                return new Block { Id = SmoothStone, IsTransparent = false, Layer = 16 };
        }

        // Flat roof
        if (ly == totalHeight + 1 && lx >= 0 && lx <= 8 && lz >= 0 && lz <= 8)
            return new Block { Id = Andesite, IsTransparent = false, Layer = 16 };

        if (ly > 0 && ly <= totalHeight + 2)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Park — open grass area with occasional trees (trees handled by Chunk)
    // ------------------------------------------------------------------

    private static Block? GetParkBlock(int wx, int wy, int wz, int surfaceY)
    {
        // Parks just clear above the surface — grass is the default surface block
        if (wy > surfaceY && wy <= surfaceY + 4)
            return Block.Air;

        return null;
    }

    // ------------------------------------------------------------------
    // Helpers
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

    private static uint PlotHash(int px, int pz)
    {
        uint h = (uint)(px * 374761393 ^ pz * 668265263 ^ 99991);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return h;
    }
}

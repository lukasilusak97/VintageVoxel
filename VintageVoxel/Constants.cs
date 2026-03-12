namespace VintageVoxel;

// --------------------------------------------------------------------------
// Slope system types (declared before GameConstants so any file can use them).
// --------------------------------------------------------------------------

/// <summary>
/// The 13 block geometry shapes used by the slope system.
/// Shape = 0 (Cube) is the default for all existing blocks.
/// Ramp directions indicate which side of the block is HIGH — e.g. RampN means
/// the north face (-Z side) is flush with the block top while the south end is at Y=0.
/// </summary>
public enum SlopeShape : byte
{
    Cube          = 0,
    RampN         = 1,  // high on north side (-Z), low on south (+Z)
    RampS         = 2,  // high on south side (+Z), low on north (-Z)
    RampE         = 3,  // high on east  side (+X), low on west  (-X)
    RampW         = 4,  // high on west  side (-X), low on east  (+X)
    OuterCornerNE = 5,
    OuterCornerNW = 6,
    OuterCornerSE = 7,
    OuterCornerSW = 8,
    InnerCornerNE = 9,
    InnerCornerNW = 10,
    InnerCornerSE = 11,
    InnerCornerSW = 12,
}

/// <summary>
/// Per-shape geometry helpers shared by the mesh builder and the collision system.
/// Local coordinates lx, lz are in [0, 1] within the block.
/// Returned height is in [0, 1] where 0 = block bottom, 1 = full block top.
/// </summary>
public static class SlopeGeometry
{
    /// <summary>
    /// Returns the surface height [0,1] at local block position (lx, lz) ∈ [0,1]².
    /// For cube-shaped blocks this always returns 1.
    /// </summary>
    public static float HeightAt(SlopeShape shape, float lx, float lz)
        => shape switch
        {
            SlopeShape.Cube          => 1f,
            // Ramps: high at the side AWAY from the drop.
            // SlopePlacer places RampN when the north neighbour is lower →
            // the block slopes DOWN to north, so it is HIGH at south (lz = 1).
            SlopeShape.RampN         => lz,               // high at south (lz=1), low at north (lz=0)
            SlopeShape.RampS         => 1f - lz,          // high at north (lz=0), low at south (lz=1)
            SlopeShape.RampE         => 1f - lx,          // high at west  (lx=0), low at east  (lx=1)
            SlopeShape.RampW         => lx,               // high at east  (lx=1), low at west  (lx=0)
            // Outer corners: N+E drop → single high corner at SW, etc.
            SlopeShape.OuterCornerNE => MathF.Min(lz, 1f - lx),
            SlopeShape.OuterCornerNW => MathF.Min(lz, lx),
            SlopeShape.OuterCornerSE => MathF.Min(1f - lz, 1f - lx),
            SlopeShape.OuterCornerSW => MathF.Min(1f - lz, lx),
            // Inner corners: notch cut at the dropped diagonal, high everywhere else.
            SlopeShape.InnerCornerNE => MathF.Min(1f, lz + (1f - lx)),
            SlopeShape.InnerCornerNW => MathF.Min(1f, lz + lx),
            SlopeShape.InnerCornerSE => MathF.Min(1f, (1f - lz) + (1f - lx)),
            SlopeShape.InnerCornerSW => MathF.Min(1f, (1f - lz) + lx),
            _                        => 1f,
        };

    // Face indices: 0=Top, 1=Bottom, 2=North(-Z), 3=South(+Z), 4=West(-X), 5=East(+X)

    /// <summary>
    /// Returns true when the given face of a slope block is fully flush with the
    /// block boundary — i.e. it completely occludes the neighbour's opposite face.
    /// Used by the mesh builder for face-culling across slope/cube boundaries.
    /// </summary>
    public static bool IsFaceSolid(SlopeShape shape, int face)
    {
        if (shape == SlopeShape.Cube) return true;
        return (shape, face) switch
        {
            (_, 1)                        => true,  // bottom always solid
            // Ramps: the HIGH side is flush with the full block face → solid.
            (SlopeShape.RampN, 3)         => true,  // south face solid (high side of RampN)
            (SlopeShape.RampS, 2)         => true,  // north face solid
            (SlopeShape.RampE, 4)         => true,  // west face solid
            (SlopeShape.RampW, 5)         => true,  // east face solid
            // Inner corners: two faces on the high sides are fully flush.
            (SlopeShape.InnerCornerNE, 3) => true,  // south solid
            (SlopeShape.InnerCornerNE, 4) => true,  // west solid
            (SlopeShape.InnerCornerNW, 3) => true,  // south solid
            (SlopeShape.InnerCornerNW, 5) => true,  // east solid
            (SlopeShape.InnerCornerSE, 2) => true,  // north solid
            (SlopeShape.InnerCornerSE, 4) => true,  // west solid
            (SlopeShape.InnerCornerSW, 2) => true,  // north solid
            (SlopeShape.InnerCornerSW, 5) => true,  // east solid
            _                             => false,
        };
    }
}

// --------------------------------------------------------------------------
// General game constants
// --------------------------------------------------------------------------

/// <summary>
/// Domain-organized constants used across multiple subsystems.
/// Centralising them here prevents duplication and gives callers a single
/// authoritative source for tuning values.
/// </summary>
public static class GameConstants
{
    /// <summary>Player physics and AABB dimensions.</summary>
    public static class Physics
    {
        /// <summary>Downward gravitational acceleration (world units/s²).</summary>
        public const float Gravity = -25f;

        /// <summary>Initial upward velocity applied when the player jumps.</summary>
        public const float JumpSpeed = 8f;

        /// <summary>Maximum downward speed — terminal velocity cap.</summary>
        public const float MaxFallSpeed = 60f;

        /// <summary>Horizontal walk speed in survival mode (world units/s).</summary>
        public const float SurvivalMoveSpeed = 5f;

        /// <summary>Eye position above the player's feet (world units).</summary>
        public const float EyeHeight = 1.7f;

        /// <summary>Half-extent of the player AABB in X and Z (world units).</summary>
        public const float PlayerHalfWidth = 0.3f;

        /// <summary>Total player height (world units).</summary>
        public const float PlayerHeight = 1.8f;
    }

    /// <summary>Light propagation limits.</summary>
    public static class Light
    {
        /// <summary>Maximum sunlight level (sky-lit voxels receive this).</summary>
        public const byte MaxSunLight = 15;

        /// <summary>Maximum block-light level emitted by light sources such as torches.</summary>
        public const byte MaxBlockLight = 14;
    }

    /// <summary>HUD layout constants (all in screen pixels).</summary>
    public static class Render
    {
        /// <summary>Width and height of one hotbar slot.</summary>
        public const int HotbarSlotSize = 50;

        /// <summary>Gap between adjacent hotbar slots.</summary>
        public const int HotbarSlotGap = 4;

        /// <summary>Distance from the screen bottom to the slot edge.</summary>
        public const int HotbarBottomPad = 6;
    }
}


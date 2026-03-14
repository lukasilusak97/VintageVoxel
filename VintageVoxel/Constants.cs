namespace VintageVoxel;

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

        /// <summary>Maximum height the player can step up without jumping (8 layers = 0.5 blocks).</summary>
        public const float StepHeight = 0.5f;

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


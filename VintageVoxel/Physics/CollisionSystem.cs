using OpenTK.Mathematics;

namespace VintageVoxel.Physics;

/// <summary>
/// Stateless AABB collision queries against the voxel world.
/// All player dimension constants live here so they are co-located with
/// the code that uses them (and can be promoted to Constants.cs in Phase 6).
/// </summary>
public static class CollisionSystem
{
    // Player AABB dimensions live in GameConstants.Physics (Constants.cs).
    private static float EyeHeight => GameConstants.Physics.EyeHeight;
    private static float PlayerHalfWidth => GameConstants.Physics.PlayerHalfWidth;
    private static float PlayerHeight => GameConstants.Physics.PlayerHeight;

    /// <summary>
    /// Returns true when the player's AABB, centred on <paramref name="eyePos"/>
    /// (eye-level position), overlaps at least one solid block in the world.
    ///
    /// The AABB spans:
    ///   X: [eyePos.X - PlayerHalfWidth, eyePos.X + PlayerHalfWidth]
    ///   Y: [eyePos.Y - EyeHeight,       eyePos.Y - EyeHeight + PlayerHeight]
    ///   Z: [eyePos.Z - PlayerHalfWidth, eyePos.Z + PlayerHalfWidth]
    /// </summary>
    public static bool IsCollidingAt(World world, Vector3 eyePos)
    {
        float feetY = eyePos.Y - EyeHeight;

        // Treat the region below world Y = 0 as solid bedrock so the player
        // cannot fall out of the world.
        if (feetY < 0f) return true;

        // The small epsilon on the max extent prevents a player standing exactly
        // on a block edge from being considered inside the next block.
        int minX = (int)MathF.Floor(eyePos.X - PlayerHalfWidth);
        int maxX = (int)MathF.Floor(eyePos.X + PlayerHalfWidth - 0.001f);
        int minY = (int)MathF.Floor(feetY);
        int maxY = (int)MathF.Floor(feetY + PlayerHeight - 0.001f);
        int minZ = (int)MathF.Floor(eyePos.Z - PlayerHalfWidth);
        int maxZ = (int)MathF.Floor(eyePos.Z + PlayerHalfWidth - 0.001f);

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                    if (!world.GetBlock(x, y, z).IsEmpty)
                        return true;

        return false;
    }

    /// <summary>
    /// Returns true when the player is resting on solid ground (a tiny downward
    /// step detects floor contact so jumping is only allowed when grounded).
    /// </summary>
    public static bool IsOnGround(World world, Vector3 eyePos)
        => IsCollidingAt(world, new Vector3(eyePos.X, eyePos.Y - 0.05f, eyePos.Z));
}

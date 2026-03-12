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
    /// Returns the surface Y (world-space) for an entity standing at world XZ.
    /// For slope blocks the height is interpolated using SlopeGeometry.HeightAt;
    /// for full-cube blocks it is simply blockY + 1.
    /// Returns 0 when no solid block is found in the column (above world floor).
    /// </summary>
    public static float GetSurfaceHeightAt(float wx, float wz, World world)
    {
        // Scan downward from a reasonable ceiling to find the top solid block.
        int bx = (int)MathF.Floor(wx);
        int bz = (int)MathF.Floor(wz);

        for (int by = Chunk.Size - 1; by >= 0; by--)
        {
            Block block = world.GetBlock(bx, by, bz);
            if (block.IsEmpty) continue;

            if (block.IsSlope)
            {
                float lx = wx - bx;   // local [0,1] within block
                float lz = wz - bz;
                return by + SlopeGeometry.HeightAt((SlopeShape)block.Shape, lx, lz);
            }

            return by + 1f;
        }
        return 0f;
    }

    /// <summary>
    /// Returns true when the player's AABB, centred on <paramref name="eyePos"/>
    /// (eye-level position), overlaps at least one solid block in the world.
    ///
    /// For slope blocks, collision is triggered when the player's foot Y is below
    /// the surface height at the player's XZ position within that block.
    /// </summary>
    public static bool IsCollidingAt(World world, Vector3 eyePos)
    {
        float feetY = eyePos.Y - EyeHeight;

        if (feetY < 0f) return true;

        int minX = (int)MathF.Floor(eyePos.X - PlayerHalfWidth);
        int maxX = (int)MathF.Floor(eyePos.X + PlayerHalfWidth - 0.001f);
        int minY = (int)MathF.Floor(feetY);
        int maxY = (int)MathF.Floor(feetY + PlayerHeight - 0.001f);
        int minZ = (int)MathF.Floor(eyePos.Z - PlayerHalfWidth);
        int maxZ = (int)MathF.Floor(eyePos.Z + PlayerHalfWidth - 0.001f);

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    Block block = world.GetBlock(x, y, z);
                    if (block.IsEmpty) continue;

                    if (block.IsSlope)
                    {
                        // For slope blocks only the layer at the player's feet matters.
                        // Check: is the player foot below the slope surface at their XZ?
                        // Clamp the local coord to [0,1] in case the player straddles
                        // multiple blocks.
                        float lx = Math.Clamp(eyePos.X - x, 0f, 1f);
                        float lz = Math.Clamp(eyePos.Z - z, 0f, 1f);
                        float surfaceY = y + SlopeGeometry.HeightAt((SlopeShape)block.Shape, lx, lz);
                        if (feetY < surfaceY)
                            return true;
                    }
                    else
                    {
                        return true;
                    }
                }

        return false;
    }

    /// <summary>
    /// Returns true when the player is resting on solid ground (a tiny downward
    /// step detects floor contact so jumping is only allowed when grounded).
    /// </summary>
    public static bool IsOnGround(World world, Vector3 eyePos)
        => IsCollidingAt(world, new Vector3(eyePos.X, eyePos.Y - 0.05f, eyePos.Z));
}

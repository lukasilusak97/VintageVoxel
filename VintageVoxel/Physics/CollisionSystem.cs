using OpenTK.Mathematics;

namespace VintageVoxel.Physics;

/// <summary>
/// Stateless AABB collision queries against the voxel world.
/// </summary>
public static class CollisionSystem
{
    private static float EyeHeight => GameConstants.Physics.EyeHeight;
    private static float PlayerHalfWidth => GameConstants.Physics.PlayerHalfWidth;
    private static float PlayerHeight => GameConstants.Physics.PlayerHeight;

    /// <summary>
    /// Returns the surface Y (world-space) for an entity standing at world XZ.
    /// For partial-layer blocks the height is blockY + layer/16;
    /// for full-cube blocks it is simply blockY + 1.
    /// Returns 0 when no solid block is found in the column.
    /// </summary>
    public static float GetSurfaceHeightAt(float wx, float wz, World world)
    {
        int bx = (int)MathF.Floor(wx);
        int bz = (int)MathF.Floor(wz);

        for (int by = Chunk.Size - 1; by >= 0; by--)
        {
            Block block = world.GetBlock(bx, by, bz);
            if (block.IsEmpty) continue;
            if (BlockRegistry.IsCrossModel(block.Id)) continue;

            return by + block.TopOffset;
        }
        return 0f;
    }

    /// <summary>
    /// Returns true when the player's AABB, centred on <paramref name="eyePos"/>
    /// (eye-level position), overlaps at least one solid block in the world.
    ///
    /// For partial-layer blocks, collision is triggered when the player's foot Y
    /// is below the block's layer surface height.
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
                    if (BlockRegistry.IsCrossModel(block.Id)) continue;

                    if (block.IsPartial)
                    {
                        float surfaceY = y + block.TopOffset;
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
    /// When horizontal movement causes a collision, determines whether the
    /// obstacle can be stepped over (surface within <paramref name="maxStepHeight"/>).
    /// Returns the new eye-level Y if the step-up is possible, or null when
    /// the obstacle is too tall or there is not enough headroom above.
    /// </summary>
    public static float? TryGetStepUpEyeY(World world, Vector3 eyePos, float maxStepHeight)
    {
        float feetY = eyePos.Y - EyeHeight;

        int minX = (int)MathF.Floor(eyePos.X - PlayerHalfWidth);
        int maxX = (int)MathF.Floor(eyePos.X + PlayerHalfWidth - 0.001f);
        int minZ = (int)MathF.Floor(eyePos.Z - PlayerHalfWidth);
        int maxZ = (int)MathF.Floor(eyePos.Z + PlayerHalfWidth - 0.001f);

        float targetSurface = feetY;
        int scanMinY = (int)MathF.Floor(feetY);
        int scanMaxY = (int)MathF.Floor(feetY + maxStepHeight);

        for (int y = scanMinY; y <= scanMaxY; y++)
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                {
                    Block block = world.GetBlock(x, y, z);
                    if (block.IsEmpty) continue;
                    if (BlockRegistry.IsCrossModel(block.Id)) continue;

                    float surface = y + block.TopOffset;
                    if (surface > feetY && surface <= feetY + maxStepHeight && surface > targetSurface)
                        targetSurface = surface;
                }

        if (targetSurface <= feetY)
            return null;

        float newEyeY = targetSurface + EyeHeight;
        if (IsCollidingAt(world, new Vector3(eyePos.X, newEyeY, eyePos.Z)))
            return null;

        return newEyeY;
    }

    /// <summary>
    /// Returns true when the player is resting on solid ground.
    /// </summary>
    public static bool IsOnGround(World world, Vector3 eyePos)
        => IsCollidingAt(world, new Vector3(eyePos.X, eyePos.Y - 0.05f, eyePos.Z));
}

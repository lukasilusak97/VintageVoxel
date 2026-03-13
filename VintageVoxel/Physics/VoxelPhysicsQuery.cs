namespace VintageVoxel.Physics;

/// <summary>
/// Queries the live <see cref="World"/> to determine whether a continuous world
/// position falls inside solid voxel geometry, respecting the 16-layer vertical
/// subdivision per block.
/// </summary>
public sealed class VoxelPhysicsQuery : IVoxelPhysicsQuery
{
    private readonly World _world;

    public VoxelPhysicsQuery(World world) => _world = world;

    /// <inheritdoc/>
    public bool IsSolid(System.Numerics.Vector3 worldPosition)
    {
        // Floor to block coordinates. MathF.Floor handles negatives correctly
        // (e.g. -0.1 → -1 rather than 0).
        int bx = (int)MathF.Floor(worldPosition.X);
        int by = (int)MathF.Floor(worldPosition.Y);
        int bz = (int)MathF.Floor(worldPosition.Z);

        Block block = _world.GetBlock(bx, by, bz);

        if (block.IsEmpty)
            return false;

        if (block.IsFullBlock)
            return true;

        // Partial block: solid only within the filled layers (bottom-up).
        // The block's solid ceiling is at by + Layer/16.
        float fractionalY = worldPosition.Y - by;
        return fractionalY < block.TopOffset;
    }
}

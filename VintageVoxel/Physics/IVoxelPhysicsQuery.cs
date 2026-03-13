namespace VintageVoxel.Physics;

/// <summary>
/// Queries solid voxel data at arbitrary world positions.
/// Used by the vehicle physics system (BepuPhysics v2) to detect ground surfaces,
/// generate collision geometry, and perform DDA raycasts against the voxel grid.
/// </summary>
public interface IVoxelPhysicsQuery
{
    /// <summary>
    /// Returns true if the given world position is inside solid voxel geometry.
    /// Accounts for the 16-layer vertical subdivision: a block with Layer = 8
    /// is only solid in the bottom half of its cell.
    /// </summary>
    bool IsSolid(System.Numerics.Vector3 worldPosition);
}

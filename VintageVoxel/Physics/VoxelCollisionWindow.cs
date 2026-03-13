using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;

namespace VintageVoxel.Physics;

/// <summary>
/// Maintains a pool of Bepu v2 <see cref="StaticHandle"/>s that are repositioned
/// each time the tracked centre moves to a new block.  Solid voxels within
/// <see cref="ScanRadius"/> are mapped to pooled statics whose pose and shape
/// match the block's layer height; unused statics are hidden far underground.
///
/// This avoids the cost of Add/Remove every frame — only Pose + Shape updates
/// are performed, which is cheap in Bepu v2's broad phase.
/// </summary>
public sealed class VoxelCollisionWindow : IDisposable
{
    private readonly Simulation _simulation;
    private readonly World _world;

    private readonly StaticHandle[] _pool;
    private readonly int _poolSize;

    // 16 box shapes: index 0 → height 1/16, index 15 → height 1 (full block).
    private readonly TypedIndex[] _layerShapes;

    /// <summary>Half-extent of the scan cube in blocks around the centre.</summary>
    public int ScanRadius { get; set; } = 3;

    // Tracked centre block — skip rescan when unchanged.
    private int _lastBx = int.MinValue;
    private int _lastBy = int.MinValue;
    private int _lastBz = int.MinValue;

    private static readonly Vector3 HiddenPos = new(0, -9999, 0);

    /// <param name="simulation">The Bepu v2 simulation that owns all statics and shapes.</param>
    /// <param name="world">The voxel world used for block queries.</param>
    /// <param name="poolSize">Maximum number of statics held in the pool.</param>
    public VoxelCollisionWindow(Simulation simulation, World world, int poolSize = 2048)
    {
        _simulation = simulation;
        _world = world;
        _poolSize = poolSize;

        // --- pre-allocate 16 box shapes (one per possible layer count) -------
        _layerShapes = new TypedIndex[16];
        for (int i = 0; i < 16; i++)
        {
            float height = (i + 1) / 16f;
            _layerShapes[i] = simulation.Shapes.Add(new Box(1f, height, 1f));
        }

        // --- pre-allocate the static pool, all hidden underground ------------
        _pool = new StaticHandle[poolSize];
        var hiddenDesc = new StaticDescription(new RigidPose(HiddenPos), _layerShapes[15]);
        for (int i = 0; i < poolSize; i++)
            _pool[i] = simulation.Statics.Add(hiddenDesc);
    }

    /// <summary>
    /// Rescans the voxel neighbourhood around <paramref name="center"/> and
    /// repositions pooled statics to match the solid blocks found.
    /// Skips work when the centre block has not changed since the last call.
    /// </summary>
    public void Update(Vector3 center)
    {
        int bx = (int)MathF.Floor(center.X);
        int by = (int)MathF.Floor(center.Y);
        int bz = (int)MathF.Floor(center.Z);

        if (bx == _lastBx && by == _lastBy && bz == _lastBz)
            return;

        _lastBx = bx;
        _lastBy = by;
        _lastBz = bz;

        int idx = 0;
        int r = ScanRadius;

        for (int dx = -r; dx <= r && idx < _poolSize; dx++)
            for (int dy = -r; dy <= r && idx < _poolSize; dy++)
                for (int dz = -r; dz <= r && idx < _poolSize; dz++)
                {
                    int wx = bx + dx;
                    int wy = by + dy;
                    int wz = bz + dz;

                    Block block = _world.GetBlock(wx, wy, wz);
                    if (block.IsEmpty)
                        continue;

                    int layer = Math.Clamp((int)block.Layer, 1, 16);
                    int shapeIdx = layer - 1;
                    float height = layer / 16f;

                    // Bottom-aligned: box centre sits at wy + height/2.
                    var pose = new RigidPose(new Vector3(wx + 0.5f, wy + height * 0.5f, wz + 0.5f));
                    var desc = new StaticDescription(pose, _layerShapes[shapeIdx]);

                    _simulation.Statics.GetStaticReference(_pool[idx]).ApplyDescription(in desc);
                    idx++;
                }

        // Hide all remaining unused statics underground.
        if (idx < _poolSize)
        {
            var hiddenDesc = new StaticDescription(new RigidPose(HiddenPos), _layerShapes[15]);
            for (int i = idx; i < _poolSize; i++)
                _simulation.Statics.GetStaticReference(_pool[i]).ApplyDescription(in hiddenDesc);
        }
    }

    /// <summary>Forces a full rescan on the next <see cref="Update"/> call.</summary>
    public void Invalidate()
    {
        _lastBx = int.MinValue;
        _lastBy = int.MinValue;
        _lastBz = int.MinValue;
    }

    /// <summary>Removes all pooled statics and shapes from the simulation.</summary>
    public void Dispose()
    {
        for (int i = 0; i < _poolSize; i++)
            _simulation.Statics.Remove(_pool[i]);

        for (int i = 0; i < 16; i++)
            _simulation.Shapes.Remove(_layerShapes[i]);
    }
}

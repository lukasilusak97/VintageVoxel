using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Generic DDA (Digital Differential Analyzer) stepping state for voxel traversal,
/// following Amanatides &amp; Woo (1987).
///
/// Works at any grid resolution: pass cellSize=1 for world-space voxels, or
/// cellSize=1f/N for a sub-voxel grid of side length N.
///
/// Usage:
///   var dda = DdaTraversal.Initialize(localOrigin, dir, cellSize);
///   int ix = (int)MathF.Floor(localOrigin.X), ... ;
///   while (true) {
///       float t = dda.Step(ref ix, ref iy, ref iz);
///       if (t > maxDist || outOfBounds) break;
///       // dda.Normal is the outward face normal of the voxel just entered.
///   }
/// </summary>
public struct DdaTraversal
{
    /// <summary>Step direction per axis: +1, -1, or 0.</summary>
    public readonly int StepX, StepY, StepZ;

    /// <summary>t-parameter at the next axis-aligned boundary crossing per axis.</summary>
    public float TMaxX, TMaxY, TMaxZ;

    /// <summary>t-distance to traverse one full cell per axis.</summary>
    public readonly float TDeltaX, TDeltaY, TDeltaZ;

    /// <summary>Outward face normal of the voxel face the ray last entered through.</summary>
    public Vector3i Normal;

    private DdaTraversal(
        int stepX, int stepY, int stepZ,
        float tMaxX, float tMaxY, float tMaxZ,
        float tDeltaX, float tDeltaY, float tDeltaZ)
    {
        StepX = stepX; StepY = stepY; StepZ = stepZ;
        TMaxX = tMaxX; TMaxY = tMaxY; TMaxZ = tMaxZ;
        TDeltaX = tDeltaX; TDeltaY = tDeltaY; TDeltaZ = tDeltaZ;
        Normal = Vector3i.Zero;
    }

    /// <summary>
    /// Creates a DDA stepping state for a ray starting at
    /// <paramref name="localOrigin"/> (in local cell coordinates) with world-space
    /// direction <paramref name="dir"/>.
    ///
    /// <para>Each integer unit in local cell space corresponds to
    /// <paramref name="cellSize"/> world units, so t-values are in world space.</para>
    ///
    /// <para>For world voxels use localOrigin = world position, cellSize = 1f.<br/>
    /// For a 16×16×16 sub-grid use localOrigin = sub-voxel float coords [0,N),
    /// cellSize = 1f/N.</para>
    /// </summary>
    public static DdaTraversal Initialize(Vector3 localOrigin, Vector3 dir, float cellSize = 1f)
    {
        int stepX = Math.Sign(dir.X);
        int stepY = Math.Sign(dir.Y);
        int stepZ = Math.Sign(dir.Z);

        float tDeltaX = stepX != 0 ? cellSize / MathF.Abs(dir.X) : float.PositiveInfinity;
        float tDeltaY = stepY != 0 ? cellSize / MathF.Abs(dir.Y) : float.PositiveInfinity;
        float tDeltaZ = stepZ != 0 ? cellSize / MathF.Abs(dir.Z) : float.PositiveInfinity;

        float fractX = localOrigin.X - MathF.Floor(localOrigin.X);
        float fractY = localOrigin.Y - MathF.Floor(localOrigin.Y);
        float fractZ = localOrigin.Z - MathF.Floor(localOrigin.Z);

        float tMaxX = stepX != 0
            ? (stepX > 0 ? 1f - fractX : fractX) * cellSize / MathF.Abs(dir.X)
            : float.PositiveInfinity;
        float tMaxY = stepY != 0
            ? (stepY > 0 ? 1f - fractY : fractY) * cellSize / MathF.Abs(dir.Y)
            : float.PositiveInfinity;
        float tMaxZ = stepZ != 0
            ? (stepZ > 0 ? 1f - fractZ : fractZ) * cellSize / MathF.Abs(dir.Z)
            : float.PositiveInfinity;

        return new DdaTraversal(
            stepX, stepY, stepZ,
            tMaxX, tMaxY, tMaxZ,
            tDeltaX, tDeltaY, tDeltaZ);
    }

    /// <summary>
    /// Advances the DDA by one cell boundary crossing.
    /// Increments the appropriate coordinate in (<paramref name="ix"/>,
    /// <paramref name="iy"/>, <paramref name="iz"/>) and sets
    /// <see cref="Normal"/> to the outward face normal of the entered voxel.
    /// Returns the world-space t-parameter of the crossing.
    /// </summary>
    public float Step(ref int ix, ref int iy, ref int iz)
    {
        float t;
        int axis;

        if (TMaxX <= TMaxY && TMaxX <= TMaxZ) { t = TMaxX; axis = 0; }
        else if (TMaxY <= TMaxZ) { t = TMaxY; axis = 1; }
        else { t = TMaxZ; axis = 2; }

        switch (axis)
        {
            case 0: ix += StepX; TMaxX += TDeltaX; Normal = new Vector3i(-StepX, 0, 0); break;
            case 1: iy += StepY; TMaxY += TDeltaY; Normal = new Vector3i(0, -StepY, 0); break;
            default: iz += StepZ; TMaxZ += TDeltaZ; Normal = new Vector3i(0, 0, -StepZ); break;
        }

        return t;
    }
}

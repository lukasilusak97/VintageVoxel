namespace VintageVoxel.Physics;

/// <summary>
/// Extension methods for converting between OpenTK.Mathematics and System.Numerics vector types.
/// Bepu v2 uses System.Numerics; the rest of the engine uses OpenTK.Mathematics.
/// </summary>
public static class MathConversions
{
    public static System.Numerics.Vector3 ToNumerics(this OpenTK.Mathematics.Vector3 v)
        => new(v.X, v.Y, v.Z);

    public static OpenTK.Mathematics.Vector3 ToOpenTK(this System.Numerics.Vector3 v)
        => new(v.X, v.Y, v.Z);

    public static System.Numerics.Quaternion ToNumerics(this OpenTK.Mathematics.Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);

    public static OpenTK.Mathematics.Quaternion ToOpenTK(this System.Numerics.Quaternion q)
        => new(q.X, q.Y, q.Z, q.W);
}

using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Represents the camera view frustum as 6 clipping planes extracted via the
/// Gribb-Hartmann method from the combined view-projection matrix.
///
/// Each plane is stored as a <see cref="Vector4"/> (a, b, c, d) where the implicit
/// plane equation is: a·x + b·y + c·z + d ≥ 0 for points inside the frustum.
/// </summary>
public readonly struct Frustum
{
    private readonly Vector4 _left, _right, _bottom, _top, _near, _far;

    private Frustum(Vector4 l, Vector4 r, Vector4 b, Vector4 t, Vector4 n, Vector4 f)
    {
        _left = l; _right = r; _bottom = b; _top = t; _near = n; _far = f;
    }

    /// <summary>
    /// Builds the frustum from pre-computed view and projection matrices.
    ///
    /// OpenTK sends matrices to OpenGL with <c>transpose=false</c>, which means
    /// its row-major storage is reinterpreted by OpenGL as column-major.  The
    /// actual clip-space transform applied to a vertex is therefore:
    ///   clip = Transpose(view × projection) × worldPos
    ///
    /// Gribb-Hartmann plane extraction is applied to that transposed combined
    /// matrix so the planes sit correctly in world space.
    /// </summary>
    public static Frustum FromViewProjection(Matrix4 view, Matrix4 projection)
    {
        // Combined VP in OpenTK row-major order.
        Matrix4 vp = view * projection;

        // Transpose to align with the column-major clip transform used in the shader.
        // After this, m.Row0–Row3 are the rows of the effective clip matrix and
        // Gribb-Hartmann plane formulas apply directly.
        Matrix4 m = Matrix4.Transpose(vp);

        return new Frustum(
            Normalize(m.Row3 + m.Row0),   // Left   (-w ≤ x)
            Normalize(m.Row3 - m.Row0),   // Right  ( x ≤ w)
            Normalize(m.Row3 + m.Row1),   // Bottom (-w ≤ y)
            Normalize(m.Row3 - m.Row1),   // Top    ( y ≤ w)
            Normalize(m.Row3 + m.Row2),   // Near   (-w ≤ z)
            Normalize(m.Row3 - m.Row2)    // Far    ( z ≤ w)
        );
    }

    private static Vector4 Normalize(Vector4 p)
    {
        float len = MathF.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
        return len > 0f ? p / len : p;
    }

    /// <summary>
    /// Returns <c>true</c> if the axis-aligned bounding box is at least partially
    /// inside the frustum, i.e., the chunk should be rendered.
    ///
    /// Returns <c>false</c> only when the box is entirely on the wrong side of at
    /// least one frustum plane — the chunk is guaranteed invisible and can be skipped.
    ///
    /// Uses the "positive vertex" optimisation: rather than checking all 8 AABB
    /// corners, each plane test picks only the corner most aligned with the plane
    /// normal — if even that corner is outside, the whole box is outside.
    /// </summary>
    public bool ContainsAabb(Vector3 min, Vector3 max)
    {
        return InsidePlane(_left, min, max)
            && InsidePlane(_right, min, max)
            && InsidePlane(_bottom, min, max)
            && InsidePlane(_top, min, max)
            && InsidePlane(_near, min, max)
            && InsidePlane(_far, min, max);
    }

    /// <summary>
    /// Tests the "positive vertex" — the AABB corner furthest in the direction of
    /// the plane normal — against the plane's half-space.
    /// If that corner is outside (distance &lt; 0), the entire AABB is outside.
    /// </summary>
    private static bool InsidePlane(Vector4 plane, Vector3 min, Vector3 max)
    {
        // Select the component (min or max) that maximises dot(normal, vertex).
        var pv = new Vector3(
            plane.X >= 0f ? max.X : min.X,
            plane.Y >= 0f ? max.Y : min.Y,
            plane.Z >= 0f ? max.Z : min.Z);

        return plane.X * pv.X + plane.Y * pv.Y + plane.Z * pv.Z + plane.W >= 0f;
    }
}

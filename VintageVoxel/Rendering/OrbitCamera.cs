using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// A spherical orbit camera that circles around a fixed world-space <see cref="Target"/>.
/// Unlike the first-person <see cref="Camera"/>, this is designed for editor viewports
/// where the user wants to inspect an object from all sides.
///
/// Controls (managed by the caller):
///   - Call <see cref="Orbit"/> with mouse-delta when the right mouse button is held.
///   - Call <see cref="Zoom"/> with mouse-wheel delta to adjust the orbit radius.
/// </summary>
public sealed class OrbitCamera
{
    /// <summary>World-space point the camera revolves around (default = origin).</summary>
    public Vector3 Target = Vector3.Zero;

    /// <summary>Horizontal angle in radians (rotation around the Y axis).</summary>
    public float Azimuth = MathHelper.PiOver4;

    /// <summary>Vertical angle in radians (0 = equator, +Pi/2 = top pole).</summary>
    public float Elevation = 0.4f;

    /// <summary>Distance from <see cref="Target"/> to the camera eye.</summary>
    public float Radius = 5f;

    private float _fovY;
    private float _aspect;

    private const float MinElevation = -MathHelper.PiOver2 + 0.01f;
    private const float MaxElevation = MathHelper.PiOver2 - 0.01f;
    private const float MinRadius = 0.5f;
    private const float MaxRadius = 100f;
    private const float NearPlane = 0.05f;
    private const float FarPlane = 500f;

    public OrbitCamera(float radius, float fovDegrees, float aspect)
    {
        Radius = radius;
        _fovY = MathHelper.DegreesToRadians(fovDegrees);
        _aspect = aspect;
    }

    // ── Derived eye position ────────────────────────────────────────────────

    /// <summary>Eye position in world space, computed from the spherical coordinates.</summary>
    public Vector3 Position => Target + new Vector3(
        Radius * MathF.Cos(Elevation) * MathF.Sin(Azimuth),
        Radius * MathF.Sin(Elevation),
        Radius * MathF.Cos(Elevation) * MathF.Cos(Azimuth)
    );

    // ── Matrices ────────────────────────────────────────────────────────────

    public Matrix4 GetViewMatrix() =>
        Matrix4.LookAt(Position, Target, Vector3.UnitY);

    public Matrix4 GetProjectionMatrix() =>
        Matrix4.CreatePerspectiveFieldOfView(_fovY, _aspect, NearPlane, FarPlane);

    // ── Input helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Rotates the orbit by the given deltas (in radians).
    /// Positive <paramref name="dAzimuth"/> rotates left; positive <paramref name="dElevation"/> tilts up.
    /// </summary>
    public void Orbit(float dAzimuth, float dElevation)
    {
        Azimuth += dAzimuth;
        Elevation = MathHelper.Clamp(Elevation + dElevation, MinElevation, MaxElevation);
    }

    /// <summary>
    /// Adjusts the orbit radius. Positive <paramref name="delta"/> moves the camera away from target.
    /// </summary>
    public void Zoom(float delta)
    {
        Radius = MathHelper.Clamp(Radius + delta, MinRadius, MaxRadius);
    }

    /// <summary>Call when the window is resized so the projection stays correct.</summary>
    public void UpdateAspect(float aspect) => _aspect = aspect;

    /// <summary>Vertical field-of-view in radians.</summary>
    public float FovY => _fovY;

    /// <summary>Viewport aspect ratio (width / height).</summary>
    public float Aspect => _aspect;
}

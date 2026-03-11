using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// A first-person fly camera. Produces the View and Projection matrices
/// that the vertex shader needs to transform world-space geometry into screen-space pixels.
/// </summary>
public class Camera
{
    // --- Position & Orientation ---
    public Vector3 Position;

    /// <summary>World-space position of the player's feet (eye position minus eye height).</summary>
    public Vector3 FeetPosition => new Vector3(Position.X, Position.Y - EyeHeight, Position.Z);
    // Pitch = vertical rotation around the X axis (up/down look).
    // Using separate scalars (not a quaternion) is simpler for FPS-style look.
    private float _yaw = -MathHelper.PiOver2; // Start facing -Z (into the screen)
    private float _pitch = 0f;

    // Pitch is clamped to ±89° to prevent gimbal-lock / flipping at the poles.
    private const float MaxPitch = MathHelper.PiOver2 - 0.01f;

    // Cached direction vectors — recomputed whenever yaw/pitch change.
    private Vector3 _front = -Vector3.UnitZ;
    private Vector3 _right = Vector3.UnitX;
    private Vector3 _up = Vector3.UnitY;

    /// <summary>Current yaw in radians. Used by the network layer to broadcast player orientation.</summary>
    public float Yaw => _yaw;
    /// <summary>Current pitch in radians. Used by the network layer to broadcast player orientation.</summary>
    public float Pitch => _pitch;

    // --- Projection parameters ---
    private float _fovY;         // Vertical field-of-view in radians.
    private float _aspectRatio;
    private const float NearPlane = 0.1f;
    private const float FarPlane = 1000f;

    // --- Movement & look sensitivity ---
    public float MoveSpeed = 10f;  // World units per second (creative fly speed)
    public float MouseSensitivity = 0.002f; // Radians per pixel

    // Eye height is retained here solely for the FeetPosition convenience property.
    // The authoritative copy lives in CollisionSystem.EyeHeight (Phase 6 will consolidate).
    private const float EyeHeight = 1.7f;

    // -------------------------------------------------------------------------
    // Physics state — updated each frame by PhysicsSystem
    // -------------------------------------------------------------------------

    /// <summary>Current linear velocity in world units per second. Zero in creative mode.</summary>
    public Vector3 Velocity;

    /// <summary>True while the player is resting on solid ground this frame.</summary>
    public bool IsOnGround;

    /// <summary>
    /// Creative/fly mode — full freedom, no gravity or collision.
    /// Toggle with F. Defaults to false (survival physics active).
    /// </summary>
    public bool CreativeMode = false;

    public Camera(Vector3 position, float fovDegrees, float aspectRatio)
    {
        Position = position;
        _fovY = MathHelper.DegreesToRadians(fovDegrees);
        _aspectRatio = aspectRatio;
        UpdateVectors();
    }

    // -------------------------------------------------------------------------
    // Matrices
    // -------------------------------------------------------------------------

    /// <summary>
    /// The View matrix transforms world-space coordinates into camera-space.
    /// LookAt builds it from position, a look-at target, and the world up vector.
    /// </summary>
    public Matrix4 GetViewMatrix() =>
        Matrix4.LookAt(Position, Position + _front, _up);

    /// <summary>
    /// The Projection matrix transforms camera-space into clip-space, applying
    /// perspective foreshortening (things farther away appear smaller).
    /// </summary>
    public Matrix4 GetProjectionMatrix() =>
        Matrix4.CreatePerspectiveFieldOfView(_fovY, _aspectRatio, NearPlane, FarPlane);

    // -------------------------------------------------------------------------
    // Input handling — called from Game.OnUpdateFrame
    // -------------------------------------------------------------------------

    /// <summary>
    /// Process a raw mouse delta (pixels moved) into yaw/pitch rotation.
    /// delta.X → yaw, delta.Y → pitch.
    /// </summary>
    public void ProcessMouseMovement(Vector2 delta)
    {
        _yaw += delta.X * MouseSensitivity;
        _pitch -= delta.Y * MouseSensitivity; // Subtract: moving mouse up should look up (+Y)

        _pitch = MathHelper.Clamp(_pitch, -MaxPitch, MaxPitch);

        UpdateVectors();
    }

    /// <summary>Call when the window is resized to keep the aspect ratio correct.</summary>
    public void SetAspectRatio(float aspectRatio) => _aspectRatio = aspectRatio;

    /// <summary>The camera's current look direction (normalised).</summary>
    public Vector3 Front => _front;

    /// <summary>The camera's current right direction (normalised).</summary>
    public Vector3 Right => _right;

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Recompute front/right/up from the current yaw and pitch using spherical coordinates.
    /// This is the standard FPS camera derivation:
    ///   front.X = cos(pitch) * cos(yaw)
    ///   front.Y = sin(pitch)
    ///   front.Z = cos(pitch) * sin(yaw)
    /// Right = normalize(cross(front, worldUp))
    /// Up    = normalize(cross(right, front))
    /// </summary>
    private void UpdateVectors()
    {
        _front.X = MathF.Cos(_pitch) * MathF.Cos(_yaw);
        _front.Y = MathF.Sin(_pitch);
        _front.Z = MathF.Cos(_pitch) * MathF.Sin(_yaw);
        _front = Vector3.Normalize(_front);

        _right = Vector3.Normalize(Vector3.Cross(_front, Vector3.UnitY));
        _up = Vector3.Normalize(Vector3.Cross(_right, _front));
    }
}

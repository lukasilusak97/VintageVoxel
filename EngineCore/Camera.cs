using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

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

    // --- Projection parameters ---
    private float _fovY;         // Vertical field-of-view in radians.
    private float _aspectRatio;
    private const float NearPlane = 0.1f;
    private const float FarPlane = 1000f;

    // --- Movement & look sensitivity ---
    public float MoveSpeed = 10f;  // World units per second (creative fly speed)
    public float MouseSensitivity = 0.002f; // Radians per pixel

    // -------------------------------------------------------------------------
    // Phase 10: Physics & Movement
    // -------------------------------------------------------------------------

    // Player AABB dimensions (standard humanoid proportions).
    private const float EyeHeight = 1.7f;       // Eye position above feet
    private const float PlayerHalfWidth = 0.3f; // Half-extent in X and Z
    private const float PlayerHeight = 1.8f;    // Total height

    // Physics constants.
    private const float Gravity = -25f;      // Downward acceleration (world units/s²)
    private const float JumpSpeed = 8f;      // Initial Y velocity when jumping
    private const float MaxFallSpeed = 60f;  // Terminal-velocity cap
    private const float SurvivalMoveSpeed = 5f; // Horizontal walk speed

    /// <summary>Current linear velocity in world units per second. Zero in creative mode.</summary>
    public Vector3 Velocity;

    /// <summary>True while the player is resting on solid ground this frame.</summary>
    public bool IsOnGround { get; private set; }

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
    /// Runs one physics/movement tick. In creative mode the player flies freely;
    /// in survival mode gravity, AABB collision and sliding are applied.
    /// </summary>
    /// <param name="world">Used for block queries during collision detection.</param>
    /// <param name="keyboard">Current keyboard state.</param>
    /// <param name="dt">Delta time in seconds.</param>
    public void PhysicsUpdate(World world, KeyboardState keyboard, float dt)
    {
        if (CreativeMode)
        {
            // --- Creative / fly mode: original free-fly behaviour ---
            float speed = MoveSpeed * dt;
            if (keyboard.IsKeyDown(Keys.W)) Position += _front * speed;
            if (keyboard.IsKeyDown(Keys.S)) Position -= _front * speed;
            if (keyboard.IsKeyDown(Keys.A)) Position -= _right * speed;
            if (keyboard.IsKeyDown(Keys.D)) Position += _right * speed;
            if (keyboard.IsKeyDown(Keys.E)) Position += Vector3.UnitY * speed;
            if (keyboard.IsKeyDown(Keys.Q)) Position -= Vector3.UnitY * speed;
            Velocity = Vector3.Zero;
            IsOnGround = false;
            return;
        }

        // --- Survival mode: gravity + AABB collision + per-axis sliding ---

        // Project look direction onto the XZ plane so the player always walks
        // horizontally regardless of the angle they are looking up or down.
        var frontXZ = new Vector3(_front.X, 0f, _front.Z);
        if (frontXZ.LengthSquared > 0.0001f) frontXZ = Vector3.Normalize(frontXZ);
        var rightXZ = new Vector3(_right.X, 0f, _right.Z);
        if (rightXZ.LengthSquared > 0.0001f) rightXZ = Vector3.Normalize(rightXZ);

        var horizontal = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W)) horizontal += frontXZ;
        if (keyboard.IsKeyDown(Keys.S)) horizontal -= frontXZ;
        if (keyboard.IsKeyDown(Keys.A)) horizontal -= rightXZ;
        if (keyboard.IsKeyDown(Keys.D)) horizontal += rightXZ;
        if (horizontal.LengthSquared > 0f)
            horizontal = Vector3.Normalize(horizontal) * SurvivalMoveSpeed;

        // Horizontal velocity comes entirely from input each frame (no momentum).
        Velocity.X = horizontal.X;
        Velocity.Z = horizontal.Z;

        // Jump — only when the player is standing on solid ground.
        if (keyboard.IsKeyDown(Keys.Space) && IsOnGround)
            Velocity.Y = JumpSpeed;

        // Integrate gravity; clamp to terminal velocity.
        Velocity.Y = MathF.Max(Velocity.Y + Gravity * dt, -MaxFallSpeed);

        // -----------------------------------------------------------------------
        // Per-axis collision resolution — the key to sliding movement.
        //
        // By resolving each axis independently, a player moving diagonally into a
        // wall will slide along it rather than stopping dead.  If only one axis
        // produces a penetration, only that axis's velocity is cancelled.
        // -----------------------------------------------------------------------

        // X axis
        float dx = Velocity.X * dt;
        Position.X += dx;
        if (IsCollidingAt(world, Position)) { Position.X -= dx; Velocity.X = 0f; }

        // Y axis
        float dy = Velocity.Y * dt;
        Position.Y += dy;
        if (IsCollidingAt(world, Position)) { Position.Y -= dy; Velocity.Y = 0f; }

        // Z axis
        float dz = Velocity.Z * dt;
        Position.Z += dz;
        if (IsCollidingAt(world, Position)) { Position.Z -= dz; Velocity.Z = 0f; }

        // Ground probe: a tiny downward step detects floor contact so jumping is
        // only allowed when the player is actually standing on something.
        IsOnGround = IsCollidingAt(world, new Vector3(Position.X, Position.Y - 0.05f, Position.Z));
    }

    // -------------------------------------------------------------------------
    // AABB collision helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true when the player's AABB, centred on <paramref name="eyePos"/>
    /// (eye-level position), overlaps at least one solid block in the world.
    ///
    /// The AABB spans:
    ///   X: [eyePos.X - PlayerHalfWidth, eyePos.X + PlayerHalfWidth]
    ///   Y: [eyePos.Y - EyeHeight,       eyePos.Y - EyeHeight + PlayerHeight]
    ///   Z: [eyePos.Z - PlayerHalfWidth, eyePos.Z + PlayerHalfWidth]
    /// </summary>
    private static bool IsCollidingAt(World world, Vector3 eyePos)
    {
        float feetY = eyePos.Y - EyeHeight;

        // Treat the region below world Y = 0 as solid bedrock so the player
        // cannot fall out of the world.
        if (feetY < 0f) return true;

        // The small epsilon on the max extent prevents a player standing exactly
        // on a block edge from being considered inside the next block.
        int minX = (int)MathF.Floor(eyePos.X - PlayerHalfWidth);
        int maxX = (int)MathF.Floor(eyePos.X + PlayerHalfWidth - 0.001f);
        int minY = (int)MathF.Floor(feetY);
        int maxY = (int)MathF.Floor(feetY + PlayerHeight - 0.001f);
        int minZ = (int)MathF.Floor(eyePos.Z - PlayerHalfWidth);
        int maxZ = (int)MathF.Floor(eyePos.Z + PlayerHalfWidth - 0.001f);

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
                for (int z = minZ; z <= maxZ; z++)
                    if (!world.GetBlock(x, y, z).IsEmpty)
                        return true;

        return false;
    }

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

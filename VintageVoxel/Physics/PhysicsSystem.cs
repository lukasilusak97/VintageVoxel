using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace VintageVoxel.Physics;

/// <summary>
/// Applies gravity, jumping, and AABB-sliding movement to the player camera each frame.
/// Creative (fly) mode bypasses all physics and lets the player move freely.
/// </summary>
public static class PhysicsSystem
{
    // Physics constants live in GameConstants.Physics (Constants.cs).
    private static float Gravity => GameConstants.Physics.Gravity;
    private static float JumpSpeed => GameConstants.Physics.JumpSpeed;
    private static float MaxFallSpeed => GameConstants.Physics.MaxFallSpeed;
    private static float SurvivalMoveSpeed => GameConstants.Physics.SurvivalMoveSpeed;

    /// <summary>
    /// Runs one physics/movement tick.
    /// In creative mode the player flies freely; in survival mode gravity,
    /// AABB collision and per-axis sliding are applied.
    /// </summary>
    /// <param name="camera">The player camera whose Position and Velocity are updated.</param>
    /// <param name="world">Used for block queries during collision detection.</param>
    /// <param name="keyboard">Current keyboard state.</param>
    /// <param name="dt">Delta time in seconds.</param>
    public static void Update(Camera camera, World world, KeyboardState keyboard, float dt)
    {
        if (camera.CreativeMode)
        {
            // Creative / fly mode: original free-fly behaviour.
            float speed = camera.MoveSpeed * dt;
            if (keyboard.IsKeyDown(Keys.W)) camera.Position += camera.Front * speed;
            if (keyboard.IsKeyDown(Keys.S)) camera.Position -= camera.Front * speed;
            if (keyboard.IsKeyDown(Keys.A)) camera.Position -= camera.Right * speed;
            if (keyboard.IsKeyDown(Keys.D)) camera.Position += camera.Right * speed;
            if (keyboard.IsKeyDown(Keys.E)) camera.Position += Vector3.UnitY * speed;
            if (keyboard.IsKeyDown(Keys.Q)) camera.Position -= Vector3.UnitY * speed;
            camera.Velocity = Vector3.Zero;
            camera.IsOnGround = false;
            return;
        }

        // --- Survival mode: gravity + AABB collision + per-axis sliding ---

        // Project look direction onto the XZ plane so the player always walks
        // horizontally regardless of the angle they are looking up or down.
        var frontXZ = new Vector3(camera.Front.X, 0f, camera.Front.Z);
        if (frontXZ.LengthSquared > 0.0001f) frontXZ = Vector3.Normalize(frontXZ);
        var rightXZ = new Vector3(camera.Right.X, 0f, camera.Right.Z);
        if (rightXZ.LengthSquared > 0.0001f) rightXZ = Vector3.Normalize(rightXZ);

        var horizontal = Vector3.Zero;
        if (keyboard.IsKeyDown(Keys.W)) horizontal += frontXZ;
        if (keyboard.IsKeyDown(Keys.S)) horizontal -= frontXZ;
        if (keyboard.IsKeyDown(Keys.A)) horizontal -= rightXZ;
        if (keyboard.IsKeyDown(Keys.D)) horizontal += rightXZ;
        if (horizontal.LengthSquared > 0f)
            horizontal = Vector3.Normalize(horizontal) * SurvivalMoveSpeed;

        // Horizontal velocity comes entirely from input each frame (no momentum).
        camera.Velocity.X = horizontal.X;
        camera.Velocity.Z = horizontal.Z;

        // Jump — only when the player is standing on solid ground.
        if (keyboard.IsKeyDown(Keys.Space) && camera.IsOnGround)
            camera.Velocity.Y = JumpSpeed;

        // Integrate gravity; clamp to terminal velocity.
        camera.Velocity.Y = MathF.Max(camera.Velocity.Y + Gravity * dt, -MaxFallSpeed);

        // -----------------------------------------------------------------------
        // Per-axis collision resolution — the key to sliding movement.
        //
        // By resolving each axis independently, a player moving diagonally into a
        // wall will slide along it rather than stopping dead. If only one axis
        // produces a penetration, only that axis's velocity is cancelled.
        // -----------------------------------------------------------------------

        // X axis
        float dx = camera.Velocity.X * dt;
        camera.Position.X += dx;
        if (CollisionSystem.IsCollidingAt(world, camera.Position)) { camera.Position.X -= dx; camera.Velocity.X = 0f; }

        // Y axis
        float dy = camera.Velocity.Y * dt;
        camera.Position.Y += dy;
        if (CollisionSystem.IsCollidingAt(world, camera.Position))
        {
            camera.Position.Y -= dy;
            camera.Velocity.Y = 0f;

            // Layer surface snapping: push the player's eye Y up to the exact
            // layer surface height so they ride smoothly over partial blocks.
            float surfaceY = CollisionSystem.GetSurfaceHeightAt(
                camera.Position.X, camera.Position.Z, world);
            float targetEyeY = surfaceY + GameConstants.Physics.EyeHeight;
            if (targetEyeY > camera.Position.Y && targetEyeY <= camera.Position.Y + 1.1f)
                camera.Position.Y = targetEyeY;
        }

        // Z axis
        float dz = camera.Velocity.Z * dt;
        camera.Position.Z += dz;
        if (CollisionSystem.IsCollidingAt(world, camera.Position)) { camera.Position.Z -= dz; camera.Velocity.Z = 0f; }

        // Ground probe: a tiny downward step detects floor contact so jumping is
        // only allowed when the player is actually standing on something.
        camera.IsOnGround = CollisionSystem.IsOnGround(world, camera.Position);
    }
}

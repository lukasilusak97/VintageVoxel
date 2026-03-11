using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// A physical dropped-item entity in the world.
/// Rendered as a small spinning textured quad by <see cref="EntityItemRenderer"/>.
/// Collected automatically when the player walks within <see cref="PickupRadius"/> units.
/// </summary>
public class EntityItem
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    /// <summary>Player eye distance (world units) within which this item is collected.</summary>
    public const float PickupRadius = 1.5f;

    /// <summary>Height offset from the floor surface to the icon centre.</summary>
    public const float HoverHeight = 0.3f;

    /// <summary>Seconds after spawning before the item can be picked up.</summary>
    public const float PickupDelay = 0.5f;

    private const float Gravity = -18f;
    private const float TerminalVelocity = -40f;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    public Item Item { get; }
    public int Count { get; set; }

    public Vector3 Position;
    public Vector3 Velocity;

    /// <summary>Current spin angle in radians; advanced by <see cref="Update"/>.</summary>
    public float SpinAngle;

    /// <summary>Time remaining before this item can be picked up (seconds).</summary>
    public float PickupCooldown;

    // -------------------------------------------------------------------------
    // Construction
    // -------------------------------------------------------------------------

    public EntityItem(Item item, int count, Vector3 spawnPosition,
                      Vector3 initialVelocity = default)
    {
        Item = item;
        Count = count;
        Position = spawnPosition;
        Velocity = initialVelocity;
        SpinAngle = Random.Shared.NextSingle() * MathF.Tau; // random initial rotation
        PickupCooldown = PickupDelay;
    }

    // -------------------------------------------------------------------------
    // Physics
    // -------------------------------------------------------------------------

    /// <summary>Integrates gravity, resolves block collisions, and advances the spin.</summary>
    public void Update(World world, float dt)
    {
        if (PickupCooldown > 0f)
            PickupCooldown -= dt;

        // --- Gravity ---
        Velocity.Y = MathF.Max(Velocity.Y + Gravity * dt, TerminalVelocity);

        Vector3 next = Position + Velocity * dt;

        int fx = (int)MathF.Floor(next.X);
        int fz = (int)MathF.Floor(next.Z);

        // --- Y axis: land on the block surface below ---
        int groundY = (int)MathF.Floor(next.Y - 0.05f);
        if (!world.GetBlock(fx, groundY, fz).IsTransparent && Velocity.Y <= 0f)
        {
            next.Y = groundY + 1f;
            Velocity.Y = 0f;
        }

        // --- X axis ---
        if (!world.GetBlock((int)MathF.Floor(next.X), (int)MathF.Floor(Position.Y), fz).IsTransparent)
        {
            next.X = Position.X;
            Velocity.X = 0f;
        }

        // --- Z axis ---
        if (!world.GetBlock(fx, (int)MathF.Floor(Position.Y), (int)MathF.Floor(next.Z)).IsTransparent)
        {
            next.Z = Position.Z;
            Velocity.Z = 0f;
        }

        Position = next;

        // Spin — one full revolution every 2 seconds.
        SpinAngle = (SpinAngle + dt * MathF.PI) % MathF.Tau;
    }
}

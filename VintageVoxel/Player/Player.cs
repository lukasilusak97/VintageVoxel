using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Holds all persistent per-player data: stats (HP, stamina), spawn point,
/// last known position, and the player's inventory.
/// Serialized to / from <c>player.bin</c> inside the world save folder via
/// <see cref="WorldPersistence.SavePlayer"/> and
/// <see cref="WorldPersistence.TryLoadPlayer"/>.
/// </summary>
public class Player
{
    public const float DefaultMaxHp = 20f;
    public const float DefaultMaxStamina = 100f;

    // ── Stats ─────────────────────────────────────────────────────────────────

    public float Hp { get; set; } = DefaultMaxHp;
    public float MaxHp { get; set; } = DefaultMaxHp;

    public float Stamina { get; set; } = DefaultMaxStamina;
    public float MaxStamina { get; set; } = DefaultMaxStamina;

    // ── World position ────────────────────────────────────────────────────────

    /// <summary>The bed / initial spawn position used on death.</summary>
    public Vector3 SpawnPoint { get; set; } = new Vector3(16f, 130f, 16f);

    // ── Inventory ─────────────────────────────────────────────────────────────

    public Inventory Inventory { get; } = new Inventory();
}

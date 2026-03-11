using OpenTK.Mathematics;

namespace VintageVoxel;

/// <summary>
/// Represents another human-controlled player visible in the world.
/// Stores the authoritative target state received from the server and
/// linearly interpolates towards it each render frame so movement looks
/// smooth despite the 20 Hz network tick rate.
/// </summary>
public sealed class RemotePlayer
{
    // -------------------------------------------------------------------------
    // Identity
    // -------------------------------------------------------------------------

    public int PlayerId { get; }
    public string Name { get; }

    // -------------------------------------------------------------------------
    // Interpolation state
    // -------------------------------------------------------------------------

    /// <summary>Rendered position — lerped towards <see cref="_targetPosition"/> every frame.</summary>
    public Vector3 Position { get; private set; }
    /// <summary>Rendered yaw in degrees.</summary>
    public float Yaw { get; private set; }
    /// <summary>Rendered pitch in degrees.</summary>
    public float Pitch { get; private set; }

    private Vector3 _targetPosition;
    private float _targetYaw;
    private float _targetPitch;

    // How quickly the visual position catches up to the server state.
    // At 20 Hz ticks this gives 1-2 frame interpolation at 60 FPS.
    private const float LerpSpeed = 20f;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public RemotePlayer(int playerId, string name, Vector3 spawnPos = default)
    {
        PlayerId = playerId;
        Name = name;
        Position = spawnPos;
        _targetPosition = spawnPos;
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Called when a <see cref="Networking.PlayerStatePacket"/> arrives for this player.
    /// Sets the interpolation target; the visual position approaches it over the
    /// next few frames.
    /// </summary>
    public void ApplyState(Vector3 position, float yaw, float pitch)
    {
        _targetPosition = position;
        _targetYaw = yaw;
        _targetPitch = pitch;
    }

    /// <summary>Advances the interpolation each frame. Call from the game update loop.</summary>
    public void Update(float dt)
    {
        float t = Math.Min(1f, LerpSpeed * dt);
        Position = Vector3.Lerp(Position, _targetPosition, t);
        Yaw = Lerp(Yaw, _targetYaw, t);
        Pitch = Lerp(Pitch, _targetPitch, t);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

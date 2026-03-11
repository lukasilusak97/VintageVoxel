using OpenTK.Mathematics;

namespace VintageVoxel.Networking;

/// <summary>
/// A single-byte prefix that identifies every packet type on the wire.
/// Sent as the first byte of every LiteNetLib message payload.
/// </summary>
public enum PacketType : byte
{
    // Server → Client
    WorldInfo = 1,   // seed, spawn pos — sent immediately after a client connects
    ChunkData = 2,   // RLE-compressed block data for one chunk column
    BlockUpdate = 3,   // a single block changed (position + new id)
    PlayerJoin = 4,   // another player connected
    PlayerLeave = 5,   // another player disconnected
    PlayerState = 6,   // position + look angles (broadcast to all other peers)
    ChatMessage = 7,   // in-game chat line
    EntityItemSpawn = 8,   // a dropped-item entity appeared in the world
    EntityItemRemove = 9,  // a dropped-item entity was picked up / despawned

    // Client → Server
    PlayerAction = 20,  // break / place / middle-click block
    PlayerStateC = 21,  // client's own position + look angles (→ server for relay)
    ChatSend = 22,  // client sends a chat message to the server
    DropItem = 23,  // client dropped an item into the world
    PickupEntity = 24, // client picked up a networked entity item
}

/// <summary>Player action kinds sent in <see cref="PlayerActionPacket"/>.</summary>
public enum PlayerActionKind : byte
{
    Break = 0,
    Place = 1,
    Chisel = 2,
}

// ---------------------------------------------------------------------------
// Server → Client packets
// ---------------------------------------------------------------------------

/// <summary>Sent to a client upon connection. Contains world seed and spawn position.</summary>
public sealed class WorldInfoPacket
{
    public int Seed;
    public bool IsFlat;
    public Vector3 SpawnPos;
}

/// <summary>Full block data for one 32×32×32 chunk, RLE-compressed.</summary>
public sealed class ChunkDataPacket
{
    public Vector3i ChunkCoord;
    /// <summary>RLE-encoded block IDs (same format as WorldPersistence).</summary>
    public byte[] BlockData = Array.Empty<byte>();
}

/// <summary>Notifies all clients that one block changed.</summary>
public sealed class BlockUpdatePacket
{
    public Vector3i BlockPos;
    public ushort BlockId;
}

/// <summary>Tells existing clients that a new player joined.</summary>
public sealed class PlayerJoinPacket
{
    public int PlayerId;
    public string Name = "";
}

/// <summary>Tells all clients that a player left.</summary>
public sealed class PlayerLeavePacket
{
    public int PlayerId;
}

/// <summary>Position + look direction snapshot for one remote player.</summary>
public sealed class PlayerStatePacket
{
    public int PlayerId;
    public Vector3 Position;
    public float Yaw;
    public float Pitch;
}

/// <summary>A chat message to display in the HUD.</summary>
public sealed class ChatMessagePacket
{
    public int PlayerId;
    public string Name = "";
    public string Message = "";
}

// ---------------------------------------------------------------------------
// Client → Server packets
// ---------------------------------------------------------------------------

/// <summary>Block interaction sent from the client to the server for authoritative validation.</summary>
public sealed class PlayerActionPacket
{
    public PlayerActionKind Kind;
    public Vector3i BlockPos;
    /// <summary>Block ID to place (unused for Break).</summary>
    public ushort BlockId;
    /// <summary>World-space face normal (for Place, to determine adjacent cell).</summary>
    public Vector3i Normal;
}

/// <summary>Client's own movement state — relayed by the server to all other peers.</summary>
public sealed class PlayerStateClientPacket
{
    public Vector3 Position;
    public float Yaw;
    public float Pitch;
}

/// <summary>Client sends a chat message to the server for broadcasting.</summary>
public sealed class ChatSendPacket
{
    public string Message = "";
}

// ---------------------------------------------------------------------------
// Entity item packets
// ---------------------------------------------------------------------------

/// <summary>Tells all clients that a dropped-item entity has appeared in the world.</summary>
public sealed class EntityItemSpawnPacket
{
    public int EntityId;
    public int ItemId;
    public int Count;
    public Vector3 Position;
    public Vector3 Velocity;
}

/// <summary>Tells all clients that a dropped-item entity was picked up or removed.</summary>
public sealed class EntityItemRemovePacket
{
    public int EntityId;
}

/// <summary>Client notifies the server that it dropped an item into the world.</summary>
public sealed class DropItemPacket
{
    public int ItemId;
    public int Count;
    public Vector3 Position;
    public Vector3 Velocity;
}

/// <summary>Client notifies the server that it picked up a networked entity item.</summary>
public sealed class PickupEntityPacket
{
    public int EntityId;
}

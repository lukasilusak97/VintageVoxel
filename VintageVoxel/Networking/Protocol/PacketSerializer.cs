using System.IO;
using LiteNetLib.Utils;
using OpenTK.Mathematics;

namespace VintageVoxel.Networking;

/// <summary>
/// Reads and writes all protocol <see cref="Packets"/> to/from a
/// <see cref="NetDataWriter"/> / <see cref="NetDataReader"/> (LiteNetLib's
/// zero-alloc byte buffer).
///
/// Each public Serialize method writes the <see cref="PacketType"/> prefix byte
/// then the packet fields. Each public Deserialize method assumes the prefix has
/// already been consumed by the caller (the routing switch) and reads only the
/// remaining fields.
/// </summary>
public static class PacketSerializer
{
    // ------------------------------------------------------------------
    // Write helpers
    // ------------------------------------------------------------------

    private static void Write(NetDataWriter w, Vector3 v)
    {
        w.Put(v.X);
        w.Put(v.Y);
        w.Put(v.Z);
    }

    private static void Write(NetDataWriter w, Vector3i v)
    {
        w.Put(v.X);
        w.Put(v.Y);
        w.Put(v.Z);
    }

    private static void Write(NetDataWriter w, Vector2i v)
    {
        w.Put(v.X);
        w.Put(v.Y);
    }

    // ------------------------------------------------------------------
    // Read helpers
    // ------------------------------------------------------------------

    private static Vector3 ReadV3(NetDataReader r)
        => new(r.GetFloat(), r.GetFloat(), r.GetFloat());

    private static Vector3i ReadV3i(NetDataReader r)
        => new(r.GetInt(), r.GetInt(), r.GetInt());

    private static Vector2i ReadV2i(NetDataReader r)
        => new(r.GetInt(), r.GetInt());

    // ------------------------------------------------------------------
    // WorldInfoPacket
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, WorldInfoPacket p)
    {
        w.Put((byte)PacketType.WorldInfo);
        w.Put(p.Seed);
        w.Put(p.IsFlat);
        Write(w, p.SpawnPos);
    }

    public static WorldInfoPacket DeserializeWorldInfo(NetDataReader r)
        => new()
        {
            Seed = r.GetInt(),
            IsFlat = r.GetBool(),
            SpawnPos = ReadV3(r),
        };

    // ------------------------------------------------------------------
    // ChunkDataPacket
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, ChunkDataPacket p)
    {
        w.Put((byte)PacketType.ChunkData);
        Write(w, p.ChunkCoord);
        w.Put(p.BlockData.Length);
        w.Put(p.BlockData);
    }

    public static ChunkDataPacket DeserializeChunkData(NetDataReader r)
    {
        var coord = ReadV3i(r);
        int len = r.GetInt();
        var data = new byte[len];
        r.GetBytes(data, len);
        return new ChunkDataPacket { ChunkCoord = coord, BlockData = data };
    }

    // ------------------------------------------------------------------
    // BlockUpdatePacket
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, BlockUpdatePacket p)
    {
        w.Put((byte)PacketType.BlockUpdate);
        Write(w, p.BlockPos);
        w.Put(p.BlockId);
    }

    public static BlockUpdatePacket DeserializeBlockUpdate(NetDataReader r)
        => new()
        {
            BlockPos = ReadV3i(r),
            BlockId = r.GetUShort(),
        };

    // ------------------------------------------------------------------
    // PlayerJoinPacket
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, PlayerJoinPacket p)
    {
        w.Put((byte)PacketType.PlayerJoin);
        w.Put(p.PlayerId);
        w.Put(p.Name);
    }

    public static PlayerJoinPacket DeserializePlayerJoin(NetDataReader r)
        => new() { PlayerId = r.GetInt(), Name = r.GetString() };

    // ------------------------------------------------------------------
    // PlayerLeavePacket
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, PlayerLeavePacket p)
    {
        w.Put((byte)PacketType.PlayerLeave);
        w.Put(p.PlayerId);
    }

    public static PlayerLeavePacket DeserializePlayerLeave(NetDataReader r)
        => new() { PlayerId = r.GetInt() };

    // ------------------------------------------------------------------
    // PlayerStatePacket  (server → clients)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, PlayerStatePacket p)
    {
        w.Put((byte)PacketType.PlayerState);
        w.Put(p.PlayerId);
        Write(w, p.Position);
        w.Put(p.Yaw);
        w.Put(p.Pitch);
    }

    public static PlayerStatePacket DeserializePlayerState(NetDataReader r)
        => new()
        {
            PlayerId = r.GetInt(),
            Position = ReadV3(r),
            Yaw = r.GetFloat(),
            Pitch = r.GetFloat(),
        };

    // ------------------------------------------------------------------
    // ChatMessagePacket
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, ChatMessagePacket p)
    {
        w.Put((byte)PacketType.ChatMessage);
        w.Put(p.PlayerId);
        w.Put(p.Name);
        w.Put(p.Message);
    }

    public static ChatMessagePacket DeserializeChatMessage(NetDataReader r)
        => new() { PlayerId = r.GetInt(), Name = r.GetString(), Message = r.GetString() };

    // ------------------------------------------------------------------
    // PlayerActionPacket  (client → server)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, PlayerActionPacket p)
    {
        w.Put((byte)PacketType.PlayerAction);
        w.Put((byte)p.Kind);
        Write(w, p.BlockPos);
        w.Put(p.BlockId);
        Write(w, p.Normal);
    }

    public static PlayerActionPacket DeserializePlayerAction(NetDataReader r)
        => new()
        {
            Kind = (PlayerActionKind)r.GetByte(),
            BlockPos = ReadV3i(r),
            BlockId = r.GetUShort(),
            Normal = ReadV3i(r),
        };

    // ------------------------------------------------------------------
    // PlayerStateClientPacket  (client → server)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, PlayerStateClientPacket p)
    {
        w.Put((byte)PacketType.PlayerStateC);
        Write(w, p.Position);
        w.Put(p.Yaw);
        w.Put(p.Pitch);
    }

    public static PlayerStateClientPacket DeserializePlayerStateClient(NetDataReader r)
        => new()
        {
            Position = ReadV3(r),
            Yaw = r.GetFloat(),
            Pitch = r.GetFloat(),
        };

    // ------------------------------------------------------------------
    // ChatSendPacket  (client → server)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, ChatSendPacket p)
    {
        w.Put((byte)PacketType.ChatSend);
        w.Put(p.Message);
    }

    public static ChatSendPacket DeserializeChatSend(NetDataReader r)
        => new() { Message = r.GetString() };

    // ------------------------------------------------------------------
    // EntityItemSpawnPacket  (server → clients)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, EntityItemSpawnPacket p)
    {
        w.Put((byte)PacketType.EntityItemSpawn);
        w.Put(p.EntityId);
        w.Put(p.ItemId);
        w.Put(p.Count);
        Write(w, p.Position);
        Write(w, p.Velocity);
    }

    public static EntityItemSpawnPacket DeserializeEntityItemSpawn(NetDataReader r)
        => new()
        {
            EntityId = r.GetInt(),
            ItemId = r.GetInt(),
            Count = r.GetInt(),
            Position = ReadV3(r),
            Velocity = ReadV3(r),
        };

    // ------------------------------------------------------------------
    // EntityItemRemovePacket  (server → clients)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, EntityItemRemovePacket p)
    {
        w.Put((byte)PacketType.EntityItemRemove);
        w.Put(p.EntityId);
    }

    public static EntityItemRemovePacket DeserializeEntityItemRemove(NetDataReader r)
        => new() { EntityId = r.GetInt() };

    // ------------------------------------------------------------------
    // DropItemPacket  (client → server)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, DropItemPacket p)
    {
        w.Put((byte)PacketType.DropItem);
        w.Put(p.ItemId);
        w.Put(p.Count);
        Write(w, p.Position);
        Write(w, p.Velocity);
    }

    public static DropItemPacket DeserializeDropItem(NetDataReader r)
        => new()
        {
            ItemId = r.GetInt(),
            Count = r.GetInt(),
            Position = ReadV3(r),
            Velocity = ReadV3(r),
        };

    // ------------------------------------------------------------------
    // PickupEntityPacket  (client → server)
    // ------------------------------------------------------------------

    public static void Serialize(NetDataWriter w, PickupEntityPacket p)
    {
        w.Put((byte)PacketType.PickupEntity);
        w.Put(p.EntityId);
    }

    public static PickupEntityPacket DeserializePickupEntity(NetDataReader r)
        => new() { EntityId = r.GetInt() };

    // ------------------------------------------------------------------
    // Chunk data helpers (encode/decode block array to byte[])
    // ------------------------------------------------------------------

    /// <summary>
    /// RLE-encodes a chunk's block IDs into a byte array suitable for network
    /// transmission. Reuses <see cref="RleCodec.WriteUshort"/> so the format is
    /// identical to the on-disk save format — no extra conversion needed.
    /// </summary>
    public static byte[] EncodeChunkBlocks(Chunk chunk)
    {
        using var ms = new MemoryStream(512);
        using var bw = new BinaryWriter(ms);
        RleCodec.WriteUshort(bw, i => chunk.GetRawBlockId(i), Chunk.Volume);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Decodes a byte array produced by <see cref="EncodeChunkBlocks"/> into an
    /// array of block IDs and loads them into <paramref name="chunk"/>.
    /// </summary>
    public static void DecodeChunkBlocks(byte[] data, Chunk chunk)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        var ids = RleCodec.ReadUshort(br, Chunk.Volume);
        chunk.LoadBlocksFromSave(ids);
    }
}

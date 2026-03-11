using LiteNetLib;
using LiteNetLib.Utils;
using OpenTK.Mathematics;
using VintageVoxel.Rendering;

namespace VintageVoxel.Networking;

/// <summary>
/// Client-side networking manager. Connects to a <see cref="GameServer"/> over UDP,
/// receives world state, and sends the local player's actions.
///
/// Connection flow:
///   1. Call <see cref="Connect"/> — asynchronous, check <see cref="IsConnected"/>.
///   2. Once connected the server sends <see cref="WorldInfoPacket"/> followed by
///      a stream of <see cref="ChunkDataPacket"/>s.
///   3. Call <see cref="Tick"/> each game frame to poll incoming messages.
///   4. Call <see cref="SendPlayerState"/> and <see cref="SendPlayerAction"/> from
///      the game loop and interaction handler respectively.
///   5. Call <see cref="Disconnect"/> when leaving the session.
/// </summary>
public sealed class GameClient : IDisposable
{
    // -------------------------------------------------------------------------
    // Events raised on the main thread after Tick()
    // -------------------------------------------------------------------------

    /// <summary>Raised when the server sends world metadata. Triggers world initialisation.</summary>
    public event Action<WorldInfoPacket>? OnWorldInfo;
    /// <summary>Raised when a chunk's block data arrives. The chunk is ready to mesh.</summary>
    public event Action<ChunkDataPacket>? OnChunkData;
    /// <summary>Raised when the server confirms a block change.</summary>
    public event Action<BlockUpdatePacket>? OnBlockUpdate;
    /// <summary>Raised when another player connects.</summary>
    public event Action<PlayerJoinPacket>? OnPlayerJoin;
    /// <summary>Raised when another player disconnects.</summary>
    public event Action<PlayerLeavePacket>? OnPlayerLeave;
    /// <summary>Raised when a remote player's state arrives.</summary>
    public event Action<PlayerStatePacket>? OnPlayerState;
    /// <summary>Raised when a chat message arrives.</summary>
    public event Action<ChatMessagePacket>? OnChatMessage;
    /// <summary>Raised when a dropped-item entity appears in the world.</summary>
    public event Action<EntityItemSpawnPacket>? OnEntityItemSpawn;
    /// <summary>Raised when a dropped-item entity is removed (picked up or despawned).</summary>
    public event Action<EntityItemRemovePacket>? OnEntityItemRemove;
    /// <summary>Raised when the connection drops or is rejected.</summary>
    public event Action<string>? OnDisconnected;

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private NetManager? _net;
    private NetPeer? _server;
    private readonly NetDataWriter _writer = new();

    /// <summary>Thread-safe queue: packets received on the poll thread, drained on main thread.</summary>
    private readonly Queue<(PacketType Type, byte[] Data)> _inbox = new();
    private readonly object _inboxLock = new();

    public bool IsConnected => _server?.ConnectionState == ConnectionState.Connected;
    public bool IsConnecting => _server?.ConnectionState == ConnectionState.Outgoing;

    /// <summary>Our own player id, assigned after receiving the first <see cref="PlayerJoinPacket"/>
    /// that matches our name. Set externally by the join flow in <see cref="Game"/>.</summary>
    public int LocalPlayerId { get; set; } = -1;

    // -------------------------------------------------------------------------
    // Connect / Disconnect
    // -------------------------------------------------------------------------

    /// <summary>
    /// Begins connecting to <paramref name="host"/>:<paramref name="port"/> with the
    /// given <paramref name="playerName"/>. Non-blocking; poll <see cref="IsConnected"/>.
    /// </summary>
    public void Connect(string host, int port, string playerName)
    {
        var listener = new EventBasedNetListener();
        _net = new NetManager(listener)
        {
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,
        };
        _net.Start();

        listener.PeerConnectedEvent += peer => _server = peer;
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            _server = null;
            OnDisconnected?.Invoke(info.Reason.ToString());
        };
        listener.NetworkReceiveEvent += (peer, reader, channel, delivery) =>
        {
            // Copy bytes so the reader can be recycled immediately.
            var data = new byte[reader.AvailableBytes];
            reader.GetBytes(data, data.Length);
            reader.Recycle();
            lock (_inboxLock)
                _inbox.Enqueue(((PacketType)data[0], data));
        };

        var writer = new NetDataWriter();
        writer.Put(GameServer.ConnectionKey);
        writer.Put(playerName);
        _net.Connect(host, port, writer);
    }

    /// <summary>Sends a clean disconnect and shuts down the socket.</summary>
    public void Disconnect()
    {
        _server?.Disconnect();
        _net?.Stop();
        _net = null;
        _server = null;
    }

    public void Dispose() => Disconnect();

    // -------------------------------------------------------------------------
    // Main-thread tick — drain inbox, fire events
    // -------------------------------------------------------------------------

    /// <summary>
    /// Must be called every frame on the main/render thread. Polls the network and
    /// dispatches buffered packets to the event subscribers.
    /// </summary>
    public void Tick()
    {
        _net?.PollEvents();

        (PacketType type, byte[] data)[] pending;
        lock (_inboxLock)
        {
            if (_inbox.Count == 0) return;
            pending = _inbox.ToArray();
            _inbox.Clear();
        }

        foreach (var (type, data) in pending)
        {
            using var ms = new System.IO.MemoryStream(data, 1, data.Length - 1, writable: false);
            using var br = new System.IO.BinaryReader(ms);
            var reader = new NetDataReader(data, 1, data.Length - 1);

            switch (type)
            {
                case PacketType.WorldInfo:
                    OnWorldInfo?.Invoke(PacketSerializer.DeserializeWorldInfo(reader));
                    break;
                case PacketType.ChunkData:
                    OnChunkData?.Invoke(PacketSerializer.DeserializeChunkData(reader));
                    break;
                case PacketType.BlockUpdate:
                    OnBlockUpdate?.Invoke(PacketSerializer.DeserializeBlockUpdate(reader));
                    break;
                case PacketType.PlayerJoin:
                    OnPlayerJoin?.Invoke(PacketSerializer.DeserializePlayerJoin(reader));
                    break;
                case PacketType.PlayerLeave:
                    OnPlayerLeave?.Invoke(PacketSerializer.DeserializePlayerLeave(reader));
                    break;
                case PacketType.PlayerState:
                    OnPlayerState?.Invoke(PacketSerializer.DeserializePlayerState(reader));
                    break;
                case PacketType.ChatMessage:
                    OnChatMessage?.Invoke(PacketSerializer.DeserializeChatMessage(reader));
                    break;
                case PacketType.EntityItemSpawn:
                    OnEntityItemSpawn?.Invoke(PacketSerializer.DeserializeEntityItemSpawn(reader));
                    break;
                case PacketType.EntityItemRemove:
                    OnEntityItemRemove?.Invoke(PacketSerializer.DeserializeEntityItemRemove(reader));
                    break;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Send helpers
    // -------------------------------------------------------------------------

    /// <summary>Sends the local player's position and look angles to the server (20 Hz).</summary>
    public void SendPlayerState(Vector3 position, float yaw, float pitch)
    {
        if (_server == null) return;
        _writer.Reset();
        PacketSerializer.Serialize(_writer, new PlayerStateClientPacket
        {
            Position = position,
            Yaw = yaw,
            Pitch = pitch,
        });
        _server.Send(_writer, DeliveryMethod.Sequenced);
    }

    /// <summary>Sends a block break/place action to the server for authoritative application.</summary>
    public void SendPlayerAction(PlayerActionKind kind, Vector3i blockPos, ushort blockId, Vector3i normal)
    {
        if (_server == null) return;
        _writer.Reset();
        PacketSerializer.Serialize(_writer, new PlayerActionPacket
        {
            Kind = kind,
            BlockPos = blockPos,
            BlockId = blockId,
            Normal = normal,
        });
        _server.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Sends a chat message to the server for broadcast.</summary>
    public void SendChat(string message)
    {
        if (_server == null || string.IsNullOrWhiteSpace(message)) return;
        _writer.Reset();
        PacketSerializer.Serialize(_writer, new ChatSendPacket { Message = message });
        _server.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Notifies the server that the local player dropped an item.</summary>
    public void SendDropItem(int itemId, int count, Vector3 position, Vector3 velocity)
    {
        if (_server == null) return;
        _writer.Reset();
        PacketSerializer.Serialize(_writer, new DropItemPacket
        {
            ItemId = itemId,
            Count = count,
            Position = position,
            Velocity = velocity,
        });
        _server.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Notifies the server that the local player picked up a networked entity item.</summary>
    public void SendPickupEntity(int entityId)
    {
        if (_server == null) return;
        _writer.Reset();
        PacketSerializer.Serialize(_writer, new PickupEntityPacket { EntityId = entityId });
        _server.Send(_writer, DeliveryMethod.ReliableOrdered);
    }
}

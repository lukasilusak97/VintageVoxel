using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using OpenTK.Mathematics;

namespace VintageVoxel.Networking;

/// <summary>
/// Headless authoritative game server. Runs on a dedicated background thread at
/// ~20 Hz. Clients connect via UDP; the server owns the canonical <see cref="World"/>
/// and broadcasts state changes to all connected peers.
///
/// Usage:
///   var server = new GameServer(savePath, seed, isFlat);
///   server.Start(port);          // non-blocking — spins up background thread
///   ...
///   server.Stop();               // saves + shuts down
/// </summary>
public sealed class GameServer : IDisposable
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    public const int DefaultPort = 25565;
    public const string ConnectionKey = "VintageVoxel";
    private const int TickRateHz = 20;

    // -------------------------------------------------------------------------
    // Per-client record
    // -------------------------------------------------------------------------

    private sealed class ConnectedPlayer
    {
        public int PlayerId;
        public string Name = "";
        public NetPeer Peer = null!;
        public Vector3 Position;
        public float Yaw;
        public float Pitch;
        /// <summary>Chunk coords already sent to this client (no need to resend).</summary>
        public readonly HashSet<Vector3i> SentChunks = new();
    }

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    private readonly string _savePath;
    private readonly int _seed;
    private readonly bool _isFlat;
    private readonly string _serverName;

    private readonly World _world;

    private NetManager? _net;
    private Thread? _thread;
    private volatile bool _running;
    private int _nextPlayerId = 1;

    private readonly Dictionary<int, ConnectedPlayer> _players = new(); // keyed by peer.Id
    private readonly NetDataWriter _writer = new();
    private readonly LanDiscovery _discovery = new();

    /// <summary>Server-side entity items keyed by their network-assigned ID.</summary>
    private readonly Dictionary<int, (int ItemId, int Count)> _serverEntityItems = new();
    private int _nextEntityId = 1;

    /// <summary>Number of players currently connected.</summary>
    public int PlayerCount => _players.Count;

    // -------------------------------------------------------------------------
    // Constructor
    // -------------------------------------------------------------------------

    public GameServer(string savePath, int seed, bool isFlat, string serverName = "VintageVoxel Server")
    {
        _savePath = savePath;
        _seed = seed;
        _isFlat = isFlat;
        _serverName = serverName;

        // Build the server-side world (no renderer needed).
        _world = new World();
        // Pre-load the origin area so the first client doesn't stall.
        _world.Update(new Vector3(0, 0, 0), out var initial, out _, out _);
        foreach (var key in initial)
        {
            if (WorldPersistence.TryLoadChunk(_savePath, key, out Chunk? saved))
                _world.ReplaceChunk(key, saved);
        }
        LightEngine.PropagateSunlight(_world);
    }

    // -------------------------------------------------------------------------
    // Lifecycle
    // -------------------------------------------------------------------------

    /// <summary>Starts listening on <paramref name="port"/> (non-blocking).</summary>
    public void Start(int port = DefaultPort)
    {
        var listener = new EventBasedNetListener();
        _net = new NetManager(listener);

        listener.ConnectionRequestEvent += OnConnectionRequest;
        listener.PeerConnectedEvent += OnPeerConnected;
        listener.PeerDisconnectedEvent += OnPeerDisconnected;
        listener.NetworkReceiveEvent += OnNetworkReceive;

        _net.Start(port);

        // Start the separate UDP responder for LAN discovery.
        _discovery.StartResponder(_serverName, () => PlayerCount);

        _running = true;

        _thread = new Thread(ServerLoop)
        {
            Name = "GameServer",
            IsBackground = true,
        };
        _thread.Start();
    }

    /// <summary>Saves the world and stops the server.</summary>
    public void Stop()
    {
        _running = false;
        WorldPersistence.SaveAll(_savePath, _world);
        _discovery.Dispose();
        _net?.Stop();
        _thread?.Join(2000);
    }

    public void Dispose() => Stop();

    // -------------------------------------------------------------------------
    // Server loop (background thread)
    // -------------------------------------------------------------------------

    private void ServerLoop()
    {
        var interval = TimeSpan.FromMilliseconds(1000.0 / TickRateHz);
        while (_running)
        {
            var start = DateTime.UtcNow;
            _net!.PollEvents();
            Tick();
            var elapsed = DateTime.UtcNow - start;
            var sleep = interval - elapsed;
            if (sleep > TimeSpan.Zero)
                Thread.Sleep(sleep);
        }
    }

    private void Tick()
    {
        // For each connected client, stream any unsent nearby chunks.
        foreach (var player in _players.Values)
            StreamChunksToPlayer(player);
    }

    // -------------------------------------------------------------------------
    // LiteNetLib event handlers
    // -------------------------------------------------------------------------

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (request.Data.TryGetString(out string key) && key == ConnectionKey)
            request.Accept();
        else
            request.Reject();
    }

    private void OnPeerConnected(NetPeer peer)
    {
        var cp = new ConnectedPlayer
        {
            PlayerId = _nextPlayerId++,
            Peer = peer,
        };

        // Read the player name sent in the connect data (written by GameClient).
        // The name was already consumed during the handshake; we re-read via user data.
        if (peer.Tag is string peerName)
            cp.Name = peerName;
        else
            cp.Name = $"Player{cp.PlayerId}";

        _players[peer.Id] = cp;

        // Tell this client world metadata.
        _writer.Reset();
        PacketSerializer.Serialize(_writer, new WorldInfoPacket
        {
            Seed = _seed,
            IsFlat = _isFlat,
            SpawnPos = new Vector3(16f, 35f, 5f),
        });
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);

        // Notify all *other* clients about the newcomer.
        BroadcastExcept(peer.Id, w =>
            PacketSerializer.Serialize(w, new PlayerJoinPacket
            {
                PlayerId = cp.PlayerId,
                Name = cp.Name,
            }), DeliveryMethod.ReliableOrdered);

        // Tell the newcomer about all already-connected players.
        foreach (var other in _players.Values)
        {
            if (other.PlayerId == cp.PlayerId) continue;
            _writer.Reset();
            PacketSerializer.Serialize(_writer, new PlayerJoinPacket
            {
                PlayerId = other.PlayerId,
                Name = other.Name,
            });
            peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo _)
    {
        if (!_players.TryGetValue(peer.Id, out var cp)) return;
        _players.Remove(peer.Id);

        BroadcastExcept(-1, w =>
            PacketSerializer.Serialize(w, new PlayerLeavePacket { PlayerId = cp.PlayerId }),
            DeliveryMethod.ReliableOrdered);
    }

    private void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
    {
        if (!_players.TryGetValue(peer.Id, out var cp)) { reader.Recycle(); return; }

        if (reader.AvailableBytes < 1) { reader.Recycle(); return; }
        var type = (PacketType)reader.GetByte();

        switch (type)
        {
            case PacketType.PlayerStateC:
                HandlePlayerState(cp, PacketSerializer.DeserializePlayerStateClient(reader));
                break;
            case PacketType.PlayerAction:
                HandlePlayerAction(cp, PacketSerializer.DeserializePlayerAction(reader));
                break;
            case PacketType.ChatSend:
                HandleChat(cp, PacketSerializer.DeserializeChatSend(reader));
                break;
            case PacketType.DropItem:
                HandleDropItem(cp, PacketSerializer.DeserializeDropItem(reader));
                break;
            case PacketType.PickupEntity:
                HandlePickupEntity(cp, PacketSerializer.DeserializePickupEntity(reader));
                break;
        }

        reader.Recycle();
    }

    // -------------------------------------------------------------------------
    // Packet handlers
    // -------------------------------------------------------------------------

    private void HandlePlayerState(ConnectedPlayer cp, PlayerStateClientPacket p)
    {
        cp.Position = p.Position;
        cp.Yaw = p.Yaw;
        cp.Pitch = p.Pitch;

        // Relay to all other clients.
        BroadcastExcept(cp.Peer.Id, w =>
            PacketSerializer.Serialize(w, new PlayerStatePacket
            {
                PlayerId = cp.PlayerId,
                Position = cp.Position,
                Yaw = cp.Yaw,
                Pitch = cp.Pitch,
            }), DeliveryMethod.Sequenced);
    }

    private void HandlePlayerAction(ConnectedPlayer cp, PlayerActionPacket p)
    {
        // Validate range (max 6 blocks).
        float dist = (p.BlockPos - new Vector3i(
            (int)cp.Position.X, (int)cp.Position.Y, (int)cp.Position.Z)).EuclideanLength;
        if (dist > 8f) return;

        switch (p.Kind)
        {
            case PlayerActionKind.Break:
                {
                    ushort brokenId = _world.GetBlock(p.BlockPos.X, p.BlockPos.Y, p.BlockPos.Z).Id;
                    _world.SetBlock(p.BlockPos.X, p.BlockPos.Y, p.BlockPos.Z, Block.Air);
                    LightEngine.UpdateAtBlock(p.BlockPos, _world);
                    BroadcastAll(w =>
                        PacketSerializer.Serialize(w, new BlockUpdatePacket
                        {
                            BlockPos = p.BlockPos,
                            BlockId = Block.Air.Id,
                        }), DeliveryMethod.ReliableOrdered);
                    // Spawn a dropped item entity for the broken block.
                    var dropPos = new Vector3(p.BlockPos.X + 0.5f, p.BlockPos.Y + 0.5f, p.BlockPos.Z + 0.5f);
                    SpawnEntityItem(brokenId, 1, dropPos, new Vector3(0f, 3f, 0f));
                    break;
                }

            case PlayerActionKind.Place:
                {
                    var target = p.BlockPos + p.Normal;
                    var block = new Block { Id = p.BlockId, IsTransparent = BlockRegistry.IsTransparent(p.BlockId), Layer = (byte)(p.BlockId == 0 ? 0 : 16) };
                    _world.SetBlock(target.X, target.Y, target.Z, block);
                    LightEngine.UpdateAtBlock(target, _world);
                    BroadcastAll(w =>
                        PacketSerializer.Serialize(w, new BlockUpdatePacket
                        {
                            BlockPos = target,
                            BlockId = p.BlockId,
                        }), DeliveryMethod.ReliableOrdered);
                    break;
                }
        }
    }

    private void HandleChat(ConnectedPlayer cp, ChatSendPacket p)
    {
        // Sanitize message length.
        var msg = p.Message.Length > 256 ? p.Message[..256] : p.Message;
        BroadcastAll(w =>
            PacketSerializer.Serialize(w, new ChatMessagePacket
            {
                PlayerId = cp.PlayerId,
                Name = cp.Name,
                Message = msg,
            }), DeliveryMethod.ReliableOrdered);
    }

    private void HandleDropItem(ConnectedPlayer cp, DropItemPacket p)
    {
        // Basic validation: item count must be positive.
        if (p.Count <= 0) return;

        int entityId = _nextEntityId++;
        _serverEntityItems[entityId] = (p.ItemId, p.Count);

        BroadcastAll(w =>
            PacketSerializer.Serialize(w, new EntityItemSpawnPacket
            {
                EntityId = entityId,
                ItemId = p.ItemId,
                Count = p.Count,
                Position = p.Position,
                Velocity = p.Velocity,
            }), DeliveryMethod.ReliableOrdered);
    }

    private void HandlePickupEntity(ConnectedPlayer cp, PickupEntityPacket p)
    {
        // Only process if the entity still exists (prevents double-pickup).
        if (!_serverEntityItems.Remove(p.EntityId)) return;

        // Notify all OTHER clients to remove the entity; the requester already removed it locally.
        BroadcastExcept(cp.Peer.Id, w =>
            PacketSerializer.Serialize(w, new EntityItemRemovePacket { EntityId = p.EntityId }),
            DeliveryMethod.ReliableOrdered);
    }

    private void SpawnEntityItem(int itemId, int count, Vector3 position, Vector3 velocity)
    {
        if (ItemRegistry.Get(itemId) == null) return;

        int entityId = _nextEntityId++;
        _serverEntityItems[entityId] = (itemId, count);

        BroadcastAll(w =>
            PacketSerializer.Serialize(w, new EntityItemSpawnPacket
            {
                EntityId = entityId,
                ItemId = itemId,
                Count = count,
                Position = position,
                Velocity = velocity,
            }), DeliveryMethod.ReliableOrdered);
    }

    // -------------------------------------------------------------------------
    // Chunk streaming
    // -------------------------------------------------------------------------

    private void StreamChunksToPlayer(ConnectedPlayer cp)
    {
        var center = World.WorldToChunk(cp.Position);
        const int streamRadius = World.RenderDistance;

        for (int dz = -streamRadius; dz <= streamRadius; dz++)
            for (int dx = -streamRadius; dx <= streamRadius; dx++)
                for (int cy = 0; cy < World.MaxChunkY; cy++)
                {
                    var key = new Vector3i(center.X + dx, cy, center.Y + dz);
                    if (cp.SentChunks.Contains(key)) continue;

                    // Ensure the chunk is loaded server-side.
                    if (!_world.Chunks.TryGetValue(key, out Chunk? chunk))
                    {
                        _world.Update(cp.Position, out _, out _, out _);
                        if (!_world.Chunks.TryGetValue(key, out chunk)) continue;
                    }

                    // Encode and send.
                    var data = PacketSerializer.EncodeChunkBlocks(chunk);
                    _writer.Reset();
                    PacketSerializer.Serialize(_writer, new ChunkDataPacket
                    {
                        ChunkCoord = key,
                        BlockData = data,
                    });
                    cp.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);
                    cp.SentChunks.Add(key);
                }
    }

    // -------------------------------------------------------------------------
    // Broadcast helpers
    // -------------------------------------------------------------------------

    private void BroadcastAll(Action<NetDataWriter> write, DeliveryMethod delivery)
    {
        _writer.Reset();
        write(_writer);
        foreach (var cp in _players.Values)
            cp.Peer.Send(_writer, delivery);
    }

    private void BroadcastExcept(int excludePeerId, Action<NetDataWriter> write, DeliveryMethod delivery)
    {
        _writer.Reset();
        write(_writer);
        foreach (var cp in _players.Values)
            if (cp.Peer.Id != excludePeerId)
                cp.Peer.Send(_writer, delivery);
    }
}

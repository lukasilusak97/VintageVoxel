using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VintageVoxel.Networking;

/// <summary>
/// LAN discovery via UDP broadcast.
///
/// Server side: <see cref="GameServer"/> calls <see cref="StartResponder"/> to listen
/// and reply to discovery probes.
///
/// Client side: call <see cref="StartSearch"/>, poll <see cref="Poll"/> each frame,
/// then read <see cref="Servers"/>.
/// </summary>
public sealed class LanDiscovery : IDisposable
{
    public const int DiscoveryPort = 25566;
    private static readonly byte[] ProbeBytes = Encoding.ASCII.GetBytes("VVDISC");
    private static readonly byte[] AckPrefix = Encoding.ASCII.GetBytes("VVACK:");

    /// <summary>A server found on the local network.</summary>
    public sealed class DiscoveredServer
    {
        public string Name = "";
        public int PlayerCount;
        public string IpAddress = "";
        public int Port;
    }

    // ── Client state ──────────────────────────────────────────────────────────
    private UdpClient? _searchClient;
    private readonly List<DiscoveredServer> _servers = new();
    private readonly object _lock = new();

    // ── Server (responder) state ───────────────────────────────────────────────
    private UdpClient? _responder;
    private Thread? _responderThread;
    private volatile bool _responderRunning;

    public IReadOnlyList<DiscoveredServer> Servers
    {
        get { lock (_lock) return _servers.ToArray(); }
    }

    // ── Client API ────────────────────────────────────────────────────────────

    /// <summary>Broadcasts a discovery probe and begins collecting responses.</summary>
    public void StartSearch()
    {
        _searchClient?.Dispose();
        lock (_lock) _servers.Clear();

        _searchClient = new UdpClient { EnableBroadcast = true };
        _searchClient.Client.ReceiveTimeout = 100;

        // Broadcast the probe.
        _searchClient.Send(ProbeBytes, ProbeBytes.Length,
                           new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
    }

    /// <summary>
    /// Drains any pending responses. Must be called frequently (every game frame).
    /// </summary>
    public void Poll()
    {
        if (_searchClient == null) return;
        try
        {
            while (_searchClient.Available > 0)
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _searchClient.Receive(ref remote);
                if (data.Length <= AckPrefix.Length) continue;

                // Validate prefix.
                for (int i = 0; i < AckPrefix.Length; i++)
                    if (data[i] != AckPrefix[i]) goto next;

                string payload = Encoding.UTF8.GetString(data, AckPrefix.Length, data.Length - AckPrefix.Length);
                // Format: "ServerName|PlayerCount"
                var parts = payload.Split('|');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int count)) goto next;

                string ip = remote.Address.ToString();
                lock (_lock)
                {
                    if (_servers.Any(s => s.IpAddress == ip)) goto next;
                    _servers.Add(new DiscoveredServer
                    {
                        Name = parts[0],
                        PlayerCount = count,
                        IpAddress = ip,
                        Port = GameServer.DefaultPort,
                    });
                }
            next:;
            }
        }
        catch { /* timeout is normal */ }
    }

    // ── Server (responder) API ─────────────────────────────────────────────────

    /// <summary>
    /// Starts a background thread that listens for discovery probes and responds.
    /// Called by <see cref="GameServer"/> on startup.
    /// </summary>
    public void StartResponder(string serverName, Func<int> getPlayerCount)
    {
        _responder = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
        _responderRunning = true;
        _responderThread = new Thread(() => ResponderLoop(serverName, getPlayerCount))
        {
            Name = "LanDiscoveryResponder",
            IsBackground = true,
        };
        _responderThread.Start();
    }

    private void ResponderLoop(string serverName, Func<int> getPlayerCount)
    {
        while (_responderRunning)
        {
            try
            {
                var remote = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _responder!.Receive(ref remote);
                if (data.Length != ProbeBytes.Length) continue;
                for (int i = 0; i < ProbeBytes.Length; i++)
                    if (data[i] != ProbeBytes[i]) goto next;

                string payload = $"{serverName}|{getPlayerCount()}";
                byte[] resp = AckPrefix.Concat(Encoding.UTF8.GetBytes(payload)).ToArray();
                _responder.Send(resp, resp.Length, remote);
            next:;
            }
            catch { /* socket closed or timeout — exit cleanly */ break; }
        }
    }

    public void Dispose()
    {
        _responderRunning = false;
        _responder?.Close();
        _searchClient?.Dispose();
    }
}

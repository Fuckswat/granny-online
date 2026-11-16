using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace GrannyMP;

public enum Msg : byte
{
    Hello = 1, Welcome = 2, Roster = 3,
    PlayerState = 10, BodyState = 11,
    Caught = 20, TrapPlace = 21, TrapSprung = 22, Zone = 23, Possess = 24, Noise = 25, Stun = 26,
    Door = 30, Obj = 31, SnapDoors = 32, SnapObjs = 33, Exit = 34, Settings = 35,
    RoundStart = 40, RoundEnd = 41, DayUpdate = 42, Escaped = 43, SceneInfo = 44, Toast = 45, GrannyChange = 46, ExecStart = 47, ExecStep = 48, ItemDrop = 49, ItemMove = 50, ItemGone = 51,
}

/// <summary>LiteNetLib wrapper. Host = authoritative relay running inside the host's game process.</summary>
public sealed class NetSession
{
    public const string Key = "GrannyMP-1";
    public const byte ChReliable = 0, ChFast = 1;

    public bool IsHost { get; private set; }
    public bool IsClient { get; private set; }
    public bool Active => IsHost || (IsClient && _server != null && _server.ConnectionState == ConnectionState.Connected);
    public bool Connecting => IsClient && !Active;
    public byte LocalId { get; set; }

    public Action<Msg, byte, NetDataReader> OnMessage;
    public Action<byte> OnPeerJoined;       // host only
    public Action<byte> OnPeerLeft;         // host only
    public Action OnConnected;              // client only
    public Action<string> OnDisconnected;   // client only

    readonly EventBasedNetListener _listener = new();
    NetManager _nm;
    NetPeer _server;
    readonly Dictionary<int, byte> _peerToId = new();
    readonly Dictionary<byte, NetPeer> _idToPeer = new();
    readonly NetDataWriter _w = new();
    byte _nextId = 1;

    static readonly HashSet<Msg> Relayed = new()
    {
        Msg.PlayerState, Msg.BodyState, Msg.Caught, Msg.TrapPlace, Msg.TrapSprung, Msg.Noise, Msg.Stun, Msg.Door, Msg.Obj, Msg.Exit, Msg.ExecStep, Msg.ItemDrop, Msg.ItemMove, Msg.ItemGone,
    };

    public NetDataWriter Writer => _w;

    public NetSession()
    {
        _listener.ConnectionRequestEvent += req =>
        {
            if (IsHost && _nm.ConnectedPeersCount < 15) req.AcceptIfKey(Key); else req.Reject();
        };
        _listener.PeerConnectedEvent += peer =>
        {
            if (IsHost)
            {
                var id = _nextId++;
                _peerToId[peer.Id] = id; _idToPeer[id] = peer;
                OnPeerJoined?.Invoke(id);
            }
            else { _server = peer; OnConnected?.Invoke(); }
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            if (IsHost)
            {
                if (_peerToId.TryGetValue(peer.Id, out var id))
                {
                    _peerToId.Remove(peer.Id); _idToPeer.Remove(id);
                    OnPeerLeft?.Invoke(id);
                }
            }
            else
            {
                var reason = info.Reason.ToString();
                Stop();
                OnDisconnected?.Invoke(reason);
            }
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            var data = new byte[reader.AvailableBytes];
            reader.GetBytes(data, data.Length);
            if (data.Length < 2) return;
            var type = (Msg)data[0];
            if (IsHost)
            {
                if (!_peerToId.TryGetValue(peer.Id, out var sender)) return;
                data[1] = sender; // never trust the sender byte from the wire
                if (Relayed.Contains(type)) _nm.SendToAll(data, channel, method, peer);
            }
            var r = new NetDataReader(data, 2, data.Length);
            try { OnMessage?.Invoke(type, data[1], r); }
            catch (Exception e) { Plugin.L.LogError($"[net] handler {type} failed: {e}"); }
        };
    }

    public string Host(int port)
    {
        Stop();
        _nm = new NetManager(_listener) { ChannelsCount = 2, DisconnectTimeout = 15000, AutoRecycle = true, UnsyncedEvents = false, IPv6Enabled = false };
        if (!_nm.Start(port)) { _nm = null; return Tr.L($"Не удалось открыть порт {port} (занят?)", $"Cannot open port {port} (in use?)"); }
        IsHost = true; LocalId = 0; _nextId = 1;
        return null;
    }

    public string Connect(string ip, int port)
    {
        Stop();
        _nm = new NetManager(_listener) { ChannelsCount = 2, DisconnectTimeout = 15000, AutoRecycle = true, IPv6Enabled = false };
        if (!_nm.Start()) { _nm = null; return Tr.L("Не удалось запустить сеть", "Cannot start the network"); }
        IsClient = true;
        try { _nm.Connect(ip.Trim(), port, Key); }
        catch (Exception e) { Stop(); return Tr.L("Ошибка подключения: ", "Connection error: ") + e.Message; }
        return null;
    }

    public void Stop()
    {
        try { _nm?.Stop(); } catch { }
        _nm = null; _server = null; IsHost = false; IsClient = false;
        _peerToId.Clear(); _idToPeer.Clear();
    }

    public void Poll() { try { _nm?.PollEvents(); } catch (Exception e) { Plugin.L.LogError("[net] poll " + e); } }

    public int Ping => _server?.Ping ?? 0;

    public NetDataWriter Begin(Msg m)
    {
        _w.Reset(); _w.Put((byte)m); _w.Put(LocalId); return _w;
    }

    /// <summary>Host: to all clients. Client: to host (host relays if needed).</summary>
    public void Send(bool reliable = true)
    {
        if (_nm == null) return;
        var ch = reliable ? ChReliable : ChFast;
        var dm = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced;
        if (IsHost) _nm.SendToAll(_w, ch, dm);
        else _server?.Send(_w, ch, dm);
    }

    public void SendTo(byte id)
    {
        if (IsHost && _idToPeer.TryGetValue(id, out var p)) p.Send(_w, ChReliable, DeliveryMethod.ReliableOrdered);
    }

    static string _friendIp = "?";
    static long _friendIpAt = -1;

    /// <summary>True when the address is on Radmin and the like: it works for a friend in another place.</summary>
    public static bool FriendIpIsOverlay { get; private set; }

    /// <summary>The one address to give to a friend: the Radmin-like network if there is one, otherwise the real Wi-Fi or cable.</summary>
    public static string FriendIp()
    {
        var now = Environment.TickCount64;
        if (_friendIpAt >= 0 && now - _friendIpAt < 5000) return _friendIp;
        _friendIpAt = now;
        try { FindFriendIp(); }
        catch (Exception e) { Plugin.L.LogWarning("[net] ip: " + e.Message); }
        return _friendIp;
    }

    static void FindFriendIp()
    {
        string[] overlay = { "radmin", "hamachi", "zerotier", "tailscale" };
        string[] skip = { "virtual", "vmware", "hyper-v", "vethernet", "wsl", "docker", "tunnel", "wintun", "wireguard", "vpn", "xray", "singbox", "sing-box", "happ", "tap-", "tun", "bluetooth" };
        var best = -1; string found = null; var foundOverlay = false;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var name = (n.Description + " " + n.Name).ToLowerInvariant();
            var isOverlay = overlay.Any(name.Contains);
            if (!isOverlay && skip.Any(name.Contains)) continue;
            var props = n.GetIPProperties();
            var gateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            var score = isOverlay ? 100 : gateway ? 50 : 10;
            if (score <= best) continue;
            foreach (var u in props.UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = u.Address.ToString();
                if (ip.StartsWith("169.254.")) continue;
                best = score; found = ip; foundOverlay = isOverlay;
                break;
            }
        }
        if (found == null) found = Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "?";
        if (found != _friendIp) Plugin.L.LogInfo($"[net] friend IP {found}, overlay={foundOverlay}");
        _friendIp = found; FriendIpIsOverlay = foundOverlay;
    }
}

public static class NetExt
{
    public static void Put(this NetDataWriter w, UnityEngine.Vector3 v) { w.Put(v.x); w.Put(v.y); w.Put(v.z); }
    public static UnityEngine.Vector3 GetVec3(this NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat());
    public static void Put(this NetDataWriter w, UnityEngine.Quaternion q) { w.Put(q.x); w.Put(q.y); w.Put(q.z); w.Put(q.w); }
    public static UnityEngine.Quaternion GetQuat(this NetDataReader r) => new(r.GetFloat(), r.GetFloat(), r.GetFloat(), r.GetFloat());
}

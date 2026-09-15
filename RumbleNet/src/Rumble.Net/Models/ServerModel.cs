using System.Collections.Concurrent;
using Rumble.Net.Events;

namespace Rumble.Net.Models;

/// <summary>
/// Live, thread-safe mirror of the server state, maintained from native events. Readers get
/// lock-free access; every channel/user object is an immutable snapshot replaced on update.
/// </summary>
public sealed class ServerModel
{
    private readonly ConcurrentDictionary<uint, Channel> _channels = new();
    private readonly ConcurrentDictionary<uint, User> _users = new();
    private readonly ConcurrentDictionary<uint, byte> _talking = new();

    /// <summary>Local session id once connected.</summary>
    public uint? Session { get; private set; }

    /// <summary>Server information.</summary>
    public ServerInfo Info { get; private set; } = new();

    /// <summary>All channels (unordered).</summary>
    public ICollection<Channel> Channels => _channels.Values;

    /// <summary>All users (unordered).</summary>
    public ICollection<User> Users => _users.Values;

    /// <summary>The root channel.</summary>
    public Channel? Root => GetChannel(0) ?? _channels.Values.FirstOrDefault(c => c.IsRoot);

    /// <summary>The local user.</summary>
    public User? Self => Session is { } s ? GetUser(s) : null;

    /// <summary>Last measured TCP ping (ms).</summary>
    public float TcpPingMs { get; private set; }

    /// <summary>Last measured UDP ping (ms).</summary>
    public float UdpPingMs { get; private set; }

    /// <summary>Voice is using UDP.</summary>
    public bool UdpActive { get; private set; }

    /// <summary>Gets a channel by id.</summary>
    public Channel? GetChannel(uint id) => _channels.TryGetValue(id, out var c) ? c : null;

    /// <summary>Gets a user by session.</summary>
    public User? GetUser(uint session) => _users.TryGetValue(session, out var u) ? u : null;

    /// <summary>Finds a channel by name (case-insensitive).</summary>
    public Channel? FindChannel(string name) =>
        _channels.Values.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a channel by path such as <c>Root/Games/Minecraft</c> or <c>Games/Minecraft</c>.</summary>
    public Channel? FindChannelByPath(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = Root;
        if (current is null)
        {
            return null;
        }

        var start = parts.Length > 0 && string.Equals(parts[0], current.Name, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        foreach (var part in parts.Skip(start))
        {
            current = current.Children.FirstOrDefault(c => string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>Finds a user by name (case-insensitive).</summary>
    public User? FindUser(string name) =>
        _users.Values.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    internal bool IsTalking(uint session) => _talking.ContainsKey(session);

    internal void Clear()
    {
        _channels.Clear();
        _users.Clear();
        _talking.Clear();
        Session = null;
        UdpActive = false;
    }

    internal void LoadSnapshot(ServerSnapshot snapshot)
    {
        Clear();
        Session = snapshot.Session;
        Info = snapshot.Server;
        foreach (var c in snapshot.Channels)
        {
            _channels[c.Id] = c.WithModel(this);
        }

        foreach (var u in snapshot.Users)
        {
            _users[u.Session] = u.WithModel(this);
        }

        TcpPingMs = snapshot.TcpPingMs;
        UdpPingMs = snapshot.UdpPingMs;
        UdpActive = snapshot.UdpActive;
    }

    /// <summary>Applies a native event to the model.</summary>
    internal void Apply(RumbleEvent e)
    {
        switch (e)
        {
            case ChannelAddedEvent a:
                _channels[a.Channel.Id] = a.Channel.WithModel(this);
                break;
            case ChannelUpdatedEvent u:
                _channels[u.Channel.Id] = u.Channel.WithModel(this);
                break;
            case ChannelRemovedEvent r:
                _channels.TryRemove(r.ChannelId, out _);
                break;
            case UserJoinedEvent j:
                _users[j.User.Session] = j.User.WithModel(this);
                break;
            case UserUpdatedEvent u:
                _users[u.User.Session] = u.User.WithModel(this);
                break;
            case UserLeftEvent l:
                _users.TryRemove(l.Session, out _);
                _talking.TryRemove(l.Session, out _);
                break;
            case UserTalkingEvent t when t.Talking:
                _talking[t.Session] = 0;
                break;
            case UserTalkingEvent t:
                _talking.TryRemove(t.Session, out _);
                break;
            case ConnectedEvent c:
                Session = c.Session;
                Info = c.Server;
                break;
            case ServerConfigUpdatedEvent s:
                Info = s.Server;
                break;
            case PermissionsUpdatedEvent p:
                ApplyPermissions(p);
                break;
            case PingUpdatedEvent p:
                TcpPingMs = p.TcpPingMs;
                UdpPingMs = p.UdpPingMs;
                UdpActive = p.UdpActive;
                break;
            case ServerCertificateEvent cert:
                Info = Info with { CertificateFingerprint = cert.Sha1Fingerprint };
                break;
            case DisconnectedEvent:
                Clear();
                break;
        }
    }

    private void ApplyPermissions(PermissionsUpdatedEvent p)
    {
        if (p.Flush)
        {
            foreach (var (id, c) in _channels)
            {
                _channels[id] = new Channel
                {
                    Id = c.Id, ParentId = c.ParentId, Name = c.Name, Description = c.Description,
                    DescriptionHash = c.DescriptionHash, Position = c.Position, Temporary = c.Temporary,
                    MaxUsers = c.MaxUsers, Links = c.Links, IsEnterRestricted = c.IsEnterRestricted,
                    CanEnter = c.CanEnter, Permissions = null, Model = this,
                };
            }
        }

        if (p.ChannelId is { } channelId && _channels.TryGetValue(channelId, out var channel))
        {
            _channels[channelId] = new Channel
            {
                Id = channel.Id, ParentId = channel.ParentId, Name = channel.Name, Description = channel.Description,
                DescriptionHash = channel.DescriptionHash, Position = channel.Position, Temporary = channel.Temporary,
                MaxUsers = channel.MaxUsers, Links = channel.Links, IsEnterRestricted = channel.IsEnterRestricted,
                CanEnter = channel.CanEnter, Permissions = p.Permissions, Model = this,
            };
        }
    }
}

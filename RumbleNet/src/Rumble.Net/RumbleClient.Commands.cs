using Rumble.Net.Events;
using Rumble.Net.Interop;
using Rumble.Net.Models;
using Channel = Rumble.Net.Models.Channel;

namespace Rumble.Net;

public sealed partial class RumbleClient
{
    private uint RequireSession() =>
        Server.Session ?? throw new RumbleException(RumbleErrorCode.NotConnected, "The client is not connected.");

    // ------------------------------------------------------------------ channels

    /// <summary>Moves the local user to a channel and waits for confirmation.</summary>
    public Task JoinChannelAsync(Channel channel, CancellationToken cancellationToken = default)
        => JoinChannelAsync(channel.Id, cancellationToken);

    /// <summary>Moves the local user to a channel and waits for confirmation.</summary>
    public async Task JoinChannelAsync(uint channelId, CancellationToken cancellationToken = default)
    {
        var me = RequireSession();
        if (Server.Self?.ChannelId == channelId)
        {
            return;
        }

        await SendAndWaitAsync<UserMovedEvent>(new JoinChannelCommand(channelId), m => m.Session == me && m.ToChannelId == channelId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Joins a channel by name or path (e.g. <c>Games/Minecraft</c>).</summary>
    public Task JoinChannelAsync(string nameOrPath, CancellationToken cancellationToken = default)
    {
        var channel = (nameOrPath.Contains('/') ? Server.FindChannelByPath(nameOrPath) : Server.FindChannel(nameOrPath))
                      ?? throw new ArgumentException($"Channel '{nameOrPath}' was not found.", nameof(nameOrPath));
        return JoinChannelAsync(channel.Id, cancellationToken);
    }

    /// <summary>Creates a channel and returns it once the server confirms.</summary>
    public async Task<Channel> CreateChannelAsync(
        Channel parent,
        string name,
        string? description = null,
        bool temporary = false,
        int? position = null,
        uint? maxUsers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var added = await SendAndWaitAsync<ChannelAddedEvent>(
            new CreateChannelCommand(parent.Id, name, description, temporary, position, maxUsers),
            a => a.Channel.ParentId == parent.Id && a.Channel.Name == name,
            cancellationToken).ConfigureAwait(false);
        return Server.GetChannel(added.Channel.Id) ?? added.Channel;
    }

    /// <summary>Updates channel properties (null values are left unchanged).</summary>
    public void UpdateChannel(Channel channel, string? name = null, string? description = null, Channel? newParent = null, int? position = null, uint? maxUsers = null)
        => Send(new UpdateChannelCommand(channel.Id, name, description, newParent?.Id, position, maxUsers));

    /// <summary>Removes a channel and waits for confirmation.</summary>
    public async Task RemoveChannelAsync(Channel channel, CancellationToken cancellationToken = default)
        => await SendAndWaitAsync<ChannelRemovedEvent>(new RemoveChannelCommand(channel.Id), r => r.ChannelId == channel.Id, cancellationToken).ConfigureAwait(false);

    /// <summary>Links channels so voice flows between them.</summary>
    public void LinkChannels(Channel channel, params Channel[] targets)
        => Send(new LinkChannelsCommand(channel.Id, [.. targets.Select(t => t.Id)]));

    /// <summary>Removes channel links.</summary>
    public void UnlinkChannels(Channel channel, params Channel[] targets)
        => Send(new UnlinkChannelsCommand(channel.Id, [.. targets.Select(t => t.Id)]));

    /// <summary>Starts/stops listening to channels without joining them.</summary>
    public void ListenToChannels(IEnumerable<Channel>? add = null, IEnumerable<Channel>? remove = null)
        => Send(new ListenToChannelsCommand([.. (add ?? []).Select(c => c.Id)], [.. (remove ?? []).Select(c => c.Id)]));

    // ------------------------------------------------------------------ messages

    /// <summary>Sends a message to a channel.</summary>
    public void SendChannelMessage(Channel channel, string message)
        => Send(new SendTextMessageCommand([channel.Id], [], [], message));

    /// <summary>Sends a message to the local user's current channel.</summary>
    public void SendChannelMessage(string message)
    {
        var channelId = Server.Self?.ChannelId ?? throw new RumbleException(RumbleErrorCode.NotConnected, "The client is not connected.");
        Send(new SendTextMessageCommand([channelId], [], [], message));
    }

    /// <summary>Sends a message to a channel and all its sub-channels.</summary>
    public void SendTreeMessage(Channel channel, string message)
        => Send(new SendTextMessageCommand([], [], [channel.Id], message));

    /// <summary>Sends a private message.</summary>
    public void SendPrivateMessage(User user, string message)
        => Send(new SendTextMessageCommand([], [user.Session], [], message));

    /// <summary>Sends a message with arbitrary targets.</summary>
    public void SendTextMessage(string message, IEnumerable<Channel>? channels = null, IEnumerable<User>? users = null, IEnumerable<Channel>? trees = null)
        => Send(new SendTextMessageCommand(
            [.. (channels ?? []).Select(c => c.Id)],
            [.. (users ?? []).Select(u => u.Session)],
            [.. (trees ?? []).Select(c => c.Id)],
            message));

    // ------------------------------------------------------------------ self state

    /// <summary>Mutes or unmutes the local microphone (unmuting also undeafens).</summary>
    public void SetSelfMute(bool mute) => Send(new SetSelfMuteCommand(mute));

    /// <summary>Deafens or undeafens (deafening also mutes).</summary>
    public void SetSelfDeaf(bool deaf) => Send(new SetSelfDeafCommand(deaf));

    /// <summary>Sets the local user's comment.</summary>
    public void SetComment(string comment) => Send(new SetCommentCommand(comment));

    /// <summary>Sets the local user's avatar (PNG/JPEG bytes).</summary>
    public void SetAvatar(byte[] image) => Send(new SetTextureCommand(image));

    /// <summary>Registers the local user (requires a client certificate).</summary>
    public void RegisterSelf() => Send(new RegisterSelfCommand());

    /// <summary>Announces recording state to other users.</summary>
    public void SetRecording(bool recording) => Send(new SetRecordingCommand(recording));

    // ------------------------------------------------------------------ user management

    /// <summary>Moves another user to a channel.</summary>
    public void MoveUser(User user, Channel channel) => Send(new MoveUserCommand(user.Session, channel.Id));

    /// <summary>Server-mutes a user.</summary>
    public void MuteUser(User user, bool mute = true) => Send(new SetUserMuteCommand(user.Session, mute));

    /// <summary>Server-deafens a user.</summary>
    public void DeafenUser(User user, bool deaf = true) => Send(new SetUserDeafCommand(user.Session, deaf));

    /// <summary>Grants or revokes priority speaker.</summary>
    public void SetPrioritySpeaker(User user, bool priority = true) => Send(new SetPrioritySpeakerCommand(user.Session, priority));

    /// <summary>Suppresses a user.</summary>
    public void SuppressUser(User user, bool suppress = true) => Send(new SetUserSuppressedCommand(user.Session, suppress));

    /// <summary>Kicks a user and waits for confirmation.</summary>
    public async Task KickUserAsync(User user, string? reason = null, CancellationToken cancellationToken = default)
        => await SendAndWaitAsync<UserLeftEvent>(new KickUserCommand(user.Session, reason), l => l.Session == user.Session, cancellationToken).ConfigureAwait(false);

    /// <summary>Bans a user and waits for confirmation.</summary>
    public async Task BanUserAsync(User user, string? reason = null, CancellationToken cancellationToken = default)
        => await SendAndWaitAsync<UserLeftEvent>(new BanUserCommand(user.Session, reason), l => l.Session == user.Session, cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ queries

    /// <summary>Requests statistics for a user.</summary>
    public Task<UserStatsEvent> GetUserStatsAsync(User user, bool statsOnly = false, CancellationToken cancellationToken = default)
        => SendAndWaitAsync<UserStatsEvent>(new RequestUserStatsCommand(user.Session, statsOnly), s => s.Session == user.Session, cancellationToken);

    /// <summary>Retrieves the ban list (requires ban permission).</summary>
    public async Task<IReadOnlyList<BanEntry>> GetBanListAsync(CancellationToken cancellationToken = default)
        => (await SendAndWaitAsync<BanListEvent>(new RequestBanListCommand(), _ => true, cancellationToken).ConfigureAwait(false)).Bans;

    /// <summary>Replaces the ban list.</summary>
    public void SetBanList(IEnumerable<BanEntry> bans) => Send(new SetBanListCommand([.. bans]));

    /// <summary>Retrieves registered user accounts.</summary>
    public async Task<IReadOnlyList<RegisteredUser>> GetRegisteredUsersAsync(CancellationToken cancellationToken = default)
        => (await SendAndWaitAsync<RegisteredUsersEvent>(new RequestRegisteredUsersCommand(), _ => true, cancellationToken).ConfigureAwait(false)).Users;

    /// <summary>Retrieves the ACL of a channel.</summary>
    public Task<AclEvent> GetAclAsync(Channel channel, CancellationToken cancellationToken = default)
        => SendAndWaitAsync<AclEvent>(new RequestAclCommand(channel.Id), a => a.ChannelId == channel.Id, cancellationToken);

    /// <summary>Queries the local user's permissions in a channel.</summary>
    public async Task<Permissions> GetPermissionsAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        var result = await SendAndWaitAsync<PermissionsUpdatedEvent>(
            new QueryPermissionsCommand(channel.Id), p => p.ChannelId == channel.Id, cancellationToken).ConfigureAwait(false);
        return (Permissions)(result.Permissions ?? 0);
    }

    /// <summary>Resolves registered user ids and names.</summary>
    public Task<UsersQueriedEvent> QueryUsersAsync(IEnumerable<uint>? ids = null, IEnumerable<string>? names = null, CancellationToken cancellationToken = default)
        => SendAndWaitAsync<UsersQueriedEvent>(new QueryUsersCommand([.. ids ?? []], [.. names ?? []]), _ => true, cancellationToken);

    /// <summary>Requests large comments, avatars or descriptions that were sent as hashes.</summary>
    public void RequestBlobs(IEnumerable<User>? textures = null, IEnumerable<User>? comments = null, IEnumerable<Channel>? descriptions = null)
        => Send(new RequestBlobCommand(
            [.. (textures ?? []).Select(u => u.Session)],
            [.. (comments ?? []).Select(u => u.Session)],
            [.. (descriptions ?? []).Select(c => c.Id)]));

    // ------------------------------------------------------------------ voice targets & plugins

    /// <summary>Registers a whisper/shout target (id 1–30). Select it with <see cref="Audio.RumbleAudio.VoiceTarget"/>.</summary>
    public void RegisterVoiceTarget(int id, params VoiceTargetEntry[] targets)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(id, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(id, 30);
        Send(new RegisterVoiceTargetCommand((uint)id, targets));
    }

    /// <summary>Sends plugin data to other clients.</summary>
    public void SendPluginData(string dataId, byte[] data, IEnumerable<User> receivers)
        => Send(new SendPluginDataCommand([.. receivers.Select(u => u.Session)], dataId, data));

    /// <summary>Executes a server-defined context action.</summary>
    public void ExecuteContextAction(string action, User? user = null, Channel? channel = null)
        => Send(new ExecuteContextActionCommand(action, user?.Session, channel?.Id));
}

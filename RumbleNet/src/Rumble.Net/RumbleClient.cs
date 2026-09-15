using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rumble.Net.Audio;
using Rumble.Net.Events;
using Rumble.Net.Interop;
using Rumble.Net.Models;
using Rumble.Net.Plugins;
using Channel = Rumble.Net.Models.Channel;

namespace Rumble.Net;

/// <summary>
/// A Mumble client backed by the Rust core.
/// </summary>
/// <remarks>
/// <para>
/// Native events are parsed on the native thread and queued to a single event loop, which updates
/// <see cref="Server"/> and then raises the .NET events in order. Handlers therefore run on a thread
/// pool thread: marshal to your UI thread when needed.
/// </para>
/// <para>
/// Made by Gravicode Studios, led by Kang Fadhil.
/// </para>
/// </remarks>
public sealed partial class RumbleClient : IAsyncDisposable, IDisposable
{
    private readonly RumbleClientOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<RumbleEvent> _queue = System.Threading.Channels.Channel.CreateUnbounded<RumbleEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly Lock _sync = new();
    private readonly List<IEventWaiter> _waiters = [];
    private ImmutableArray<Channel<RumbleEvent>> _subscribers = [];
    private readonly Task _eventLoop;
    private RumbleClientSafeHandle? _handle;
    private TaskCompletionSource? _connectTcs;
    private string? _lastRejectType;
    private string? _lastRejectReason;
    private bool _everConnected;
    private int _disposed;

    /// <summary>Creates a client. Call <see cref="ConnectAsync"/> to connect.</summary>
    public RumbleClient(RumbleClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = options.LoggerFactory?.CreateLogger<RumbleClient>() ?? NullLogger<RumbleClient>.Instance;
        if (options.LoggerFactory is not null)
        {
            RumbleNative.ConfigureLogging(options.LoggerFactory, options.NativeLogLevel);
        }

        Audio = new RumbleAudio(this);
        Plugins = new PluginHost(this, _logger);
        _eventLoop = Task.Run(RunEventLoopAsync);
    }

    /// <summary>Creates a client with the given connection parameters.</summary>
    public RumbleClient(string host, string username, int port = 64738, string? password = null)
        : this(new RumbleClientOptions { Host = host, Username = username, Port = port, Password = password })
    {
    }

    /// <summary>Options used by this client.</summary>
    public RumbleClientOptions Options => _options;

    /// <summary>Live server model (channels, users, server info).</summary>
    public ServerModel Server { get; } = new();

    /// <summary>Audio pipeline controls.</summary>
    public RumbleAudio Audio { get; }

    /// <summary>Registered plugins.</summary>
    public PluginHost Plugins { get; }

    /// <summary>Current connection state.</summary>
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    /// <summary>True when connected and synchronized.</summary>
    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>The local user.</summary>
    public User? Self => Server.Self;

    /// <summary>All channels ordered as a depth-first tree walk.</summary>
    public IEnumerable<Channel> Channels => Server.Root?.DescendantsAndSelf() ?? Server.Channels;

    /// <summary>All users ordered by name.</summary>
    public IEnumerable<User> Users => Server.Users.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase);

    internal ILogger Logger => _logger;

    internal RumbleClientSafeHandle? Handle => _handle is { IsClosed: false } h ? h : null;

    // ------------------------------------------------------------------ events

    /// <summary>Raised for every native event after the model has been updated.</summary>
    public event EventHandler<RumbleEvent>? EventReceived;

    /// <summary>Connection state changed.</summary>
    public event EventHandler<ConnectionState>? StateChanged;

    /// <summary>Connected and synchronized.</summary>
    public event EventHandler<ConnectedEvent>? Connected;

    /// <summary>Disconnected (check <see cref="DisconnectedEvent.WillReconnect"/>).</summary>
    public event EventHandler<DisconnectedEvent>? Disconnected;

    /// <summary>A reconnect attempt started.</summary>
    public event EventHandler<ConnectingEvent>? Reconnecting;

    /// <summary>The server rejected the login.</summary>
    public event EventHandler<RejectedEvent>? Rejected;

    /// <summary>The local user was kicked or banned.</summary>
    public event EventHandler<KickedEvent>? Kicked;

    /// <summary>Server certificate received (trust-on-first-use).</summary>
    public event EventHandler<ServerCertificateEvent>? ServerCertificateReceived;

    /// <summary>A channel was added.</summary>
    public event EventHandler<Channel>? ChannelAdded;

    /// <summary>A channel was updated.</summary>
    public event EventHandler<Channel>? ChannelUpdated;

    /// <summary>A channel was removed (argument: the last known snapshot, if any).</summary>
    public event EventHandler<ChannelRemovedEvent>? ChannelRemoved;

    /// <summary>A user joined the server (onUserJoin).</summary>
    public event EventHandler<User>? UserJoined;

    /// <summary>A user's state changed.</summary>
    public event EventHandler<UserUpdatedEventArgs>? UserUpdated;

    /// <summary>A user moved between channels.</summary>
    public event EventHandler<UserMovedEventArgs>? UserMoved;

    /// <summary>A user left the server.</summary>
    public event EventHandler<UserLeftEventArgs>? UserLeft;

    /// <summary>A user started or stopped talking.</summary>
    public event EventHandler<UserTalkingEventArgs>? UserTalkingChanged;

    /// <summary>Any text message received.</summary>
    public event EventHandler<TextMessage>? TextMessageReceived;

    /// <summary>A text message sent to a channel (onChannelMessage).</summary>
    public event EventHandler<TextMessage>? ChannelMessageReceived;

    /// <summary>A private text message.</summary>
    public event EventHandler<TextMessage>? PrivateMessageReceived;

    /// <summary>The server denied a request.</summary>
    public event EventHandler<PermissionDeniedEvent>? PermissionDenied;

    /// <summary>Server configuration changed.</summary>
    public event EventHandler<ServerInfo>? ServerInfoChanged;

    /// <summary>Periodic ping statistics.</summary>
    public event EventHandler<PingUpdatedEvent>? PingUpdated;

    /// <summary>Plugin data received from another client.</summary>
    public event EventHandler<PluginDataEvent>? PluginDataReceived;

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Connects, authenticates and waits until the server state is synchronized.</summary>
    /// <exception cref="RumbleConnectionException">The connection failed or was rejected.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        _options.Validate();
        if (Handle is not null)
        {
            throw new InvalidOperationException("The client is already connected or connecting.");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _connectTcs = tcs;
            _everConnected = false;
            _lastRejectType = null;
            _lastRejectReason = null;
        }

        Server.Clear();
        _handle = CreateNativeClient();
        try
        {
            Audio.ApplyAll(_handle);
        }
        catch (RumbleException ex)
        {
            _logger.LogWarning(ex, "Audio could not be started; continuing without audio");
        }

        var timeout = _options.ConnectTimeout * 2 + TimeSpan.FromSeconds(5);
        try
        {
            await tcs.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CloseHandle();
            if (ex is TimeoutException)
            {
                throw new RumbleConnectionException($"Connecting to {_options.Host}:{_options.Port} timed out.");
            }

            throw;
        }
        finally
        {
            lock (_sync)
            {
                _connectTcs = null;
            }
        }
    }

    private unsafe RumbleClientSafeHandle CreateNativeClient()
    {
        var json = Native.Json(_options.ToNative(), RumbleJsonContext.Default.NativeClientConfig);
        var gc = GCHandle.Alloc(this, GCHandleType.Weak);
        try
        {
            RumbleClientSafeHandle handle;
            fixed (byte* p = json.Span)
            {
                Native.Check(NativeMethods.rumble_client_create(p, (nuint)json.Span.Length, &OnNativeEvent, GCHandle.ToIntPtr(gc), out handle));
            }

            handle.CallbackHandle = gc;
            return handle;
        }
        catch
        {
            gc.Free();
            throw;
        }
    }

    internal nint CallbackUserData => _handle is { } h && h.CallbackHandle.IsAllocated ? GCHandle.ToIntPtr(h.CallbackHandle) : 0;

    /// <summary>Disconnects gracefully (no automatic reconnect).</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var handle = Handle;
        if (handle is null)
        {
            return;
        }

        if (State != ConnectionState.Disconnected)
        {
            var wait = WaitForEventAsync<DisconnectedEvent>(d => !d.WillReconnect, TimeSpan.FromSeconds(5), cancellationToken);
            NativeMethods.rumble_client_disconnect(handle);
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RumbleException or TimeoutException or OperationCanceledException)
            {
                _logger.LogDebug(ex, "Disconnect did not complete cleanly");
            }
        }

        CloseHandle();
    }

    private void CloseHandle()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
        {
            return;
        }

        var wasConnected = State != ConnectionState.Disconnected;
        handle.Dispose();
        if (wasConnected)
        {
            // Native callbacks are silenced after destroy; synthesize the final transitions.
            _queue.Writer.TryWrite(new StateChangedEvent(ConnectionState.Disconnected));
            _queue.Writer.TryWrite(new DisconnectedEvent("disconnected", false));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while disconnecting during dispose");
        }

        await Plugins.ShutdownAllAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();
        await _eventLoop.ConfigureAwait(false);
        foreach (var s in _subscribers)
        {
            s.Writer.TryComplete();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CloseHandle();
        _queue.Writer.TryComplete();
        foreach (var s in _subscribers)
        {
            s.Writer.TryComplete();
        }
    }

    // ------------------------------------------------------------------ event pipeline

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnNativeEvent(nint userData, byte* json, nuint length)
    {
        try
        {
            if (GCHandle.FromIntPtr(userData).Target is not RumbleClient client)
            {
                return;
            }

            if (ParseEvent(new ReadOnlySpan<byte>(json, checked((int)length))) is { } evt)
            {
                client._queue.Writer.TryWrite(evt);
            }
        }
        catch
        {
            // Never let exceptions cross into native code.
        }
    }

    /// <summary>
    /// Parses a native event. Event types introduced by newer native libraries become
    /// <see cref="UnknownEvent"/> instead of failing.
    /// </summary>
    internal static RumbleEvent? ParseEvent(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, RumbleJsonContext.Default.RumbleEvent);
        }
        catch (JsonException)
        {
            var reader = new Utf8JsonReader(json);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1 && reader.ValueTextEquals("type"u8) && reader.Read())
                {
                    return new UnknownEvent(reader.GetString() ?? string.Empty);
                }
            }

            return null;
        }
    }

    private async Task RunEventLoopAsync()
    {
        await foreach (var e in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Dispatch(e);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception while dispatching {EventType}", e.GetType().Name);
            }
        }
    }

    private void Dispatch(RumbleEvent e)
    {
        // Capture state needed for rich event args before the model changes.
        var previousUser = e switch
        {
            UserUpdatedEvent u => Server.GetUser(u.User.Session),
            UserLeftEvent l => Server.GetUser(l.Session),
            _ => null,
        };

        Server.Apply(e);

        switch (e)
        {
            case StateChangedEvent s:
                State = s.State;
                Raise(StateChanged, s.State);
                break;
            case ConnectingEvent c when c.Attempt > 1 || _everConnected:
                Raise(Reconnecting, c);
                break;
            case ConnectedEvent c:
                TaskCompletionSource? tcs;
                lock (_sync)
                {
                    _everConnected = true;
                    tcs = _connectTcs;
                }

                State = ConnectionState.Connected;
                tcs?.TrySetResult();
                _logger.LogInformation("Connected to {Host}:{Port} as session {Session}", _options.Host, _options.Port, c.Session);
                Raise(Connected, c);
                break;
            case DisconnectedEvent d:
                FailConnect(d);
                State = ConnectionState.Disconnected;
                _logger.LogInformation("Disconnected: {Reason} (reconnect: {WillReconnect})", d.Reason, d.WillReconnect);
                Raise(Disconnected, d);
                break;
            case RejectedEvent r:
                _lastRejectType = r.RejectType;
                _lastRejectReason = r.Reason;
                Raise(Rejected, r);
                break;
            case KickedEvent k:
                Raise(Kicked, k);
                break;
            case ServerCertificateEvent cert:
                Raise(ServerCertificateReceived, cert);
                break;
            case ChannelAddedEvent a:
                Raise(ChannelAdded, Server.GetChannel(a.Channel.Id) ?? a.Channel);
                break;
            case ChannelUpdatedEvent u:
                Raise(ChannelUpdated, Server.GetChannel(u.Channel.Id) ?? u.Channel);
                break;
            case ChannelRemovedEvent r:
                Raise(ChannelRemoved, r);
                break;
            case UserJoinedEvent j:
                Raise(UserJoined, Server.GetUser(j.User.Session) ?? j.User);
                break;
            case UserUpdatedEvent u:
                Raise(UserUpdated, new UserUpdatedEventArgs(Server.GetUser(u.User.Session) ?? u.User, previousUser, ActorOf(u.Actor)));
                break;
            case UserMovedEvent m when Server.GetUser(m.Session) is { } movedUser:
                Raise(UserMoved, new UserMovedEventArgs(movedUser, Server.GetChannel(m.FromChannelId), Server.GetChannel(m.ToChannelId), ActorOf(m.Actor)));
                break;
            case UserLeftEvent l:
                Raise(UserLeft, new UserLeftEventArgs(previousUser, l.Session, ActorOf(l.Actor), l.Reason, l.Ban));
                break;
            case UserTalkingEvent t when Server.GetUser(t.Session) is { } talker:
                Raise(UserTalkingChanged, new UserTalkingEventArgs(talker, t.Talking));
                break;
            case TextMessageEvent t:
                var message = new TextMessage(
                    ActorOf(t.Actor),
                    [.. t.ChannelIds.Concat(t.TreeIds).Select(Server.GetChannel).OfType<Channel>()],
                    t.Message,
                    t.IsPrivate,
                    DateTimeOffset.Now,
                    t);
                Raise(TextMessageReceived, message);
                Raise(message.IsPrivate ? PrivateMessageReceived : ChannelMessageReceived, message);
                break;
            case PermissionDeniedEvent p:
                _logger.LogWarning("Permission denied: {Type} {Reason}", p.DenyType, p.Reason);
                Raise(PermissionDenied, p);
                break;
            case ServerConfigUpdatedEvent s:
                Raise(ServerInfoChanged, s.Server);
                break;
            case PingUpdatedEvent p:
                Raise(PingUpdated, p);
                break;
            case PluginDataEvent p:
                Raise(PluginDataReceived, p);
                break;
        }

        Raise(EventReceived, e);
        foreach (var subscriber in _subscribers)
        {
            subscriber.Writer.TryWrite(e);
        }

        CompleteWaiters(e);
    }

    private void FailConnect(DisconnectedEvent d)
    {
        TaskCompletionSource? tcs;
        lock (_sync)
        {
            if (_everConnected)
            {
                return;
            }

            tcs = _connectTcs;
        }

        tcs?.TrySetException(new RumbleConnectionException(_lastRejectReason ?? d.Reason, _lastRejectType));
    }

    private User? ActorOf(uint? session) => session is { } s ? Server.GetUser(s) : null;

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        foreach (var d in handler.GetInvocationList())
        {
            try
            {
                ((EventHandler<T>)d)(this, args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Event handler for {EventArgs} threw", typeof(T).Name);
            }
        }
    }

    /// <summary>Streams all events until cancelled or the client is disposed.</summary>
    public async IAsyncEnumerable<RumbleEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = System.Threading.Channels.Channel.CreateUnbounded<RumbleEvent>(new UnboundedChannelOptions { SingleReader = true });
        ImmutableInterlocked.Update(ref _subscribers, s => s.Add(channel));
        try
        {
            await foreach (var e in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return e;
            }
        }
        finally
        {
            ImmutableInterlocked.Update(ref _subscribers, s => s.Remove(channel));
        }
    }

    // ------------------------------------------------------------------ waiters

    private interface IEventWaiter
    {
        bool TryHandle(RumbleEvent e);
    }

    /// <param name="match">Predicate selecting the awaited event.</param>
    /// <param name="isCommandResponse">
    /// True for request/response waits: they fail on permission denials and when the connection
    /// drops (the response can never arrive). Public event waits survive reconnects.
    /// </param>
    private sealed class EventWaiter<T>(Func<T, bool> match, bool isCommandResponse) : IEventWaiter
        where T : RumbleEvent
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryHandle(RumbleEvent e)
        {
            if (e is T t && match(t))
            {
                return Completion.TrySetResult(t);
            }

            if (isCommandResponse && e is PermissionDeniedEvent denied)
            {
                return Completion.TrySetException(new RumblePermissionDeniedException(denied));
            }

            if (isCommandResponse && e is DisconnectedEvent)
            {
                return Completion.TrySetException(new RumbleException(RumbleErrorCode.NotConnected, "The connection closed while waiting for a response."));
            }

            return false;
        }
    }

    private void CompleteWaiters(RumbleEvent e)
    {
        lock (_sync)
        {
            _waiters.RemoveAll(w => w.TryHandle(e));
        }
    }

    /// <summary>Waits for the next event of type <typeparamref name="T"/> matching <paramref name="match"/>.</summary>
    public Task<T> WaitForEventAsync<T>(Func<T, bool>? match = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        where T : RumbleEvent
        => WaitCoreAsync(match ?? (_ => true), timeout, failOnPermissionDenied: false, cancellationToken);

    private async Task<T> WaitCoreAsync<T>(Func<T, bool> match, TimeSpan? timeout, bool failOnPermissionDenied, CancellationToken cancellationToken)
        where T : RumbleEvent
    {
        var waiter = new EventWaiter<T>(match, failOnPermissionDenied);
        lock (_sync)
        {
            _waiters.Add(waiter);
        }

        try
        {
            return await waiter.Completion.Task.WaitAsync(timeout ?? _options.CommandTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new RumbleException(RumbleErrorCode.Timeout, $"Timed out waiting for {typeof(T).Name}.");
        }
        finally
        {
            lock (_sync)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    // ------------------------------------------------------------------ command plumbing

    internal RumbleClientSafeHandle RequireHandle() =>
        Handle ?? throw new RumbleException(RumbleErrorCode.NotConnected, "The client is not connected.");

    private unsafe void Send(RumbleCommand command)
    {
        var handle = RequireHandle();
        var json = Native.Json(command, RumbleJsonContext.Default.RumbleCommand);
        fixed (byte* p = json.Span)
        {
            Native.Check(NativeMethods.rumble_client_send_command(handle, p, (nuint)json.Span.Length));
        }
    }

    private async Task<T> SendAndWaitAsync<T>(RumbleCommand command, Func<T, bool> match, CancellationToken cancellationToken)
        where T : RumbleEvent
    {
        var wait = WaitCoreAsync(match, null, failOnPermissionDenied: true, cancellationToken);
        try
        {
            Send(command);
        }
        catch
        {
            // Observe the waiter so it does not surface as an unobserved exception.
            _ = wait.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
            throw;
        }

        return await wait.ConfigureAwait(false);
    }

    /// <summary>Fetches a fresh snapshot of the native state cache.</summary>
    public unsafe ServerSnapshot GetSnapshot()
    {
        var handle = RequireHandle();
        Native.Check(NativeMethods.rumble_client_snapshot(handle, out var ptr, out var len));
        var snapshot = Native.ReadJson(ptr, len, RumbleJsonContext.Default.ServerSnapshot);
        foreach (var c in snapshot.Channels)
        {
            c.Model = Server;
        }

        foreach (var u in snapshot.Users)
        {
            u.Model = Server;
        }

        return snapshot;
    }

    /// <summary>Queries a server's version, user count and latency without connecting.</summary>
    public static Task<ServerQueryResult> QueryServerAsync(string host, int port = 64738, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        => RumbleNative.QueryServerAsync(host, port, timeout, cancellationToken);
}

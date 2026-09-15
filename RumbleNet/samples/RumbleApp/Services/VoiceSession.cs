using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rumble.Net;
using Rumble.Net.Audio;
using Rumble.Net.Events;
using Rumble.Net.Models;
using Rumble.Net.Testing;
using Channel = Rumble.Net.Models.Channel;

namespace RumbleApp.Services;

/// <summary>A line in the chat log.</summary>
public sealed record ChatEntry(DateTimeOffset Time, string Author, string Text, ChatKind Kind, string? Destination = null);

public enum ChatKind
{
    Incoming,
    Outgoing,
    Private,
    System,
}

/// <summary>
/// Owns the connection for the whole app: wraps <see cref="RumbleClient"/>, keeps the chat log and
/// voice traces, and notifies the UI through <see cref="Changed"/> and <see cref="Tick"/>.
/// </summary>
public sealed class VoiceSession : IAsyncDisposable
{
    private const int MaxChat = 400;

    private readonly SettingsStore _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<uint, VoiceTrace> _traces = new();
    private readonly List<ChatEntry> _chat = [];
    private readonly Lock _chatLock = new();
    private MockMumbleServer? _demoServer;
    private IDispatcherTimer? _timer;
    private bool _pushToTalk;

    public VoiceSession(SettingsStore settings, ILoggerFactory loggerFactory)
    {
        _settings = settings;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Structural changes: connection, channels, users, chat.</summary>
    public event Action? Changed;

    /// <summary>20 Hz clock for meters and traces.</summary>
    public event Action? Tick;

    public RumbleClient? Client { get; private set; }

    public SavedServer? Server { get; private set; }

    public ConnectionState State => Client?.State ?? ConnectionState.Disconnected;

    public bool IsConnected => Client?.IsConnected == true;

    public bool IsBusy { get; private set; }

    /// <summary>Last connection problem, phrased for the person using the app.</summary>
    public string? Problem { get; private set; }

    /// <summary>Audio could not start on devices; voice runs without speakers/microphone.</summary>
    public string? AudioNotice { get; private set; }

    public Channel? ChatChannel => Client?.Self?.Channel;

    public User? PrivateTarget { get; set; }

    public IReadOnlyList<ChatEntry> Chat
    {
        get
        {
            lock (_chatLock)
            {
                return [.. _chat];
            }
        }
    }

    public float InputLevelDb { get; private set; } = -96f;

    public bool IsTransmitting { get; private set; }

    public VoiceTrace TraceFor(uint session) => _traces.GetOrAdd(session, _ => new VoiceTrace());

    public bool PushToTalk
    {
        get => _pushToTalk;
        set
        {
            if (_pushToTalk == value)
            {
                return;
            }

            _pushToTalk = value;
            if (Client is not null)
            {
                Client.Audio.PushToTalk = value;
            }
        }
    }

    public async Task ConnectAsync(SavedServer server)
    {
        if (IsBusy)
        {
            return;
        }

        await DisconnectAsync();
        IsBusy = true;
        Problem = null;
        AudioNotice = null;
        Server = server;
        Notify();

        var settings = _settings.Current;
        var options = new RumbleClientOptions
        {
            Host = server.Host,
            Port = server.Port,
            Username = string.IsNullOrWhiteSpace(server.Username) ? DefaultName() : server.Username,
            Password = string.IsNullOrEmpty(server.Password) ? null : server.Password,
            CertificatePem = settings.CertificatePem,
            PrivateKeyPem = settings.PrivateKeyPem,
            ForceTcpVoice = settings.ForceTcpVoice,
            PositionalTransmit = settings.PositionalAudio,
            ClientRelease = "RumbleApp 1.0 (Rumble.Net)",
            LoggerFactory = _loggerFactory,
        };
        options.Audio.Mode = await MicrophoneAllowedAsync() ? AudioMode.Devices : AudioMode.Headless;
        options.Audio.TransmitMode = settings.TransmitMode;
        options.Audio.VoiceActivityThresholdDb = settings.VoiceActivityThresholdDb;
        options.Audio.Bitrate = settings.Bitrate;
        options.Audio.FramesPerPacket = settings.FramesPerPacket;
        options.Audio.NoiseGateDb = settings.NoiseGate ? -55f : null;
        options.Audio.InputDeviceId = settings.InputDeviceId;
        options.Audio.OutputDeviceId = settings.OutputDeviceId;
        options.Audio.MasterVolume = settings.MasterVolume;

        if (server.IsDemo)
        {
            _demoServer ??= MockMumbleServer.Start();
            options.Host = _demoServer.Host;
            options.Port = _demoServer.Port;
        }

        var client = new RumbleClient(options);
        Attach(client);
        Client = client;
        try
        {
            await client.ConnectAsync();
            if (settings.PositionalAudio)
            {
                client.Audio.EnablePositionalAudio();
            }

            if (options.Audio.Mode == AudioMode.Devices && !AudioDevicesWork())
            {
                client.Audio.SetMode(AudioMode.Headless);
                AudioNotice = "No microphone or speakers were found. You can still chat and see who is talking.";
            }

            AddSystem($"Connected to {server.Name} as {client.Self?.Name}.");
            if (!string.IsNullOrWhiteSpace(client.Server.Info.WelcomeText))
            {
                AddSystem(HtmlText.ToPlainText(client.Server.Info.WelcomeText));
            }

            StartClock();
        }
        catch (RumbleConnectionException ex)
        {
            Problem = ex.RejectType switch
            {
                "WrongServerPw" => "The server password is wrong. Edit the server and enter the correct password.",
                "WrongUserPw" => "This name is registered with a password. Enter the password or choose another name.",
                "UsernameInUse" => "Someone is already using that name on this server. Choose another name.",
                "InvalidUsername" => "The server does not accept that name. Choose another name.",
                "ServerFull" => "The server is full. Try again later.",
                _ => $"Could not connect to {server.Host}:{server.Port}. {ex.Message}",
            };
            await DropClientAsync();
        }
        catch (Exception ex)
        {
            Problem = $"Could not connect: {ex.Message}";
            await DropClientAsync();
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    public async Task DisconnectAsync()
    {
        if (Client is null)
        {
            return;
        }

        await Client.DisconnectAsync();
        await DropClientAsync();
        AddSystem("Disconnected.");
        Notify();
    }

    private async Task DropClientAsync()
    {
        _timer?.Stop();
        var client = Client;
        Client = null;
        PrivateTarget = null;
        _traces.Clear();
        if (client is not null)
        {
            await client.DisposeAsync();
        }
    }

    public async Task JoinChannelAsync(Channel channel)
    {
        if (Client is null)
        {
            return;
        }

        try
        {
            await Client.JoinChannelAsync(channel);
            PrivateTarget = null;
        }
        catch (RumblePermissionDeniedException)
        {
            AddSystem($"You are not allowed to enter {channel.Name}.");
        }
        catch (RumbleException ex)
        {
            AddSystem($"Could not move to {channel.Name}: {ex.Message}");
        }

        Notify();
    }

    public void SendMessage(string text)
    {
        if (Client?.Self is not { } me || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var html = System.Net.WebUtility.HtmlEncode(text.Trim());
        if (PrivateTarget is { } target)
        {
            Client.SendPrivateMessage(target, html);
            Add(new ChatEntry(DateTimeOffset.Now, me.Name, text.Trim(), ChatKind.Outgoing, $"to {target.Name}"));
        }
        else if (me.Channel is { } channel)
        {
            Client.SendChannelMessage(channel, html);
            Add(new ChatEntry(DateTimeOffset.Now, me.Name, text.Trim(), ChatKind.Outgoing, channel.Name));
        }

        Notify();
    }

    public void ToggleMute()
    {
        if (Client?.Self is { } me)
        {
            Client.SetSelfMute(!me.SelfMute);
        }
    }

    public void ToggleDeafen()
    {
        if (Client?.Self is { } me)
        {
            Client.SetSelfDeaf(!me.SelfDeaf);
        }
    }

    public void SetTransmitMode(TransmitMode mode)
    {
        _settings.Update(s => s with { TransmitMode = mode });
        if (Client is not null)
        {
            Client.Audio.TransmitMode = mode;
        }

        Notify();
    }

    // ------------------------------------------------------------------ wiring

    private void Attach(RumbleClient client)
    {
        client.StateChanged += (_, _) => Notify();
        client.ChannelAdded += (_, _) => Notify();
        client.ChannelUpdated += (_, _) => Notify();
        client.ChannelRemoved += (_, _) => Notify();
        client.UserUpdated += (_, _) => Notify();
        client.UserTalkingChanged += (_, _) => Notify();
        client.UserJoined += (_, user) =>
        {
            if (client.IsConnected)
            {
                AddSystem($"{user.Name} connected.");
            }

            Notify();
        };
        client.UserLeft += (_, e) =>
        {
            _traces.TryRemove(e.Session, out VoiceTrace? _);
            if (e.User is { } user)
            {
                AddSystem(e.Banned ? $"{user.Name} was banned." : $"{user.Name} disconnected.");
            }

            Notify();
        };
        client.UserMoved += (_, e) =>
        {
            if (e.User.IsSelf || e.To?.Id == client.Self?.ChannelId || e.From?.Id == client.Self?.ChannelId)
            {
                AddSystem($"{(e.User.IsSelf ? "You" : e.User.Name)} moved to {e.To?.Name}.");
            }

            Notify();
        };
        client.TextMessageReceived += (_, m) =>
        {
            Add(new ChatEntry(m.Timestamp, m.Sender?.Name ?? "Server", m.PlainText, m.IsPrivate ? ChatKind.Private : ChatKind.Incoming,
                m.IsPrivate ? "private" : m.Channels.FirstOrDefault()?.Name));
            Notify();
        };
        client.PermissionDenied += (_, p) =>
        {
            AddSystem(p.Reason ?? $"The server denied that action ({p.DenyType}).");
            Notify();
        };
        client.Disconnected += (_, d) =>
        {
            if (d.WillReconnect)
            {
                AddSystem($"Connection lost ({d.Reason}). Reconnecting…");
            }

            Notify();
        };
        client.Connected += (_, _) => Notify();
        client.Kicked += (_, k) =>
        {
            Problem = k.Ban ? $"You were banned: {k.Reason}" : $"You were kicked: {k.Reason}";
            Notify();
        };
        client.Audio.AudioFrameReceived += OnAudioFrame;
    }

    private void OnAudioFrame(in AudioFrame frame)
    {
        if (!frame.IsConcealed)
        {
            TraceFor(frame.Session).Report(frame.Rms);
        }
    }

    private void StartClock()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        _timer ??= dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick -= OnClock;
        _timer.Tick += OnClock;
        _timer.Start();
    }

    private void OnClock(object? sender, EventArgs e)
    {
        if (Client is not { } client)
        {
            return;
        }

        InputLevelDb = client.Audio.InputLevelDb;
        IsTransmitting = client.Audio.IsTransmitting;
        if (client.Self is { } me)
        {
            TraceFor(me.Session).PushDecibels(IsTransmitting ? InputLevelDb : -96f);
        }

        foreach (var trace in _traces.Values)
        {
            trace.Tick();
        }

        Tick?.Invoke();
    }

    private bool AudioDevicesWork()
    {
        try
        {
            var devices = RumbleNative.GetAudioDevices();
            return devices.Any(d => d.IsInput) && devices.Any(d => d.IsOutput);
        }
        catch (RumbleException)
        {
            return false;
        }
    }

    private static async Task<bool> MicrophoneAllowedAsync()
    {
        try
        {
            var status = await MainThread.InvokeOnMainThreadAsync(() => Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<Microsoft.Maui.ApplicationModel.Permissions.Microphone>());
            return status == PermissionStatus.Granted;
        }
        catch (Exception)
        {
            // Desktop platforms without a permission model.
            return true;
        }
    }

    private string DefaultName() =>
        string.IsNullOrWhiteSpace(_settings.Current.DefaultUsername) ? $"Rumbler{Random.Shared.Next(100, 999)}" : _settings.Current.DefaultUsername;

    private void AddSystem(string text) => Add(new ChatEntry(DateTimeOffset.Now, string.Empty, text, ChatKind.System));

    private void Add(ChatEntry entry)
    {
        lock (_chatLock)
        {
            _chat.Add(entry);
            if (_chat.Count > MaxChat)
            {
                _chat.RemoveRange(0, _chat.Count - MaxChat);
            }
        }
    }

    private void Notify() => Changed?.Invoke();

    public async ValueTask DisposeAsync()
    {
        await DropClientAsync();
        _demoServer?.Dispose();
    }
}

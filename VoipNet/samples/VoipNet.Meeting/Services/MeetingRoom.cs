using System.Collections.Concurrent;

namespace VoipNet.Meeting.Services;

/// <summary>A line in the room's log.</summary>
/// <param name="Time">When it happened.</param>
/// <param name="Kind">Which part of the room it came from, used as the tag colour.</param>
/// <param name="Text">What happened.</param>
public sealed record RoomEvent(DateTime Time, string Kind, string Text);

/// <summary>One browser in the meeting.</summary>
public sealed class Participant
{
    internal Participant(VoipCall call, string name)
    {
        Call = call;
        Name = name;
    }

    /// <summary>The call this browser is on.</summary>
    public VoipCall Call { get; }

    /// <summary>The name they typed before joining.</summary>
    public string Name { get; }

    /// <summary>When they joined.</summary>
    public DateTime Joined { get; } = DateTime.Now;

    /// <summary>SRTP protection profile, once DTLS has settled.</summary>
    public string? Security { get; internal set; }

    /// <summary>True once video has arrived from this browser.</summary>
    public bool SendingVideo { get; internal set; }

    /// <summary>Whole frames the room has reassembled from this browser.</summary>
    public int VideoFrames { get; internal set; }

    /// <summary>True while the call is up.</summary>
    public bool IsLive => Call.IsActive;

    /// <summary>What the engine measured on this leg.</summary>
    public CallStatistics? Stats => Call.IsActive ? Call.GetStatistics() : Call.FinalStatistics;
}

/// <summary>
/// A meeting room: every browser calls the same endpoint over SIP/WebSocket with DTLS-SRTP, and
/// each call is added to one conference. Audio is mixed minus the listener; video is forwarded, so
/// everyone watches whoever is speaking — or whoever a host pinned.
/// </summary>
public sealed class MeetingRoom(ILogger<MeetingRoom> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>Port browsers connect their SIP WebSocket to.</summary>
    public const int WebSocketPort = 5091;

    private readonly ConcurrentQueue<RoomEvent> _events = new();
    private readonly List<Participant> _participants = [];
    private readonly object _gate = new();
    private VoipClient? _edge;
    private VoipConference? _conference;
    private Participant? _pinned;

    /// <summary>Raised whenever the roster or the log changes.</summary>
    public event Action? Changed;

    /// <summary>The log, newest first.</summary>
    public IReadOnlyList<RoomEvent> Events => _events.Reverse().ToArray();

    /// <summary>Everyone currently in the room.</summary>
    public IReadOnlyList<Participant> Participants
    {
        get
        {
            lock (_gate)
            {
                return [.. _participants.Where(p => p.IsLive)];
            }
        }
    }

    /// <summary>Who everyone is watching: the pinned participant, or the loudest speaker.</summary>
    public Participant? OnScreen
    {
        get
        {
            if (Pinned && _pinned is { IsLive: true })
            {
                return _pinned;
            }

            var call = _conference?.ActiveSpeaker;
            return call is null ? null : Participants.FirstOrDefault(p => p.Call.Id == call.Id);
        }
    }

    /// <summary>True while a host has fixed the picture on one participant.</summary>
    public bool Pinned => _conference?.Layout == ConferenceLayout.Pinned;

    /// <summary>The address browsers dial.</summary>
    public string Address => _edge?.LocalAddress ?? $"0.0.0.0:{WebSocketPort}";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _edge = new VoipClient(new VoipClientOptions
        {
            BindAddress = "0.0.0.0",
            SipPort = WebSocketPort,
            Transport = SipTransport.Ws,
            Username = "room",
            Srtp = SrtpMode.Mandatory,
            SrtpKeying = SrtpKeying.Dtls,
            Ice = true,
            // Every participant sends a camera; the conference forwards one of them to everyone else.
            Video = true,
            // A browser tab that vanishes never sends BYE, so a leg that goes quiet is dropped instead
            // of sitting on the roster for ever.
            RtpTimeoutMs = 10000,
            UserAgent = "Voip.NET meeting room",
        });

        _edge.IncomingCall += (_, e) => _ = Guard(() => JoinAsync(e.Call, e.From));
        _edge.MediaNotification += (_, e) => OnMedia(e);
        return StartEdgeAsync(cancellationToken);
    }

    private async Task StartEdgeAsync(CancellationToken cancellationToken)
    {
        await _edge!.StartAsync(cancellationToken);
        _conference = _edge.CreateConference();
        Log("room", $"listening for browsers on ws://{_edge.LocalAddress}");
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        _conference?.Dispose();
        _conference = null;
        if (_edge is not null)
        {
            await _edge.DisposeAsync();
            _edge = null;
        }
    }

    /// <summary>Fixes the picture on one participant for everybody.</summary>
    /// <param name="participant">Who to show.</param>
    public void Pin(Participant participant)
    {
        _conference?.Pin(participant.Call);
        _pinned = participant;
        Log("room", $"pinned {participant.Name}");
        Changed?.Invoke();
    }

    /// <summary>Goes back to showing whoever is speaking.</summary>
    public void FollowSpeaker()
    {
        _conference?.FollowSpeaker();
        _pinned = null;
        Log("room", "following the speaker again");
        Changed?.Invoke();
    }

    private async Task JoinAsync(VoipCall call, string from)
    {
        var name = NameFrom(from);
        var participant = new Participant(call, name);
        lock (_gate)
        {
            _participants.Add(participant);
            if (_participants.Count > 50)
            {
                _participants.RemoveAt(0);
            }
        }

        Log("signaling", $"INVITE from {name}");
        await call.AnswerAsync();
        _conference!.Add(call);
        Log("room", $"{name} joined · {Participants.Count} in the room");

        call.VideoFrameReceived += (_, _, _, _, _) =>
        {
            participant.VideoFrames++;
            if (!participant.SendingVideo)
            {
                participant.SendingVideo = true;
                Log("media", $"video from {name} ({call.VideoCodec})");
                Changed?.Invoke();
            }
        };

        call.StateChanged += (_, e) =>
        {
            if (e.State == CallState.Terminated)
            {
                Log("signaling", $"{name} left ({e.Reason})");
                Changed?.Invoke();
            }
        };

        Changed?.Invoke();
    }

    /// <summary>Reads the name a browser put in the user part of its From URI.</summary>
    private static string NameFrom(string from)
    {
        var user = from.Split('@')[0].Split(':').Last().Trim();
        var name = Uri.UnescapeDataString(user).Replace('+', ' ').Trim();
        return name.Length == 0 ? "Someone" : name;
    }

    private void OnMedia(MediaEventArgs e)
    {
        switch (e.Kind)
        {
            case "dtls-connected":
                if (Participants.FirstOrDefault(p => p.Call.Id == e.Call.Id) is { } secured)
                {
                    secured.Security = e.Detail;
                }

                Log("secure", $"DTLS done · SRTP {e.Detail}");
                break;
            case "dtls-failed":
            case "ice-failed":
                Log("secure", $"{e.Kind}: {e.Detail}");
                break;
            case "ice-connected":
                Log("secure", $"ICE selected {e.Detail}");
                break;
            case "rtp-timeout":
                if (Participants.FirstOrDefault(p => p.Call.Id == e.Call.Id) is { } gone)
                {
                    Log("room", $"{gone.Name} went quiet, dropping them");
                    gone.Call.Hangup();
                }

                break;
            default:
                return;
        }

        Changed?.Invoke();
    }

    private async Task Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The room could not take a call");
            Log("signaling", $"call failed: {ex.Message}");
            Changed?.Invoke();
        }
    }

    private void Log(string kind, string text)
    {
        _events.Enqueue(new RoomEvent(DateTime.Now, kind, text));
        while (_events.Count > 60 && _events.TryDequeue(out _))
        {
        }

        logger.LogInformation("[{Kind}] {Text}", kind, text);
        Changed?.Invoke();
    }
}

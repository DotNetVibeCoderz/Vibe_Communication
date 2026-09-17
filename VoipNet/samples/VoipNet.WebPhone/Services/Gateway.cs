using System.Collections.Concurrent;

namespace VoipNet.WebPhone.Services;

/// <summary>Which hop of the signal path an entry or state belongs to.</summary>
public enum Hop
{
    Browser,
    Signaling,
    Secure,
    Bridge,
    Trunk,
    Desk,
}

/// <summary>A line in the gateway's event log.</summary>
public sealed record GatewayEvent(DateTime Time, Hop Hop, string Text);

/// <summary>One browser call bridged to a SIP desk phone.</summary>
public sealed class Bridge
{
    internal Bridge(VoipCall browserLeg, string desk)
    {
        BrowserLeg = browserLeg;
        Desk = desk;
    }

    public VoipCall BrowserLeg { get; }

    public VoipCall? TrunkLeg { get; internal set; }

    /// <summary>The desk phone the browser asked for (echo or tones).</summary>
    public string Desk { get; }

    public DateTime Started { get; } = DateTime.Now;

    public bool IceConnected { get; internal set; }

    /// <summary>SRTP protection profile agreed by DTLS, once the handshake completes.</summary>
    public string? DtlsProfile { get; internal set; }

    public string? SecurityError { get; internal set; }

    public bool Ended => BrowserLeg.State == CallState.Terminated;

    public CallStatistics? BrowserStats => Stats(BrowserLeg);

    public CallStatistics? TrunkStats => TrunkLeg is null ? null : Stats(TrunkLeg);

    private static CallStatistics? Stats(VoipCall call) => call.IsActive ? call.GetStatistics() : call.FinalStatistics;
}

/// <summary>
/// A WebRTC to SIP gateway. Browsers speak SIP over WebSocket with ICE and DTLS-SRTP to the edge endpoint;
/// every call is answered and relayed, frame by frame, to a plain SIP/UDP call placed by the trunk endpoint
/// to a desk phone on this machine.
/// </summary>
public sealed class Gateway(ILogger<Gateway> logger) : IHostedService, IAsyncDisposable
{
    /// <summary>Port browsers connect their SIP WebSocket to.</summary>
    public const int WebSocketPort = 5090;

    private readonly ConcurrentQueue<GatewayEvent> _events = new();
    private readonly List<Bridge> _bridges = [];
    private VoipClient? _edge;
    private VoipClient? _trunk;
    private VoipClient? _desk;

    /// <summary>Raised whenever a bridge or the event log changes.</summary>
    public event Action? Changed;

    public IReadOnlyList<GatewayEvent> Events => _events.Reverse().ToArray();

    public Bridge? Current
    {
        get
        {
            lock (_bridges)
            {
                return _bridges.Count == 0 ? null : _bridges[^1];
            }
        }
    }

    public int BridgeCount
    {
        get
        {
            lock (_bridges)
            {
                return _bridges.Count;
            }
        }
    }

    public string? EdgeAddress => _edge?.LocalAddress;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _desk = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "desk", DisplayName = "Desk phone" });
        _trunk = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "gateway", DisplayName = "WebRTC gateway" });
        _edge = new VoipClient(new VoipClientOptions
        {
            BindAddress = "0.0.0.0",
            SipPort = WebSocketPort,
            Transport = SipTransport.Ws,
            Username = "gateway",
            Srtp = SrtpMode.Mandatory,
            SrtpKeying = SrtpKeying.Dtls,
            Ice = true,
            UserAgent = "Voip.NET WebRTC gateway",
        });

        _desk.IncomingCall += (_, e) => _ = Guard(() => AnswerDeskAsync(e.Call, e.To));
        _edge.IncomingCall += (_, e) => _ = Guard(() => BridgeAsync(e.Call, e.To));
        _edge.MediaNotification += (_, e) => OnSecurity(e);

        await _desk.StartAsync(cancellationToken);
        await _trunk.StartAsync(cancellationToken);
        await _edge.StartAsync(cancellationToken);
        Log(Hop.Signaling, $"listening for browsers on ws://{_edge.LocalAddress}");
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in new[] { _edge, _trunk, _desk })
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }

        (_edge, _trunk, _desk) = (null, null, null);
    }

    private async Task BridgeAsync(VoipCall browserLeg, string to)
    {
        var desk = to.Contains("tones", StringComparison.OrdinalIgnoreCase) ? "tones" : "echo";
        var bridge = new Bridge(browserLeg, desk);
        lock (_bridges)
        {
            _bridges.Add(bridge);
            if (_bridges.Count > 20)
            {
                _bridges.RemoveAt(0);
            }
        }

        Log(Hop.Signaling, $"INVITE from the browser for {desk}");
        await browserLeg.AnswerAsync();
        Log(Hop.Bridge, $"answered the browser, dialling the {desk} desk over SIP/UDP");

        var trunkLeg = _trunk!.Call($"sip:{desk}@{_desk!.LocalAddress}");
        bridge.TrunkLeg = trunkLeg;
        Changed?.Invoke();

        // Relay decoded audio in both directions; the engine resamples and paces each leg.
        browserLeg.AudioReceived += (_, direction, rate, samples) =>
        {
            if (direction == AudioDirection.Inbound && trunkLeg.IsActive)
            {
                trunkLeg.SendAudio(samples, rate);
            }
        };
        trunkLeg.AudioReceived += (_, direction, rate, samples) =>
        {
            if (direction == AudioDirection.Inbound && browserLeg.IsActive)
            {
                browserLeg.SendAudio(samples, rate);
            }
        };
        browserLeg.StateChanged += (_, e) =>
        {
            if (e.State == CallState.Terminated)
            {
                Log(Hop.Signaling, $"browser hung up ({e.Reason})");
                if (trunkLeg.State != CallState.Terminated)
                {
                    trunkLeg.Hangup();
                }
            }
        };
        trunkLeg.StateChanged += (_, e) =>
        {
            if (e.State == CallState.Terminated && browserLeg.State != CallState.Terminated)
            {
                Log(Hop.Trunk, $"desk leg ended ({e.StatusCode} {e.Reason}), hanging up the browser");
                browserLeg.Hangup();
            }
        };

        await trunkLeg.Connected.WaitAsync(TimeSpan.FromSeconds(10));
        Log(Hop.Trunk, $"desk answered · {trunkLeg.Codec} over plain RTP");
    }

    private async Task AnswerDeskAsync(VoipCall call, string to)
    {
        await call.AnswerAsync();
        if (to.Contains("tones", StringComparison.OrdinalIgnoreCase))
        {
            Log(Hop.Desk, "tones desk answered, playing a melody");
            _ = Guard(() => PlayTonesAsync(call));
            return;
        }

        Log(Hop.Desk, "echo desk answered, returning every frame");
        call.AudioReceived += (c, direction, rate, samples) =>
        {
            if (direction == AudioDirection.Inbound)
            {
                c.SendAudio(samples, rate);
            }
        };
    }

    private static async Task PlayTonesAsync(VoipCall call)
    {
        double[] notes = [523.25, 659.25, 783.99, 1046.5, 783.99, 659.25];
        const int rate = 16000;
        var frame = new short[rate * 300 / 1000];
        for (var n = 0; call.IsActive; n++)
        {
            if (call.QueuedAudioMs > 400)
            {
                await Task.Delay(100);
                continue;
            }

            var frequency = notes[n % notes.Length];
            for (var i = 0; i < frame.Length; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, frame.Length - i) / 800.0);
                frame[i] = (short)(7000 * envelope * Math.Sin(2 * Math.PI * frequency * i / rate));
            }

            call.SendAudio(frame, rate);
        }
    }

    private void OnSecurity(MediaEventArgs e)
    {
        var bridge = Find(e.Call);
        switch (e.Kind)
        {
            case "ice-connected":
                bridge?.IceConnected = true;
                Log(Hop.Secure, $"ICE check succeeded from {e.Detail}");
                break;
            case "dtls-connected":
                bridge?.DtlsProfile = e.Detail;
                Log(Hop.Secure, $"DTLS handshake done · SRTP {e.Detail}");
                break;
            case "dtls-failed":
                bridge?.SecurityError = e.Detail;
                Log(Hop.Secure, $"DTLS failed: {e.Detail}");
                break;
        }
    }

    private Bridge? Find(VoipCall call)
    {
        lock (_bridges)
        {
            return _bridges.LastOrDefault(b => b.BrowserLeg == call);
        }
    }

    private void Log(Hop hop, string text)
    {
        _events.Enqueue(new GatewayEvent(DateTime.Now, hop, text));
        while (_events.Count > 60)
        {
            _events.TryDequeue(out _);
        }

        logger.LogInformation("[{Hop}] {Text}", hop, text);
        Changed?.Invoke();
    }

    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is VoipException or TimeoutException or ObjectDisposedException)
        {
            Log(Hop.Bridge, ex.Message);
        }
    }
}

using VoipNet.Audio;

namespace VoipNet.Softphone.Services;

/// <summary>
/// In-process SIP endpoints so the softphone can be tried without a PBX. Dial <c>echo</c> to hear
/// yourself, or <c>music</c> for a test melody that also answers DTMF with a tone.
/// </summary>
public sealed class DemoBots : IAsyncDisposable
{
    private readonly VoipClient _bots;

    private DemoBots(VoipClient bots)
    {
        _bots = bots;
    }

    /// <summary>Address the bots listen on.</summary>
    public string Address => _bots.LocalAddress;

    /// <summary>Starts the bots on the loopback interface.</summary>
    public static async Task<DemoBots> StartAsync()
    {
        var client = new VoipClient(new VoipClientOptions
        {
            BindAddress = "127.0.0.1",
            SipPort = 0,
            Username = "bots",
            DisplayName = "Voip.NET Demo",
            RtpPortMin = 46000,
            RtpPortMax = 46999,
            Video = CallVideo.IsSupported,
        });
        await client.StartAsync();
        var bots = new DemoBots(client);
        client.IncomingCall += bots.OnIncomingCall;
        return bots;
    }

    /// <summary>Turns a short name such as <c>echo</c> into the bot's SIP URI.</summary>
    /// <param name="name">Bot name.</param>
    public string UriFor(string name) => $"sip:{name}@{Address}";

    private async void OnIncomingCall(object? sender, IncomingCallEventArgs e)
    {
        var call = e.Call;
        var user = e.To.Split(':', '@') is { Length: > 1 } parts ? parts[1] : "echo";
        await Task.Delay(900);

        if (user.Equals("music", StringComparison.OrdinalIgnoreCase))
        {
            call.DtmfReceived += (_, d) => call.SendAudio(ToneGenerator.Dtmf(d.Digit, 16000, 160), 16000);
            await call.AnswerAsync();
            _ = PlayMelodyAsync(call);
            return;
        }

        call.AudioReceived += (c, direction, rate, samples) =>
        {
            if (direction == AudioDirection.Inbound)
            {
                c.SendAudio(samples, rate);
            }
        };

        // The picture goes back the way it came, frame for frame, so a camera can be seen coming home
        // over real RTP rather than out of the same process's memory.
        call.VideoFrameReceived += (c, timestamp, keyframe, frame, content) =>
        {
            if (content == "main")
            {
                c.SendVideoFrame(timestamp, frame);
            }
        };
        await call.AnswerAsync();
    }

    private static async Task PlayMelodyAsync(VoipCall call)
    {
        double[] notes = [523.25, 659.25, 783.99, 1046.5, 783.99, 659.25];
        while (call.IsActive)
        {
            foreach (var frequency in notes)
            {
                var samples = new short[16000 * 350 / 1000];
                for (var i = 0; i < samples.Length; i++)
                {
                    var envelope = Math.Min(1, Math.Min(i / 400.0, (samples.Length - i) / 800.0));
                    samples[i] = (short)(7000 * envelope * Math.Sin(2 * Math.PI * frequency * i / 16000));
                }

                if (!call.IsActive)
                {
                    return;
                }

                call.SendAudio(samples, 16000);
            }

            while (call.IsActive && call.QueuedAudioMs > 200)
            {
                await Task.Delay(100);
            }
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => _bots.DisposeAsync();
}

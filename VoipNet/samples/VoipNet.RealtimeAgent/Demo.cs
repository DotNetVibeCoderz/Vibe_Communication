using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoipNet.AI.Speech;
using VoipNet.Audio;

namespace VoipNet.RealtimeAgent;

internal static class DemoScript
{
    public static readonly string[] Lines =
    [
        "Halo, internet saya mati sejak tadi pagi.",
        "Nomor pelanggan saya 4321, alamatnya di Bandung.",
        "Oke, terima kasih banyak.",
    ];
}

/// <summary>Turns each utterance detected in the call audio into the next scripted line.</summary>
internal sealed class ScriptedCaller(IReadOnlyList<string> lines) : ISpeechToText
{
    public string Name => "scripted caller";

    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(IAsyncEnumerable<AudioChunk> audio, SpeechRecognitionOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var vad = new VoiceActivityDetector(16000) { SilenceHangoverMs = 350 };
        var (index, speaking) = (0, false);
        await foreach (var chunk in audio.WithCancellation(cancellationToken))
        {
            var now = vad.Process(Pcm.AsSamples(chunk.Data.Span));
            if (speaking && !now && index < lines.Count)
            {
                yield return new TranscriptSegment(lines[index++], true);
            }

            speaking = now;
        }
    }

    public Task<string> TranscribeOnceAsync(ReadOnlyMemory<byte> pcm, int sampleRate, SpeechRecognitionOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(lines[0]);
}

/// <summary>A stand-in voice: one short tone per word, so the call carries audio without a TTS key.</summary>
internal sealed class SyllableVoice : ITextToSpeech
{
    public string Name => "syllables";

    public int PreferredSampleRate => 16000;

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(string text, SpeechSynthesisOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var length = 16 * Math.Clamp(word.Length * 45, 90, 380);
            var samples = new short[length + 1200];
            for (var i = 0; i < length; i++)
            {
                samples[i] = (short)(6000 * Math.Sin(Math.PI * i / length) * Math.Sin(2 * Math.PI * 190 * i / 16000));
            }

            yield return new AudioChunk(Pcm.ToBytes(samples), 16000);
            await Task.Yield();
        }
    }
}

/// <summary>Calls the agent from a second in-process endpoint and speaks the scripted lines.</summary>
internal sealed class DemoCaller(VoipClient agentEndpoint, IHostApplicationLifetime lifetime, ILogger<DemoCaller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (string.IsNullOrEmpty(agentEndpoint.LocalAddress) && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(100, stoppingToken);
        }

        await using var caller = new VoipClient(new VoipClientOptions { BindAddress = "127.0.0.1", SipPort = 0, Username = "demo-caller" });
        await caller.StartAsync(stoppingToken);
        logger.LogInformation("Demo: calling the agent at {Address}", agentEndpoint.LocalAddress);
        var call = await caller.CallAsync($"sip:ai-agent@{agentEndpoint.LocalAddress}", stoppingToken);

        var lastHeard = DateTime.UtcNow;
        call.AudioReceived += (_, direction, _, samples) =>
        {
            if (direction == AudioDirection.Inbound && Pcm.Rms(samples) > 0.02)
            {
                lastHeard = DateTime.UtcNow;
            }
        };

        foreach (var _ in DemoScript.Lines)
        {
            await WaitForAgentToFinishAsync(() => lastHeard, stoppingToken);
            call.SendAudio(Voice(1300), 16000);
            await Task.Delay(2500, stoppingToken);
        }

        await WaitForAgentToFinishAsync(() => lastHeard, stoppingToken);
        await call.HangupAsync(stoppingToken);
        logger.LogInformation("Demo finished.");
        lifetime.StopApplication();
    }

    private static async Task WaitForAgentToFinishAsync(Func<DateTime> lastHeard, CancellationToken cancellationToken)
    {
        // The agent is done when it has spoken and then stayed quiet for a moment.
        var deadline = DateTime.UtcNow.AddSeconds(40);
        await Task.Delay(1500, cancellationToken);
        while (DateTime.UtcNow < deadline && DateTime.UtcNow - lastHeard() < TimeSpan.FromMilliseconds(1600))
        {
            await Task.Delay(200, cancellationToken);
        }
    }

    private static short[] Voice(int milliseconds)
    {
        var samples = new short[16 * milliseconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(7000 * Math.Max(0, Math.Sin(Math.PI * i / 4200.0)) * Math.Sin(2 * Math.PI * 220 * i / 16000));
        }

        return samples;
    }
}

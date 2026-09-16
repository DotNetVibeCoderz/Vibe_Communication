namespace VoipNet.AI.Speech;

/// <summary>A block of 16-bit mono PCM audio.</summary>
/// <param name="Data">Little-endian PCM bytes.</param>
/// <param name="SampleRate">Sample rate in hertz.</param>
public readonly record struct AudioChunk(ReadOnlyMemory<byte> Data, int SampleRate)
{
    /// <summary>Duration of the chunk.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Data.Length / 2.0 / Math.Max(SampleRate, 1));
}

/// <summary>A piece of recognised speech.</summary>
/// <param name="Text">Recognised text.</param>
/// <param name="IsFinal">False for interim hypotheses that may still change.</param>
/// <param name="Confidence">Recogniser confidence from 0 to 1, when reported.</param>
/// <param name="Offset">Offset from the start of the stream.</param>
/// <param name="Duration">Length of the recognised audio.</param>
/// <param name="Language">Detected language, when reported.</param>
public sealed record TranscriptSegment(
    string Text,
    bool IsFinal,
    double Confidence = 0,
    TimeSpan Offset = default,
    TimeSpan Duration = default,
    string? Language = null);

/// <summary>Options for a transcription request.</summary>
public sealed class SpeechRecognitionOptions
{
    /// <summary>BCP-47 language tag, for example <c>id-ID</c> or <c>en-US</c>.</summary>
    public string? Language { get; set; }

    /// <summary>Provider specific model name.</summary>
    public string? Model { get; set; }

    /// <summary>Ask for interim results while the caller is still speaking.</summary>
    public bool InterimResults { get; set; } = true;

    /// <summary>Silence that ends an utterance, in milliseconds.</summary>
    public int EndpointingMs { get; set; } = 500;

    /// <summary>Words or names to bias the recogniser towards.</summary>
    public IList<string> Hints { get; } = [];
}

/// <summary>Options for a synthesis request.</summary>
public sealed class SpeechSynthesisOptions
{
    /// <summary>Provider specific voice identifier.</summary>
    public string? Voice { get; set; }

    /// <summary>Provider specific model name.</summary>
    public string? Model { get; set; }

    /// <summary>BCP-47 language tag.</summary>
    public string? Language { get; set; }

    /// <summary>Speaking rate, where 1.0 is the natural rate.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Requested sample rate. Providers fall back to their nearest supported rate.</summary>
    public int SampleRate { get; set; } = 16000;
}

/// <summary>Converts speech to text, in streaming or one-shot form.</summary>
public interface ISpeechToText
{
    /// <summary>Name of the provider, for logs and dashboards.</summary>
    string Name { get; }

    /// <summary>
    /// Transcribes a live audio stream. The returned sequence yields interim segments while the
    /// caller speaks and a final segment at the end of each utterance.
    /// </summary>
    /// <param name="audio">Audio chunks, typically straight from a call.</param>
    /// <param name="options">Recognition options.</param>
    /// <param name="cancellationToken">Stops the recognition session.</param>
    IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Transcribes a complete recording.</summary>
    /// <param name="pcm">Little-endian 16-bit mono PCM.</param>
    /// <param name="sampleRate">Sample rate of <paramref name="pcm"/>.</param>
    /// <param name="options">Recognition options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Converts text to speech as 16-bit mono PCM.</summary>
public interface ITextToSpeech
{
    /// <summary>Name of the provider, for logs and dashboards.</summary>
    string Name { get; }

    /// <summary>Sample rate the provider produces by default.</summary>
    int PreferredSampleRate { get; }

    /// <summary>
    /// Synthesises speech, yielding PCM as soon as the provider produces it so playback can start
    /// before the whole sentence is ready.
    /// </summary>
    /// <param name="text">Text to speak.</param>
    /// <param name="options">Synthesis options.</param>
    /// <param name="cancellationToken">Stops synthesis.</param>
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SpeechSynthesisOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Convenience helpers for speech providers.</summary>
public static class SpeechExtensions
{
    /// <summary>Collects a full synthesis into one buffer.</summary>
    /// <param name="tts">The provider.</param>
    /// <param name="text">Text to speak.</param>
    /// <param name="options">Synthesis options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static async Task<AudioChunk> SynthesizeOnceAsync(
        this ITextToSpeech tts,
        string text,
        SpeechSynthesisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tts);
        var buffer = new List<byte>(64 * 1024);
        var rate = options?.SampleRate ?? tts.PreferredSampleRate;
        await foreach (var chunk in tts.SynthesizeAsync(text, options, cancellationToken).ConfigureAwait(false))
        {
            rate = chunk.SampleRate;
            buffer.AddRange(chunk.Data.ToArray());
        }

        return new AudioChunk(buffer.ToArray(), rate);
    }

    /// <summary>Speaks text directly into a call, with back-pressure so audio is not queued up front.</summary>
    /// <param name="tts">The provider.</param>
    /// <param name="call">Call to speak into.</param>
    /// <param name="text">Text to speak.</param>
    /// <param name="options">Synthesis options.</param>
    /// <param name="cancellationToken">Stops speaking.</param>
    public static async Task SpeakAsync(
        this ITextToSpeech tts,
        VoipCall call,
        string text,
        SpeechSynthesisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tts);
        ArgumentNullException.ThrowIfNull(call);
        await foreach (var chunk in tts.SynthesizeAsync(text, options, cancellationToken).ConfigureAwait(false))
        {
            if (!call.IsActive || cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var queued = call.SendAudio(chunk.Data.Span, chunk.SampleRate);
            while (queued > 1500 && call.IsActive && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                queued = call.QueuedAudioMs;
            }
        }
    }

    /// <summary>Adapts the audio of a call into chunks a recogniser can consume.</summary>
    /// <param name="call">Call to read from.</param>
    /// <param name="direction">Which side of the conversation to transcribe.</param>
    /// <param name="cancellationToken">Stops reading.</param>
    public static async IAsyncEnumerable<AudioChunk> ReadAudioChunksAsync(
        this VoipCall call,
        AudioDirection direction = AudioDirection.Inbound,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        await foreach (var segment in call.ReadAudioAsync(direction, cancellationToken).ConfigureAwait(false))
        {
            yield return new AudioChunk(VoipNet.Audio.Pcm.ToBytes(segment.Samples.Span), segment.SampleRate);
        }
    }
}

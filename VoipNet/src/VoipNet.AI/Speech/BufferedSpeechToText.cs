using System.Runtime.CompilerServices;
using VoipNet.Audio;

namespace VoipNet.AI.Speech;

/// <summary>
/// Base class for recognisers that only offer a one-shot HTTP endpoint. It watches the audio with
/// a voice activity detector, cuts it into utterances, and transcribes each one as the caller
/// finishes speaking. Providers with a true streaming API (Deepgram, for example) do not use this.
/// </summary>
public abstract class BufferedSpeechToText : ISpeechToText
{
    /// <inheritdoc/>
    public abstract string Name { get; }

    /// <summary>Longest utterance to buffer before transcribing anyway.</summary>
    public TimeSpan MaxUtterance { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Audio kept before speech starts, so the first syllable is not clipped.</summary>
    public TimeSpan PreRoll { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <inheritdoc/>
    public abstract Task<string> TranscribeOnceAsync(
        ReadOnlyMemory<byte> pcm,
        int sampleRate,
        SpeechRecognitionOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public async IAsyncEnumerable<TranscriptSegment> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        SpeechRecognitionOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var detector = new VoiceActivityDetector { SilenceHangoverMs = Math.Max(options?.EndpointingMs ?? 500, 200) };
        var utterance = new List<byte>(64 * 1024);
        var preRoll = new Queue<byte[]>();
        var sampleRate = 16000;
        var wasSpeaking = false;
        var offset = TimeSpan.Zero;
        var utteranceStart = TimeSpan.Zero;

        await foreach (var chunk in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            sampleRate = chunk.SampleRate;
            offset += chunk.Duration;
            var samples = Pcm.AsSamples(chunk.Data.Span);
            var speaking = detector.Process(samples);

            if (speaking)
            {
                if (!wasSpeaking)
                {
                    utteranceStart = offset;
                    while (preRoll.Count > 0)
                    {
                        utterance.AddRange(preRoll.Dequeue());
                    }
                }

                utterance.AddRange(chunk.Data.ToArray());
            }
            else
            {
                preRoll.Enqueue(chunk.Data.ToArray());
                var preRollChunks = Math.Max((int)(PreRoll.TotalMilliseconds / Math.Max(chunk.Duration.TotalMilliseconds, 1)), 1);
                while (preRoll.Count > preRollChunks)
                {
                    preRoll.Dequeue();
                }
            }

            var tooLong = utterance.Count > sampleRate * 2 * MaxUtterance.TotalSeconds;
            if ((wasSpeaking && !speaking && utterance.Count > 0) || tooLong)
            {
                var pcm = utterance.ToArray();
                utterance.Clear();
                var text = await TranscribeOnceAsync(pcm, sampleRate, options, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return new TranscriptSegment(
                        text.Trim(),
                        IsFinal: true,
                        Confidence: 1,
                        Offset: utteranceStart,
                        Duration: TimeSpan.FromSeconds(pcm.Length / 2.0 / sampleRate),
                        Language: options?.Language);
                }
            }

            wasSpeaking = speaking;
        }

        if (utterance.Count > 0)
        {
            var text = await TranscribeOnceAsync(utterance.ToArray(), sampleRate, options, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new TranscriptSegment(text.Trim(), IsFinal: true, Confidence: 1, Offset: utteranceStart);
            }
        }
    }

    /// <summary>Wraps raw PCM in a WAV container, which is what most HTTP endpoints expect.</summary>
    /// <param name="pcm">Little-endian 16-bit mono PCM.</param>
    /// <param name="sampleRate">Sample rate of the audio.</param>
    protected static byte[] ToWav(ReadOnlyMemory<byte> pcm, int sampleRate)
    {
        using var stream = new MemoryStream(pcm.Length + 64);
        using (var writer = new WavWriter(stream, sampleRate, 1))
        {
            writer.Write(Pcm.AsSamples(pcm.Span));
        }

        return stream.ToArray();
    }
}

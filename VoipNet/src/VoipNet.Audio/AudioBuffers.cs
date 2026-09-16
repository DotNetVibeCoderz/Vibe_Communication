using System.Runtime.InteropServices;

namespace VoipNet.Audio;

/// <summary>Conversions between 16-bit PCM samples and byte buffers.</summary>
public static class Pcm
{
    /// <summary>Reinterprets little-endian PCM bytes as samples.</summary>
    /// <param name="bytes">Raw PCM bytes.</param>
    public static ReadOnlySpan<short> AsSamples(ReadOnlySpan<byte> bytes) => MemoryMarshal.Cast<byte, short>(bytes);

    /// <summary>Reinterprets samples as little-endian PCM bytes.</summary>
    /// <param name="samples">PCM samples.</param>
    public static ReadOnlySpan<byte> AsBytes(ReadOnlySpan<short> samples) => MemoryMarshal.AsBytes(samples);

    /// <summary>Copies samples into a new byte array.</summary>
    /// <param name="samples">PCM samples.</param>
    public static byte[] ToBytes(ReadOnlySpan<short> samples) => AsBytes(samples).ToArray();

    /// <summary>Root mean square level of a block, from 0 to 1.</summary>
    /// <param name="samples">PCM samples.</param>
    public static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
        }

        return Math.Sqrt(sum / samples.Length) / short.MaxValue;
    }

    /// <summary>Mixes <paramref name="source"/> into <paramref name="destination"/> with saturation.</summary>
    /// <param name="destination">Buffer that receives the mix.</param>
    /// <param name="source">Samples to add.</param>
    public static void MixInto(Span<short> destination, ReadOnlySpan<short> source)
    {
        var n = Math.Min(destination.Length, source.Length);
        for (var i = 0; i < n; i++)
        {
            destination[i] = (short)Math.Clamp(destination[i] + source[i], short.MinValue, short.MaxValue);
        }
    }

    /// <summary>Applies a linear gain with saturation.</summary>
    /// <param name="samples">Samples to scale in place.</param>
    /// <param name="gain">Linear gain, where 1.0 leaves the signal unchanged.</param>
    public static void ApplyGain(Span<short> samples, double gain)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        }
    }
}

/// <summary>Linear resampler for 16-bit mono PCM, with state carried across calls.</summary>
/// <param name="sourceRate">Input sample rate.</param>
/// <param name="targetRate">Output sample rate.</param>
public sealed class AudioResampler(int sourceRate, int targetRate)
{
    private long _position;
    private short _last;

    /// <summary>Input sample rate.</summary>
    public int SourceRate { get; } = sourceRate;

    /// <summary>Output sample rate.</summary>
    public int TargetRate { get; } = targetRate;

    /// <summary>Resamples a block, appending the result to <paramref name="output"/>.</summary>
    /// <param name="input">Input samples.</param>
    /// <param name="output">Receives the resampled audio.</param>
    public void Process(ReadOnlySpan<short> input, List<short> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (SourceRate == TargetRate)
        {
            foreach (var s in input)
            {
                output.Add(s);
            }

            return;
        }

        if (input.IsEmpty)
        {
            return;
        }

        long from = SourceRate;
        long to = TargetRate;
        var total = input.Length * to;
        while (_position < total)
        {
            var index = (int)(_position / to);
            var frac = (int)(_position % to);
            var a = index == 0 ? _last : input[index - 1];
            var b = input[Math.Min(index, input.Length - 1)];
            output.Add((short)Math.Clamp(a + (b - a) * frac / to, short.MinValue, short.MaxValue));
            _position += from;
        }

        _position -= total;
        _last = input[^1];
    }

    /// <summary>Resamples a block into a new array.</summary>
    /// <param name="input">Input samples.</param>
    public short[] Process(ReadOnlySpan<short> input)
    {
        var list = new List<short>(input.Length * TargetRate / Math.Max(SourceRate, 1) + 2);
        Process(input, list);
        return [.. list];
    }
}

/// <summary>
/// Energy based voice activity detector with an adaptive noise floor. It is intentionally simple:
/// telephony audio is band limited and the detector only has to decide when a caller starts and
/// stops speaking.
/// </summary>
/// <param name="sampleRate">Sample rate of the audio fed to the detector.</param>
public sealed class VoiceActivityDetector(int sampleRate = 16000)
{
    private double _noiseFloor = 0.01;
    private int _speechMs;
    private int _silenceMs;

    /// <summary>Level above the noise floor (linear ratio) that counts as speech.</summary>
    public double Threshold { get; set; } = 3.0;

    /// <summary>Speech must last this long before <see cref="IsSpeaking"/> turns on.</summary>
    public int SpeechOnsetMs { get; set; } = 100;

    /// <summary>Silence must last this long before <see cref="IsSpeaking"/> turns off.</summary>
    public int SilenceHangoverMs { get; set; } = 500;

    /// <summary>True while the detector believes someone is speaking.</summary>
    public bool IsSpeaking { get; private set; }

    /// <summary>Most recent level, from 0 to 1.</summary>
    public double Level { get; private set; }

    /// <summary>Raised when speech starts.</summary>
    public event EventHandler? SpeechStarted;

    /// <summary>Raised when speech stops, carrying the length of the utterance.</summary>
    public event EventHandler<TimeSpan>? SpeechEnded;

    /// <summary>Feeds one block of audio.</summary>
    /// <param name="samples">PCM samples at the configured sample rate.</param>
    /// <returns>True when the detector considers the block to be speech.</returns>
    public bool Process(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return IsSpeaking;
        }

        var durationMs = samples.Length * 1000 / Math.Max(sampleRate, 1);
        Level = Pcm.Rms(samples);
        var speech = Level > _noiseFloor * Threshold && Level > 0.005;

        // Track the noise floor slowly, and only downwards while speech is present.
        _noiseFloor = speech ? Math.Min(_noiseFloor, Level) * 0.999 + _noiseFloor * 0.001 : _noiseFloor * 0.95 + Level * 0.05;
        _noiseFloor = Math.Clamp(_noiseFloor, 0.0005, 0.2);

        if (speech)
        {
            _speechMs += durationMs;
            _silenceMs = 0;
            if (!IsSpeaking && _speechMs >= SpeechOnsetMs)
            {
                IsSpeaking = true;
                SpeechStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        else
        {
            _silenceMs += durationMs;
            if (IsSpeaking && _silenceMs >= SilenceHangoverMs)
            {
                IsSpeaking = false;
                var spoken = TimeSpan.FromMilliseconds(_speechMs);
                _speechMs = 0;
                SpeechEnded?.Invoke(this, spoken);
            }
            else if (!IsSpeaking)
            {
                _speechMs = 0;
            }
        }

        return IsSpeaking;
    }

    /// <summary>Clears the detector state between calls.</summary>
    public void Reset()
    {
        IsSpeaking = false;
        _speechMs = 0;
        _silenceMs = 0;
        _noiseFloor = 0.01;
    }
}

/// <summary>Generates call progress tones and DTMF locally, for example for a softphone UI.</summary>
public static class ToneGenerator
{
    /// <summary>European style ringback: 425 Hz, one second on, four seconds off.</summary>
    /// <param name="sampleRate">Sample rate to generate.</param>
    /// <param name="durationMs">Total duration.</param>
    public static short[] Ringback(int sampleRate, int durationMs) => Cadence(sampleRate, durationMs, 425, 1000, 4000);

    /// <summary>Busy tone: 425 Hz, 500 ms on, 500 ms off.</summary>
    /// <param name="sampleRate">Sample rate to generate.</param>
    /// <param name="durationMs">Total duration.</param>
    public static short[] Busy(int sampleRate, int durationMs) => Cadence(sampleRate, durationMs, 425, 500, 500);

    /// <summary>Dial tone: continuous 425 Hz.</summary>
    /// <param name="sampleRate">Sample rate to generate.</param>
    /// <param name="durationMs">Total duration.</param>
    public static short[] DialTone(int sampleRate, int durationMs) => Cadence(sampleRate, durationMs, 425, durationMs, 0);

    /// <summary>A DTMF digit as a dual tone.</summary>
    /// <param name="digit">Digit to render (0-9, *, #, A-D).</param>
    /// <param name="sampleRate">Sample rate to generate.</param>
    /// <param name="durationMs">Tone duration.</param>
    public static short[] Dtmf(char digit, int sampleRate, int durationMs = 120)
    {
        var (low, high) = Frequencies(digit);
        var samples = new short[sampleRate * durationMs / 1000];
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)sampleRate;
            samples[i] = (short)(8000 * (Math.Sin(2 * Math.PI * low * t) + Math.Sin(2 * Math.PI * high * t)) / 2);
        }

        return samples;
    }

    private static short[] Cadence(int sampleRate, int durationMs, double frequency, int onMs, int offMs)
    {
        var samples = new short[sampleRate * durationMs / 1000];
        var period = Math.Max(onMs + offMs, 1);
        for (var i = 0; i < samples.Length; i++)
        {
            var positionMs = i * 1000 / sampleRate % period;
            if (positionMs >= onMs)
            {
                continue;
            }

            samples[i] = (short)(9000 * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        }

        return samples;
    }

    private static (double Low, double High) Frequencies(char digit)
    {
        const string keypad = "123A456B789C*0#D";
        double[] low = [697, 770, 852, 941];
        double[] high = [1209, 1336, 1477, 1633];
        var index = keypad.IndexOf(char.ToUpperInvariant(digit));
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(digit), digit, "Not a DTMF digit.");
        }

        return (low[index / 4], high[index % 4]);
    }
}

using System.Numerics;
using System.Runtime.InteropServices;

namespace Rumble.Net.Audio;

/// <summary>In-place processor for mono 48 kHz frames (runs on the audio thread; must not block or allocate).</summary>
public interface IAudioFilter
{
    /// <summary>Processes one frame in place.</summary>
    void Process(Span<float> samples);
}

/// <summary>Vectorized audio math helpers.</summary>
public static class AudioMath
{
    /// <summary>Samples per second used by Mumble.</summary>
    public const int SampleRate = 48_000;

    /// <summary>Samples in one 10 ms frame.</summary>
    public const int FrameSize = 480;

    /// <summary>Root mean square of a buffer (SIMD accelerated).</summary>
    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        var sum = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var acc = Vector<float>.Zero;
            var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
            foreach (var v in vectors)
            {
                acc += v * v;
            }

            sum = Vector.Sum(acc);
            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < samples.Length; i++)
        {
            sum += samples[i] * samples[i];
        }

        return MathF.Sqrt(sum / samples.Length);
    }

    /// <summary>Peak absolute value.</summary>
    public static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var s in samples)
        {
            peak = MathF.Max(peak, MathF.Abs(s));
        }

        return peak;
    }

    /// <summary>Linear level to dBFS (−96 for silence).</summary>
    public static float ToDecibels(float level) => level <= 1e-5f ? -96f : 20f * MathF.Log10(level);

    /// <summary>dB to linear gain.</summary>
    public static float FromDecibels(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Multiplies a buffer by a gain (SIMD accelerated).</summary>
    public static void ApplyGain(Span<float> samples, float gain)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<float>.Count)
        {
            var g = new Vector<float>(gain);
            var vectors = MemoryMarshal.Cast<float, Vector<float>>(samples);
            for (var v = 0; v < vectors.Length; v++)
            {
                vectors[v] *= g;
            }

            i = vectors.Length * Vector<float>.Count;
        }

        for (; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    /// <summary>Converts 16-bit PCM to float.</summary>
    public static void ToFloat(ReadOnlySpan<short> input, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = input[i] / 32768f;
        }
    }

    /// <summary>Converts float PCM to 16-bit with saturation.</summary>
    public static void ToInt16(ReadOnlySpan<float> input, Span<short> output)
    {
        for (var i = 0; i < input.Length; i++)
        {
            output[i] = (short)Math.Clamp(input[i] * 32767f, short.MinValue, short.MaxValue);
        }
    }
}

/// <summary>Applies a fixed gain in decibels.</summary>
public sealed class GainFilter(float gainDb) : IAudioFilter
{
    private float _gain = AudioMath.FromDecibels(gainDb);

    /// <summary>Gain in dB.</summary>
    public float GainDb
    {
        get => AudioMath.ToDecibels(_gain);
        set => _gain = AudioMath.FromDecibels(value);
    }

    /// <inheritdoc />
    public void Process(Span<float> samples) => AudioMath.ApplyGain(samples, _gain);
}

/// <summary>Silences frames below a threshold with smooth attack and release.</summary>
public sealed class NoiseGateFilter(float thresholdDb = -50f) : IAudioFilter
{
    private float _gain;

    /// <summary>Threshold in dBFS.</summary>
    public float ThresholdDb { get; set; } = thresholdDb;

    /// <inheritdoc />
    public void Process(Span<float> samples)
    {
        var open = AudioMath.ToDecibels(AudioMath.Rms(samples)) >= ThresholdDb;
        var target = open ? 1f : 0f;
        var end = _gain + (target - _gain) * (open ? 0.6f : 0.08f);
        var step = (end - _gain) / samples.Length;
        var g = _gain;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= g;
            g += step;
        }

        _gain = end;
    }
}

/// <summary>Brick-wall limiter with smooth gain reduction.</summary>
public sealed class LimiterFilter(float ceilingDb = -1f) : IAudioFilter
{
    private float _envelope = 1f;

    /// <summary>Output ceiling in dBFS.</summary>
    public float CeilingDb { get; set; } = ceilingDb;

    /// <inheritdoc />
    public void Process(Span<float> samples)
    {
        var ceiling = AudioMath.FromDecibels(CeilingDb);
        var peak = AudioMath.Peak(samples);
        var target = peak > ceiling ? ceiling / peak : 1f;
        _envelope = target < _envelope ? target : _envelope + (target - _envelope) * 0.05f;
        AudioMath.ApplyGain(samples, _envelope);
    }
}

/// <summary>Delegate that processes samples in place.</summary>
public delegate void AudioSpanAction(Span<float> samples);

/// <summary>Wraps a delegate as a filter.</summary>
public sealed class DelegateAudioFilter(AudioSpanAction process) : IAudioFilter
{
    /// <inheritdoc />
    public void Process(Span<float> samples) => process(samples);
}

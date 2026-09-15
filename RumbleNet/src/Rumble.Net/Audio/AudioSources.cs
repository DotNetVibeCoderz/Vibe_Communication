using System.Buffers.Binary;

namespace Rumble.Net.Audio;

/// <summary>Generates test tones.</summary>
public static class ToneGenerator
{
    /// <summary>Creates a mono 48 kHz sine wave.</summary>
    public static float[] Sine(float frequency, TimeSpan duration, float amplitude = 0.4f)
    {
        var samples = new float[(int)(duration.TotalSeconds * AudioMath.SampleRate)];
        var step = MathF.Tau * frequency / AudioMath.SampleRate;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = MathF.Sin(i * step) * amplitude;
        }

        return samples;
    }

    /// <summary>Creates a sequence of notes (frequency, duration) with short fades to avoid clicks.</summary>
    public static float[] Melody(IEnumerable<(float Frequency, TimeSpan Duration)> notes, float amplitude = 0.35f)
    {
        var buffer = new List<float>();
        foreach (var (frequency, duration) in notes)
        {
            var tone = Sine(frequency, duration, amplitude);
            var fade = Math.Min(480, tone.Length / 4);
            for (var i = 0; i < fade; i++)
            {
                var g = i / (float)fade;
                tone[i] *= g;
                tone[^(i + 1)] *= g;
            }

            buffer.AddRange(tone);
        }

        return [.. buffer];
    }
}

/// <summary>Minimal WAV reader producing mono 48 kHz float samples.</summary>
public static class WaveFile
{
    /// <summary>Reads a PCM (8/16/24/32-bit) or IEEE float WAV file and converts it to mono 48 kHz.</summary>
    public static float[] ReadMono48k(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Not a RIFF file.");
        }

        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Not a WAVE file.");
        }

        int format = 0, channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        while (stream.Position < stream.Length - 8)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                format = reader.ReadInt16();
                channels = reader.ReadInt16();
                rate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bits = reader.ReadInt16();
                if (size > 16)
                {
                    var extra = reader.ReadBytes(size - 16);
                    if (format == 0xFFFE && extra.Length >= 10)
                    {
                        format = BinaryPrimitives.ReadInt16LittleEndian(extra.AsSpan(8));
                    }
                }
            }
            else if (id == "data")
            {
                data = reader.ReadBytes(size);
                break;
            }
            else
            {
                reader.ReadBytes(size + (size & 1));
            }
        }

        if (data is null || channels == 0)
        {
            throw new InvalidDataException("WAV file has no audio data.");
        }

        var bytesPerSample = bits / 8;
        var frames = data.Length / (bytesPerSample * channels);
        var mono = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var c = 0; c < channels; c++)
            {
                var span = data.AsSpan((f * channels + c) * bytesPerSample, bytesPerSample);
                sum += (format, bits) switch
                {
                    (3, 32) => BinaryPrimitives.ReadSingleLittleEndian(span),
                    (_, 8) => (span[0] - 128) / 128f,
                    (_, 16) => BinaryPrimitives.ReadInt16LittleEndian(span) / 32768f,
                    (_, 24) => ((span[2] << 24 | span[1] << 16 | span[0] << 8) >> 8) / 8388608f,
                    (_, 32) => BinaryPrimitives.ReadInt32LittleEndian(span) / 2147483648f,
                    _ => throw new NotSupportedException($"Unsupported WAV format {format}/{bits}-bit."),
                };
            }

            mono[f] = sum / channels;
        }

        return rate == AudioMath.SampleRate ? mono : Resample(mono, rate, AudioMath.SampleRate);
    }

    /// <summary>Writes mono 48 kHz float samples as a 16-bit PCM WAV.</summary>
    public static void WriteMono48k(Stream stream, ReadOnlySpan<float> samples)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        var dataSize = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(AudioMath.SampleRate);
        writer.Write(AudioMath.SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (var s in samples)
        {
            writer.Write((short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue));
        }
    }

    /// <summary>Linear resampler for offline conversion.</summary>
    public static float[] Resample(ReadOnlySpan<float> input, int fromRate, int toRate)
    {
        var output = new float[(int)((long)input.Length * toRate / fromRate)];
        var ratio = (double)fromRate / toRate;
        for (var i = 0; i < output.Length; i++)
        {
            var pos = i * ratio;
            var idx = (int)pos;
            var frac = (float)(pos - idx);
            var a = input[Math.Min(idx, input.Length - 1)];
            var b = input[Math.Min(idx + 1, input.Length - 1)];
            output[i] = a + (b - a) * frac;
        }

        return output;
    }
}

/// <summary>Streams PCM to a client in real time (headless mode).</summary>
public static class AudioPlayer
{
    /// <summary>Plays mono 48 kHz samples, pacing 20 ms chunks to wall-clock time.</summary>
    public static async Task PlayAsync(RumbleClient client, ReadOnlyMemory<float> samples, CancellationToken cancellationToken = default)
    {
        const int chunk = AudioMath.FrameSize * 2;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sent = 0;
        while (sent < samples.Length && !cancellationToken.IsCancellationRequested)
        {
            var n = Math.Min(chunk, samples.Length - sent);
            client.Audio.SendPcm(samples.Span.Slice(sent, n));
            sent += n;
            var due = TimeSpan.FromSeconds(sent / (double)AudioMath.SampleRate) - clock.Elapsed - TimeSpan.FromMilliseconds(40);
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due, cancellationToken).ConfigureAwait(false);
            }
        }

        client.Audio.EndTransmission();
    }

    /// <summary>Plays a WAV file.</summary>
    public static async Task PlayWaveAsync(RumbleClient client, string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        await PlayAsync(client, WaveFile.ReadMono48k(stream), cancellationToken).ConfigureAwait(false);
    }
}

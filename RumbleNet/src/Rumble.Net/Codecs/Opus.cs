using Rumble.Net.Interop;

namespace Rumble.Net.Codecs;

/// <summary>Opus application profile.</summary>
public enum OpusApplication
{
    /// <summary>Optimized for speech.</summary>
    Voip = 0,
    /// <summary>Optimized for music.</summary>
    Audio = 1,
    /// <summary>Lowest delay.</summary>
    LowDelay = 2,
}

/// <summary>Opus encoder (48 kHz) backed by the native core. Not thread-safe.</summary>
public sealed unsafe class OpusEncoder : IDisposable
{
    private nint _handle;

    /// <summary>Creates an encoder.</summary>
    public OpusEncoder(int channels = 1, int bitrate = 48_000, OpusApplication application = OpusApplication.Voip)
    {
        Native.Check(NativeMethods.rumble_opus_encoder_create(channels, (int)application, bitrate, out _handle));
        Channels = channels;
    }

    /// <summary>Channel count.</summary>
    public int Channels { get; }

    /// <summary>Encodes interleaved PCM whose length is a valid Opus frame (2.5–60 ms). Returns the packet length.</summary>
    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (float* p = pcm)
        fixed (byte* o = packet)
        {
            return Native.Check(NativeMethods.rumble_opus_encode(_handle, p, (nuint)pcm.Length, o, (nuint)packet.Length));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
        {
            NativeMethods.rumble_opus_encoder_destroy(h);
        }
    }
}

/// <summary>Opus decoder (48 kHz) backed by the native core. Not thread-safe.</summary>
public sealed unsafe class OpusDecoder : IDisposable
{
    private nint _handle;

    /// <summary>Largest frame in samples per channel (120 ms).</summary>
    public const int MaxFrameSamples = 5760;

    /// <summary>Creates a decoder.</summary>
    public OpusDecoder(int channels = 1)
    {
        Native.Check(NativeMethods.rumble_opus_decoder_create(channels, out _handle));
        Channels = channels;
    }

    /// <summary>Channel count.</summary>
    public int Channels { get; }

    /// <summary>Decodes a packet. Returns samples per channel.</summary>
    public int Decode(ReadOnlySpan<byte> packet, Span<float> pcm)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (byte* p = packet)
        fixed (float* o = pcm)
        {
            return Native.Check(NativeMethods.rumble_opus_decode(_handle, p, (nuint)packet.Length, o, (nuint)pcm.Length));
        }
    }

    /// <summary>Synthesizes a lost frame (packet loss concealment) filling <paramref name="pcm"/>.</summary>
    public int Conceal(Span<float> pcm)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        fixed (float* o = pcm)
        {
            return Native.Check(NativeMethods.rumble_opus_decode(_handle, null, 0, o, (nuint)pcm.Length));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
        {
            NativeMethods.rumble_opus_decoder_destroy(h);
        }
    }
}

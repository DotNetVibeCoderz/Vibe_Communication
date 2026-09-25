using System.Runtime.Versioning;
using VoipNet.Video.MediaFoundation;

namespace VoipNet.Video;

/// <summary>A picture handed to an encoder, or handed back by a decoder: NV12, tightly packed.</summary>
/// <remarks>
/// NV12 is what every hardware encoder on every platform takes without a conversion of its own: a
/// full-size plane of luma, then one plane of blue and red differences at half resolution, two bytes
/// to a pair of pixels. <see cref="VideoPicture.Nv12Length"/> is the size a frame must be.
/// </remarks>
/// <param name="Width">Picture width in pixels; must be even.</param>
/// <param name="Height">Picture height in pixels; must be even.</param>
/// <param name="Data">The NV12 planes, one after the other.</param>
/// <param name="Timestamp">When the picture was taken, on the call's own clock.</param>
public readonly record struct VideoPicture(int Width, int Height, ReadOnlyMemory<byte> Data, TimeSpan Timestamp)
{
    /// <summary>How many bytes an NV12 picture of this size takes.</summary>
    public static int Nv12Length(int width, int height) => (width * height) + (width * height / 2);
}

/// <summary>One encoded picture, in the Annex B form the RTP packetiser takes.</summary>
/// <param name="Data">The access unit, start codes and all.</param>
/// <param name="Keyframe">True when a decoder could start here.</param>
/// <param name="Timestamp">The timestamp of the picture it came from.</param>
public readonly record struct EncodedVideoFrame(ReadOnlyMemory<byte> Data, bool Keyframe, TimeSpan Timestamp);

/// <summary>Turns pictures into an encoded stream.</summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>The codec produced, as it is named in SDP — for example <c>H264</c>.</summary>
    string Codec { get; }

    /// <summary>Picture width.</summary>
    int Width { get; }

    /// <summary>Picture height.</summary>
    int Height { get; }

    /// <summary>
    /// Which encoder is doing the work, as the platform names it — a card's own where there is one,
    /// otherwise the operating system's. Worth logging when a call turns out to cost more CPU than
    /// expected.
    /// </summary>
    string Implementation { get; }

    /// <summary>Asks for the next picture to be a keyframe, which is what answering a PLI means.</summary>
    void RequestKeyframe();

    /// <summary>
    /// Encodes one picture. An encoder may hold a picture back or return several at once, so the
    /// result is a list — often empty for the first few frames.
    /// </summary>
    IReadOnlyList<EncodedVideoFrame> Encode(VideoPicture picture);

    /// <summary>Encodes whatever is still held back, at the end of a call.</summary>
    IReadOnlyList<EncodedVideoFrame> Drain();
}

/// <summary>Turns an encoded stream back into pictures.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>The codec accepted, as it is named in SDP.</summary>
    string Codec { get; }

    /// <summary>
    /// Decodes one access unit. Like an encoder, a decoder may give back none or several, and the
    /// picture size is only known once the stream says what it is.
    /// </summary>
    IReadOnlyList<VideoPicture> Decode(ReadOnlySpan<byte> frame, TimeSpan timestamp);
}

/// <summary>How an encoder should run: size, rate and how much bandwidth it may take.</summary>
public sealed class VideoEncoderOptions
{
    /// <summary>Picture width in pixels.</summary>
    public int Width { get; set; } = 640;

    /// <summary>Picture height in pixels.</summary>
    public int Height { get; set; } = 360;

    /// <summary>Frames per second the encoder is told to expect.</summary>
    public int FramesPerSecond { get; set; } = 30;

    /// <summary>Target bitrate. A call is sized by what the far end says it can take (REMB).</summary>
    public int BitsPerSecond { get; set; } = 1_000_000;

    /// <summary>
    /// How often a keyframe is produced without being asked for. A call mostly gets its keyframes
    /// from PLI, so this only bounds how long a late joiner waits.
    /// </summary>
    public TimeSpan KeyframeInterval { get; set; } = TimeSpan.FromSeconds(4);
}

/// <summary>The codecs this machine can encode and decode with.</summary>
public static class VideoCodecs
{
    private static bool? _h264;

    /// <summary>
    /// Whether H.264 can be encoded and decoded here. This asks the platform rather than assuming:
    /// Windows Server installs without Media Foundation unless somebody adds it, and a machine that
    /// has no codec should say so rather than throw halfway through a call.
    /// </summary>
    public static bool IsH264Available => _h264 ??= ProbeH264();

    private static bool ProbeH264()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var encoder = CreateWindowsEncoder(new VideoEncoderOptions { Width = 320, Height = 240, FramesPerSecond = 15, BitsPerSecond = 200_000 });
            using var decoder = CreateWindowsDecoder();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or TypeInitializationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Creates an H.264 encoder using the platform's own codec: Media Foundation on Windows, which
    /// prefers a graphics card's encoder where one will take pictures from ordinary memory and uses
    /// the software encoder otherwise. VideoToolbox and VA-API are not wired up, so other platforms
    /// have none.
    /// </summary>
    /// <param name="options">Size, rate and bitrate.</param>
    /// <exception cref="PlatformNotSupportedException">No H.264 encoder on this platform.</exception>
    public static IVideoEncoder CreateH264Encoder(VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("H.264 encoding needs a platform codec; only Media Foundation on Windows is wired up so far.");
        }

        return CreateWindowsEncoder(options);
    }

    /// <summary>Creates an H.264 decoder using the platform's own codec.</summary>
    /// <exception cref="PlatformNotSupportedException">No H.264 decoder on this platform.</exception>
    public static IVideoDecoder CreateH264Decoder()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("H.264 decoding needs a platform codec; only Media Foundation on Windows is wired up so far.");
        }

        return CreateWindowsDecoder();
    }

    [SupportedOSPlatform("windows")]
    private static IVideoEncoder CreateWindowsEncoder(VideoEncoderOptions options) => new MediaFoundationH264Encoder(options);

    [SupportedOSPlatform("windows")]
    private static IVideoDecoder CreateWindowsDecoder() => new MediaFoundationH264Decoder();
}

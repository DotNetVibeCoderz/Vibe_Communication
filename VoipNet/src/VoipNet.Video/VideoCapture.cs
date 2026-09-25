using System.Runtime.Versioning;
using VoipNet.Video.MediaFoundation;

namespace VoipNet.Video;

/// <summary>A camera the operating system knows about.</summary>
/// <param name="Id">What the system calls it, which is what to pass to open it again later.</param>
/// <param name="Name">What a person would call it, for a list on screen.</param>
public readonly record struct VideoCaptureDevice(string Id, string Name);

/// <summary>A source of pictures — a camera today, a screen later.</summary>
/// <remarks>
/// Reading is a pull rather than a stream of events: ask for the next picture and the device's own
/// pacing decides when it arrives. A caller that falls behind simply reads the next picture rather
/// than a queue of stale ones, which is what a call wants.
/// </remarks>
public interface IVideoCaptureSource : IDisposable
{
    /// <summary>What the device is called.</summary>
    string Name { get; }

    /// <summary>Picture width, which may differ from the size asked for if the device refused it.</summary>
    int Width { get; }

    /// <summary>Picture height.</summary>
    int Height { get; }

    /// <summary>
    /// Waits for the next picture. Returns null when the device has stopped, which is the end of the
    /// stream and not an error.
    /// </summary>
    VideoPicture? Read();
}

/// <summary>The cameras on this machine, and how to open one.</summary>
public static class VideoCapture
{
    /// <summary>Whether cameras and the screen can be read here at all.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>
    /// Lists the cameras the operating system offers; empty where there is no support, and empty
    /// rather than thrown where the platform has no media stack installed at all.
    /// </summary>
    public static IReadOnlyList<VideoCaptureDevice> Cameras()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return ListWindows();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
    }

    /// <summary>
    /// Opens a camera. The size and rate are what the device is asked for; a device that cannot do
    /// them gives what it can, and <see cref="IVideoCaptureSource.Width"/> says what that turned out
    /// to be.
    /// </summary>
    /// <param name="device">Which camera, or null for the first one.</param>
    /// <param name="width">Preferred width in pixels.</param>
    /// <param name="height">Preferred height in pixels.</param>
    /// <param name="framesPerSecond">Preferred frame rate.</param>
    /// <exception cref="PlatformNotSupportedException">Cameras are not wired up on this platform.</exception>
    /// <exception cref="InvalidOperationException">There is no camera, or it is in use by something else.</exception>
    public static IVideoCaptureSource OpenCamera(VideoCaptureDevice? device = null, int width = 640, int height = 360, int framesPerSecond = 30)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Camera capture is only wired up on Windows so far; see PLAN 1.3.");
        }

        return OpenWindows(device, width, height, framesPerSecond);
    }

    /// <summary>
    /// Opens the screen as a source of pictures, for sharing it as the second video stream
    /// (<c>a=content:slides</c>). The desktop has no frame rate of its own, so the reader paces
    /// itself: ask for the next picture and it arrives when the interval is up.
    /// </summary>
    /// <param name="wholeDesktop">True for every screen side by side, false for the main one.</param>
    /// <param name="width">Width to scale to; a whole 4K desktop costs more to encode than it is worth.</param>
    /// <param name="height">Height to scale to.</param>
    /// <param name="framesPerSecond">How often to copy the screen.</param>
    /// <exception cref="PlatformNotSupportedException">Screen capture is not wired up on this platform.</exception>
    public static IVideoCaptureSource OpenScreen(bool wholeDesktop = false, int width = 1280, int height = 720, int framesPerSecond = 10)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Screen capture is only wired up on Windows so far; see PLAN 1.3.");
        }

        return OpenWindowsScreen(wholeDesktop, width, height, framesPerSecond);
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<VideoCaptureDevice> ListWindows() => MediaFoundationCamera.Devices();

    [SupportedOSPlatform("windows")]
    private static IVideoCaptureSource OpenWindowsScreen(bool wholeDesktop, int width, int height, int framesPerSecond) =>
        new Windows.GdiScreenCapture(wholeDesktop, width, height, framesPerSecond);

    [SupportedOSPlatform("windows")]
    private static IVideoCaptureSource OpenWindows(VideoCaptureDevice? device, int width, int height, int framesPerSecond) =>
        new MediaFoundationCamera(device?.Id ?? string.Empty, device?.Name ?? "camera", width, height, framesPerSecond);
}

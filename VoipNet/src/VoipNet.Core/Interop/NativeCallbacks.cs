using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VoipNet.Interop;

/// <summary>
/// Entry points the native engine calls. They run on engine threads, so they translate the
/// arguments and hand off to the owning <see cref="VoipClient"/> without blocking.
/// </summary>
internal static unsafe class NativeCallbacks
{
    [UnmanagedCallersOnly]
    internal static void OnEvent(nint user, byte* json)
    {
        var client = Resolve(user);
        if (client is null || json is null)
        {
            return;
        }

        try
        {
            var text = Marshal.PtrToStringUTF8((nint)json);
            if (text is not null)
            {
                client.HandleEvent(text);
            }
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }
    }

    [UnmanagedCallersOnly]
    internal static void OnAudio(nint user, ulong callId, int direction, uint sampleRate, short* pcm, int samples)
    {
        var client = Resolve(user);
        if (client is null || pcm is null || samples <= 0)
        {
            return;
        }

        try
        {
            client.HandleAudio(callId, direction, (int)sampleRate, pcm, samples);
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }
    }

    [UnmanagedCallersOnly]
    internal static void OnDtmf(nint user, ulong callId, uint digit, int source)
    {
        var client = Resolve(user);
        try
        {
            client?.HandleDtmf(callId, (char)digit, (DtmfSource)source);
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }
    }

    [UnmanagedCallersOnly]
    internal static void OnVideo(nint user, ulong callId, uint timestamp, int keyframe, byte* data, int length)
    {
        var client = Resolve(user);
        if (client is null || data is null || length <= 0)
        {
            return;
        }

        try
        {
            client.HandleVideoFrame(callId, timestamp, keyframe != 0, data, length);
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }
    }

    [UnmanagedCallersOnly]
    internal static void OnEncoded(nint user, ulong callId, byte payloadType, uint timestamp, int marker, byte* data, int length)
    {
        var client = Resolve(user);
        if (client is null || data is null || length <= 0)
        {
            return;
        }

        try
        {
            client.HandleEncoded(callId, payloadType, timestamp, marker != 0, data, length);
        }
        catch (Exception ex)
        {
            Swallow(ex);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static VoipClient? Resolve(nint user)
    {
        if (user == nint.Zero)
        {
            return null;
        }

        var handle = GCHandle.FromIntPtr(user);
        return handle.IsAllocated ? handle.Target as VoipClient : null;
    }

    /// <summary>
    /// Exceptions must never cross back into Rust: an unwind through the FFI boundary would
    /// abort the process. Application handlers are expected to deal with their own errors.
    /// </summary>
    private static void Swallow(Exception ex) => Debug(ex);

    private static void Debug(Exception ex) => System.Diagnostics.Debug.WriteLine($"Voip.NET callback error: {ex}");
}

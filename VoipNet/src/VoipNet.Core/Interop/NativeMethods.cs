using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VoipNet.Interop;

/// <summary>Raw bindings to the Voip.NET native engine (<c>voipnet_core</c>).</summary>
internal static unsafe partial class NativeMethods
{
    internal const string Library = "voipnet_core";

    static NativeMethods()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);
    }

    /// <summary>Forces the static constructor (and therefore the resolver) to run.</summary>
    internal static void EnsureLoaded() => RuntimeHelpers.RunClassConstructor(typeof(NativeMethods).TypeHandle);

    private static nint Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != Library)
        {
            return nint.Zero;
        }

        // Default probing first (NuGet runtimes/<rid>/native and the application directory).
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
        {
            return handle;
        }

        var fileName = OperatingSystem.IsWindows() ? "voipnet_core.dll"
            : OperatingSystem.IsMacOS() ? "libvoipnet_core.dylib"
            : "libvoipnet_core.so";

        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, fileName),
            Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName),
            // Repository layout: run samples and tests without installing the package.
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "native", "target", "release", fileName),
            Path.Combine(baseDir, "..", "..", "..", "..", "..", "..", "native", "target", "release", fileName),
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Callbacks
    {
        public delegate* unmanaged<nint, byte*, void> OnEvent;
        public delegate* unmanaged<nint, ulong, int, uint, short*, int, void> OnAudio;
        public delegate* unmanaged<nint, ulong, uint, int, void> OnDtmf;
        public delegate* unmanaged<nint, ulong, byte, uint, int, byte*, int, void> OnEncoded;
        public nint UserData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MediaStatsNative
    {
        public ulong PacketsSent;
        public ulong PacketsReceived;
        public ulong BytesSent;
        public ulong BytesReceived;
        public ulong PacketsLost;
        public ulong PacketsLate;
        public double JitterMs;
        public uint JitterBufferMs;
        public uint PayloadType;
        public uint SampleRate;
        public double Mos;
        public byte SrtpActive;
        public byte IceConnected;
        public uint OutboundQueuedMs;
        public double RemoteLossPercent;
        public double RemoteJitterMs;
        public double RoundTripMs;
    }

    [LibraryImport(Library, EntryPoint = "voipnet_endpoint_create", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int EndpointCreate(string configJson, Callbacks callbacks, out nint handle, byte* errorBuffer, int errorLength);

    [LibraryImport(Library, EntryPoint = "voipnet_endpoint_destroy")]
    internal static partial int EndpointDestroy(nint handle);

    [LibraryImport(Library, EntryPoint = "voipnet_register")]
    internal static partial int Register(nint handle);

    [LibraryImport(Library, EntryPoint = "voipnet_unregister")]
    internal static partial int Unregister(nint handle);

    [LibraryImport(Library, EntryPoint = "voipnet_make_call", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int MakeCall(nint handle, string target, out ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_answer")]
    internal static partial int Answer(nint handle, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_reject")]
    internal static partial int Reject(nint handle, ulong callId, ushort statusCode);

    [LibraryImport(Library, EntryPoint = "voipnet_hangup")]
    internal static partial int Hangup(nint handle, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_set_hold")]
    internal static partial int SetHold(nint handle, ulong callId, int hold);

    [LibraryImport(Library, EntryPoint = "voipnet_set_mute")]
    internal static partial int SetMute(nint handle, ulong callId, int mute);

    [LibraryImport(Library, EntryPoint = "voipnet_restart_ice")]
    internal static partial int RestartIce(nint handle, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_transfer", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Transfer(nint handle, ulong callId, string target);

    [LibraryImport(Library, EntryPoint = "voipnet_transfer_attended")]
    internal static partial int TransferAttended(nint handle, ulong callId, ulong consultCallId);

    [LibraryImport(Library, EntryPoint = "voipnet_send_dtmf", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SendDtmf(nint handle, ulong callId, string digits, uint durationMs);

    [LibraryImport(Library, EntryPoint = "voipnet_send_audio")]
    internal static partial int SendAudio(nint handle, ulong callId, short* pcm, int samples, uint sampleRate, out uint queuedMs);

    [LibraryImport(Library, EntryPoint = "voipnet_clear_audio")]
    internal static partial int ClearAudio(nint handle, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_send_encoded")]
    internal static partial int SendEncoded(nint handle, ulong callId, byte payloadType, uint timestamp, int marker, byte* data, int length);

    [LibraryImport(Library, EntryPoint = "voipnet_call_stats")]
    internal static partial int CallStats(nint handle, ulong callId, out MediaStatsNative stats);

    [LibraryImport(Library, EntryPoint = "voipnet_call_info_json")]
    internal static partial nint CallInfoJson(nint handle, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_calls_json")]
    internal static partial nint CallsJson(nint handle);

    [LibraryImport(Library, EntryPoint = "voipnet_string_free")]
    internal static partial void StringFree(nint value);

    [LibraryImport(Library, EntryPoint = "voipnet_send_options", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SendOptions(nint handle, string target, out ulong requestId);

    [LibraryImport(Library, EntryPoint = "voipnet_send_message", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int SendMessage(nint handle, string target, string contentType, string body, out ulong requestId);

    [LibraryImport(Library, EntryPoint = "voipnet_conference_create")]
    internal static partial int ConferenceCreate(nint handle, out ulong conferenceId);

    [LibraryImport(Library, EntryPoint = "voipnet_conference_add")]
    internal static partial int ConferenceAdd(nint handle, ulong conferenceId, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_conference_remove")]
    internal static partial int ConferenceRemove(nint handle, ulong callId);

    [LibraryImport(Library, EntryPoint = "voipnet_conference_destroy")]
    internal static partial int ConferenceDestroy(nint handle, ulong conferenceId);

    [LibraryImport(Library, EntryPoint = "voipnet_local_address")]
    internal static partial nint LocalAddress(nint handle);

    [LibraryImport(Library, EntryPoint = "voipnet_tls_fingerprint")]
    internal static partial nint TlsFingerprint(nint handle);

    [LibraryImport(Library, EntryPoint = "voipnet_version")]
    internal static partial nint Version();

    internal static string? ConsumeString(nint pointer)
    {
        if (pointer == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(pointer);
        }
        finally
        {
            StringFree(pointer);
        }
    }
}

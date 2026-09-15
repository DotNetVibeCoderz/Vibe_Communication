using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Rumble.Net.Interop;
using Rumble.Net.Models;

namespace Rumble.Net;

/// <summary>Library-wide native utilities.</summary>
public static unsafe class RumbleNative
{
    private static ILogger? s_nativeLogger;

    /// <summary>Version of the native core.</summary>
    public static string Version => Marshal.PtrToStringUTF8((nint)NativeMethods.rumble_version()) ?? "unknown";

    /// <summary>Credits shown in apps and documentation.</summary>
    public const string Credits = "Rumble.Net — made by Gravicode Studios, led by Kang Fadhil";

    /// <summary>Forwards native core logs to <paramref name="loggerFactory"/> (category <c>Rumble.Native</c>).</summary>
    public static void ConfigureLogging(ILoggerFactory? loggerFactory, LogLevel minimumLevel = LogLevel.Information)
    {
        if (loggerFactory is null)
        {
            s_nativeLogger = null;
            Native.Check(NativeMethods.rumble_set_log_callback(null, 0, 5));
            return;
        }

        s_nativeLogger = loggerFactory.CreateLogger("Rumble.Native");
        Native.Check(NativeMethods.rumble_set_log_callback(&OnLog, 0, (int)minimumLevel));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(nint userData, int level, byte* target, nuint targetLength, byte* message, nuint messageLength)
    {
        try
        {
            if (s_nativeLogger is not { } logger)
            {
                return;
            }

            var text = Encoding.UTF8.GetString(message, (int)messageLength);
            var source = Encoding.UTF8.GetString(target, (int)targetLength);
            logger.Log((LogLevel)Math.Clamp(level, 0, 5), "[{Target}] {Message}", source, text);
        }
        catch
        {
            // Never throw into native code.
        }
    }

    /// <summary>Lists audio input and output devices.</summary>
    public static IReadOnlyList<AudioDevice> GetAudioDevices()
    {
        Native.Check(NativeMethods.rumble_audio_list_devices(out var ptr, out var len));
        return Native.ReadJson(ptr, len, RumbleJsonContext.Default.ListAudioDevice);
    }

    /// <summary>Generates a self-signed client certificate for registering users.</summary>
    public static GeneratedCertificate GenerateCertificate(string commonName)
    {
        using var name = new Native.Utf8Buffer(commonName);
        fixed (byte* p = name.Span)
        {
            Native.Check(NativeMethods.rumble_generate_certificate(p, (nuint)name.Span.Length, out var ptr, out var len));
            return Native.ReadJson(ptr, len, RumbleJsonContext.Default.GeneratedCertificate);
        }
    }

    /// <summary>Queries a server's version, user count and latency without connecting.</summary>
    public static Task<ServerQueryResult> QueryServerAsync(string host, int port = 64738, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var ms = (uint)(timeout ?? TimeSpan.FromSeconds(3)).TotalMilliseconds;
        return Task.Run(() =>
        {
            using var h = new Native.Utf8Buffer(host);
            fixed (byte* p = h.Span)
            {
                Native.Check(NativeMethods.rumble_query_server(p, (nuint)h.Span.Length, (ushort)port, ms, out var ptr, out var len));
                return Native.ReadJson(ptr, len, RumbleJsonContext.Default.ServerQueryResult);
            }
        }, cancellationToken);
    }

    /// <summary>Runs the native micro-benchmarks (crypto, Opus, mixer).</summary>
    public static IReadOnlyList<BenchmarkResult> RunBenchmarks()
    {
        Native.Check(NativeMethods.rumble_run_benchmarks(out var ptr, out var len));
        return Native.ReadJson(ptr, len, RumbleJsonContext.Default.ListBenchmarkResult);
    }
}

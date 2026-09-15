using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization.Metadata;

namespace Rumble.Net.Interop;

/// <summary>Helpers shared by all interop call sites.</summary>
internal static unsafe class Native
{
    private static int s_resolverInstalled;

    /// <summary>
    /// Installs a resolver that also probes <c>runtimes/{rid}/native</c> next to the assembly, which
    /// covers self-contained, single-file and app-model layouts that do not flatten native assets.
    /// </summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "The resolver must be installed before the first P/Invoke from any entry point.")]
    internal static void InstallResolver()
    {
        if (Interlocked.Exchange(ref s_resolverInstalled, 1) != 0)
        {
            return;
        }

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, Resolve);
        }
        catch (InvalidOperationException)
        {
            // A resolver was already set by the host application.
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != NativeMethods.Library)
        {
            return 0;
        }

        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
        {
            return handle;
        }

        var fileName = OperatingSystem.IsWindows() ? "rumble_native.dll"
            : OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ? "librumble_native.dylib"
            : "librumble_native.so";

        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(baseDir))
        {
            var candidates = new[]
            {
                Path.Combine(baseDir, fileName),
                Path.Combine(baseDir, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", fileName),
                Path.Combine(baseDir, "runtimes", PortableRid(), "native", fileName),
            };
            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out handle))
                {
                    return handle;
                }
            }
        }

        return 0;
    }

    private static string PortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsAndroid() ? "android" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    /// <summary>Throws a <see cref="RumbleException"/> for negative status codes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Check(int status)
    {
        if (status < 0)
        {
            ThrowLastError(status);
        }

        return status;
    }

    private static void ThrowLastError(int status) => throw new RumbleException((RumbleErrorCode)status, LastErrorMessage());

    internal static string LastErrorMessage()
    {
        Span<byte> stack = stackalloc byte[512];
        fixed (byte* p = stack)
        {
            var length = NativeMethods.rumble_last_error(p, (nuint)stack.Length);
            if (length <= stack.Length)
            {
                return Encoding.UTF8.GetString(stack[..Math.Max(0, length)]);
            }
        }

        var heap = new byte[NativeMethods.rumble_last_error(null, 0)];
        fixed (byte* p = heap)
        {
            var length = NativeMethods.rumble_last_error(p, (nuint)heap.Length);
            return Encoding.UTF8.GetString(heap, 0, Math.Min(length, heap.Length));
        }
    }

    /// <summary>Encodes a string to a pooled UTF-8 buffer for the duration of a native call.</summary>
    internal ref struct Utf8Buffer
    {
        private byte[]? _rented;
        public readonly ReadOnlySpan<byte> Span;

        public Utf8Buffer(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                _rented = null;
                Span = default;
                return;
            }

            _rented = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(value.Length));
            var written = Encoding.UTF8.GetBytes(value, _rented);
            Span = _rented.AsSpan(0, written);
        }

        public void Dispose()
        {
            if (_rented is not null)
            {
                ArrayPool<byte>.Shared.Return(_rented);
                _rented = null;
            }
        }
    }

    /// <summary>Copies and frees a native buffer, deserializing it as JSON.</summary>
    internal static T ReadJson<T>(byte* ptr, nuint len, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(new ReadOnlySpan<byte>(ptr, checked((int)len)), typeInfo)
                   ?? throw new RumbleException(RumbleErrorCode.Json, "native library returned an empty document");
        }
        finally
        {
            NativeMethods.rumble_free(ptr, len);
        }
    }

    /// <summary>Serializes <paramref name="value"/> to UTF-8 JSON in a pooled buffer.</summary>
    internal static PooledJson Json<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        var writer = new ArrayBufferWriter<byte>(256);
        using (var json = new System.Text.Json.Utf8JsonWriter(writer))
        {
            System.Text.Json.JsonSerializer.Serialize(json, value, typeInfo);
        }

        return new PooledJson(writer);
    }

    internal readonly struct PooledJson(ArrayBufferWriter<byte> writer)
    {
        public ReadOnlySpan<byte> Span => writer.WrittenSpan;
    }
}

using Rumble.Net.Interop;

namespace Rumble.Net.Testing;

/// <summary>
/// An in-process Mumble server for tests, demos and offline development. It speaks the real
/// protocol (TLS, OCB2-AES UDP voice, TCP tunnel) and includes an <b>EchoBot</b> in the Lobby that
/// echoes text messages and plays back voice from users in its channel.
/// </summary>
public sealed unsafe class MockMumbleServer : IDisposable
{
    /// <summary>Session id of the EchoBot.</summary>
    public const uint EchoBotSession = 1;

    /// <summary>Id of the Lobby channel.</summary>
    public const uint LobbyChannelId = 1;

    /// <summary>Id of the AFK channel.</summary>
    public const uint AfkChannelId = 2;

    private nint _handle;

    private MockMumbleServer(nint handle, string host, int port)
    {
        _handle = handle;
        Host = host;
        Port = port;
    }

    /// <summary>Host the server is bound to.</summary>
    public string Host { get; }

    /// <summary>Port (TCP and UDP).</summary>
    public int Port { get; }

    /// <summary>Starts a server. Port 0 selects a free port.</summary>
    public static MockMumbleServer Start(string host = "127.0.0.1", int port = 0, string? password = null)
    {
        using var bind = new Native.Utf8Buffer($"{host}:{port}");
        using var pwd = new Native.Utf8Buffer(password);
        fixed (byte* b = bind.Span)
        fixed (byte* p = pwd.Span)
        {
            Native.Check(NativeMethods.rumble_mock_server_start(b, (nuint)bind.Span.Length, p, (nuint)pwd.Span.Length, out var actualPort, out var handle));
            return new MockMumbleServer(handle, host, actualPort);
        }
    }

    /// <summary>Creates client options pointing at this server.</summary>
    public RumbleClientOptions CreateClientOptions(string username, string? password = null) => new()
    {
        Host = Host,
        Port = Port,
        Username = username,
        Password = password,
    };

    /// <summary>Closes all client connections abruptly (to exercise reconnect logic).</summary>
    public void DropAllConnections()
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        Native.Check(NativeMethods.rumble_mock_server_drop_connections(_handle));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, 0);
        if (h != 0)
        {
            NativeMethods.rumble_mock_server_stop(h);
        }
    }
}

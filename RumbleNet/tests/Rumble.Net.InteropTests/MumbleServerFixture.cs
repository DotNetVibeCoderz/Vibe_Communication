namespace Rumble.Net.InteropTests;

/// <summary>
/// Points the interop suite at a real Mumble server (Murmur, mumble-server, Grumble…).
/// Configure it with environment variables; without them every test skips, so the suite is
/// safe to run anywhere:
///
///   RUMBLE_INTEROP_HOST      host name or IP (required)
///   RUMBLE_INTEROP_PORT      port, default 64738
///   RUMBLE_INTEROP_PASSWORD  server password, if the server requires one
///   RUMBLE_INTEROP_LABEL     name shown in test output, e.g. "mumble-server v1.5.735"
/// </summary>
public static class InteropServer
{
    public static string? Host { get; } = Environment.GetEnvironmentVariable("RUMBLE_INTEROP_HOST");

    public static int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("RUMBLE_INTEROP_PORT"), out var p) ? p : 64738;

    public static string? Password { get; } = Environment.GetEnvironmentVariable("RUMBLE_INTEROP_PASSWORD");

    public static string Label { get; } = Environment.GetEnvironmentVariable("RUMBLE_INTEROP_LABEL") ?? "server under test";

    /// <summary>Skips the calling test when no server is configured.</summary>
    public static void SkipIfUnavailable() =>
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Host), "Set RUMBLE_INTEROP_HOST to run the interop suite.");

    /// <summary>Options for a client with a unique name, so parallel runs never collide.</summary>
    public static RumbleClientOptions Options(string role) => new()
    {
        Host = Host!,
        Port = Port,
        Username = $"rumble-{role}-{Random.Shared.Next(1000, 9999)}",
        Password = string.IsNullOrEmpty(Password) ? null : Password,
        ConnectTimeout = TimeSpan.FromSeconds(20),
        PingInterval = TimeSpan.FromMilliseconds(700),
        PingTimeout = TimeSpan.FromSeconds(20),
        AutoReconnect = false,
    };
}

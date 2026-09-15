using System.Text.Json;
using System.Text.Json.Serialization;
using Rumble.Net;

namespace RumbleApp.Services;

/// <summary>A server entry saved by the user.</summary>
public sealed record SavedServer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 64738;
    public string Username { get; init; } = string.Empty;
    public string? Password { get; init; }

    /// <summary>The built-in demo server started inside the app.</summary>
    public bool IsDemo { get; init; }
}

/// <summary>User preferences.</summary>
public sealed record AppSettings
{
    public TransmitMode TransmitMode { get; init; } = TransmitMode.VoiceActivity;
    public float VoiceActivityThresholdDb { get; init; } = -42f;
    public int Bitrate { get; init; } = 48_000;
    public int FramesPerPacket { get; init; } = 2;
    public bool NoiseGate { get; init; }
    public string? InputDeviceId { get; init; }
    public string? OutputDeviceId { get; init; }
    public float MasterVolume { get; init; } = 1f;
    public bool PositionalAudio { get; init; }
    public bool ForceTcpVoice { get; init; }
    public string? CertificatePem { get; init; }
    public string? PrivateKeyPem { get; init; }
    public string? CertificateFingerprint { get; init; }
    public string DefaultUsername { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<SavedServer>))]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>Persists saved servers in the app data directory.</summary>
public sealed class ServerStore
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "servers.json");
    private List<SavedServer> _servers;

    public ServerStore()
    {
        _servers = Load();
    }

    public event Action? Changed;

    public IReadOnlyList<SavedServer> Servers => _servers;

    public SavedServer Demo { get; } = new()
    {
        Id = Guid.Parse("0f1d2c3b-4a59-4687-9786-a5b4c3d2e1f0"),
        Name = "Demo server",
        Host = "built-in",
        IsDemo = true,
    };

    public SavedServer? Find(Guid id) => id == Demo.Id ? Demo : _servers.FirstOrDefault(s => s.Id == id);

    public void Save(SavedServer server)
    {
        var index = _servers.FindIndex(s => s.Id == server.Id);
        if (index >= 0)
        {
            _servers[index] = server;
        }
        else
        {
            _servers.Add(server);
        }

        Persist();
    }

    public void Remove(Guid id)
    {
        _servers.RemoveAll(s => s.Id == id);
        Persist();
    }

    private List<SavedServer> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.ListSavedServer) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Persist()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_servers, AppJsonContext.Default.ListSavedServer));
        Changed?.Invoke();
    }
}

/// <summary>Persists <see cref="AppSettings"/>.</summary>
public sealed class SettingsStore
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "settings.json");

    public SettingsStore()
    {
        try
        {
            Current = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.AppSettings) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            Current = new AppSettings();
        }
    }

    public AppSettings Current { get; private set; }

    public event Action? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        File.WriteAllText(_path, JsonSerializer.Serialize(Current, AppJsonContext.Default.AppSettings));
        Changed?.Invoke();
    }
}

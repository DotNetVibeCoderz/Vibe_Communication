using System.Text.Json;
using System.Text.Json.Serialization;
using Rumble.Net;

namespace RumbleApp.Services;

/// <summary>A server entry saved by the user. The password never reaches the JSON file.</summary>
public sealed record SavedServer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 64738;
    public string Username { get; init; } = string.Empty;

    /// <summary>Kept in <see cref="SecretStore"/>, not in servers.json.</summary>
    [JsonIgnore]
    public string? Password { get; init; }

    /// <summary>The built-in demo server started inside the app.</summary>
    public bool IsDemo { get; init; }
}

/// <summary>User preferences. The certificate private key never reaches the JSON file.</summary>
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

    /// <summary>Kept in <see cref="SecretStore"/>, not in settings.json.</summary>
    [JsonIgnore]
    public string? PrivateKeyPem { get; init; }

    public string? CertificateFingerprint { get; init; }
    public string DefaultUsername { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<SavedServer>))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

/// <summary>Persists saved servers; passwords go to <see cref="SecretStore"/>.</summary>
public sealed class ServerStore(SecretStore secrets)
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "servers.json");
    private readonly List<(Guid Id, string Password)> _toMigrate = [];
    private List<SavedServer> _servers = [];
    private bool _loaded;

    public event Action? Changed;

    public IReadOnlyList<SavedServer> Servers
    {
        get
        {
            EnsureLoaded();
            return _servers;
        }
    }

    public SavedServer Demo { get; } = new()
    {
        Id = Guid.Parse("0f1d2c3b-4a59-4687-9786-a5b4c3d2e1f0"),
        Name = "Demo server",
        Host = "built-in",
        IsDemo = true,
    };

    public SavedServer? Find(Guid id) => id == Demo.Id ? Demo : Servers.FirstOrDefault(s => s.Id == id);

    /// <summary>Saves the entry, keeping its password in secure storage.</summary>
    public async Task SaveAsync(SavedServer server)
    {
        EnsureLoaded();
        var index = _servers.FindIndex(s => s.Id == server.Id);
        if (index >= 0)
        {
            _servers[index] = server;
        }
        else
        {
            _servers.Add(server);
        }

        await secrets.SetAsync(PasswordKey(server.Id), server.Password);
        Persist();
    }

    public async Task RemoveAsync(Guid id)
    {
        EnsureLoaded();
        _servers.RemoveAll(s => s.Id == id);
        await secrets.RemoveAsync(PasswordKey(id));
        Persist();
    }

    /// <summary>The server's password from secure storage, or null when it has none.</summary>
    public Task<string?> GetPasswordAsync(Guid id) => secrets.GetAsync(PasswordKey(id));

    /// <summary>
    /// Moves passwords written by earlier versions out of servers.json into secure storage.
    /// Call once at startup.
    /// </summary>
    public async Task MigrateSecretsAsync()
    {
        EnsureLoaded();
        if (_toMigrate.Count == 0)
        {
            return;
        }

        foreach (var (id, password) in _toMigrate)
        {
            await secrets.SetAsync(PasswordKey(id), password);
        }

        _toMigrate.Clear();
        Persist();   // rewrites the file without the passwords
    }

    private static string PasswordKey(Guid id) => $"server-password-{id}";

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            _servers = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListSavedServer) ?? [];

            // Passwords from older versions are still in the file; remember them for migration.
            using var document = JsonDocument.Parse(json);
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("Password", out var password) &&
                    password.ValueKind == JsonValueKind.String &&
                    password.GetString() is { Length: > 0 } value &&
                    element.TryGetProperty("Id", out var id) && id.TryGetGuid(out var guid))
                {
                    _toMigrate.Add((guid, value));
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _servers = [];
        }
    }

    private void Persist()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_servers, AppJsonContext.Default.ListSavedServer));
        Changed?.Invoke();
    }
}

/// <summary>Persists <see cref="AppSettings"/>; the private key goes to <see cref="SecretStore"/>.</summary>
public sealed class SettingsStore(SecretStore secrets)
{
    private const string PrivateKeyKey = "certificate-private-key";

    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "settings.json");
    private string? _legacyPrivateKey;
    private bool _loaded;
    private AppSettings _current = new();

    public event Action? Changed;

    public AppSettings Current
    {
        get
        {
            EnsureLoaded();
            return _current;
        }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        EnsureLoaded();
        _current = change(_current);
        Persist();
    }

    /// <summary>Updates settings and stores the private key securely.</summary>
    public async Task UpdateWithPrivateKeyAsync(Func<AppSettings, AppSettings> change, string? privateKeyPem)
    {
        EnsureLoaded();
        _current = change(_current) with { PrivateKeyPem = privateKeyPem };
        await secrets.SetAsync(PrivateKeyKey, privateKeyPem);
        Persist();
    }

    /// <summary>Loads the private key from secure storage into <see cref="Current"/>.</summary>
    public async Task LoadSecretsAsync()
    {
        EnsureLoaded();
        if (_legacyPrivateKey is { Length: > 0 })
        {
            // Written in plain text by an earlier version: move it and rewrite the file.
            await secrets.SetAsync(PrivateKeyKey, _legacyPrivateKey);
            _current = _current with { PrivateKeyPem = _legacyPrivateKey };
            _legacyPrivateKey = null;
            Persist();
            return;
        }

        _current = _current with { PrivateKeyPem = await secrets.GetAsync(PrivateKeyKey) };
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var json = File.ReadAllText(_path);
            _current = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettings) ?? new AppSettings();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("PrivateKeyPem", out var key) &&
                key.ValueKind == JsonValueKind.String)
            {
                _legacyPrivateKey = key.GetString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _current = new AppSettings();
        }
    }

    private void Persist()
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(_current, AppJsonContext.Default.AppSettings));
        Changed?.Invoke();
    }
}

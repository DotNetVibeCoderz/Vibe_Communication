using System.Text.Json;

namespace RumbleApp.Services;

/// <summary>
/// Stores secrets (server passwords, the certificate private key) in the platform's secure
/// storage: Keychain on Apple platforms, Keystore on Android, DPAPI on Windows.
/// </summary>
/// <remarks>
/// <see cref="SecureStorage"/> is unavailable in some Windows configurations (notably unpackaged
/// apps on older builds), so Windows falls back to a DPAPI-encrypted file scoped to the current
/// user. If neither works the secret is kept in memory for the session only, and
/// <see cref="IsPersistent"/> reports false so the UI can say so.
/// </remarks>
public sealed class SecretStore
{
    private readonly Dictionary<string, string> _memory = new(StringComparer.Ordinal);
    private readonly Lock _sync = new();

    /// <summary>False when secrets could not be stored securely and live only for this session.</summary>
    public bool IsPersistent { get; private set; } = true;

    /// <summary>Reads a secret, or null when it is not set.</summary>
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var value = await SecureStorage.Default.GetAsync(key);
            if (value is not null)
            {
                return value;
            }
        }
        catch (Exception)
        {
            // Falls through to the platform fallback below.
        }

        if (WindowsSecrets.TryRead(key, out var fallback))
        {
            return fallback;
        }

        lock (_sync)
        {
            return _memory.GetValueOrDefault(key);
        }
    }

    /// <summary>Writes a secret. A null or empty value removes it.</summary>
    public async Task SetAsync(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            await RemoveAsync(key);
            return;
        }

        try
        {
            await SecureStorage.Default.SetAsync(key, value);
            return;
        }
        catch (Exception)
        {
            // Falls through to the platform fallback below.
        }

        if (WindowsSecrets.TryWrite(key, value))
        {
            return;
        }

        IsPersistent = false;
        lock (_sync)
        {
            _memory[key] = value;
        }
    }

    /// <summary>Removes a secret from every store.</summary>
    public Task RemoveAsync(string key)
    {
        try
        {
            SecureStorage.Default.Remove(key);
        }
        catch (Exception)
        {
            // Ignored: the fallbacks below still run.
        }

        WindowsSecrets.Remove(key);
        lock (_sync)
        {
            _memory.Remove(key);
        }

        return Task.CompletedTask;
    }

    /// <summary>DPAPI-encrypted file used when <see cref="SecureStorage"/> is unavailable on Windows.</summary>
    private static class WindowsSecrets
    {
        private static readonly string Path = System.IO.Path.Combine(FileSystem.AppDataDirectory, "secrets.dat");
        private static readonly Lock Sync = new();

        public static bool TryRead(string key, out string? value)
        {
            value = null;
#if WINDOWS
            lock (Sync)
            {
                var all = Load();
                return all.TryGetValue(key, out value);
            }
#else
            return false;
#endif
        }

        public static bool TryWrite(string key, string value)
        {
#if WINDOWS
            lock (Sync)
            {
                var all = Load();
                all[key] = value;
                return Save(all);
            }
#else
            return false;
#endif
        }

        public static void Remove(string key)
        {
#if WINDOWS
            lock (Sync)
            {
                var all = Load();
                if (all.Remove(key))
                {
                    Save(all);
                }
            }
#endif
        }

#if WINDOWS
        private static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                var plain = System.Security.Cryptography.ProtectedData.Unprotect(
                    File.ReadAllBytes(Path), null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize(plain, AppJsonContext.Default.DictionaryStringString)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception)
            {
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }

        private static bool Save(Dictionary<string, string> all)
        {
            try
            {
                var plain = JsonSerializer.SerializeToUtf8Bytes(all, AppJsonContext.Default.DictionaryStringString);
                File.WriteAllBytes(Path, System.Security.Cryptography.ProtectedData.Protect(
                    plain, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
#endif
    }
}

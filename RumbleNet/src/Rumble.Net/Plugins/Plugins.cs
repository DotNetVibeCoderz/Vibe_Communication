using System.Collections;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Rumble.Net.Audio;
using Rumble.Net.Events;

namespace Rumble.Net.Plugins;

/// <summary>An extension that hooks into a <see cref="RumbleClient"/> (filters, effects, bots, integrations).</summary>
public interface IRumblePlugin
{
    /// <summary>Unique plugin name.</summary>
    string Name { get; }

    /// <summary>Called when the plugin is added.</summary>
    ValueTask InitializeAsync(RumbleClient client, CancellationToken cancellationToken);

    /// <summary>Called when the plugin is removed or the client is disposed.</summary>
    ValueTask ShutdownAsync();
}

/// <summary>Convenience base class for plugins.</summary>
public abstract class RumblePlugin : IRumblePlugin
{
    private RumbleClient? _client;

    /// <inheritdoc />
    public virtual string Name => GetType().Name;

    /// <summary>The client this plugin is attached to.</summary>
    protected RumbleClient Client => _client ?? throw new InvalidOperationException("The plugin is not initialized.");

    /// <inheritdoc />
    public ValueTask InitializeAsync(RumbleClient client, CancellationToken cancellationToken)
    {
        _client = client;
        return OnInitializeAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask ShutdownAsync()
    {
        await OnShutdownAsync().ConfigureAwait(false);
        _client = null;
    }

    /// <summary>Override to subscribe to events.</summary>
    protected virtual ValueTask OnInitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>Override to unsubscribe and release resources.</summary>
    protected virtual ValueTask OnShutdownAsync() => ValueTask.CompletedTask;
}

/// <summary>Manages plugins of a client.</summary>
public sealed class PluginHost : IEnumerable<IRumblePlugin>
{
    private readonly RumbleClient _client;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IRumblePlugin> _plugins = new(StringComparer.OrdinalIgnoreCase);

    internal PluginHost(RumbleClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>Number of plugins.</summary>
    public int Count => _plugins.Count;

    /// <summary>Adds and initializes a plugin.</summary>
    public async Task AddAsync(IRumblePlugin plugin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        if (!_plugins.TryAdd(plugin.Name, plugin))
        {
            throw new InvalidOperationException($"A plugin named '{plugin.Name}' is already registered.");
        }

        try
        {
            await plugin.InitializeAsync(_client, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Plugin {Plugin} initialized", plugin.Name);
        }
        catch
        {
            _plugins.TryRemove(plugin.Name, out _);
            throw;
        }
    }

    /// <summary>Removes and shuts down a plugin.</summary>
    public async Task<bool> RemoveAsync(string name)
    {
        if (!_plugins.TryRemove(name, out var plugin))
        {
            return false;
        }

        await plugin.ShutdownAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Gets a plugin by type.</summary>
    public T? Get<T>() where T : class, IRumblePlugin => _plugins.Values.OfType<T>().FirstOrDefault();

    internal async Task ShutdownAllAsync()
    {
        foreach (var plugin in _plugins.Values)
        {
            try
            {
                await plugin.ShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plugin {Plugin} failed to shut down", plugin.Name);
            }
        }

        _plugins.Clear();
    }

    /// <inheritdoc />
    public IEnumerator<IRumblePlugin> GetEnumerator() => _plugins.Values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Keeps a bounded, thread-safe history of text messages.</summary>
public sealed class ChatHistoryPlugin(int capacity = 500) : RumblePlugin
{
    private readonly ConcurrentQueue<TextMessage> _messages = new();

    /// <summary>Raised after a message is stored.</summary>
    public event EventHandler<TextMessage>? MessageStored;

    /// <summary>Stored messages, oldest first.</summary>
    public IReadOnlyCollection<TextMessage> Messages => _messages;

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync(CancellationToken cancellationToken)
    {
        Client.TextMessageReceived += OnMessage;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnShutdownAsync()
    {
        Client.TextMessageReceived -= OnMessage;
        return ValueTask.CompletedTask;
    }

    private void OnMessage(object? sender, TextMessage message)
    {
        _messages.Enqueue(message);
        while (_messages.Count > capacity && _messages.TryDequeue(out _))
        {
        }

        MessageStored?.Invoke(this, message);
    }
}

/// <summary>Records every speaker to a separate WAV file (mono 48 kHz, 16-bit).</summary>
public sealed class VoiceRecorderPlugin(string directory) : RumblePlugin
{
    private readonly ConcurrentDictionary<uint, List<float>> _buffers = new();

    /// <summary>Output directory.</summary>
    public string Directory { get; } = directory;

    /// <inheritdoc />
    protected override ValueTask OnInitializeAsync(CancellationToken cancellationToken)
    {
        System.IO.Directory.CreateDirectory(Directory);
        Client.Audio.AudioFrameReceived += OnFrame;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override ValueTask OnShutdownAsync()
    {
        Client.Audio.AudioFrameReceived -= OnFrame;
        Flush();
        return ValueTask.CompletedTask;
    }

    private void OnFrame(in AudioFrame frame)
    {
        var buffer = _buffers.GetOrAdd(frame.Session, _ => new List<float>(AudioMath.SampleRate * 10));
        lock (buffer)
        {
            buffer.AddRange(frame.Samples);
        }
    }

    /// <summary>Writes all buffered audio to disk and returns the created files.</summary>
    public IReadOnlyList<string> Flush()
    {
        var files = new List<string>();
        foreach (var (session, buffer) in _buffers)
        {
            float[] samples;
            lock (buffer)
            {
                samples = [.. buffer];
                buffer.Clear();
            }

            if (samples.Length == 0)
            {
                continue;
            }

            var name = Client.Server.GetUser(session)?.Name ?? $"session-{session}";
            var safe = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var path = Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{safe}.wav");
            using var file = File.Create(path);
            WaveFile.WriteMono48k(file, samples);
            files.Add(path);
        }

        return files;
    }
}

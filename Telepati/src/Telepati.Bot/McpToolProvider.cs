using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;
using Telepati.Shared.Contracts;

namespace Telepati.Bot;

/// <summary>
/// Turns the MCP servers an admin enabled into kernel functions the model can call.
///
/// Connections are cached for the lifetime of the process: a stdio server is a child process,
/// and starting one per conversation turn would be both slow and a way to accumulate orphaned
/// processes. A server that fails to start is remembered as failed so every later turn does not
/// pay the same timeout again.
/// </summary>
public interface IMcpToolProvider
{
    /// <summary>Adds every reachable enabled server's tools to the kernel.</summary>
    Task<int> RegisterAsync(Kernel kernel, IReadOnlyList<McpServerDto> servers, CancellationToken ct = default);

    /// <summary>Connects once and reports what the server offers, without registering anything.</summary>
    Task<McpTestResultDto> TestAsync(McpServerDto server, CancellationToken ct = default);

    Task DisconnectAllAsync();
}

public sealed class McpToolProvider(ILogger<McpToolProvider> logger) : IMcpToolProvider, IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>How long a failed server is skipped before it is worth retrying.</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);

    public async Task<int> RegisterAsync(Kernel kernel, IReadOnlyList<McpServerDto> servers, CancellationToken ct = default)
    {
        var registered = 0;

        foreach (var server in servers)
        {
            if (_failures.TryGetValue(server.Slug, out var failedAt) && DateTimeOffset.UtcNow - failedAt < FailureCooldown)
            {
                continue;
            }

            try
            {
                var client = await GetOrConnectAsync(server, ct);
                var tools = await client.ListToolsAsync(cancellationToken: ct);

                if (tools.Count == 0) continue;

                // Each MCP tool becomes a kernel function; the plugin name keeps them grouped
                // and prevents two servers exposing "search" from colliding.
                kernel.Plugins.AddFromFunctions(
                    PluginName(server.Slug),
                    tools.Select(tool => tool.AsKernelFunction()));

                registered += tools.Count;
                logger.LogInformation("MCP {Server} contributed {Count} tools", server.Name, tools.Count);
            }
            catch (Exception e)
            {
                // One broken server must never stop the bot from answering.
                logger.LogWarning(e, "MCP server {Server} unavailable; skipping for {Minutes} min",
                    server.Name, FailureCooldown.TotalMinutes);
                _failures[server.Slug] = DateTimeOffset.UtcNow;
            }
        }

        return registered;
    }

    public async Task<McpTestResultDto> TestAsync(McpServerDto server, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // A test always dials fresh — the point is to prove the current configuration works,
            // not to report on a connection opened before the admin edited it.
            await using var client = await ConnectAsync(server, ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            stopwatch.Stop();

            // A successful test clears the cooldown so the next turn tries again immediately.
            _failures.Remove(server.Slug);

            var names = tools.Select(t => t.Name).OrderBy(n => n).ToList();
            return new McpTestResultDto(true,
                tools.Count == 0
                    ? "Terhubung, tetapi server ini tidak menawarkan tool apa pun."
                    : $"Terhubung. {tools.Count} tool tersedia.",
                names, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            stopwatch.Stop();
            return new McpTestResultDto(false, Explain(e, server), [], stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<McpClient> GetOrConnectAsync(McpServerDto server, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_clients.TryGetValue(server.Slug, out var existing)) return existing;

            var client = await ConnectAsync(server, ct);
            _clients[server.Slug] = client;
            return client;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<McpClient> ConnectAsync(McpServerDto server, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectTimeout);

        if (server.Transport == 1)
        {
            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri))
                throw new InvalidOperationException("URL server tidak valid.");

            return await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = uri }),
                cancellationToken: timeout.Token);
        }

        if (string.IsNullOrWhiteSpace(server.Command))
            throw new InvalidOperationException("Perintah server stdio belum diisi.");

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = server.Name,
            Command = server.Command,
            Arguments = server.Arguments.ToArray(),
            EnvironmentVariables = ParseEnvironment(server.EnvironmentVariables)
        });

        return await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
    }

    /// <summary>Turns the admin's <c>KEY=value</c> lines into the process environment.</summary>
    private static Dictionary<string, string?>? ParseEnvironment(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return null;

        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            // The gallery masks secrets on the way out; a masked value coming back would
            // overwrite the real one with bullet characters.
            if (value == "••••••") continue;

            if (key.Length > 0) result[key] = value;
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>Most MCP failures have the same few causes; naming them saves a lot of guessing.</summary>
    private static string Explain(Exception e, McpServerDto server)
    {
        var message = e.Message;

        if (message.Contains("The system cannot find the file", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return $"Perintah '{server.Command}' tidak ditemukan di server ini. " +
                   (server.Command == "npx" ? "Pasang Node.js dulu." :
                    server.Command == "uvx" ? "Pasang uv (Python) dulu." : "Pastikan perintahnya terpasang.");
        }

        if (e is OperationCanceledException or TaskCanceledException)
        {
            return $"Waktu tunggu habis setelah {ConnectTimeout.TotalSeconds:0} detik. " +
                   "Paket mungkin sedang diunduh untuk pertama kali — coba lagi sebentar lagi.";
        }

        if (server.RequiresApiKey && !server.IsConfigured)
        {
            return $"Server ini butuh {server.ApiKeyEnvironmentName}. Isi dulu di parameter environment.";
        }

        return message;
    }

    private static string PluginName(string slug)
    {
        // Kernel plugin names allow only letters, digits and underscore.
        var cleaned = new string(slug.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        return $"mcp_{(cleaned.Length == 0 ? "server" : cleaned)}";
    }

    public async Task DisconnectAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var client in _clients.Values)
            {
                try { await client.DisposeAsync(); }
                catch { /* a dead child process is already gone */ }
            }

            _clients.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAllAsync();
        _gate.Dispose();
    }
}

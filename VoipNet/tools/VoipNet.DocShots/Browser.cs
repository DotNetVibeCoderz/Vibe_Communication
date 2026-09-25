using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoipNet.DocShots;

/// <summary>A headless browser driven over a JSON web socket protocol.</summary>
internal abstract class Browser : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private readonly Process _process;
    private int _id;

    protected Browser(ClientWebSocket socket, Process process)
    {
        _socket = socket;
        _process = process;
    }

    public const int Width = 1500;
    public const int Height = 1150;

    protected static async Task<ClientWebSocket> ConnectAsync(Uri url)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        await socket.ConnectAsync(url, CancellationToken.None);
        return socket;
    }

    /// <summary>Sends a command and waits for its reply, skipping events.</summary>
    protected async Task<JsonNode?> SendAsync(string method, JsonObject parameters)
    {
        var id = Interlocked.Increment(ref _id);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters };
        await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        var buffer = new byte[1 << 20];
        while (true)
        {
            using var stream = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                stream.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            var reply = JsonNode.Parse(stream.ToArray());
            if (reply?["id"]?.GetValue<int>() == id)
            {
                if (reply["error"] is { } error)
                {
                    throw new InvalidOperationException($"{method}: {error} {reply["message"]}");
                }

                return reply["result"];
            }
        }
    }

    public abstract Task NavigateAsync(string url);

    /// <summary>Evaluates an expression and returns its JSON value (strings, numbers, booleans).</summary>
    public abstract Task<JsonNode?> EvaluateAsync(string expression);

    protected abstract Task<byte[]> CaptureAsync(bool fullPage);

    /// <summary>Waits for a condition; returns false (with a warning) on timeout.</summary>
    /// <summary>Waits until a condition holds in the page.</summary>
    /// <param name="condition">A JavaScript expression.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <param name="quiet">True when a timeout is expected and should not be reported.</param>
    public async Task<bool> WaitForAsync(string condition, TimeSpan timeout, bool quiet = false)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await EvaluateAsync($"!!({condition})") is JsonValue value && value.GetValue<bool>())
            {
                return true;
            }

            await Task.Delay(250);
        }

        if (!quiet)
        {
            Console.Error.WriteLine($"warning: timed out waiting for {condition}");
        }

        return false;
    }

    public async Task ClickTextAsync(string selector, string text)
    {
        var script = $$"""
            (() => {
              const el = [...document.querySelectorAll({{JsonSerializer.Serialize(selector)}})]
                .find(e => e.textContent.trim() === {{JsonSerializer.Serialize(text)}});
              if (!el) return false;
              el.click();
              return true;
            })()
            """;
        if (await EvaluateAsync(script) is not JsonValue v || !v.GetValue<bool>())
        {
            Console.Error.WriteLine($"warning: no {selector} with text '{text}'");
        }
    }

    public async Task TypeAsync(string selector, string text)
    {
        var script = $$"""
            (() => {
              const el = document.querySelector({{JsonSerializer.Serialize(selector)}});
              el.focus();
              el.value = {{JsonSerializer.Serialize(text)}};
              el.dispatchEvent(new Event('input', { bubbles: true }));
              el.dispatchEvent(new Event('change', { bubbles: true }));
            })()
            """;
        await EvaluateAsync(script);
        await Task.Delay(300);
    }

    public async Task ScreenshotAsync(string path, bool fullPage)
    {
        await File.WriteAllBytesAsync(path, await CaptureAsync(fullPage));
        Console.WriteLine($"saved {Path.GetFileName(path)}");
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The browser may already be closing.
        }

        _socket.Dispose();
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }

        _process.Dispose();
    }

    protected static string NewProfileFolder() => Path.Combine(Path.GetTempPath(), $"voipnet-docshots-{Guid.NewGuid():N}");
}

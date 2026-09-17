// Captures documentation screenshots of the Blazor samples by driving a headless Chromium browser
// (Edge or Chrome) over the DevTools protocol: it clicks, types and waits like a user would.
//
// Usage: dotnet run --project tools/VoipNet.DocShots -- <scenario> <baseUrl> <outputFolder>
//   scenarios: callcenter, ivrstudio, webphone

using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <callcenter|ivrstudio|webphone> <baseUrl> <outputFolder>");
    return 2;
}

var (scenario, baseUrl, output) = (args[0], args[1].TrimEnd('/'), args[2]);
Directory.CreateDirectory(output);

var browserPath = new[]
{
    @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    @"C:\Program Files\Google\Chrome\Application\chrome.exe",
    "/usr/bin/chromium",
    "/usr/bin/google-chrome",
}.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("No Chromium based browser found.");

const int port = 9333;
var profile = Path.Combine(Path.GetTempPath(), $"voipnet-docshots-{Guid.NewGuid():N}");
using var browser = Process.Start(new ProcessStartInfo(browserPath,
    $"--headless=new --disable-gpu --hide-scrollbars --use-fake-device-for-media-stream --use-fake-ui-for-media-stream --autoplay-policy=no-user-gesture-required --no-first-run --remote-debugging-port={port} --user-data-dir=\"{profile}\" --window-size=1500,1150 about:blank")
{
    UseShellExecute = false,
    RedirectStandardError = true,
})!;

try
{
    using var http = new HttpClient();
    JsonArray? targets = null;
    for (var i = 0; i < 50 && targets is null; i++)
    {
        try
        {
            targets = await http.GetFromJsonAsync<JsonArray>($"http://127.0.0.1:{port}/json/list");
        }
        catch (HttpRequestException)
        {
            await Task.Delay(200);
        }
    }

    var page = targets!.First(t => t!["type"]!.GetValue<string>() == "page")!;
    await using var cdp = await Cdp.ConnectAsync(page["webSocketDebuggerUrl"]!.GetValue<string>());
    await cdp.SendAsync("Emulation.setEmulatedMedia", new JsonObject
    {
        ["features"] = new JsonArray(new JsonObject { ["name"] = "prefers-color-scheme", ["value"] = "light" }),
    });
    await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
    {
        ["width"] = 1500, ["height"] = 1150, ["deviceScaleFactor"] = 1, ["mobile"] = false,
    });

    switch (scenario)
    {
        case "callcenter":
            await cdp.NavigateAsync($"{baseUrl}/");
            await Task.Delay(TimeSpan.FromSeconds(4));
            await cdp.ScreenshotAsync(Path.Combine(output, "callcenter-wallboard-light.png"), fullPage: true);
            await cdp.ClickTextAsync("button", "Ask for advice");
            await cdp.WaitForAsync("document.querySelector('pre.advice') !== null", TimeSpan.FromSeconds(90));
            await Task.Delay(1000);
            await cdp.ScreenshotAsync(Path.Combine(output, "callcenter-ai-supervisor.png"), fullPage: true);
            break;

        case "ivrstudio":
            await cdp.NavigateAsync($"{baseUrl}/");
            await Task.Delay(TimeSpan.FromSeconds(4));
            await cdp.ScreenshotAsync(Path.Combine(output, "ivrstudio-editor.png"), fullPage: false);
            await cdp.ClickTextAsync("button", "Call this flow");
            await cdp.WaitForAsync("document.body.innerText.includes('Connected')", TimeSpan.FromSeconds(15));
            await Task.Delay(TimeSpan.FromSeconds(9));
            await cdp.ClickTextAsync("button.padkey", "2");
            await Task.Delay(TimeSpan.FromSeconds(6));
            foreach (var key in "4321#")
            {
                await cdp.ClickTextAsync("button.padkey", key.ToString());
                await Task.Delay(500);
            }

            await Task.Delay(TimeSpan.FromSeconds(8));
            await cdp.ClickTextAsync("button.padkey", "1");
            await cdp.WaitForAsync("document.querySelector('form.say input') !== null", TimeSpan.FromSeconds(30));
            await Task.Delay(TimeSpan.FromSeconds(5));
            await cdp.TypeAsync("form.say input", "Internet saya mati sejak pagi, apa ada gangguan?");
            await cdp.ClickTextAsync("form.say button", "Say");
            await cdp.WaitForAsync("document.querySelectorAll('.transcript li.ai').length >= 2", TimeSpan.FromSeconds(90));
            await Task.Delay(TimeSpan.FromSeconds(3));
            await cdp.ScreenshotAsync(Path.Combine(output, "ivrstudio-test-call.png"), fullPage: false);
            break;

        case "webphone":
            await cdp.NavigateAsync($"{baseUrl}/");
            await Task.Delay(TimeSpan.FromSeconds(3));
            await cdp.ScreenshotAsync(Path.Combine(output, "webphone-idle.png"), fullPage: true);
            await cdp.ClickTextAsync("button", "Call echo desk");
            await cdp.WaitForAsync("document.body.innerText.includes('Media is flowing')", TimeSpan.FromSeconds(30));
            await cdp.WaitForAsync("document.querySelectorAll('.jack.live').length === 6", TimeSpan.FromSeconds(20));
            await Task.Delay(TimeSpan.FromSeconds(4));
            await cdp.ScreenshotAsync(Path.Combine(output, "webphone-call.png"), fullPage: true);
            // Print what the browser measured so a run doubles as an interop check.
            Console.WriteLine(await cdp.EvaluateAsync("[...document.querySelectorAll('.facts div')].map(d => d.innerText.replace('\\n', ': ')).join('\\n')"));
            Console.WriteLine(await cdp.EvaluateAsync("[...document.querySelectorAll('.jack')].map(j => j.className + ' ' + j.querySelector('.jack-detail').innerText).join('\\n')"));
            await cdp.ClickTextAsync("button", "Hang up");
            await Task.Delay(TimeSpan.FromSeconds(2));
            break;

        default:
            Console.Error.WriteLine($"unknown scenario {scenario}");
            return 2;
    }

    return 0;
}
finally
{
    try
    {
        browser.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException)
    {
        // Already gone.
    }
}

/// <summary>A minimal DevTools protocol client: request/response over one web socket.</summary>
internal sealed class Cdp : IAsyncDisposable
{
    private readonly ClientWebSocket _socket;
    private int _id;

    private Cdp(ClientWebSocket socket) => _socket = socket;

    public static async Task<Cdp> ConnectAsync(string url)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        await socket.ConnectAsync(new Uri(url), CancellationToken.None);
        return new Cdp(socket);
    }

    public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters = null)
    {
        var id = Interlocked.Increment(ref _id);
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
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
                    throw new InvalidOperationException($"{method}: {error}");
                }

                return reply["result"];
            }
        }
    }

    public async Task NavigateAsync(string url)
    {
        await SendAsync("Page.navigate", new JsonObject { ["url"] = url });
        await WaitForAsync("document.readyState === 'complete'", TimeSpan.FromSeconds(20));
    }

    public async Task<JsonNode?> EvaluateAsync(string expression)
    {
        var result = await SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true });
        return result?["result"]?["value"];
    }

    public async Task WaitForAsync(string condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await EvaluateAsync($"!!({condition})") is JsonValue value && value.GetValue<bool>())
            {
                return;
            }

            await Task.Delay(250);
        }

        Console.Error.WriteLine($"warning: timed out waiting for {condition}");
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
        var parameters = new JsonObject { ["format"] = "png", ["captureBeyondViewport"] = fullPage };
        if (fullPage && await EvaluateAsync("JSON.stringify([document.documentElement.scrollWidth, document.documentElement.scrollHeight])") is JsonValue size)
        {
            var dims = JsonSerializer.Deserialize<int[]>(size.GetValue<string>())!;
            parameters["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = dims[0], ["height"] = dims[1], ["scale"] = 1 };
        }

        var result = await SendAsync("Page.captureScreenshot", parameters);
        await File.WriteAllBytesAsync(path, Convert.FromBase64String(result!["data"]!.GetValue<string>()));
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
    }
}

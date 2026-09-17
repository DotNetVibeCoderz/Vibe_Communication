using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoipNet.DocShots;

/// <summary>Edge or Chrome over the DevTools protocol, with a fake microphone.</summary>
internal sealed class Chromium : Browser
{
    private const int Port = 9333;

    private Chromium(ClientWebSocket socket, Process process)
        : base(socket, process)
    {
    }

    public static async Task<Chromium> LaunchAsync()
    {
        var path = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "/usr/bin/chromium",
            "/usr/bin/google-chrome",
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("No Chromium based browser found.");

        var process = Process.Start(new ProcessStartInfo(path,
            $"--headless=new --disable-gpu --hide-scrollbars --use-fake-device-for-media-stream --use-fake-ui-for-media-stream --autoplay-policy=no-user-gesture-required --no-first-run --remote-debugging-port={Port} --user-data-dir=\"{NewProfileFolder()}\" --window-size={Width},{Height} about:blank")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        })!;

        using var http = new HttpClient();
        JsonArray? targets = null;
        for (var i = 0; i < 50 && targets is null; i++)
        {
            try
            {
                targets = await http.GetFromJsonAsync<JsonArray>($"http://127.0.0.1:{Port}/json/list");
            }
            catch (HttpRequestException)
            {
                await Task.Delay(200);
            }
        }

        var page = targets!.First(t => t!["type"]!.GetValue<string>() == "page")!;
        var browser = new Chromium(await ConnectAsync(new Uri(page["webSocketDebuggerUrl"]!.GetValue<string>())), process);
        await browser.SendAsync("Emulation.setEmulatedMedia", new JsonObject
        {
            ["features"] = new JsonArray(new JsonObject { ["name"] = "prefers-color-scheme", ["value"] = "light" }),
        });
        await browser.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
        {
            ["width"] = Width, ["height"] = Height, ["deviceScaleFactor"] = 1, ["mobile"] = false,
        });
        return browser;
    }

    public override async Task NavigateAsync(string url)
    {
        await SendAsync("Page.navigate", new JsonObject { ["url"] = url });
        await WaitForAsync("document.readyState === 'complete'", TimeSpan.FromSeconds(20));
    }

    public override async Task<JsonNode?> EvaluateAsync(string expression)
    {
        var result = await SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["returnByValue"] = true });
        return result?["result"]?["value"];
    }

    protected override async Task<byte[]> CaptureAsync(bool fullPage)
    {
        var parameters = new JsonObject { ["format"] = "png", ["captureBeyondViewport"] = fullPage };
        if (fullPage && await EvaluateAsync("JSON.stringify([document.documentElement.scrollWidth, document.documentElement.scrollHeight])") is JsonValue size)
        {
            var dims = JsonSerializer.Deserialize<int[]>(size.GetValue<string>())!;
            parameters["clip"] = new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = dims[0], ["height"] = dims[1], ["scale"] = 1 };
        }

        var result = await SendAsync("Page.captureScreenshot", parameters);
        return Convert.FromBase64String(result!["data"]!.GetValue<string>());
    }
}

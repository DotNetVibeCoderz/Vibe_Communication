using System.Diagnostics;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoipNet.DocShots;

/// <summary>Edge or Chrome over the DevTools protocol, with a fake microphone.</summary>
internal sealed class Chromium : Browser
{
    /// <summary>Default DevTools port; a second browser (a second participant) takes the next one.</summary>
    private const int Port = 9333;

    private Chromium(ClientWebSocket socket, Process process)
        : base(socket, process)
    {
    }

    /// <summary>Starts a headless browser with a fake camera and microphone.</summary>
    /// <param name="portOffset">Moves the DevTools port, so several browsers can run side by side.</param>
    public static async Task<Chromium> LaunchAsync(int portOffset = 0)
    {
        var port = Port + portOffset;
        var path = new[]
        {
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            "/usr/bin/chromium",
            "/usr/bin/google-chrome",
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("No Chromium based browser found.");

        // --no-sandbox is needed on CI images that run without user namespaces, and /dev/shm there is
        // too small for Chrome's default shared memory use.
        var process = Process.Start(new ProcessStartInfo(path,
            $"--headless=new --disable-gpu --no-sandbox --disable-dev-shm-usage --hide-scrollbars --use-fake-device-for-media-stream --use-fake-ui-for-media-stream --auto-select-desktop-capture-source=\"Entire screen\" --auto-accept-this-tab-capture --allow-http-screen-capture --autoplay-policy=no-user-gesture-required --no-first-run --remote-debugging-port={port} --user-data-dir=\"{NewProfileFolder()}\" --window-size={Width},{Height} about:blank")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        // Drain both streams so a chatty browser can never block on a full pipe, keeping the last lines
        // for the error message when the browser never comes up.
        var complaints = new Queue<string>();
        void Remember(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null)
            {
                return;
            }

            lock (complaints)
            {
                complaints.Enqueue(e.Data);
                while (complaints.Count > 5)
                {
                    complaints.Dequeue();
                }
            }
        }

        process.OutputDataReceived += Remember;
        process.ErrorDataReceived += Remember;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var http = new HttpClient();
        JsonNode? page = null;
        // Busy CI runners can take a while to start a browser, and the DevTools port answers a moment
        // before the first tab exists — so wait for the tab, not just for the port.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (page is null && DateTime.UtcNow < deadline)
        {
            try
            {
                var targets = await http.GetFromJsonAsync<JsonArray>($"http://127.0.0.1:{port}/json/list");
                // A fresh profile can carry pages of its own — an extension options tab, a sign-in
                // dialog — and attaching to one of those drives the wrong window. Take the blank tab
                // the browser was started with, or any ordinary page if it has already been used.
                var pages = targets?.Where(t => t?["type"]?.GetValue<string>() == "page" && t["webSocketDebuggerUrl"] is not null).ToArray() ?? [];
                page = pages.FirstOrDefault(t => Ordinary(t?["url"]?.GetValue<string>()));
            }
            catch (HttpRequestException)
            {
                // The port is not up yet.
            }

            if (page is null)
            {
                await Task.Delay(250);
            }
        }

        if (page is null)
        {
            var state = process.HasExited ? $"exited with code {process.ExitCode}" : "is still running";
            string tail;
            lock (complaints)
            {
                tail = string.Join(" | ", complaints);
            }

            throw new InvalidOperationException($"{path} never offered a DevTools page; it {state}. Last output: {tail}");
        }

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

    /// <summary>True for a page a sample can be opened in, rather than a browser or extension page.</summary>
    private static bool Ordinary(string? url) =>
        url is null || url.Length == 0 || url == "about:blank" || url.StartsWith("http", StringComparison.Ordinal);

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

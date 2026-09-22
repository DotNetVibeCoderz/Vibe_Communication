using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

namespace VoipNet.DocShots;

/// <summary>Firefox over WebDriver BiDi, with fake media devices and no permission prompts.</summary>
internal sealed class Firefox : Browser
{
    private const int Port = 9334;
    private string _context = string.Empty;

    private Firefox(ClientWebSocket socket, Process process)
        : base(socket, process)
    {
    }

    /// <param name="path">firefox executable; defaults to the usual install locations.</param>
    public static async Task<Firefox> LaunchAsync(string? path)
    {
        path ??= new[]
        {
            @"C:\Program Files\Mozilla Firefox\firefox.exe",
            @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe",
            "/usr/bin/firefox",
            "/Applications/Firefox.app/Contents/MacOS/firefox",
        }.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Firefox not found; pass --firefox <path>.");

        var profile = NewProfileFolder();
        Directory.CreateDirectory(profile);
        await File.WriteAllLinesAsync(Path.Combine(profile, "user.js"),
        [
            """user_pref("media.navigator.streams.fake", true);""",
            """user_pref("media.navigator.permission.disabled", true);""",
            """user_pref("media.autoplay.default", 0);""",
            // Real host addresses instead of mDNS names, as when the page already has microphone permission.
            """user_pref("media.peerconnection.ice.obfuscate_host_addresses", false);""",
            """user_pref("browser.shell.checkDefaultBrowser", false);""",
            """user_pref("ui.systemUsesDarkTheme", 0);""",
        ]);

        var process = Process.Start(new ProcessStartInfo(path,
            $"--headless --remote-debugging-port {Port} --profile \"{profile}\" --width {Width} --height {Height} --no-remote")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        // Firefox logs freely; drain both streams so it never blocks on a full pipe.
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            return await AttachAsync(process);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<Firefox> AttachAsync(Process process)
    {
        ClientWebSocket? socket = null;
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (socket is null && DateTime.UtcNow < deadline)
        {
            try
            {
                socket = await ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/session"));
            }
            catch (WebSocketException)
            {
                await Task.Delay(250);
            }
        }

        var browser = new Firefox(socket ?? throw new InvalidOperationException("Firefox did not open its WebDriver BiDi port."), process);
        await browser.SendAsync("session.new", new JsonObject { ["capabilities"] = new JsonObject() });
        // The start-up tab refuses viewport changes without system access, so use a new one.
        var tab = await browser.SendAsync("browsingContext.create", new JsonObject { ["type"] = "tab" });
        browser._context = tab!["context"]!.GetValue<string>();
        await browser.SendAsync("browsingContext.setViewport", new JsonObject
        {
            ["context"] = browser._context,
            ["viewport"] = new JsonObject { ["width"] = Width, ["height"] = Height },
        });
        return browser;
    }

    public override async Task NavigateAsync(string url)
    {
        await SendAsync("browsingContext.navigate", new JsonObject { ["context"] = _context, ["url"] = url, ["wait"] = "complete" });
    }

    public override async Task<JsonNode?> EvaluateAsync(string expression)
    {
        var result = await SendAsync("script.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["target"] = new JsonObject { ["context"] = _context },
            ["awaitPromise"] = false,
        });
        // BiDi serializes values as { type, value }; primitives are all the scenarios read.
        return result?["result"]?["value"]?.DeepClone();
    }

    protected override async Task<byte[]> CaptureAsync(bool fullPage)
    {
        var result = await SendAsync("browsingContext.captureScreenshot", new JsonObject
        {
            ["context"] = _context,
            ["origin"] = fullPage ? "document" : "viewport",
        });
        return Convert.FromBase64String(result!["data"]!.GetValue<string>());
    }
}

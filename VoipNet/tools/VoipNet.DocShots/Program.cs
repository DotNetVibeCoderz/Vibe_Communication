// Captures documentation screenshots of the Blazor samples by driving a headless browser: Edge or Chrome
// over the DevTools protocol, or Firefox over WebDriver BiDi. It clicks, types and waits like a user would.
//
// Usage: dotnet run --project tools/VoipNet.DocShots -- <scenario> <baseUrl> <outputFolder> [--firefox [path]]
//   scenarios: callcenter, ivrstudio, webphone
//
// The webphone scenario doubles as a browser interop test: it exits with 1 when the browser call does not
// carry encrypted audio both ways.

using System.Text.Json.Nodes;
using VoipNet.DocShots;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <callcenter|ivrstudio|webphone> <baseUrl> <outputFolder> [--firefox [path]]");
    return 2;
}

var (scenario, baseUrl, output) = (args[0], args[1].TrimEnd('/'), args[2]);
var firefoxIndex = Array.IndexOf(args, "--firefox");
Directory.CreateDirectory(output);

await using Browser browser = firefoxIndex < 0
    ? await Chromium.LaunchAsync()
    : await Firefox.LaunchAsync(args.ElementAtOrDefault(firefoxIndex + 1));
var suffix = firefoxIndex < 0 ? "" : "-firefox";

switch (scenario)
{
    case "callcenter":
        await browser.NavigateAsync($"{baseUrl}/");
        await Task.Delay(TimeSpan.FromSeconds(4));
        await browser.ScreenshotAsync(Path.Combine(output, $"callcenter-wallboard-light{suffix}.png"), fullPage: true);
        await browser.ClickTextAsync("button", "Ask for advice");
        await browser.WaitForAsync("document.querySelector('pre.advice') !== null", TimeSpan.FromSeconds(90));
        await Task.Delay(1000);
        await browser.ScreenshotAsync(Path.Combine(output, $"callcenter-ai-supervisor{suffix}.png"), fullPage: true);
        return 0;

    case "ivrstudio":
        await browser.NavigateAsync($"{baseUrl}/");
        await Task.Delay(TimeSpan.FromSeconds(4));
        await browser.ScreenshotAsync(Path.Combine(output, $"ivrstudio-editor{suffix}.png"), fullPage: false);
        await browser.ClickTextAsync("button", "Call this flow");
        await browser.WaitForAsync("document.body.innerText.includes('Connected')", TimeSpan.FromSeconds(15));
        await Task.Delay(TimeSpan.FromSeconds(9));
        await browser.ClickTextAsync("button.padkey", "2");
        await Task.Delay(TimeSpan.FromSeconds(6));
        foreach (var key in "4321#")
        {
            await browser.ClickTextAsync("button.padkey", key.ToString());
            await Task.Delay(500);
        }

        await Task.Delay(TimeSpan.FromSeconds(8));
        await browser.ClickTextAsync("button.padkey", "1");
        await browser.WaitForAsync("document.querySelector('form.say input') !== null", TimeSpan.FromSeconds(30));
        await Task.Delay(TimeSpan.FromSeconds(5));
        await browser.TypeAsync("form.say input", "Internet saya mati sejak pagi, apa ada gangguan?");
        await browser.ClickTextAsync("form.say button", "Say");
        await browser.WaitForAsync("document.querySelectorAll('.transcript li.ai').length >= 2", TimeSpan.FromSeconds(90));
        await Task.Delay(TimeSpan.FromSeconds(3));
        await browser.ScreenshotAsync(Path.Combine(output, $"ivrstudio-test-call{suffix}.png"), fullPage: false);
        return 0;

    case "webphone":
        await browser.NavigateAsync($"{baseUrl}/");
        await browser.WaitForAsync("[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Call echo desk' && !b.disabled)", TimeSpan.FromSeconds(15));
        await browser.ScreenshotAsync(Path.Combine(output, $"webphone-idle{suffix}.png"), fullPage: true);
        // The fake camera is on for these runs, so the call carries video as well as audio.
        await browser.EvaluateAsync("document.querySelector('.video-toggle input').click()");
        await browser.ClickTextAsync("button", "Call echo desk");
        var flowing = await browser.WaitForAsync("document.body.innerText.includes('Media is flowing')", TimeSpan.FromSeconds(30));
        await browser.WaitForAsync("document.querySelectorAll('.jack.live').length === 6", TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(4));
        await browser.ScreenshotAsync(Path.Combine(output, $"webphone-call{suffix}.png"), fullPage: true);

        // Print what the browser measured so a run doubles as an interop check.
        Console.WriteLine(await browser.EvaluateAsync("[...document.querySelectorAll('.facts div')].map(d => d.innerText.replace('\\n', ': ')).join('\\n')"));
        Console.WriteLine(await browser.EvaluateAsync("[...document.querySelectorAll('.jack')].map(j => j.className + ' ' + j.querySelector('.jack-detail').innerText).join('\\n')"));
        var received = await browser.EvaluateAsync("(() => { const t = [...document.querySelectorAll('.facts div')].find(d => d.innerText.startsWith('Packets') || d.innerText.startsWith('PACKETS')); return t ? parseInt(t.querySelector('dd').innerText) || 0 : 0; })()");
        var live = await browser.EvaluateAsync("document.querySelectorAll('.jack.live').length");
        // Frames the browser decoded from what the gateway sent back: the video path, end to end.
        var videoBack = await browser.EvaluateAsync("(() => { const el = document.querySelector('.return-video video'); return el ? el.videoWidth : 0; })()");
        await browser.ClickTextAsync("button", "Hang up");
        await Task.Delay(TimeSpan.FromSeconds(2));

        var ok = flowing && Number(received) > 50 && Number(live) == 6 && Number(videoBack) > 0;
        Console.WriteLine($"video returned: {Number(videoBack)}px wide");
        Console.WriteLine(ok
            ? "interop: OK"
            : $"interop: FAILED (flowing={flowing}, packets in={Number(received)}, live hops={Number(live)}, video width={Number(videoBack)})");
        return ok ? 0 : 1;

    default:
        Console.Error.WriteLine($"unknown scenario {scenario}");
        return 2;
}

static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

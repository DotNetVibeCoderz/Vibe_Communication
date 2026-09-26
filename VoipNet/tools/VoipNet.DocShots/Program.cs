// Captures documentation screenshots of the Blazor samples by driving a headless browser: Edge or Chrome
// over the DevTools protocol, or Firefox over WebDriver BiDi. It clicks, types and waits like a user would.
//
// Usage: dotnet run --project tools/VoipNet.DocShots -- <scenario> <baseUrl> <outputFolder> [--firefox [path]]
//   scenarios: callcenter, ivrstudio, webphone, meeting
//
// The webphone scenario doubles as a browser interop test: it exits with 1 when the browser call does not
// carry encrypted audio, video and a data channel message both ways.

using System.Text.Json.Nodes;
using VoipNet.DocShots;

if (args.Length < 3)
{
    Console.Error.WriteLine("usage: <callcenter|ivrstudio|webphone|meeting> <baseUrl> <outputFolder> [--firefox [path]]");
    return 2;
}

var (scenario, baseUrl, output) = (args[0], args[1].TrimEnd('/'), args[2]);
var firefoxIndex = Array.IndexOf(args, "--firefox");
Directory.CreateDirectory(output);

if (scenario == "meeting")
{
    return await MeetingAsync(baseUrl, output);
}

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
        await browser.WaitForAsync("document.querySelectorAll('.canvas [data-menu]').length >= 2", TimeSpan.FromSeconds(20));
        // The gestures below need the page's own pointer handling to be listening.
        await browser.WaitForAsync("document.querySelector('.canvas')?.dataset.graph === 'ready'", TimeSpan.FromSeconds(20));

        // The designer is driven by pointer gestures, so the test performs them: drag a menu to a new
        // place, then drag its handle onto another menu to connect the two.
        var before = Number(await browser.EvaluateAsync(NodeX("support")));
        await browser.EvaluateAsync(DragNode("support", 110, 30));
        await browser.WaitForAsync($"({NodeX("support")}) > {before + 60}", TimeSpan.FromSeconds(10));
        Console.WriteLine($"dragged the support menu from x={before} to x={Number(await browser.EvaluateAsync(NodeX("support")))}");

        var options = Number(await browser.EvaluateAsync("document.querySelectorAll('#menu-support .options tbody tr').length"));
        await browser.EvaluateAsync(LinkNodes("support", "main"));
        var linked = await browser.WaitForAsync(
            $"document.querySelectorAll('#menu-support .options tbody tr').length > {options}",
            TimeSpan.FromSeconds(10));
        Console.WriteLine(linked ? "connected support to main by dragging its handle" : "the handle drag did not connect anything");

        await browser.ClickTextAsync("button", "Save");
        await browser.WaitForAsync("document.querySelectorAll('.version-list li').length >= 1", TimeSpan.FromSeconds(10));
        Console.WriteLine("the save kept a version");

        await Task.Delay(TimeSpan.FromSeconds(2));
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

        // The data channel: type a line and wait for the desk to answer on it (RFC 8831 over SCTP/DTLS).
        var chatOpen = await browser.WaitForAsync("document.querySelector('.chat-send input:not([disabled])') !== null", TimeSpan.FromSeconds(20));
        await browser.TypeAsync(".chat-send input", "halo dari browser");
        await browser.ClickTextAsync(".chat-send button", "Send");
        var answered = await browser.WaitForAsync("document.querySelectorAll('.chat-log li.gateway').length > 0", TimeSpan.FromSeconds(20));
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

        var ok = flowing && Number(received) > 50 && Number(live) == 6 && Number(videoBack) > 0 && chatOpen && answered;
        Console.WriteLine($"video returned: {Number(videoBack)}px wide");
        Console.WriteLine($"data channel: {(answered ? "the desk answered" : "no answer")}");
        Console.WriteLine(ok
            ? "interop: OK"
            : $"interop: FAILED (flowing={flowing}, packets in={Number(received)}, live hops={Number(live)}, video width={Number(videoBack)}, chat open={chatOpen}, chat answered={answered})");
        return ok ? 0 : 1;

    default:
        Console.Error.WriteLine($"unknown scenario {scenario}");
        return 2;
}

static double Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
// Where a menu's node sits on the designer canvas, as the page records it.
static string NodeX(string menu) =>
    "(parseFloat(document.querySelector('.canvas [data-menu=\"" + menu + "\"]')?.dataset.x) || 0)";

// Dragging in a page is three pointer events; the studio listens for them on the canvas.
static string DragNode(string menu, int dx, int dy) =>
    "(() => {"
    + "  const node = document.querySelector('.canvas [data-menu=\"" + menu + "\"]');"
    + "  const canvas = document.querySelector('.canvas');"
    + "  const box = node.getBoundingClientRect();"
    + "  const point = (type, x, y) => new PointerEvent(type, { bubbles: true, clientX: x, clientY: y, pointerId: 1, isPrimary: true, button: 0 });"
    + "  const x = box.left + 40, y = box.top + 12;"
    + "  node.dispatchEvent(point('pointerdown', x, y));"
    + "  canvas.dispatchEvent(point('pointermove', x + " + dx + ", y + " + dy + "));"
    + "  canvas.dispatchEvent(point('pointerup', x + " + dx + ", y + " + dy + "));"
    + "  return true;"
    + "})()";

// Dropping one menu's handle on another connects them, which the page turns into a new key.
static string LinkNodes(string from, string to) =>
    "(() => {"
    + "  const source = document.querySelector('.canvas [data-menu=\"" + from + "\"] [data-link]');"
    + "  const target = document.querySelector('.canvas [data-menu=\"" + to + "\"] .node-body');"
    + "  const canvas = document.querySelector('.canvas');"
    + "  const a = source.getBoundingClientRect(), b = target.getBoundingClientRect();"
    + "  const point = (type, x, y) => new PointerEvent(type, { bubbles: true, clientX: x, clientY: y, pointerId: 1, isPrimary: true, button: 0 });"
    + "  source.dispatchEvent(point('pointerdown', a.left + a.width / 2, a.top + a.height / 2));"
    + "  canvas.dispatchEvent(point('pointermove', b.left + b.width / 2, b.top + b.height / 2));"
    + "  canvas.dispatchEvent(point('pointerup', b.left + b.width / 2, b.top + b.height / 2));"
    + "  return true;"
    + "})()";


// Two browsers join the same room, so the conference has somebody to mix and a camera to forward.
// It doubles as an interop test: both participants must decode video that came from the other one.
static async Task<int> MeetingAsync(string baseUrl, string output)
{
    await using var first = await Chromium.LaunchAsync();
    await using var second = await Chromium.LaunchAsync(portOffset: 1);
    var joined = 0;
    foreach (var (browser, name) in new[] { (first, "Sari"), (second, "Budi") })
    {
        await browser.NavigateAsync($"{baseUrl}/");
        await browser.WaitForAsync("document.querySelector('#name') !== null", TimeSpan.FromSeconds(15));
        // The name only reaches the page once its circuit is interactive, and the button stays
        // disabled until it does — so type until the button comes alive, then press it.
        const string enabled = "[...document.querySelectorAll('button')].some(b => b.textContent.trim() === 'Join the room' && !b.disabled)";
        var ready = false;
        for (var attempt = 0; attempt < 10 && !ready; attempt++)
        {
            await browser.TypeAsync("#name", name);
            ready = await browser.WaitForAsync(enabled, TimeSpan.FromSeconds(3), quiet: true);
        }

        await browser.ClickTextAsync("button", "Join the room");
        if (await browser.WaitForAsync("document.body.innerText.includes('In the room.')", TimeSpan.FromSeconds(40)))
        {
            joined++;
        }
        else
        {
            // Say what the page was doing, so a flaky browser is told apart from a broken room.
            Console.WriteLine($"{name} did not join: {await browser.EvaluateAsync("document.querySelector('.status')?.innerText ?? document.body.innerText.slice(0, 120)")}");
        }
    }

    await first.WaitForAsync("document.querySelectorAll('.roster tbody tr').length >= 2", TimeSpan.FromSeconds(20));

    // The room forwards one participant to everyone else, so whoever holds the floor sees nobody.
    // Pinning each of them in turn asks the room for both directions rather than hoping the floor
    // moves: with two browsers playing the same test tone, who is loudest is anybody's guess.
    const string hasPicture = "(() => { const v = document.querySelector('.stage-frame video'); return v && v.videoWidth > 0; })()";
    await Pin(first, 0);
    var second_picture = await second.WaitForAsync(hasPicture, TimeSpan.FromSeconds(60));
    await Pin(first, 1);
    var picture = await first.WaitForAsync(hasPicture, TimeSpan.FromSeconds(60));

    foreach (var (browser, name) in new[] { (first, "Sari"), (second, "Budi") })
    {
        Console.WriteLine($"{name} sees: {await browser.EvaluateAsync("(document.querySelector('.on-screen')?.innerText ?? '') + ' | ' + (document.querySelector('.stage-caption .mono')?.innerText ?? '') + ' | ' + [...document.querySelectorAll('.facts div')].map(d => d.innerText.replace(String.fromCharCode(10), ': ')).join(' ; ')")}");
    }

    // One of them shares a screen: it has a stream of its own, so it arrives beside the faces rather
    // than replacing one, and the room sends it to everybody without the floor being involved.
    // Headless Chrome has no desktop to share, so the page is told to offer its own tab instead.
    await second.EvaluateAsync("window.__captureCurrentTab = true");
    await second.EvaluateAsync("[...document.querySelectorAll('button')].find(b => b.innerText.includes('Share screen'))?.click()");
    await Task.Delay(TimeSpan.FromSeconds(3));
    Console.WriteLine($"sharer says: {await second.EvaluateAsync("document.querySelector('.status')?.innerText ?? ''")}"
        + $" · buttons: {await second.EvaluateAsync("[...document.querySelectorAll('button')].map(b => b.innerText).join('/')")}");
    var shared = await first.WaitForAsync(
        "(() => { const v = document.querySelector('.stage-frame video.shared'); return v && v.videoWidth > 0 && !v.classList.contains('hidden'); })()",
        TimeSpan.FromSeconds(45));
    Console.WriteLine($"shared screen seen by the other browser: {shared}");

    // The second participant stays pinned for the shot, so the picture is settled.
    await Task.Delay(TimeSpan.FromSeconds(6));
    var pinnedIn = Number(await first.EvaluateAsync("""
        (() => {
          const text = document.querySelector('.stage-caption .mono')?.innerText ?? '';
          const match = /(\d+) frames in/.exec(text);
          return match ? +match[1] : 0;
        })()
        """));
    await first.ScreenshotAsync(Path.Combine(output, "meeting-room.png"), fullPage: true);
    Console.WriteLine($"frames decoded after pinning: {pinnedIn}");
    // The roster and the log say what the engine made of the call, which is what a failure needs.
    Console.WriteLine(await first.EvaluateAsync(
        "[...document.querySelectorAll('.roster tbody tr')].map(r => 'roster: ' + r.innerText.split(String.fromCharCode(10)).join(' ')).join(String.fromCharCode(10))"));
    Console.WriteLine(await first.EvaluateAsync(
        "[...document.querySelectorAll('.events li')].map(e => 'log: ' + e.innerText.split(String.fromCharCode(10)).join(' ')).join(String.fromCharCode(10))"));
    Console.WriteLine(await first.EvaluateAsync("document.querySelector('.on-screen')?.innerText ?? ''"));
    var width = Number(await first.EvaluateAsync("document.querySelector('.stage-frame video').videoWidth"));

    var ok = joined == 2 && picture && second_picture && shared;
    Console.WriteLine($"forwarded video: {width}px wide");
    Console.WriteLine(ok ? "meeting: OK" : $"meeting: FAILED (joined={joined}, first sees video={picture}, second sees video={second_picture}, screen shared={shared})");
    return ok ? 0 : 1;
}

/// <summary>Pins the participant in that row of the roster, so the room forwards them to everyone else.</summary>
static async Task Pin(Browser browser, int row) =>
    await browser.EvaluateAsync($"[...document.querySelectorAll('.roster tbody tr')][{row}]?.querySelector('button')?.click()");

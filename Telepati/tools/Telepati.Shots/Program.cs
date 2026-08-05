using Microsoft.Playwright;

// Captures the documentation screenshots from the running apps. Nothing here is mocked — if an
// app is not running, its shots are skipped and reported rather than faked.
//
//   dotnet run --project src/Telepati.Server     # terminal 1
//   dotnet run --project src/Telepati.Web        # terminal 2
//   dotnet run --project src/Telepati.Admin      # terminal 3
//   dotnet run --project tools/Telepati.Shots    # terminal 4

var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/screenshots"));
Directory.CreateDirectory(output);

var web = Environment.GetEnvironmentVariable("TELEPATI_WEB") ?? "https://localhost:7200";
var admin = Environment.GetEnvironmentVariable("TELEPATI_ADMIN") ?? "https://localhost:7210";
var api = Environment.GetEnvironmentVariable("TELEPATI_API") ?? "https://localhost:7180";

Console.WriteLine($"Output: {output}");
Console.WriteLine($"Web: {web} | Admin: {admin} | API: {api}");

Microsoft.Playwright.Program.Main(["install", "chromium"]);

using var playwright = await Playwright.CreateAsync();
await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });

var captured = 0;
var skipped = new List<string>();

// One viewport for every desktop shot so the images sit together in the docs without resizing.
await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
    IgnoreHTTPSErrors = true,
    DeviceScaleFactor = 2,
    ColorScheme = ColorScheme.Light
});

var page = await context.NewPageAsync();

static string FirstLine(Exception e) => e.Message.Split('\n')[0].Trim();

// Blazor Server holds a WebSocket open for the whole session, so "network idle" never fires.
// DOMContentLoaded plus a fixed settle is what actually works.
async Task<bool> ShotAsync(string file, string url, int settleMs = 1800)
{
    try
    {
        await page.GotoAsync(url, new PageGotoOptions { Timeout = 25000, WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForTimeoutAsync(settleMs);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(output, file) });

        Console.WriteLine($"  captured {file}");
        captured++;
        return true;
    }
    catch (Exception e)
    {
        Console.WriteLine($"  SKIPPED  {file} - {FirstLine(e)}");
        skipped.Add(file);
        return false;
    }
}

// Screenshots a state reached by clicking inside the app, without navigating away.
async Task StepAsync(string file, Func<Task> action, int settleMs = 1800)
{
    try
    {
        await action();
        await page.WaitForTimeoutAsync(settleMs);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(output, file) });

        Console.WriteLine($"  captured {file}");
        captured++;
    }
    catch (Exception e)
    {
        Console.WriteLine($"  SKIPPED  {file} - {FirstLine(e)}");
        skipped.Add(file);
    }
}

async Task SignInAsync(string baseUrl, string user = "kangfadhil")
{
    await page.GotoAsync($"{baseUrl}/login", new PageGotoOptions { Timeout = 25000, WaitUntil = WaitUntilState.DOMContentLoaded });
    await page.WaitForTimeoutAsync(2000);
    await page.FillAsync("#identifier", user);
    await page.FillAsync("#password", "Telepati123!");
    await page.ClickAsync("button[type=submit]");
    await page.WaitForTimeoutAsync(4500);
}

Console.WriteLine();
Console.WriteLine("Web messenger");
await ShotAsync("01-login.png", $"{web}/login", 2500);

// The rest of the web tour runs in one session: navigating away restarts the Blazor circuit
// and drops the conversation that was open, which is exactly what makes shots come out empty.
try
{
    await SignInAsync(web);

    await StepAsync("02-chat-terang.png", async () =>
    {
        var conversation = page.Locator(".tp-conv").Nth(1);
        if (await conversation.CountAsync() > 0)
            await conversation.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
    }, 2500);

    // The toggle's title flips with the current mode, so both labels are matched.
    const string themeToggle = "button[title='Mode gelap'], button[title='Mode terang']";

    await StepAsync("03-chat-gelap.png", async () =>
        await page.ClickAsync(themeToggle, new PageClickOptions { Timeout = 8000 }), 1500);

    await StepAsync("04-grup.png", async () =>
    {
        await page.ClickAsync(themeToggle, new PageClickOptions { Timeout = 8000 });
        await page.WaitForTimeoutAsync(700);

        var group = page.Locator(".tp-conv").Nth(3);
        if (await group.CountAsync() > 0)
            await group.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
    }, 2200);

    await StepAsync("05-bot.png", async () =>
    {
        var bot = page.Locator(".tp-conv", new PageLocatorOptions { HasTextString = "Bacot" }).First;
        if (await bot.CountAsync() > 0)
            await bot.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
    }, 2200);

    await StepAsync("06-kontak.png", async () =>
        await page.ClickAsync("button[title='Kontak']", new PageClickOptions { Timeout = 8000 }), 2000);

    await StepAsync("07-kontak-sekitar.png", async () =>
    {
        var nearby = page.Locator("button", new PageLocatorOptions { HasTextString = "Sekitar" }).First;
        if (await nearby.CountAsync() > 0)
            await nearby.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
    }, 2000);

    await StepAsync("08-status.png", async () =>
        await page.ClickAsync("button[title='Status']", new PageClickOptions { Timeout = 8000 }), 2000);

    // Opening a status is the whole point of the feed, so the viewer gets its own shot.
    // The image post is preferred: it proves the media path renders, not just coloured text.
    await StepAsync("09-status-viewer.png", async () =>
    {
        var withPhoto = page.Locator(".tp-conv", new PageLocatorOptions { HasTextString = "Foto" }).First;
        var target = await withPhoto.CountAsync() > 0 ? withPhoto : page.Locator(".tp-conv").First;

        if (await target.CountAsync() > 0)
            await target.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
    }, 2500);

    await StepAsync("09b-status-foto.png", async () =>
    {
        // The run is chronological, so the photo is the second post — one step forward.
        var next = page.Locator("button", new PageLocatorOptions { HasTextString = "Berikutnya" }).First;
        if (await next.CountAsync() > 0)
            await next.ClickAsync(new LocatorClickOptions { Timeout = 8000 });
    }, 2500);

    await StepAsync("10-pengaturan.png", async () =>
        await page.ClickAsync("button[title='Pengaturan']", new PageClickOptions { Timeout = 8000 }), 2200);
}
catch (Exception e)
{
    Console.WriteLine($"  web session failed - {FirstLine(e)}");
}

Console.WriteLine();
Console.WriteLine("Admin console");
try
{
    await SignInAsync(admin);
    await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(output, "11-admin-dashboard.png") });
    Console.WriteLine("  captured 11-admin-dashboard.png");
    captured++;

    await ShotAsync("12-admin-pengguna.png", $"{admin}/users", 2500);
    await ShotAsync("13-admin-tema.png", $"{admin}/themes", 2500);
    await ShotAsync("14-admin-pengaturan.png", $"{admin}/settings", 3000);
    await ShotAsync("15-admin-percakapan.png", $"{admin}/chats", 2500);

    // The galleries take a moment: the skills tab reads GitHub, the MCP tab renders the
    // whole seeded catalogue.
    await ShotAsync("16-admin-skills.png", $"{admin}/skills", 3000);
    await ShotAsync("17-admin-mcp.png", $"{admin}/mcp", 3000);

    // The browse tab is the point of the skills gallery, so it gets its own shot.
    try
    {
        await page.GotoAsync($"{admin}/skills", new PageGotoOptions { Timeout = 25000, WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForTimeoutAsync(2500);
        await page.ClickAsync("button:has-text('Jelajahi')", new PageClickOptions { Timeout = 8000 });
        await page.WaitForTimeoutAsync(9000);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(output, "18-admin-skills-browse.png") });
        Console.WriteLine("  captured 18-admin-skills-browse.png");
        captured++;
    }
    catch (Exception e)
    {
        Console.WriteLine($"  SKIPPED  17-admin-skills-browse.png - {FirstLine(e)}");
        skipped.Add("18-admin-skills-browse.png");
    }
}
catch (Exception e)
{
    Console.WriteLine($"  admin session failed - {FirstLine(e)}");
}

Console.WriteLine();
Console.WriteLine("API");
await ShotAsync("19-swagger.png", $"{api}/swagger", 3500);

Console.WriteLine();
Console.WriteLine("Mobile viewport");
await using var mobileContext = await browser.NewContextAsync(new BrowserNewContextOptions
{
    ViewportSize = new ViewportSize { Width = 412, Height = 900 },
    IgnoreHTTPSErrors = true,
    DeviceScaleFactor = 3,
    IsMobile = true,
    HasTouch = true
});

try
{
    var mobilePage = await mobileContext.NewPageAsync();
    await mobilePage.GotoAsync($"{web}/login", new PageGotoOptions { Timeout = 25000, WaitUntil = WaitUntilState.DOMContentLoaded });
    await mobilePage.WaitForTimeoutAsync(2000);
    await mobilePage.FillAsync("#identifier", "sitinurhaliza");
    await mobilePage.FillAsync("#password", "Telepati123!");
    await mobilePage.ClickAsync("button[type=submit]");
    await mobilePage.WaitForTimeoutAsync(5000);

    await mobilePage.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(output, "20-mobile.png") });
    Console.WriteLine("  captured 20-mobile.png");
    captured++;
}
catch (Exception e)
{
    Console.WriteLine($"  SKIPPED  16-mobile.png - {FirstLine(e)}");
    skipped.Add("20-mobile.png");
}

Console.WriteLine();
Console.WriteLine($"{captured} captured, {skipped.Count} skipped.");
if (skipped.Count > 0) Console.WriteLine($"Skipped: {string.Join(", ", skipped)}");

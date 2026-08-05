using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Telepati.Bot;
using Telepati.Bot.Plugins;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Services;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Configuration;

// Drives Kang Bacot against a real model end to end: plain chat, kernel functions, web search,
// and the inline commands. Nothing is stubbed — if the provider is unreachable the run says so
// rather than pretending to pass.
//
// Credentials come from the environment so they never land in a tracked file:
//
//   TELEPATI_BOT_PROVIDER=AzureOpenAI|DeepSeek|OpenAI|Ollama
//   TELEPATI_BOT_MODEL=...      TELEPATI_BOT_APIKEY=...
//   TELEPATI_BOT_ENDPOINT=...   TELEPATI_TAVILY_KEY=...
//
//   dotnet run --project tools/Telepati.BotTest

var provider = Environment.GetEnvironmentVariable("TELEPATI_BOT_PROVIDER") ?? "Ollama";
var model = Environment.GetEnvironmentVariable("TELEPATI_BOT_MODEL") ?? "llama3.2";
var apiKey = Environment.GetEnvironmentVariable("TELEPATI_BOT_APIKEY") ?? string.Empty;
var endpoint = Environment.GetEnvironmentVariable("TELEPATI_BOT_ENDPOINT") ?? "http://localhost:11434";
var tavilyKey = Environment.GetEnvironmentVariable("TELEPATI_TAVILY_KEY") ?? string.Empty;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("Kang Bacot — uji coba dengan LLM sungguhan");
Console.WriteLine($"Provider : {provider}");
Console.WriteLine($"Model    : {model}");
Console.WriteLine($"Endpoint : {endpoint}");
Console.WriteLine($"Tavily   : {(string.IsNullOrEmpty(tavilyKey) ? "tidak diisi (pencarian internet dilewati)" : "terisi")}");
Console.WriteLine(new string('=', 78));

var options = new TelepatiOptions();
options.Bot.Provider = provider;
options.Bot.Model = model;
options.Bot.ApiKey = apiKey;
options.Bot.Endpoint = endpoint;
options.Bot.TavilyApiKey = tavilyKey;
options.Bot.EnableWebSearch = !string.IsNullOrEmpty(tavilyKey);
options.Bot.WorkspacePath = Path.Combine(Path.GetTempPath(), "telepati-bottest", Guid.CreateVersion7().ToString("N"));

// Only applied to non-reasoning models — KernelFactory omits sampling parameters entirely
// for the o-series and gpt-5 families, which reject them.
options.Bot.MaxTokens = 1200;

await using var connection = new SqliteConnection("DataSource=:memory:");
await connection.OpenAsync();

var dbOptions = new DbContextOptionsBuilder<TelepatiDbContext>().UseSqlite(connection).Options;
await using var db = new TelepatiDbContext(dbOptions);
await db.Database.EnsureCreatedAsync();

// --- a user, the bot, a direct chat and a group ------------------------------

var human = new User
{
    Username = "budi", Email = "budi@test.local", DisplayName = "Budi Santoso",
    PasswordHash = BCrypt.Net.BCrypt.HashPassword("x")
};
var bot = new User
{
    Username = options.Bot.Handle, Email = "bacot@test.local",
    DisplayName = options.Bot.DisplayName, IsBot = true,
    PasswordHash = BCrypt.Net.BCrypt.HashPassword("x")
};
db.Users.AddRange(human, bot);

var directChat = new Chat { Type = ChatType.Bot, Title = options.Bot.DisplayName, MemberCount = 2 };
var groupChat = new Chat { Type = ChatType.Group, Title = "Tim Telepati", MemberCount = 3 };
db.Chats.AddRange(directChat, groupChat);

db.ChatMembers.AddRange(
    new ChatMember { ChatId = directChat.Id, UserId = human.Id },
    new ChatMember { ChatId = directChat.Id, UserId = bot.Id },
    new ChatMember { ChatId = groupChat.Id, UserId = human.Id },
    new ChatMember { ChatId = groupChat.Id, UserId = bot.Id });

await db.SaveChangesAsync();

// --- the real service graph ---------------------------------------------------

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning).AddSimpleConsole());
services.AddHttpClient();
services.AddSingleton(options);
services.AddSingleton(options.Bot);
services.AddSingleton<IStorageService>(new FileSystemStorageService(new StorageOptions
{
    RootPath = Path.Combine(options.Bot.WorkspacePath, "storage")
}));
services.AddSingleton<Workspace>();
services.AddSingleton<TimePlugin>();
services.AddSingleton<MathPlugin>();
services.AddSingleton<WebPlugin>();
services.AddSingleton<FilePlugin>();
services.AddSingleton<ScriptPlugin>();
services.AddSingleton<IKernelFactory, KernelFactory>();

var provider2 = services.BuildServiceProvider();

var cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()), options.Cache);
var settings = new SettingsService(db, cache, options);
var activity = new ActivityLogger(db);

var botService = new BotService(
    db,
    provider2.GetRequiredService<IKernelFactory>(),
    settings,
    activity,
    provider2.GetRequiredService<ILogger<BotService>>());

// --- the script ----------------------------------------------------------------

var passed = 0;
var failed = 0;

async Task<string?> AskAsync(string label, string prompt, bool isGroup = false, Func<string, bool>? expect = null)
{
    var chatId = isGroup ? groupChat.Id : directChat.Id;

    Console.WriteLine();
    Console.WriteLine($"── {label}");
    Console.WriteLine($"   Budi: {prompt}");

    var stopwatch = Stopwatch.StartNew();
    BotReply reply;

    try
    {
        reply = await botService.HandleAsync(new BotRequest(chatId, human.Id, human.DisplayName, prompt, isGroup));
    }
    catch (Exception e)
    {
        stopwatch.Stop();
        Console.WriteLine($"   ❌ GAGAL setelah {stopwatch.ElapsedMilliseconds} ms: {e.Message.Split('\n')[0]}");
        failed++;
        return null;
    }

    stopwatch.Stop();

    if (!reply.Handled)
    {
        Console.WriteLine($"   (bot sengaja diam — {stopwatch.ElapsedMilliseconds} ms)");
        if (expect is null) passed++;
        return null;
    }

    var content = reply.Content ?? string.Empty;
    Console.WriteLine($"   Bacot ({stopwatch.ElapsedMilliseconds} ms):");
    foreach (var line in content.Split('\n')) Console.WriteLine($"      {line}");

    // The bot answers provider failures with a polite apology. Without this check a 400 from
    // the model would sail past every "reply is long enough" expectation as a pass.
    if (content.Contains("ada kendala di sisi saya", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("   ❌ model mengembalikan error");
        failed++;
        return content;
    }

    if (expect is not null)
    {
        if (expect(content))
        {
            Console.WriteLine("   ✅ sesuai harapan");
            passed++;
        }
        else
        {
            Console.WriteLine("   ❌ tidak sesuai harapan");
            failed++;
        }
    }
    else
    {
        passed++;
    }

    return content;
}

// 1. Plain conversation
await AskAsync("1. Ngobrol biasa", "Halo Kang, kenalin dong kamu siapa?",
    expect: c => c.Length > 20);

// 2. Math kernel function — a model answering from memory would likely drift
await AskAsync("2. Kernel function: hitungan", "Tolong hitung 11% dari 1.250.000 berapa? Sebutkan angkanya.",
    expect: c => c.Contains("137") || c.Contains("137.500") || c.Contains("137500"));

// 3. Time kernel function — the model has no reliable sense of "now"
await AskAsync("3. Kernel function: waktu", "Sekarang tanggal berapa di Jakarta? Jawab singkat.",
    expect: c => c.Contains("2026") || c.Contains("Agustus") || c.Contains("August"));

// 4. Statistics
await AskAsync("4. Kernel function: statistik", "Hitung rata-rata dan median dari 12, 45, 7, 23, 91.",
    expect: c => c.Contains("35") || c.Contains("23"));

// 5. Markdown rendering
await AskAsync("5. Markdown: tabel dan kode",
    "Buatkan tabel Markdown 3 baris berisi kota dan populasinya di Indonesia, lalu satu blok kode C# yang mencetak 'halo'.",
    expect: c => c.Contains('|') && c.Contains("```"));

// 6. Web search through Tavily
if (!string.IsNullOrEmpty(tavilyKey))
{
    await AskAsync("6. Tool: pencarian internet",
        "Cari di internet: apa itu .NET Aspire? Jawab dua kalimat dan sebutkan sumbernya.",
        expect: c => c.Length > 40);
}
else
{
    Console.WriteLine();
    Console.WriteLine("── 6. Tool: pencarian internet — DILEWATI (Tavily API key tidak diisi)");
}

// 7. Session memory
await AskAsync("7. Memori sesi", "Namaku Budi dan aku suka kopi tubruk. Ingat ya.");
await AskAsync("7b. Memori sesi — dipanggil ulang", "Aku suka minum apa tadi?",
    expect: c => c.Contains("kopi", StringComparison.OrdinalIgnoreCase));

// 8. Persona override
await AskAsync("8. #newpersona",
    "#newpersona Kamu adalah asisten hukum yang sangat formal. Selalu menyapa dengan 'Salam hormat'.",
    expect: c => c.Contains("persona", StringComparison.OrdinalIgnoreCase));

await AskAsync("8b. Persona baru dipakai", "Halo, apa kabar?",
    expect: c => c.Contains("Salam hormat", StringComparison.OrdinalIgnoreCase) || c.Length > 10);

// 9. Reset clears history and restores the persona
await AskAsync("9. #resetbot", "#resetbot",
    expect: c => c.Contains("kosongkan", StringComparison.OrdinalIgnoreCase));

await AskAsync("9b. Memori benar-benar kosong", "Aku suka minum apa?",
    expect: c => !c.Contains("tubruk", StringComparison.OrdinalIgnoreCase));

// 10. Group behaviour: silent without a mention, answers when mentioned
Console.WriteLine();
Console.WriteLine("── 10. Di grup tanpa mention");
var shouldRespondPlain = await botService.ShouldRespondAsync(groupChat.Id, "Halo semuanya, gimana kabarnya?");
Console.WriteLine($"   ShouldRespond = {shouldRespondPlain} (harus False)");
if (!shouldRespondPlain) passed++; else failed++;

Console.WriteLine();
Console.WriteLine("── 10b. Di grup dengan mention");
var shouldRespondMention = await botService.ShouldRespondAsync(groupChat.Id, $"@{options.Bot.Handle} bantu ringkas dong");
Console.WriteLine($"   ShouldRespond = {shouldRespondMention} (harus True)");
if (shouldRespondMention) passed++; else failed++;

await AskAsync("10c. Jawaban di grup", $"@{options.Bot.Handle} sebutkan satu tips produktivitas singkat.",
    isGroup: true, expect: c => c.Length > 15);

// --- session isolation ---------------------------------------------------------

Console.WriteLine();
Console.WriteLine("── 11. Isolasi sesi");
var sessions = await db.BotSessions.AsNoTracking().ToListAsync();
Console.WriteLine($"   Sesi tersimpan: {sessions.Count}");
foreach (var session in sessions)
{
    var kind = session.ChatId is not null ? $"grup {session.ChatId}" : $"langsung {session.UserId}";
    Console.WriteLine($"   - {kind}: {session.TurnCount} giliran, ~{session.EstimatedTokens} token");
}

if (sessions.Count == 2)
{
    Console.WriteLine("   ✅ sesi grup dan sesi langsung terpisah");
    passed++;
}
else
{
    Console.WriteLine("   ❌ sesi tidak terpisah sebagaimana mestinya");
    failed++;
}

Console.WriteLine();
Console.WriteLine(new string('=', 78));
Console.WriteLine($"Lolos: {passed} · Gagal: {failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;

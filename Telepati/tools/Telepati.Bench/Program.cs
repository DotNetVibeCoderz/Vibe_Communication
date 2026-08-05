using System.Diagnostics;
using System.Text.Json;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Telepati.Client.Data;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Services;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

// Measures the optimisations rather than describing them. Every number printed here is
// produced on the machine that runs it; Performance.md records one such run verbatim.
//
//   dotnet run --project tools/Telepati.Bench -c Release

const int Warmup = 3;
const int Iterations = 20;

Console.WriteLine("Telepati — performance benchmark");
Console.WriteLine($"Machine: {Environment.MachineName} · {Environment.ProcessorCount} logical cores · .NET {Environment.Version}");
Console.WriteLine($"Started: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
Console.WriteLine(new string('=', 78));

var bench = new Benchmark();
await bench.RunAsync();

Console.WriteLine(new string('=', 78));
Console.WriteLine("Done.");

internal sealed class Benchmark
{
    private const int Warmup = 3;
    private const int Iterations = 20;

    // Big enough that query shape matters; small enough to build in seconds.
    private const int ChatCount = 60;
    private const int MessagesPerChat = 250;
    private const int UserCount = 40;

    private SqliteConnection _connection = default!;
    private TelepatiDbContext _db = default!;
    private Guid _busyChatId;
    private Guid _denseChatId;
    private Guid _meId;

    public async Task RunAsync()
    {
        await SeedAsync();

        Section("1. EF Core — single query vs split query");
        await CompareIncludeStrategiesAsync();

        Section("2. Chat list — the messenger's first screen");
        await MeasureChatListAsync();

        Section("3. Settings lookup — cold vs cached");
        await MeasureSettingsAsync();

        Section("4. Local store — device cache vs a server round trip");
        await MeasureLocalStoresAsync();

        Section("5. Wire format — JSON vs MessagePack");
        MeasurePayloadSize();

        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // -- 1. include strategy --------------------------------------------------

    private async Task CompareIncludeStrategiesAsync()
    {
        // Three collection Includes on one root multiply together. This is the cartesian
        // product AsSplitQuery removes.
        var singleRows = await _db.Messages.AsNoTracking()
            .Where(m => m.ChatId == _busyChatId)
            .OrderByDescending(m => m.CreatedAt).Take(50)
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .ToListAsync();

        var single = await MeasureAsync("single query  (4 Includes)", async () =>
            await _db.Messages.AsNoTracking()
                .Where(m => m.ChatId == _busyChatId)
                .OrderByDescending(m => m.CreatedAt).Take(50)
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
                .Include(m => m.Mentions)
                .ToListAsync());

        var split = await MeasureAsync("split query   (AsSplitQuery)", async () =>
            await _db.Messages.AsNoTracking()
                .Where(m => m.ChatId == _busyChatId)
                .OrderByDescending(m => m.CreatedAt).Take(50)
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
                .Include(m => m.Mentions)
                .AsSplitQuery()
                .ToListAsync());

        Improvement(single, split);
        Console.WriteLine($"    sparse page: 50 messages · {singleRows.Sum(m => m.Attachments.Count)} attachments · " +
                          $"{singleRows.Sum(m => m.Reactions.Count)} reactions · {singleRows.Sum(m => m.Mentions.Count)} mentions");

        // The sparse case above is the common one and split queries barely move it. The
        // cartesian product only bites when the collections are dense — a busy group where
        // messages collect many reactions — so that case is measured separately rather than
        // claiming a win the numbers do not support.
        Console.WriteLine();
        await SeedDenseCollectionsAsync();

        var denseRows = await _db.Messages.AsNoTracking()
            .Where(m => m.ChatId == _denseChatId)
            .OrderByDescending(m => m.CreatedAt).Take(50)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            .ToListAsync();

        var denseSingle = await MeasureAsync("dense: single query", async () =>
            await _db.Messages.AsNoTracking()
                .Where(m => m.ChatId == _denseChatId)
                .OrderByDescending(m => m.CreatedAt).Take(50)
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
                .Include(m => m.Mentions)
                .ToListAsync());

        var denseSplit = await MeasureAsync("dense: split query", async () =>
            await _db.Messages.AsNoTracking()
                .Where(m => m.ChatId == _denseChatId)
                .OrderByDescending(m => m.CreatedAt).Take(50)
                .Include(m => m.Sender)
                .Include(m => m.Attachments)
                .Include(m => m.Reactions)
                .Include(m => m.Mentions)
                .AsSplitQuery()
                .ToListAsync());

        Improvement(denseSingle, denseSplit);

        var attachments = denseRows.Sum(m => m.Attachments.Count);
        var reactions = denseRows.Sum(m => m.Reactions.Count);
        var mentions = denseRows.Sum(m => m.Mentions.Count);
        Console.WriteLine($"    dense page: 50 messages · {attachments} attachments · {reactions} reactions · {mentions} mentions");
        Console.WriteLine($"    a single query materialises up to {denseRows.Max(m => Math.Max(1, m.Attachments.Count) * Math.Max(1, m.Reactions.Count) * Math.Max(1, m.Mentions.Count))} rows for one message");
    }

    /// <summary>Builds one conversation where every message carries many child rows.</summary>
    private async Task SeedDenseCollectionsAsync()
    {
        var chat = await _db.Chats.AsNoTracking().FirstAsync(c => c.Id == _denseChatId);
        var memberIds = await _db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chat.Id).Select(m => m.UserId).ToListAsync();

        var messages = await _db.Messages
            .Where(m => m.ChatId == _denseChatId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(50)
            .ToListAsync();

        var emojis = new[] { "😂", "🔥", "❤️", "🎉", "🙏", "👏" };

        foreach (var message in messages)
        {
            for (var i = 0; i < 6; i++)
            {
                _db.MessageReactions.Add(new MessageReaction
                {
                    MessageId = message.Id,
                    UserId = memberIds[i % memberIds.Count],
                    Emoji = emojis[i]
                });
            }

            for (var i = 0; i < 3; i++)
            {
                _db.MessageAttachments.Add(new MessageAttachment
                {
                    MessageId = message.Id,
                    FileName = $"berkas-{i}.png",
                    ContentType = "image/png",
                    SizeBytes = 96_000,
                    StorageKey = $"attachments/2026/08/06/{Guid.CreateVersion7():N}.png"
                });
            }

            for (var i = 0; i < 3; i++)
            {
                _db.MessageMentions.Add(new MessageMention
                {
                    MessageId = message.Id,
                    MentionedUserId = memberIds[i % memberIds.Count]
                });
            }
        }

        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    // -- 2. chat list ---------------------------------------------------------

    private async Task MeasureChatListAsync()
    {
        var services = BuildServices();

        var chats = await MeasureAsync("GetChatsAsync (30 conversations)", async () =>
            await services.Chats.GetChatsAsync(_meId, 1, 30, false));

        var messages = await MeasureAsync("GetMessagesAsync (50 messages)", async () =>
            await services.Messages.GetMessagesAsync(_meId, _busyChatId, 1, 50, null));

        Console.WriteLine($"    chat list: {chats.Mean:F2} ms · thread page: {messages.Mean:F2} ms");
    }

    // -- 3. settings ----------------------------------------------------------

    private async Task MeasureSettingsAsync()
    {
        var cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()), new CacheOptions());
        var settings = new SettingsService(_db, cache, new TelepatiOptions());

        // Every request that checks a feature flag or a limit goes through this.
        var cold = await MeasureAsync("GetOptionsAsync (cache cleared each time)", async () =>
        {
            await cache.RemoveAsync(CacheKeys.Settings());
            return await settings.GetOptionsAsync();
        });

        var warm = await MeasureAsync("GetOptionsAsync (cached)", async () => await settings.GetOptionsAsync());

        Improvement(cold, warm);
    }

    // -- 4. local store -------------------------------------------------------

    private async Task MeasureLocalStoresAsync()
    {
        var page = await _db.Messages.AsNoTracking()
            .Where(m => m.ChatId == _busyChatId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(200)
            .Include(m => m.Sender)
            .AsSplitQuery()
            .ToListAsync();

        var storage = new FileSystemStorageService(new StorageOptions { RootPath = TempFolder("storage") });
        var dtos = new List<MessageDto>();
        foreach (var message in page) dtos.Add(await message.ToDtoAsync(storage));

        foreach (var provider in new[] { "Sqlite", "LiteDb" })
        {
            var options = new ClientOptions();
            options.LocalDatabase.Provider = provider;
            options.LocalDatabase.Path = TempFolder($"bench-{provider}");

            await using ILocalStore store = provider == "LiteDb"
                ? new LiteDbLocalStore(options, NullLogger<LiteDbLocalStore>.Instance)
                : new SqliteLocalStore(options, NullLogger<SqliteLocalStore>.Instance);

            await store.InitializeAsync(Guid.CreateVersion7());
            await store.SaveMessagesAsync(dtos);

            var read = await MeasureAsync($"{provider,-7} read 50 cached messages", async () =>
                await store.GetMessagesAsync(_busyChatId, 50));

            var write = await MeasureAsync($"{provider,-7} write 50 messages", async () =>
            {
                await store.SaveMessagesAsync(dtos.Take(50));
                return 0;
            });

            var stats = await store.GetStatsAsync();
            Console.WriteLine($"    {provider}: {stats.MessageCount} rows on disk, {stats.SizeBytes / 1024.0:F0} KB " +
                              $"(read {read.Mean:F2} ms, write {write.Mean:F2} ms)");
        }
    }

    // -- 5. wire format -------------------------------------------------------

    private void MeasurePayloadSize()
    {
        var sample = Enumerable.Range(0, 50).Select(i => new MessageDto
        {
            Id = Guid.CreateVersion7(),
            ChatId = _busyChatId,
            SenderId = Guid.CreateVersion7(),
            SenderName = "Siti Nurhaliza",
            SenderAvatarUrl = "/files/avatars/2026/08/06/abc123.png",
            Type = 0,
            Content = "Rekan-rekan, jangan lupa standup jam 9 ya 🙌 nanti kita bahas rilis berikutnya.",
            DeliveryState = 3,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i)
        }).ToList();

        var json = JsonSerializer.SerializeToUtf8Bytes(sample, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // ContractlessStandardResolver is what SignalR's MessagePack protocol uses for DTOs
        // that carry no MessagePack attributes. The saving is real but modest — these payloads
        // are mostly GUIDs and free text, which neither format can compress away.
        var messagePackOptions = MessagePackSerializerOptions.Standard.WithResolver(ContractlessStandardResolver.Instance);
        var messagePack = MessagePackSerializer.Serialize(sample, messagePackOptions);

        Console.WriteLine($"  JSON        {json.Length,7:N0} bytes");
        Console.WriteLine($"  MessagePack {messagePack.Length,7:N0} bytes");
        Console.WriteLine($"  → {100.0 - messagePack.Length * 100.0 / json.Length:F1}% smaller on the wire (50-message page)");
    }

    // -- harness --------------------------------------------------------------

    private static async Task<Timing> MeasureAsync<T>(string label, Func<Task<T>> action)
    {
        for (var i = 0; i < Warmup; i++) await action();

        var samples = new double[Iterations];
        for (var i = 0; i < Iterations; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            await action();
            stopwatch.Stop();
            samples[i] = stopwatch.Elapsed.TotalMilliseconds;
        }

        Array.Sort(samples);
        var timing = new Timing(
            samples.Average(),
            samples[samples.Length / 2],
            samples[(int)(samples.Length * 0.95)],
            samples[0]);

        Console.WriteLine($"  {label,-42} mean {timing.Mean,7:F2} ms · p50 {timing.Median,7:F2} ms · p95 {timing.P95,7:F2} ms");
        return timing;
    }

    private static void Improvement(Timing before, Timing after)
    {
        var delta = (before.Mean - after.Mean) / before.Mean * 100;
        var verdict = delta > 0 ? $"{delta:F1}% faster" : $"{-delta:F1}% slower";
        Console.WriteLine($"  → {verdict} (mean {before.Mean:F2} ms → {after.Mean:F2} ms)");
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', 78));
    }

    private static string TempFolder(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "telepati-bench", name);
        Directory.CreateDirectory(path);
        return path;
    }

    private (IChatService Chats, IMessageService Messages) BuildServices()
    {
        var cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()), new CacheOptions());
        var storage = new FileSystemStorageService(new StorageOptions { RootPath = TempFolder("storage") });
        var options = new TelepatiOptions();
        var settings = new SettingsService(_db, cache, options);
        var activity = new ActivityLogger(_db);
        var notifier = new NullRealtimeNotifier();

        var chats = new ChatService(_db, cache, storage, settings, activity, notifier);
        var messages = new MessageService(_db, cache, storage, settings, chats, activity, notifier);

        return (chats, messages);
    }

    private async Task SeedAsync()
    {
        Console.WriteLine($"Seeding: {UserCount} users · {ChatCount} chats · {MessagesPerChat} messages per chat…");
        var stopwatch = Stopwatch.StartNew();

        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var builder = new DbContextOptionsBuilder<TelepatiDbContext>().UseSqlite(_connection);
        _db = new TelepatiDbContext(builder.Options);
        await _db.Database.EnsureCreatedAsync();

        var random = new Random(20260806);
        var hash = BCrypt.Net.BCrypt.HashPassword("bench");

        var users = Enumerable.Range(0, UserCount).Select(i => new User
        {
            Username = $"user{i}",
            Email = $"user{i}@bench.local",
            DisplayName = $"Pengguna {i}",
            PasswordHash = hash
        }).ToList();

        _db.Users.AddRange(users);
        await _db.SaveChangesAsync();
        _meId = users[0].Id;

        for (var c = 0; c < ChatCount; c++)
        {
            var members = users.OrderBy(_ => random.Next()).Take(random.Next(2, 8)).ToList();
            if (!members.Any(u => u.Id == _meId)) members[0] = users[0];

            var chat = new Chat
            {
                Type = members.Count == 2 ? ChatType.Direct : ChatType.Group,
                Title = members.Count == 2 ? null : $"Grup {c}",
                MemberCount = members.Count,
                CreatedById = members[0].Id
            };
            _db.Chats.Add(chat);

            foreach (var member in members)
            {
                _db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = member.Id });
            }

            Message? last = null;
            var at = DateTimeOffset.UtcNow.AddDays(-30);

            for (var m = 0; m < MessagesPerChat; m++)
            {
                at = at.AddMinutes(random.Next(1, 12));
                last = new Message
                {
                    ChatId = chat.Id,
                    SenderId = members[random.Next(members.Count)].Id,
                    Type = MessageType.Text,
                    Content = $"Pesan nomor {m} di percakapan {c}. Ini teks yang panjangnya wajar untuk sebuah obrolan.",
                    CreatedAt = at,
                    DeliveryState = MessageDeliveryState.Read
                };
                _db.Messages.Add(last);

                // Roughly a fifth of messages carry a reaction or an attachment — enough for
                // the cartesian product to be visible in the single-query case.
                if (m % 5 == 0)
                {
                    _db.MessageReactions.Add(new MessageReaction
                    {
                        MessageId = last.Id,
                        UserId = members[random.Next(members.Count)].Id,
                        Emoji = "👍"
                    });
                }

                if (m % 7 == 0)
                {
                    _db.MessageAttachments.Add(new MessageAttachment
                    {
                        MessageId = last.Id,
                        FileName = $"lampiran-{m}.png",
                        ContentType = "image/png",
                        SizeBytes = 128_000,
                        StorageKey = $"attachments/2026/08/06/{Guid.CreateVersion7():N}.png"
                    });
                }

                if (m % 11 == 0)
                {
                    _db.MessageMentions.Add(new MessageMention
                    {
                        MessageId = last.Id,
                        MentionedUserId = members[random.Next(members.Count)].Id
                    });
                }
            }

            chat.LastMessageId = last?.Id;
            chat.LastMessageAt = last?.CreatedAt;

            if (c == 0) _busyChatId = chat.Id;
            if (c == 1) _denseChatId = chat.Id;
        }

        await _db.SaveChangesAsync();

        var total = await _db.Messages.CountAsync();
        Console.WriteLine($"Seeded {total:N0} messages in {stopwatch.Elapsed.TotalSeconds:F1} s");
    }

    private readonly record struct Timing(double Mean, double Median, double P95, double Min);
}

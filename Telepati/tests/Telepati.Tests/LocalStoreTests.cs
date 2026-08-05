using Microsoft.Extensions.Logging.Abstractions;
using Telepati.Client.Data;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Tests;

/// <summary>
/// Both local stores must behave identically — the provider is a user setting, so a chat cached
/// under SQLite and one cached under LiteDB have to read back the same way.
/// </summary>
public class LocalStoreTests
{
    public static TheoryData<string> Providers => new() { "Sqlite", "LiteDb" };

    private static ILocalStore Create(string provider, out string folder)
    {
        folder = Path.Combine(Path.GetTempPath(), "telepati-store-tests", Guid.CreateVersion7().ToString("N"));

        var options = new ClientOptions();
        options.LocalDatabase.Provider = provider;
        options.LocalDatabase.Path = folder;

        return provider == "LiteDb"
            ? new LiteDbLocalStore(options, NullLogger<LiteDbLocalStore>.Instance)
            : new SqliteLocalStore(options, NullLogger<SqliteLocalStore>.Instance);
    }

    private static MessageDto Message(Guid chatId, int minutesAgo, string content) => new()
    {
        Id = Guid.CreateVersion7(),
        ChatId = chatId,
        SenderId = Guid.CreateVersion7(),
        SenderName = "Alice",
        Content = content,
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo)
    };

    [Theory, MemberData(nameof(Providers))]
    public async Task Messages_round_trip_and_come_back_in_chronological_order(string provider)
    {
        await using var store = Create(provider, out _);
        await store.InitializeAsync(Guid.CreateVersion7());

        var chatId = Guid.CreateVersion7();
        await store.SaveMessagesAsync([
            Message(chatId, 30, "paling lama"),
            Message(chatId, 20, "tengah"),
            Message(chatId, 10, "paling baru")
        ]);

        var page = await store.GetMessagesAsync(chatId, 10);

        Assert.Equal(3, page.Count);
        Assert.Equal("paling lama", page[0].Content);
        Assert.Equal("paling baru", page[^1].Content);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Cursor_paging_matches_how_the_server_pages(string provider)
    {
        await using var store = Create(provider, out _);
        await store.InitializeAsync(Guid.CreateVersion7());

        var chatId = Guid.CreateVersion7();
        var messages = Enumerable.Range(1, 30).Select(i => Message(chatId, i, $"pesan {i}")).ToList();
        await store.SaveMessagesAsync(messages);

        var newest = await store.GetMessagesAsync(chatId, 10);
        Assert.Equal(10, newest.Count);

        // Scrolling up must return strictly older rows, exactly like the API's `before` cursor.
        var older = await store.GetMessagesAsync(chatId, 10, newest[0].CreatedAt);

        Assert.Equal(10, older.Count);
        Assert.All(older, m => Assert.True(m.CreatedAt < newest[0].CreatedAt));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Saving_the_same_message_twice_updates_rather_than_duplicates(string provider)
    {
        await using var store = Create(provider, out _);
        await store.InitializeAsync(Guid.CreateVersion7());

        var chatId = Guid.CreateVersion7();
        var message = Message(chatId, 5, "asli");

        await store.SaveMessagesAsync([message]);
        await store.SaveMessagesAsync([message with { Content = "sudah diubah", IsEdited = true }]);

        var page = await store.GetMessagesAsync(chatId, 10);

        Assert.Single(page);
        Assert.Equal("sudah diubah", page[0].Content);
        Assert.True(page[0].IsEdited);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Chats_are_ordered_pinned_first_then_by_recency(string provider)
    {
        await using var store = Create(provider, out _);
        await store.InitializeAsync(Guid.CreateVersion7());

        var now = DateTimeOffset.UtcNow;
        await store.SaveChatsAsync([
            new ChatDto { Id = Guid.CreateVersion7(), Title = "lama", LastMessageAt = now.AddHours(-5) },
            new ChatDto { Id = Guid.CreateVersion7(), Title = "baru", LastMessageAt = now },
            new ChatDto { Id = Guid.CreateVersion7(), Title = "disematkan", LastMessageAt = now.AddDays(-3), IsPinned = true }
        ]);

        var chats = await store.GetChatsAsync();

        Assert.Equal("disematkan", chats[0].Title);
        Assert.Equal("baru", chats[1].Title);
        Assert.Equal("lama", chats[2].Title);
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Pruning_drops_old_messages_but_keeps_the_newest_per_chat(string provider)
    {
        await using var store = Create(provider, out _);
        await store.InitializeAsync(Guid.CreateVersion7());

        var chatId = Guid.CreateVersion7();

        // Everything is older than the cutoff, so only the "keep newest N" rule protects rows.
        var old = Enumerable.Range(1, 50)
            .Select(i => Message(chatId, 60 * 24 * 40 + i, $"lama {i}"))
            .ToList();
        await store.SaveMessagesAsync(old);

        var removed = await store.PruneAsync(keepPerChat: 10, maxAge: TimeSpan.FromDays(30));

        Assert.Equal(40, removed);
        Assert.Equal(10, await store.CountMessagesAsync(chatId));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Clearing_empties_every_collection(string provider)
    {
        await using var store = Create(provider, out _);
        await store.InitializeAsync(Guid.CreateVersion7());

        var chatId = Guid.CreateVersion7();
        await store.SaveChatsAsync([new ChatDto { Id = chatId, Title = "Tim" }]);
        await store.SaveMessagesAsync([Message(chatId, 1, "halo")]);
        await store.SetSyncedAtAsync("chats", DateTimeOffset.UtcNow);

        await store.ClearAsync();

        Assert.Empty(await store.GetChatsAsync());
        Assert.Equal(0, await store.CountMessagesAsync(chatId));
        Assert.Null(await store.GetSyncedAtAsync("chats"));
    }

    [Theory, MemberData(nameof(Providers))]
    public async Task Each_account_gets_its_own_file(string provider)
    {
        var alice = Guid.CreateVersion7();
        var bob = Guid.CreateVersion7();

        await using var aliceStore = Create(provider, out var folder);
        await aliceStore.InitializeAsync(alice);
        await aliceStore.SaveChatsAsync([new ChatDto { Id = Guid.CreateVersion7(), Title = "punya Alice" }]);

        var aliceStats = await aliceStore.GetStatsAsync();
        Assert.Contains(alice.ToString("N"), aliceStats.Location);
        Assert.DoesNotContain(bob.ToString("N"), aliceStats.Location);
    }

    [Fact]
    public async Task Disabling_the_store_makes_every_call_a_harmless_no_op()
    {
        await using var store = new NullLocalStore();
        await store.InitializeAsync(Guid.CreateVersion7());

        await store.SaveChatsAsync([new ChatDto { Id = Guid.CreateVersion7(), Title = "x" }]);

        Assert.False(store.IsReady);
        Assert.Empty(await store.GetChatsAsync());
    }
}

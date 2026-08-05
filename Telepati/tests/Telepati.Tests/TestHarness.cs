using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Services;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Configuration;

namespace Telepati.Tests;

/// <summary>
/// Spins up the real service graph over an in-memory SQLite database. Tests exercise the same
/// EF Core model and the same services the server runs, rather than mocks that could drift
/// from the implementation.
/// </summary>
public sealed class TestHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public TestHarness(Action<TelepatiOptions>? configure = null)
    {
        Options = new TelepatiOptions();
        Options.Database.Provider = "Sqlite";
        Options.Database.SeedSampleData = false;
        configure?.Invoke(Options);

        // A shared in-memory database lives only as long as its connection is open.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Deliberately no tracking override — the harness must behave exactly like the server.
        var builder = new DbContextOptionsBuilder<TelepatiDbContext>().UseSqlite(_connection);

        Db = new TelepatiDbContext(builder.Options);
        Db.Database.EnsureCreated();

        Cache = new MemoryCacheService(new MemoryCache(new MemoryCacheOptions()), Options.Cache);
        Storage = new FileSystemStorageService(new StorageOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "telepati-tests", Guid.CreateVersion7().ToString("N"))
        });

        Notifier = new RecordingNotifier();

        Settings = new SettingsService(Db, Cache, Options);
        Activity = new ActivityLogger(Db);
        Auth = new AuthService(Db, Settings, Activity);
        Users = new UserService(Db, Cache, Settings, Notifier);
        Contacts = new ContactService(Db, Activity);
        Chats = new ChatService(Db, Cache, Storage, Settings, Activity, Notifier);
        Messages = new MessageService(Db, Cache, Storage, Settings, Chats, Activity, Notifier);
        Themes = new ThemeService(Db, Cache, Settings);
    }

    public TelepatiOptions Options { get; }
    public TelepatiDbContext Db { get; }
    public ICacheService Cache { get; }
    public IStorageService Storage { get; }
    public RecordingNotifier Notifier { get; }
    public ISettingsService Settings { get; }
    public IActivityLogger Activity { get; }
    public IAuthService Auth { get; }
    public IUserService Users { get; }
    public IContactService Contacts { get; }
    public IChatService Chats { get; }
    public IMessageService Messages { get; }
    public IThemeService Themes { get; }

    public async Task<Guid> CreateUserAsync(string username, string password = "Telepati123!")
    {
        var result = await Auth.RegisterAsync(new Shared.Contracts.RegisterRequest(
            username, $"{username}@test.local", null, username, password));

        return result.User?.Id ?? throw new InvalidOperationException(result.Error ?? "Registration failed.");
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>Captures pushes so tests can assert what would have been delivered.</summary>
public sealed class RecordingNotifier : IRealtimeNotifier
{
    public List<(Shared.Contracts.MessageDto Message, IReadOnlyList<Guid> Recipients)> Sent { get; } = [];
    public List<Shared.Contracts.TypingNotification> Typing { get; } = [];
    public List<Shared.Contracts.PresenceNotification> Presence { get; } = [];
    public List<Guid> Deleted { get; } = [];

    public Task MessageSentAsync(Shared.Contracts.MessageDto m, IReadOnlyList<Guid> r, CancellationToken ct = default)
    {
        Sent.Add((m, r));
        return Task.CompletedTask;
    }

    public Task MessageEditedAsync(Shared.Contracts.MessageDto m, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;

    public Task MessageDeletedAsync(Guid c, Guid m, IReadOnlyList<Guid> r, CancellationToken ct = default)
    {
        Deleted.Add(m);
        return Task.CompletedTask;
    }

    public Task ReceiptAsync(Guid c, Guid m, Guid u, int s, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;

    public Task TypingAsync(Shared.Contracts.TypingNotification n, IReadOnlyList<Guid> r, CancellationToken ct = default)
    {
        Typing.Add(n);
        return Task.CompletedTask;
    }

    public Task PresenceAsync(Shared.Contracts.PresenceNotification n, IReadOnlyList<Guid> r, CancellationToken ct = default)
    {
        Presence.Add(n);
        return Task.CompletedTask;
    }

    public Task CallSignalAsync(Shared.Contracts.CallSignalDto s, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;

    public Task ChatUpdatedAsync(Shared.Contracts.ChatDto c, IReadOnlyList<Guid> r, CancellationToken ct = default) => Task.CompletedTask;
}

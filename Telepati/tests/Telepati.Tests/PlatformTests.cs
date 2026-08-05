using Microsoft.EntityFrameworkCore;
using Telepati.Bot.Plugins;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;
using Telepati.UI.Services;

namespace Telepati.Tests;

public class AuthTests
{
    [Fact]
    public async Task Login_succeeds_with_correct_credentials_and_fails_otherwise()
    {
        await using var harness = new TestHarness();
        await harness.CreateUserAsync("alice");

        var good = await harness.Auth.LoginAsync(new LoginRequest("alice", "Telepati123!", null, "test", "test", null), null, null);
        var bad = await harness.Auth.LoginAsync(new LoginRequest("alice", "salah", null, "test", "test", null), null, null);

        Assert.True(good.Success);
        Assert.NotNull(good.AccessToken);
        Assert.False(bad.Success);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_so_the_old_one_stops_working()
    {
        await using var harness = new TestHarness();
        await harness.CreateUserAsync("alice");

        var login = await harness.Auth.LoginAsync(new LoginRequest("alice", "Telepati123!", null, "test", "test", null), null, null);
        var first = await harness.Auth.RefreshAsync(login.RefreshToken!);

        Assert.True(first.Success);
        Assert.NotEqual(login.RefreshToken, first.RefreshToken);

        // Replaying the consumed token must be rejected.
        var replay = await harness.Auth.RefreshAsync(login.RefreshToken!);
        Assert.False(replay.Success);
    }

    [Fact]
    public async Task Changing_the_password_revokes_every_existing_session()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");

        var login = await harness.Auth.LoginAsync(new LoginRequest("alice", "Telepati123!", null, "test", "test", null), null, null);
        await harness.Users.ChangePasswordAsync(alice, "Telepati123!", "PasswordBaru123!");

        var replay = await harness.Auth.RefreshAsync(login.RefreshToken!);
        Assert.False(replay.Success);
    }
}

public class DiscoveryTests
{
    [Fact]
    public async Task Nearby_search_only_returns_users_who_opted_in_and_are_within_the_radius()
    {
        await using var harness = new TestHarness();
        var me = await harness.CreateUserAsync("me");
        var near = await harness.CreateUserAsync("near");
        var far = await harness.CreateUserAsync("far");
        var hidden = await harness.CreateUserAsync("hidden");

        // Bandung, roughly. ~0.05° latitude is about 5.5 km.
        await harness.Users.UpdateLocationAsync(me, -6.9175, 107.6191, true);
        await harness.Users.UpdateLocationAsync(near, -6.9600, 107.6191, true);
        await harness.Users.UpdateLocationAsync(far, -7.9175, 107.6191, true);
        await harness.Users.UpdateLocationAsync(hidden, -6.9180, 107.6191, shareForDiscovery: false);

        var results = await harness.Users.SearchAsync(me, new ContactSearchRequest
        {
            Mode = "nearby",
            Latitude = -6.9175,
            Longitude = 107.6191,
            RadiusKm = 10
        });

        Assert.Contains(results, u => u.Id == near);
        Assert.DoesNotContain(results, u => u.Id == far);
        Assert.DoesNotContain(results, u => u.Id == hidden);
        Assert.All(results, u => Assert.NotNull(u.DistanceKm));
    }

    [Fact]
    public async Task Phone_import_matches_numbers_written_in_different_formats()
    {
        await using var harness = new TestHarness();
        var me = await harness.CreateUserAsync("me");

        var other = await harness.CreateUserAsync("other");

        var otherUser = await harness.Db.Users.AsTracking().FirstAsync(u => u.Id == other);
        otherUser.PhoneNumber = "081234567890";
        await harness.Db.SaveChangesAsync();

        // The same subscriber written as +62, 0-prefixed and spaced must all match.
        var matches = await harness.Contacts.MatchPhoneNumbersAsync(me, ["+62 812-3456-7890"]);

        Assert.Contains(matches, u => u.Id == other);
    }

    [Fact]
    public async Task Contact_qr_round_trips_back_to_the_same_user()
    {
        await using var harness = new TestHarness();
        var alice = await harness.CreateUserAsync("alice");
        var bob = await harness.CreateUserAsync("bob");

        var card = await harness.Contacts.GetContactCardAsync(alice);
        Assert.True(card.Success);

        var resolved = await harness.Contacts.ResolveContactCardAsync(bob, card.Data!.Payload);

        Assert.True(resolved.Success);
        Assert.Equal(alice, resolved.Data!.Id);
    }
}

public class ConfigurationTests
{
    [Fact]
    public async Task Database_overrides_win_over_the_appsettings_value()
    {
        await using var harness = new TestHarness();

        Assert.True((await harness.Settings.GetOptionsAsync()).Features.EnableVoiceCall);

        await harness.Settings.SetValueAsync("Telepati:Features:EnableVoiceCall", "false");

        Assert.False((await harness.Settings.GetOptionsAsync()).Features.EnableVoiceCall);
    }

    [Fact]
    public async Task Resetting_an_override_restores_the_file_value()
    {
        await using var harness = new TestHarness();

        await harness.Settings.SetValueAsync("Telepati:Limits:MaxGroupMembers", "12");
        Assert.Equal(12, (await harness.Settings.GetOptionsAsync()).Limits.MaxGroupMembers);

        await harness.Settings.ResetAsync("Telepati:Limits:MaxGroupMembers");
        Assert.Equal(500, (await harness.Settings.GetOptionsAsync()).Limits.MaxGroupMembers);
    }

    [Fact]
    public async Task Seasonal_theme_inside_its_window_outranks_the_activated_one()
    {
        await using var harness = new TestHarness();

        await harness.Themes.CreateAsync(new ThemeDto(Guid.Empty, "Biasa", null,
            "#111111", "#222222", "#333333", "#FFFFFF", "#EEEEEE", "#000000", "💬", false, false, false, null, null));

        var all = await harness.Themes.GetAllAsync();
        await harness.Themes.ActivateAsync(all.Single(t => t.Name == "Biasa").Id);

        await harness.Themes.CreateAsync(new ThemeDto(Guid.Empty, "Lebaran", null,
            "#1E8449", "#F1C40F", "#E67E22", "#FFFDF5", "#F3EFE0", "#1B2B22", "🌙", false, false, true,
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1)));

        var active = await harness.Themes.GetActiveAsync();
        Assert.Equal("Lebaran", active.Name);
    }
}

public class ShardingTests
{
    [Fact]
    public void Shard_selection_is_stable_and_spread_across_nodes()
    {
        var options = new DatabaseOptions
        {
            Sharding = new ShardingOptions
            {
                Enabled = true,
                Shards = ["Data Source=s0.db", "Data Source=s1.db", "Data Source=s2.db", "Data Source=s3.db"]
            }
        };

        var resolver = new ShardResolver(options);
        var key = Guid.Parse("11111111-2222-3333-4444-555555555555");

        // The same key must always land on the same node, in any process.
        Assert.Equal(resolver.GetShardIndex(key), resolver.GetShardIndex(key));

        var buckets = Enumerable.Range(0, 2000)
            .Select(_ => resolver.GetShardIndex(Guid.CreateVersion7()))
            .GroupBy(i => i)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(4, buckets.Count);
        Assert.All(buckets.Values, count => Assert.InRange(count, 350, 650));
    }

    [Fact]
    public void Sharding_disabled_collapses_to_a_single_node()
    {
        var resolver = new ShardResolver(new DatabaseOptions());

        Assert.False(resolver.IsEnabled);
        Assert.Equal(1, resolver.ShardCount);
        Assert.Equal(0, resolver.GetShardIndex(Guid.CreateVersion7()));
    }
}

public class BotSandboxTests
{
    [Fact]
    public void Workspace_refuses_paths_that_escape_the_sandbox()
    {
        var workspace = new Workspace(new BotOptions
        {
            WorkspacePath = Path.Combine(Path.GetTempPath(), "telepati-ws", Guid.CreateVersion7().ToString("N"))
        });

        Assert.Throws<UnauthorizedAccessException>(() => workspace.Resolve("../../etc/passwd"));
        Assert.Throws<UnauthorizedAccessException>(() => workspace.Resolve(@"C:\Windows\System32\config"));

        var inside = workspace.Resolve("laporan/hasil.csv");
        Assert.StartsWith(workspace.Root, inside, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Session_folders_are_isolated_per_conversation()
    {
        var workspace = new Workspace(new BotOptions
        {
            WorkspacePath = Path.Combine(Path.GetTempPath(), "telepati-ws", Guid.CreateVersion7().ToString("N"))
        });

        var first = workspace.ResolveSessionFolder(Guid.CreateVersion7());
        var second = workspace.ResolveSessionFolder(Guid.CreateVersion7());

        Assert.NotEqual(first, second);
        Assert.True(Directory.Exists(first));
    }
}

public class MarkdownTests
{
    private readonly MarkdownRenderer _renderer = new();

    [Fact]
    public void Tables_and_code_survive_rendering()
    {
        var html = _renderer.Render("""
            | Kota | Penduduk |
            |------|----------|
            | Bandung | 2.5 juta |

            ```csharp
            var x = 1;
            ```
            """).Value;

        Assert.Contains("<table>", html);
        Assert.Contains("tp-md__tablewrap", html);
        Assert.Contains("language-csharp", html);
    }

    [Fact]
    public void Script_tags_and_event_handlers_are_stripped()
    {
        // The bot's output is model-generated and lands in the page as raw HTML.
        var html = _renderer.Render("""
            Halo <script>alert('xss')</script> dunia

            <img src="x" onerror="alert(1)" />

            [klik](javascript:alert(1))
            """).Value;

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bare_media_urls_become_playable_elements()
    {
        Assert.Contains("<video", _renderer.Render("https://contoh.com/klip.mp4").Value);
        Assert.Contains("<audio", _renderer.Render("https://contoh.com/lagu.mp3").Value);
        Assert.Contains("<img", _renderer.Render("https://contoh.com/foto.png").Value);
    }

    [Fact]
    public void Preview_strips_markup_and_truncates()
    {
        var preview = MarkdownRenderer.ToPreview("**Tebal** dan _miring_ dengan `kode` " + new string('a', 200), 40);

        Assert.DoesNotContain("**", preview);
        Assert.True(preview.Length <= 41);
    }
}

using Rumble.Net;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Channels", "Query the server with LINQ", "Navigate channels and users with Parent, Children, Descendants and Users.",
    Description = "Channel and User objects are immutable snapshots whose navigation properties read the live, thread-safe model — ideal for LINQ.",
    Order = 25)]
public static class LinqQueriesSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var alice = new RumbleClient(ctx.CreateOptions("alice"));
        await using var bob = new RumbleClient(ctx.CreateOptions("bob"));
        await Task.WhenAll(alice.ConnectAsync(ctx.Token), bob.ConnectAsync(ctx.Token));
        await bob.JoinChannelAsync("AFK", ctx.Token);
        await Task.Delay(200, ctx.Token);

        var server = alice.Server;

        var busiest = server.Channels
            .OrderByDescending(c => c.TotalUserCount)
            .First();
        ctx.Log($"Busiest channel: {busiest.Name} ({busiest.TotalUserCount} users incl. sub-channels)");

        var bots = server.Users.Where(u => u.IsRegistered).Select(u => u.Name);
        ctx.Log($"Registered users: {string.Join(", ", bots)}");

        var afk = server.FindChannelByPath("Root/AFK");
        ctx.Log($"In AFK: {string.Join(", ", afk!.Users.Select(u => u.Name))}");

        var emptyLeaves = server.Root!.Descendants().Where(c => !c.Children.Any() && !c.Users.Any());
        ctx.Log($"Empty leaf channels: {string.Join(", ", emptyLeaves.Select(c => c.Path)).DefaultIfEmpty("none")}");

        var byChannel = server.Users
            .GroupBy(u => u.Channel?.Name ?? "?")
            .Select(g => $"{g.Key}: {g.Count()}");
        ctx.Success($"Users per channel → {string.Join(" · ", byChannel)}");
    }

    private static string DefaultIfEmpty(this string value, string fallback) => value.Length == 0 ? fallback : value;
}

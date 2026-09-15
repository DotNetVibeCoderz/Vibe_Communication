using Rumble.Net;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("User management", "Mute, move and kick users", "Moderate a channel from code and observe the changes from another client.",
    Description = "Moderation actions require the matching ACL permissions on real servers (the demo server allows everything). Use GetPermissionsAsync to check before acting.",
    Order = 110)]
public static class AdminSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var admin = new RumbleClient(ctx.CreateOptions("Moderator"));
        await using var troll = new RumbleClient(ctx.CreateOptions("NoisyNeighbor"));
        await Task.WhenAll(admin.ConnectAsync(ctx.Token), troll.ConnectAsync(ctx.Token));
        await troll.JoinChannelAsync("Lobby", ctx.Token);

        troll.UserUpdated += (_, e) =>
        {
            if (e.User.IsSelf)
            {
                ctx.Note($"NoisyNeighbor sees: muted={e.User.Mute}, deafened={e.User.Deaf} (by {e.Actor?.Name})");
            }
        };
        troll.Kicked += (_, e) => ctx.Warn($"NoisyNeighbor was kicked: {e.Reason}");

        var permissions = await admin.GetPermissionsAsync(admin.Server.Root!, ctx.Token);
        ctx.Log($"Moderator permissions in Root: {permissions}");

        await Task.Delay(200, ctx.Token);
        var target = admin.Server.FindUser("NoisyNeighbor")!;

        admin.MuteUser(target);
        await Task.Delay(300, ctx.Token);

        admin.MoveUser(target, admin.Server.FindChannel("AFK")!);
        await Task.Delay(300, ctx.Token);
        ctx.Log($"NoisyNeighbor is now in {admin.Server.FindUser("NoisyNeighbor")?.Channel?.Name}");

        await admin.KickUserAsync(target, "Please take a break", ctx.Token);
        ctx.Success($"Kicked. Users left: {string.Join(", ", admin.Server.Users.Select(u => u.Name))}");
    }
}

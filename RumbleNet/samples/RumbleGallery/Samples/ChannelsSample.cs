using Rumble.Net;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Channels", "Join, create and remove channels", "Move between channels and manage a temporary channel.",
    Description = "Request/response operations such as JoinChannelAsync and CreateChannelAsync wait for the server's confirmation and throw RumblePermissionDeniedException when ACLs forbid the action.",
    Order = 20)]
public static class ChannelsSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        await using var client = new RumbleClient(ctx.CreateOptions("ChannelHopper"));
        client.UserMoved += (_, e) => ctx.Note($"event: {e.User.Name} moved {e.From?.Name} → {e.To?.Name}");
        await client.ConnectAsync(ctx.Token);

        // Join by name, by path or by Channel object.
        await client.JoinChannelAsync("Lobby", ctx.Token);
        ctx.Success($"Now in {client.Self!.Channel!.Path}");

        var room = await client.CreateChannelAsync(
            parent: client.Server.Root!,
            name: "Gallery Room",
            description: "Created by RumbleGallery",
            temporary: true,
            cancellationToken: ctx.Token);
        ctx.Success($"Created channel #{room.Id} '{room.Name}' under {room.Parent!.Name}");

        await client.JoinChannelAsync(room, ctx.Token);
        ctx.Log($"Users in {room.Name}: {string.Join(", ", room.Users.Select(u => u.Name))}");

        await client.JoinChannelAsync("Lobby", ctx.Token);
        await client.RemoveChannelAsync(room, ctx.Token);
        ctx.Success("Removed the channel again");
    }
}

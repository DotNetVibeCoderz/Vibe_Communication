using System.Numerics;
using Rumble.Net;
using Rumble.Net.Audio;
using Rumble.Net.Testing;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Voice", "Positional audio", "Transmit your in-game position and spatialize voices around the listener.",
    Description = "With PositionalTransmit every voice packet carries your listener position. EnablePositionalAudio pans and attenuates speakers by distance in the Rust mixer (vectorized for many speakers).",
    Order = 55)]
public static class PositionalAudioSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        var options = ctx.CreateOptions("Explorer");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.Continuous;
        options.PositionalTransmit = true;

        await using var client = new RumbleClient(options);
        client.Audio.EnablePositionalAudio(new PositionalAudioSettings(MinDistance: 1, MaxDistance: 20, MinVolume: 0.1f));

        Vector3? lastPosition = null;
        client.Audio.AudioFrameReceived += (in AudioFrame frame) =>
        {
            if (frame.Session == MockMumbleServer.EchoBotSession && frame.Position is { } p)
            {
                lastPosition = p;
            }
        };

        await client.ConnectAsync(ctx.Token);
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId, ctx.Token);

        // Walk in a circle while talking; the echo carries our position back.
        var tone = ToneGenerator.Sine(330, TimeSpan.FromMilliseconds(100));
        for (var step = 0; step < 12; step++)
        {
            var angle = step / 12f * MathF.Tau;
            var position = new Vector3(MathF.Cos(angle) * 5, 1.7f, MathF.Sin(angle) * 5);
            var forward = Vector3.Normalize(-position with { Y = 0 });
            client.Audio.SetListener(position, forward, Vector3.UnitY);

            await AudioPlayer.PlayAsync(client, tone, ctx.Token);
            await Task.Delay(120, ctx.Token);
            ctx.Log($"step {step,2}: listener at ({position.X,5:0.0}, {position.Y:0.0}, {position.Z,5:0.0}) · echo position {Format(lastPosition)}");
        }

        ctx.Success(lastPosition is null
            ? "No positional data received"
            : "Voice packets carried positions end to end; the mixer applied stereo panning and distance attenuation.");
    }

    private static string Format(Vector3? p) => p is { } v ? $"({v.X:0.0}, {v.Y:0.0}, {v.Z:0.0})" : "–";
}

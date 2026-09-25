using Xunit;

namespace VoipNet.Tests;

/// <summary>ZRTP: the two ends agree on keys over the media path and read the same four characters.</summary>
public class ZrtpTests
{
    [Fact]
    public async Task BothSidesEndUpWithTheSameAuthenticationString()
    {
        await using var pair = await LoopbackPair.ConnectAsync("alice", "bob", o =>
        {
            o.Srtp = SrtpMode.Optional;
            o.SrtpKeying = SrtpKeying.Zrtp;
        });

        var announced = new List<string>();
        void Watch(object? _, MediaEventArgs e)
        {
            if (e.Kind == "zrtp-connected")
            {
                lock (announced)
                {
                    announced.Add(e.Detail);
                }
            }
        }

        pair.Caller.MediaNotification += Watch;
        pair.Callee.MediaNotification += Watch;

        await TestHelpers.WaitUntilAsync(
            () => pair.CallerLeg.AuthenticationString is not null && pair.CalleeLeg.AuthenticationString is not null,
            // Agreeing keys means two round trips and an elliptic-curve exchange at each end, which a
            // loaded CI runner can take its time over.
            TimeSpan.FromSeconds(40),
            "both sides finish the ZRTP exchange");

        var caller = pair.CallerLeg.AuthenticationString;
        Assert.Equal(caller, pair.CalleeLeg.AuthenticationString);
        Assert.Equal(4, caller!.Length);

        // The audio that follows is protected with the keys they just agreed.
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 700), 16000);
        await TestHelpers.ReceivedAudioAsync(pair.CalleeLeg, 400);
        Assert.True(pair.CalleeLeg.GetStatistics().SecureRtp, "the stream is encrypted");

        await pair.CallerLeg.HangupAsync();
    }

    [Fact]
    public async Task ACallWithoutZrtpHasNoAuthenticationString()
    {
        await using var pair = await LoopbackPair.ConnectAsync("plain", "peer");

        Assert.Null(pair.CallerLeg.AuthenticationString);

        await pair.CallerLeg.HangupAsync();
    }
}

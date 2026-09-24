using System.Text;
using Xunit;

namespace VoipNet.Tests;

/// <summary>Data channels: SCTP inside the call's DTLS tunnel (RFC 8831), beside the audio.</summary>
public class DataChannelTests
{
    [Fact]
    public async Task ADataChannelCarriesTextAndBinaryMessages()
    {
        await using var pair = await LoopbackPair.ConnectAsync("chatter", "listener", o =>
        {
            o.DataChannels = true;
            o.Srtp = SrtpMode.Mandatory;
            o.SrtpKeying = SrtpKeying.Dtls;
        });

        var received = new List<(ushort Stream, bool Text, byte[] Data)>();
        pair.CalleeLeg.DataMessageReceived += (_, stream, text, data) =>
        {
            lock (received)
            {
                received.Add((stream, text, data.ToArray()));
            }
        };

        var echoed = new List<byte[]>();
        pair.CallerLeg.DataMessageReceived += (_, _, _, data) =>
        {
            lock (echoed)
            {
                echoed.Add(data.ToArray());
            }
        };

        // The channel is asked for straight away and opens once the association finishes its handshake.
        pair.CallerLeg.OpenDataChannel("chat");
        await TestHelpers.WaitUntilAsync(
            () => pair.CallerLeg.DataChannels.Count == 1 && pair.CalleeLeg.DataChannels.Count == 1,
            TimeSpan.FromSeconds(20),
            "both sides see the channel open");

        var channel = pair.CallerLeg.DataChannels[0];
        Assert.Equal("chat", channel.Label);
        Assert.Equal(channel, pair.CalleeLeg.DataChannels[0]);

        pair.CallerLeg.SendData(channel.Stream, "halo dunia");
        await TestHelpers.WaitUntilAsync(
            () =>
            {
                lock (received)
                {
                    return received.Count == 1;
                }
            },
            TimeSpan.FromSeconds(10),
            "the text message arrives");

        lock (received)
        {
            var (stream, text, data) = received[0];
            Assert.Equal(channel.Stream, stream);
            Assert.True(text, "a string is sent as text");
            Assert.Equal("halo dunia", Encoding.UTF8.GetString(data));
        }

        // Binary the other way, longer than one SCTP packet so it is fragmented and reassembled.
        var payload = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
        pair.CalleeLeg.SendData(channel.Stream, payload);
        await TestHelpers.WaitUntilAsync(
            () =>
            {
                lock (echoed)
                {
                    return echoed.Count == 1;
                }
            },
            TimeSpan.FromSeconds(10),
            "the binary message arrives whole");

        lock (echoed)
        {
            Assert.Equal(payload, echoed[0]);
        }

        await pair.CallerLeg.HangupAsync();
    }

    [Fact]
    public async Task ACallWithoutDataChannelsRefusesToSend()
    {
        await using var pair = await LoopbackPair.ConnectAsync("plain", "peer");

        // No m=application was offered, so there is nothing to open a channel on.
        Assert.Throws<VoipException>(() => pair.CallerLeg.OpenDataChannel("chat"));
        Assert.Empty(pair.CallerLeg.DataChannels);

        await pair.CallerLeg.HangupAsync();
    }
}

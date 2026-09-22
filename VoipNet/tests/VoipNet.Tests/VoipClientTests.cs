using System.Collections.Concurrent;
using Xunit;

namespace VoipNet.Tests;

/// <summary>End-to-end tests over the loopback interface, exercising the real engine.</summary>
public sealed class VoipClientTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task VideoCallsCarryFramesAlongsideAudio()
    {
        await using var pair = await LoopbackPair.ConnectAsync("caller", "callee", o => o.Video = true);
        Assert.Equal("H264", pair.CallerLeg.VideoCodec);
        Assert.Equal("H264", pair.CalleeLeg.VideoCodec);

        var received = new List<(uint Timestamp, bool Keyframe, byte[] Data)>();
        pair.CalleeLeg.VideoFrameReceived += (_, timestamp, keyframe, frame) =>
        {
            lock (received)
            {
                received.Add((timestamp, keyframe, frame.ToArray()));
            }
        };

        // An access unit that has to be split across several RTP packets.
        var keyframe = new byte[] { 0, 0, 0, 1, 0x67, 0x42, 0xE0, 0x1F, 0, 0, 0, 1, 0x65 }
            .Concat(Enumerable.Range(0, 3000).Select(i => (byte)(i % 251 | 1)))
            .ToArray();
        for (var i = 0u; i < 10; i++)
        {
            pair.CallerLeg.SendVideoFrame(90000 + (i * 3000), keyframe);
            await Task.Delay(100);
            lock (received)
            {
                if (received.Count > 0)
                {
                    break;
                }
            }
        }

        (uint Timestamp, bool Keyframe, byte[] Data) first;
        lock (received)
        {
            Assert.NotEmpty(received);
            first = received[0];
        }

        Assert.True(first.Keyframe);
        Assert.Equal(keyframe, first.Data);
        await pair.CallerLeg.HangupAsync();
    }

    [Fact]
    public void EngineVersionIsReported()
    {
        Assert.False(string.IsNullOrWhiteSpace(VoipClient.EngineVersion));
    }

    [Fact]
    public async Task CallsCarryAudioDtmfAndStatistics()
    {
        await using var alice = new VoipClient(TestHelpers.LoopbackOptions("alice"));
        await using var bob = new VoipClient(TestHelpers.LoopbackOptions("bob"));
        await alice.StartAsync();
        await bob.StartAsync();

        VoipCall? incoming = null;
        bob.IncomingCall += (_, e) => incoming = e.Call;

        var callTask = alice.CallAsync($"sip:bob@{bob.LocalAddress}");
        var bobCall = await TestHelpers.WaitAsync(() => incoming, Timeout, "incoming call");

        var receivedSamples = 0;
        bobCall.AudioReceived += (_, direction, _, samples) =>
        {
            if (direction == AudioDirection.Inbound)
            {
                Interlocked.Add(ref receivedSamples, samples.Length);
            }
        };
        var digits = new ConcurrentQueue<char>();
        alice.DtmfReceived += (_, e) => digits.Enqueue(e.Digit);

        await bobCall.AnswerAsync();
        var aliceCall = await callTask;

        Assert.Equal(CallState.Connected, aliceCall.State);
        Assert.Equal("opus", aliceCall.Codec);
        Assert.Equal(48000, aliceCall.SampleRate);

        aliceCall.SendAudio(TestHelpers.Tone(16000, 500), 16000);
        bobCall.SendDtmf("7#", 80);

        await TestHelpers.WaitUntilAsync(() => receivedSamples > 4000 && bobCall.GetStatistics().PacketsReceived > 10, Timeout, "audio frames at Bob");
        await TestHelpers.WaitUntilAsync(() => digits.Count == 2, Timeout, "DTMF digits at Alice");
        Assert.Equal("7#", string.Concat(digits));

        var stats = bobCall.GetStatistics();
        Assert.True(stats.PacketsReceived > 10, $"packets: {stats.PacketsReceived}");
        Assert.True(stats.Mos > 3.5, $"MOS: {stats.Mos}");
        Assert.Equal(48000, stats.SampleRate);

        await aliceCall.HangupAsync();
        await TestHelpers.WaitUntilAsync(() => bobCall.State == CallState.Terminated, Timeout, "Bob's call to end");
        Assert.True(aliceCall.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task AudioCanBeStreamedAndRead()
    {
        await using var alice = new VoipClient(TestHelpers.LoopbackOptions("alice"));
        await using var bob = new VoipClient(TestHelpers.LoopbackOptions("bob"));
        await alice.StartAsync();
        await bob.StartAsync();

        VoipCall? incoming = null;
        bob.IncomingCall += (_, e) => incoming = e.Call;
        var callTask = alice.CallAsync($"sip:bob@{bob.LocalAddress}");
        var bobCall = await TestHelpers.WaitAsync(() => incoming, Timeout, "incoming call");
        await bobCall.AnswerAsync();
        var aliceCall = await callTask;

        using var cts = new CancellationTokenSource(Timeout);
        var readTask = Task.Run(async () =>
        {
            var total = 0;
            await foreach (var segment in bobCall.ReadAudioAsync(AudioDirection.Inbound, cts.Token))
            {
                total += segment.Samples.Length;
                if (total > 8000)
                {
                    return total;
                }
            }

            return total;
        });

        aliceCall.SendAudio(TestHelpers.Tone(16000, 1000), 16000);
        var read = await readTask;
        Assert.True(read > 8000, $"read {read} samples");

        // Barge-in: clearing the queue stops queued audio from playing.
        aliceCall.SendAudio(TestHelpers.Tone(16000, 3000), 16000);
        Assert.True(aliceCall.QueuedAudioMs > 1000);
        aliceCall.ClearAudio();
        Assert.True(aliceCall.QueuedAudioMs < 200);

        await aliceCall.HangupAsync();
    }

    [Fact]
    public async Task HoldMuteAndCallListWork()
    {
        await using var alice = new VoipClient(TestHelpers.LoopbackOptions("alice"));
        await using var bob = new VoipClient(TestHelpers.LoopbackOptions("bob"));
        await alice.StartAsync();
        await bob.StartAsync();

        VoipCall? incoming = null;
        bob.IncomingCall += (_, e) => incoming = e.Call;
        var callTask = alice.CallAsync($"sip:bob@{bob.LocalAddress}");
        var bobCall = await TestHelpers.WaitAsync(() => incoming, Timeout, "incoming call");
        await bobCall.AnswerAsync();
        var aliceCall = await callTask;

        aliceCall.SetHold(true);
        await TestHelpers.WaitUntilAsync(() => aliceCall.State == CallState.OnHold, Timeout, "hold");
        await TestHelpers.WaitUntilAsync(() => bobCall.State == CallState.RemoteHold, Timeout, "remote hold");

        aliceCall.SetHold(false);
        await TestHelpers.WaitUntilAsync(() => aliceCall.State == CallState.Connected, Timeout, "resume");

        aliceCall.SetMute(true);
        Assert.True(aliceCall.IsMuted);
        aliceCall.SetMute(false);

        Assert.Contains(alice.Calls, c => c.Id == aliceCall.Id);
        await aliceCall.HangupAsync();
    }

    [Fact]
    public async Task RejectedCallReportsBusy()
    {
        await using var alice = new VoipClient(TestHelpers.LoopbackOptions("alice"));
        await using var bob = new VoipClient(TestHelpers.LoopbackOptions("bob"));
        await alice.StartAsync();
        await bob.StartAsync();

        bob.IncomingCall += (_, e) => e.Call.Reject(486);
        var call = alice.Call($"sip:bob@{bob.LocalAddress}");
        var ended = await call.Completion.WaitAsync(Timeout);

        Assert.Equal(CallState.Terminated, ended.State);
        Assert.Equal(486, ended.StatusCode);
        await Assert.ThrowsAsync<VoipException>(() => call.Connected);
    }

    [Fact]
    public async Task OptionsPingAndInstantMessageRoundTrip()
    {
        await using var alice = new VoipClient(TestHelpers.LoopbackOptions("alice"));
        await using var bob = new VoipClient(TestHelpers.LoopbackOptions("bob"));
        await alice.StartAsync();
        await bob.StartAsync();

        SipMessageEventArgs? received = null;
        bob.MessageReceived += (_, e) => received = e;

        var ping = await alice.PingAsync($"sip:bob@{bob.LocalAddress}");
        Assert.True(ping.IsSuccess, $"ping returned {ping.StatusCode}");

        var sent = await alice.SendMessageAsync($"sip:bob@{bob.LocalAddress}", "Halo dari Voip.NET");
        Assert.True(sent.IsSuccess);
        var message = await TestHelpers.WaitAsync(() => received, Timeout, "instant message");
        Assert.Equal("Halo dari Voip.NET", message.Body);
    }

    [Fact]
    public async Task ConferenceMixesParticipants()
    {
        await using var host = new VoipClient(TestHelpers.LoopbackOptions("host"));
        await using var p1 = new VoipClient(TestHelpers.LoopbackOptions("p1"));
        await using var p2 = new VoipClient(TestHelpers.LoopbackOptions("p2"));
        await host.StartAsync();
        await p1.StartAsync();
        await p2.StartAsync();

        VoipCall? c1 = null;
        VoipCall? c2 = null;
        p1.IncomingCall += (_, e) => { c1 = e.Call; e.Call.AnswerAsync(); };
        p2.IncomingCall += (_, e) => { c2 = e.Call; e.Call.AnswerAsync(); };

        var leg1 = await host.CallAsync($"sip:p1@{p1.LocalAddress}");
        var leg2 = await host.CallAsync($"sip:p2@{p2.LocalAddress}");
        var remote1 = await TestHelpers.WaitAsync(() => c1, Timeout, "first participant");
        var remote2 = await TestHelpers.WaitAsync(() => c2, Timeout, "second participant");

        using var conference = host.CreateConference();
        conference.Add(leg1);
        conference.Add(leg2);
        Assert.Equal(2, conference.Participants.Count);

        var heardByTwo = 0;
        remote2.AudioReceived += (_, direction, _, samples) =>
        {
            if (direction == AudioDirection.Inbound)
            {
                foreach (var s in samples)
                {
                    if (Math.Abs((int)s) > 500)
                    {
                        Interlocked.Increment(ref heardByTwo);
                        break;
                    }
                }
            }
        };

        // Audio from participant one must reach participant two through the bridge.
        remote1.SendAudio(TestHelpers.Tone(16000, 1500), 16000);
        await TestHelpers.WaitUntilAsync(() => heardByTwo > 10, Timeout, "conference audio");

        conference.Remove(leg1);
        await leg1.HangupAsync();
        await leg2.HangupAsync();
    }

    [Fact]
    public async Task TlsSignalingWithPinnedCertificate()
    {
        await using var bob = new VoipClient(Tls(TestHelpers.LoopbackOptions("bob")));
        await bob.StartAsync();
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", bob.TlsFingerprint);

        await using var alice = new VoipClient(Tls(TestHelpers.LoopbackOptions("alice"), bob.TlsFingerprint));
        await alice.StartAsync();
        bob.IncomingCall += async (_, e) => await e.Call.AnswerAsync();

        var call = await alice.CallAsync($"sip:bob@{bob.LocalAddress}").WaitAsync(Timeout);
        Assert.Equal(CallState.Connected, call.State);
        await TestHelpers.WaitUntilAsync(() => call.GetStatistics().SecureRtp, Timeout, "SRTP over the TLS call");
        await call.HangupAsync();

        await using var mallory = new VoipClient(Tls(TestHelpers.LoopbackOptions("mallory"), "00:11:22"));
        await mallory.StartAsync();
        var refused = mallory.Call($"sip:bob@{bob.LocalAddress}");
        var ended = await refused.Completion.WaitAsync(Timeout);
        Assert.Equal(503, ended.StatusCode);

        static VoipClientOptions Tls(VoipClientOptions options, string? pin = null)
        {
            options.Transport = SipTransport.Tls;
            options.Srtp = SrtpMode.Mandatory;
            if (pin is not null)
            {
                options.TlsPinnedFingerprints = [pin];
            }

            return options;
        }
    }

    [Fact]
    public async Task WebSocketSignalingWithDtlsSrtp()
    {
        await using var gateway = new VoipClient(WebRtc(TestHelpers.LoopbackOptions("gateway")));
        await using var browser = new VoipClient(WebRtc(TestHelpers.LoopbackOptions("browser")));
        await gateway.StartAsync();
        await browser.StartAsync();

        var secured = new ConcurrentQueue<string>();
        gateway.MediaNotification += (_, e) =>
        {
            if (e.Kind == "dtls-connected")
            {
                secured.Enqueue(e.Detail);
            }
        };
        VoipCall? incoming = null;
        gateway.IncomingCall += async (_, e) =>
        {
            incoming = e.Call;
            await e.Call.AnswerAsync();
        };

        var call = await browser.CallAsync($"sip:gateway@{gateway.LocalAddress}").WaitAsync(Timeout);
        await TestHelpers.WaitUntilAsync(() => !secured.IsEmpty, Timeout, "DTLS handshake");
        call.SendAudio(TestHelpers.Tone(16000, 500), 16000);
        await TestHelpers.WaitUntilAsync(() => incoming?.GetStatistics().PacketsReceived > 10, Timeout, "encrypted audio at the gateway");
        Assert.True(call.GetStatistics().SecureRtp);
        await call.HangupAsync();

        static VoipClientOptions WebRtc(VoipClientOptions options)
        {
            options.Transport = SipTransport.Ws;
            options.Srtp = SrtpMode.Mandatory;
            options.SrtpKeying = SrtpKeying.Dtls;
            return options;
        }
    }

    [Fact]
    public async Task IceSelectsAPairAndRestarts()
    {
        await using var alice = new VoipClient(Ice(TestHelpers.LoopbackOptions("alice")));
        await using var bob = new VoipClient(Ice(TestHelpers.LoopbackOptions("bob")));
        await alice.StartAsync();
        await bob.StartAsync();

        var selections = new ConcurrentQueue<string>();
        alice.MediaNotification += (_, e) =>
        {
            if (e.Kind == "ice-connected")
            {
                selections.Enqueue(e.Detail);
            }
        };
        bob.IncomingCall += async (_, e) => await e.Call.AnswerAsync();

        var call = await alice.CallAsync($"sip:bob@{bob.LocalAddress}").WaitAsync(Timeout);
        await TestHelpers.WaitUntilAsync(() => selections.Count == 1, Timeout, "ICE pair selection");
        Assert.True(call.GetStatistics().IceConnected);

        call.RestartIce();
        await TestHelpers.WaitUntilAsync(() => selections.Count == 2, Timeout, "selection after ICE restart");
        await call.HangupAsync();

        static VoipClientOptions Ice(VoipClientOptions options)
        {
            options.Ice = true;
            return options;
        }
    }

    [Fact]
    public async Task RtcpReportsRoundTripAndRemoteQuality()
    {
        await using var pair = await LoopbackPair.ConnectAsync();
        pair.CallerLeg.SendAudio(TestHelpers.Tone(48000, 12000), 48000);
        pair.CalleeLeg.SendAudio(TestHelpers.Tone(48000, 12000, 660), 48000);

        // Reports start a second into the call; a round trip needs the second exchange.
        await TestHelpers.WaitUntilAsync(() => pair.CallerLeg.GetStatistics().RemoteMos > 0, TimeSpan.FromSeconds(20), "RTCP reports from the peer");
        var stats = pair.CallerLeg.GetStatistics();
        // Loopback round trips are under a millisecond, which the RTCP clock can report as zero.
        Assert.InRange(stats.RoundTripMs, 0, 500);
        Assert.InRange(stats.RemoteLossPercent, 0, 5);
        Assert.InRange(stats.RemoteJitterMs, 0, 200);
        // RTCP XR carries the MOS the peer measures for what it hears.
        Assert.InRange(stats.RemoteMos, 3.0, 4.5);
    }
}

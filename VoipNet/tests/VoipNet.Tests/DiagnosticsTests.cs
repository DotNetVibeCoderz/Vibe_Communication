using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Net;
using VoipNet.Diagnostics;
using Xunit;

namespace VoipNet.Tests;

public sealed class DiagnosticsTests
{
    private static byte[] Rtp(ushort sequence, uint timestamp, uint ssrc, byte payloadType = 0)
    {
        var packet = new byte[12 + 160];
        packet[0] = 0x80;
        packet[1] = payloadType;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);
        return packet;
    }

    [Fact]
    public void PcapRoundTripFeedsTheRtpAnalyzer()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-{Guid.NewGuid():N}.pcap");
        var source = new IPEndPoint(IPAddress.Parse("10.0.0.1"), 40000);
        var destination = new IPEndPoint(IPAddress.Parse("10.0.0.2"), 50000);
        var start = DateTimeOffset.UtcNow;

        using (var writer = new PcapWriter(path))
        {
            for (ushort seq = 65500, n = 0; n < 100; seq++, n++)
            {
                if (n is 10 or 11 or 50)
                {
                    continue; // three lost packets, across a sequence wrap
                }

                // 20 ms spacing with a little alternating jitter.
                var at = start.AddMilliseconds((n * 20) + (n % 2 == 0 ? 0 : 6));
                writer.WriteUdp(source, destination, Rtp(seq, (uint)(n * 160), 0xABCD), at);
            }

            writer.WriteUdp(source, destination, "OPTIONS sip:x SIP/2.0\r\n\r\n"u8, start);
        }

        var reports = RtpStreamAnalyzer.AnalyzeFile(path);
        File.Delete(path);

        var report = Assert.Single(reports);
        Assert.Equal(0xABCDu, report.Ssrc);
        Assert.Equal("PCMU", report.Codec);
        Assert.Equal(97, report.Packets);
        Assert.Equal(100, report.Expected);
        Assert.Equal(3, report.Lost);
        Assert.InRange(report.JitterMs, 1, 10);
        Assert.InRange(report.Mos, 3.0, 4.5);
        Assert.Equal("10.0.0.1:40000", report.Source.ToString());
    }

    [Fact]
    public async Task SipTraceIsCapturedAndFinalStatisticsSurviveHangup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"voipnet-sip-{Guid.NewGuid():N}.pcap");
        var options = TestHelpers.LoopbackOptions("traced");
        options.TraceSip = true;
        await using var caller = new VoipClient(options);
        await using var callee = new VoipClient(TestHelpers.LoopbackOptions("peer"));
        await caller.StartAsync();
        await callee.StartAsync();
        callee.IncomingCall += (_, e) => _ = e.Call.AnswerAsync();

        var measurements = new List<string>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == VoipMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => { lock (measurements) measurements.Add(instrument.Name); });
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => { lock (measurements) measurements.Add(instrument.Name); });
        listener.Start();

        using (var capture = new PcapWriter(path))
        {
            capture.Attach(caller);
            var call = await caller.CallAsync($"sip:peer@{callee.LocalAddress}");
            call.SendAudio(TestHelpers.Tone(16000, 400), 16000);
            await Task.Delay(700);
            await call.HangupAsync();
            await Task.Delay(200);

            Assert.NotNull(call.FinalStatistics);
            Assert.True(call.FinalStatistics.PacketsSent > 10);
        }

        var sipPackets = PcapReader.ReadUdp(path).Count(d => System.Text.Encoding.ASCII.GetString(d.Payload.Span).Contains("SIP/2.0", StringComparison.Ordinal));
        File.Delete(path);
        Assert.True(sipPackets >= 5, $"captured {sipPackets} SIP packets");

        lock (measurements)
        {
            Assert.Contains("voipnet.calls.started", measurements);
            Assert.Contains("voipnet.calls.answered", measurements);
            Assert.Contains("voipnet.call.duration", measurements);
        }
    }
}

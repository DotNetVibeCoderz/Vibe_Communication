using System.Diagnostics;
using Rumble.Net.Audio;
using Rumble.Net.Testing;

namespace Rumble.Net.Benchmarks;

/// <summary>
/// Measures mouth-to-ear latency through the full stack on localhost: capture pipeline → Opus
/// encode → OCB2-AES → UDP → server relay (EchoBot) → UDP → decrypt → jitter buffer → decode →
/// mixer clock → frame callback. Network latency is ~0 on loopback, so the result is the
/// processing + buffering budget added by Rumble.Net.
/// </summary>
public static class LatencyProbe
{
    public static async Task RunAsync(int rounds)
    {
        using var server = MockMumbleServer.Start();
        var options = server.CreateClientOptions("latency");
        options.Audio.Mode = AudioMode.Headless;
        options.Audio.TransmitMode = TransmitMode.PushToTalk;
        options.Audio.FramesPerPacket = 1;
        await using var client = new RumbleClient(options);

        long sentAt = 0;
        var arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Audio.AudioFrameReceived += (in AudioFrame frame) =>
        {
            if (frame.Session == MockMumbleServer.EchoBotSession && !frame.IsConcealed && frame.Rms > 0.05f)
            {
                arrived.TrySetResult(Stopwatch.GetTimestamp());
            }
        };

        await client.ConnectAsync();
        await client.JoinChannelAsync(MockMumbleServer.LobbyChannelId);
        await Task.Delay(1500); // let UDP become active

        var silence = new float[AudioMath.FrameSize];
        var burst = ToneGenerator.Sine(1000, TimeSpan.FromMilliseconds(10), 0.8f);
        var samples = new List<double>();

        for (var i = 0; i < rounds; i++)
        {
            arrived = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Audio.PushToTalk = true;

            // A 100 ms tone paced like a real microphone. The clock starts at the first sample, so the
            // result includes Opus look-ahead, the jitter buffer's playout delay and the mixer clock.
            sentAt = Stopwatch.GetTimestamp();
            var frameClock = Stopwatch.StartNew();
            for (var k = 0; k < 10; k++)
            {
                client.Audio.SendPcm(burst);
                var due = TimeSpan.FromMilliseconds(10 * (k + 1)) - frameClock.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due);
                }
            }

            try
            {
                var end = await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
                samples.Add(Stopwatch.GetElapsedTime(sentAt, end).TotalMilliseconds);
            }
            catch (TimeoutException)
            {
                Console.WriteLine($"round {i}: no echo");
            }

            client.Audio.PushToTalk = false;
            client.Audio.SendPcm(silence);
            await Task.Delay(400);
        }

        if (samples.Count == 0)
        {
            Console.WriteLine("No measurements.");
            return;
        }

        samples.Sort();
        double P(double q) => samples[(int)Math.Clamp(Math.Round(q * (samples.Count - 1)), 0, samples.Count - 1)];
        Console.WriteLine($"Rounds: {samples.Count}  transport: {(client.Server.UdpActive ? "UDP" : "TCP")}");
        Console.WriteLine($"Latency ms  min {samples[0]:0.0}  p50 {P(0.5):0.0}  p95 {P(0.95):0.0}  max {samples[^1]:0.0}");
        Console.WriteLine(P(0.95) < 50 ? "PASS: p95 below the 50 ms target" : "WARN: p95 above the 50 ms target");
    }
}

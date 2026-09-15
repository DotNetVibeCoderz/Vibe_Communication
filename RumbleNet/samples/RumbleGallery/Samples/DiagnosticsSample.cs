using Microsoft.Extensions.Logging;
using Rumble.Net;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Diagnostics", "Logging with ILogger", "Route SDK and native core logs through Microsoft.Extensions.Logging.",
    Description = "Set RumbleClientOptions.LoggerFactory to receive SDK logs and the Rust core's tracing output (category Rumble.Native) in your existing logging pipeline.",
    Order = 120)]
public static class LoggingSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        using var loggerFactory = ctx.CreateLoggerFactory(LogLevel.Debug);

        var options = ctx.CreateOptions("Logger");
        options.LoggerFactory = loggerFactory;
        options.NativeLogLevel = LogLevel.Debug;
        options.Audio.Mode = AudioMode.Headless;

        await using var client = new RumbleClient(options);
        await client.ConnectAsync(ctx.Token);
        await Task.Delay(500, ctx.Token);
        await client.DisconnectAsync(ctx.Token);

        RumbleNative.ConfigureLogging(null);
        ctx.Success("Logs above came from Rumble.Net (.NET) and rumble_native (Rust)");
    }
}

[Sample("Diagnostics", "Ping a server without connecting", "Query version, user count and latency like a server browser.",
    Description = "QueryServerAsync sends Mumble's lightweight UDP info ping. Use it to show live status in server lists.",
    Order = 125)]
public static class QueryServerSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        for (var i = 1; i <= 5; i++)
        {
            var info = await RumbleClient.QueryServerAsync(ctx.Server.Host, ctx.Server.Port, TimeSpan.FromSeconds(2), ctx.Token);
            ctx.Log($"#{i}: Mumble {info.Version} · {info.Users}/{info.MaxUsers} users · {info.MaxBandwidth / 1000} kbit/s · {info.PingMs:0.00} ms");
            await Task.Delay(200, ctx.Token);
        }

        ctx.Success("Server is responding");
    }
}

[Sample("Diagnostics", "Native benchmarks", "Measure crypto, Opus and mixer throughput on this machine.",
    Description = "The Rust core ships micro-benchmarks for its hot paths. The full Criterion and BenchmarkDotNet suites live in native/ and benchmarks/.",
    Order = 130)]
public static class BenchmarkSample
{
    public static Task RunAsync(SampleContext ctx)
    {
        ctx.Log("Running (a few seconds)…");
        foreach (var result in RumbleNative.RunBenchmarks())
        {
            var perOp = result.NanosecondsPerOperation >= 1000
                ? $"{result.NanosecondsPerOperation / 1000:0.00} µs"
                : $"{result.NanosecondsPerOperation:0} ns";
            ctx.Log($"{result.Name}");
            ctx.Success($"    {perOp}/{result.Unit} · {result.OperationsPerSecond:N0} {result.Unit}s/s");
        }

        ctx.Note("A 10 ms voice frame budget is 10 000 µs.");
        return Task.CompletedTask;
    }
}

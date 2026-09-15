using BenchmarkDotNet.Running;
using Rumble.Net.Benchmarks;

// Usage:
//   dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks                 # pick benchmarks interactively
//   dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- --filter *   # run everything
//   dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- latency      # end-to-end voice latency probe
//   dotnet run -c Release --project benchmarks/Rumble.Net.Benchmarks -- native       # Rust core micro-benchmarks

if (args.Length > 0 && args[0] == "latency")
{
    await LatencyProbe.RunAsync(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 20);
    return;
}

if (args.Length > 0 && args[0] == "native")
{
    foreach (var r in Rumble.Net.RumbleNative.RunBenchmarks())
    {
        Console.WriteLine($"{r.Name,-60} {r.NanosecondsPerOperation / 1000,10:0.000} µs/{r.Unit}  {r.OperationsPerSecond,14:N0} {r.Unit}s/s");
    }

    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

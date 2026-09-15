using System.Text;
using BenchmarkDotNet.Attributes;
using Rumble.Net.Audio;
using Rumble.Net.Codecs;
using Rumble.Net.Interop;
using Rumble.Net.Testing;

namespace Rumble.Net.Benchmarks;

/// <summary>Cost of the managed side of the event pipeline (native JSON → typed event).</summary>
[MemoryDiagnoser]
public class EventParsingBenchmarks
{
    private readonly byte[] _talking = Encoding.UTF8.GetBytes("""{"type":"UserTalking","session":42,"talking":true}""");
    private readonly byte[] _userJoined = Encoding.UTF8.GetBytes(
        """{"type":"UserJoined","user":{"session":7,"userId":12,"name":"alice","channelId":3,"mute":false,"deaf":false,"suppress":false,"selfMute":true,"selfDeaf":false,"prioritySpeaker":false,"recording":false,"comment":"hello","commentHash":null,"texture":null,"textureHash":null,"certificateHash":"0123456789abcdef0123456789abcdef01234567","listeningChannels":[]}}""");

    [Benchmark(Baseline = true)]
    public object? ParseUserTalking() => RumbleClient.ParseEvent(_talking);

    [Benchmark]
    public object? ParseUserJoined() => RumbleClient.ParseEvent(_userJoined);

    [Benchmark]
    public int SerializeCommand() => Native.Json<RumbleCommand>(new SendTextMessageCommand([1], [], [], "hello world"), RumbleJsonContext.Default.RumbleCommand).Span.Length;
}

/// <summary>SIMD audio helpers used by filters and meters.</summary>
public class AudioMathBenchmarks
{
    private readonly float[] _frame = ToneGenerator.Sine(440, TimeSpan.FromMilliseconds(10));

    [Benchmark(Baseline = true)]
    public float ScalarRms()
    {
        var sum = 0f;
        foreach (var s in _frame)
        {
            sum += s * s;
        }

        return MathF.Sqrt(sum / _frame.Length);
    }

    [Benchmark]
    public float VectorizedRms() => AudioMath.Rms(_frame);

    [Benchmark]
    public void VectorizedGain() => AudioMath.ApplyGain(_frame, 0.999f);
}

/// <summary>P/Invoke + native Opus cost per 10 ms frame.</summary>
[MemoryDiagnoser]
public class OpusInteropBenchmarks
{
    private readonly float[] _frame = ToneGenerator.Sine(440, TimeSpan.FromMilliseconds(10));
    private readonly byte[] _packet = new byte[1500];
    private readonly float[] _pcm = new float[OpusDecoder.MaxFrameSamples];
    private OpusEncoder _encoder = null!;
    private OpusDecoder _decoder = null!;
    private int _packetLength;

    [GlobalSetup]
    public void Setup()
    {
        _encoder = new OpusEncoder(bitrate: 48_000);
        _decoder = new OpusDecoder();
        _packetLength = _encoder.Encode(_frame, _packet);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }

    [Benchmark]
    public int Encode10ms() => _encoder.Encode(_frame, _packet);

    [Benchmark]
    public int Decode10ms() => _decoder.Decode(_packet.AsSpan(0, _packetLength), _pcm);
}

/// <summary>Commands and snapshots against a live (in-process) server.</summary>
[MemoryDiagnoser]
public class ClientBenchmarks
{
    private MockMumbleServer _server = null!;
    private RumbleClient _client = null!;

    [GlobalSetup]
    public void Setup()
    {
        _server = MockMumbleServer.Start();
        _client = new RumbleClient(_server.CreateClientOptions("bench"));
        _client.ConnectAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _server.Dispose();
    }

    [Benchmark]
    public void SendChannelMessage() => _client.SendChannelMessage(_client.Server.Root!, "benchmark");

    [Benchmark]
    public int GetSnapshot() => _client.GetSnapshot().Channels.Count;

    [Benchmark]
    public int LinqTreeWalk() => _client.Server.Root!.DescendantsAndSelf().Sum(c => c.Users.Count());
}

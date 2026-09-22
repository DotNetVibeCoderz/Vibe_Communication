namespace VoipNet.Tests;

/// <summary>Helpers shared by the integration tests: two loopback clients that call each other.</summary>
public static class TestHelpers
{
    private static int _portSeed = 31000;

    public static VoipClientOptions LoopbackOptions(string user) => new()
    {
        BindAddress = "127.0.0.1",
        SipPort = 0,
        Username = user,
        DisplayName = user.ToUpperInvariant(),
        RtpPortMin = Interlocked.Add(ref _portSeed, 200),
        RtpPortMax = _portSeed + 199,
        JitterMinMs = 20,
    };

    public static short[] Tone(int sampleRate, int milliseconds, double frequency = 440, double amplitude = 9000)
    {
        var samples = new short[sampleRate * milliseconds / 1000];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)(amplitude * Math.Sin(2 * Math.PI * frequency * i / sampleRate));
        }

        return samples;
    }

    /// <summary>
    /// Waits until a call has received at least <paramref name="milliseconds"/> of inbound audio.
    /// Media is paced one frame per packet time, and a loaded CI machine paces it slower than the wall
    /// clock, so tests wait for the audio itself instead of sleeping a fixed time.
    /// </summary>
    public static async Task<int> ReceivedAudioAsync(VoipCall call, int milliseconds, int timeoutMs = 20000)
    {
        var received = 0;
        void OnAudio(VoipCall _, AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples)
        {
            if (direction == AudioDirection.Inbound)
            {
                Interlocked.Add(ref received, samples.Length * 1000 / Math.Max(sampleRate, 1));
            }
        }

        call.AudioReceived += OnAudio;
        try
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (Volatile.Read(ref received) < milliseconds && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            return Volatile.Read(ref received);
        }
        finally
        {
            call.AudioReceived -= OnAudio;
        }
    }

    /// <summary>Waits for a probe that has to await something itself, for example a database read.</summary>
    public static async Task<T> WaitAsync<T>(Func<Task<T?>> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await probe() is { } value)
            {
                return value;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public static async Task<T> WaitAsync<T>(Func<T?> probe, TimeSpan timeout, string what) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }

    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}

namespace RumbleApp.Services;

/// <summary>
/// Rolling history of one speaker's voice level, fed from the audio thread and sampled by the UI.
/// The audio thread only writes a single float (the running peak), so no locks are needed.
/// </summary>
public sealed class VoiceTrace
{
    public const int Length = 36;

    private readonly float[] _levels = new float[Length];
    private int _head;
    private int _pendingPeakBits;

    /// <summary>Called from the audio thread with a frame RMS (0–1).</summary>
    public void Report(float rms)
    {
        var current = BitConverter.Int32BitsToSingle(Volatile.Read(ref _pendingPeakBits));
        if (rms > current)
        {
            Volatile.Write(ref _pendingPeakBits, BitConverter.SingleToInt32Bits(rms));
        }
    }

    /// <summary>Called on the UI clock: commits the peak since the last tick. Returns true if audible.</summary>
    public bool Tick()
    {
        var peak = BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _pendingPeakBits, 0));
        // Map −60 … 0 dBFS to 0 … 1.
        var db = peak <= 1e-5f ? -96f : 20f * MathF.Log10(peak);
        var normalized = Math.Clamp((db + 60f) / 60f, 0f, 1f);
        _levels[_head] = normalized;
        _head = (_head + 1) % Length;
        return normalized > 0.02f;
    }

    /// <summary>Pushes a level directly (UI thread), e.g. the local microphone meter.</summary>
    public void PushDecibels(float db) => Report(db <= -96f ? 0f : MathF.Pow(10f, db / 20f));

    /// <summary>Levels oldest → newest.</summary>
    public IEnumerable<float> Levels()
    {
        for (var i = 0; i < Length; i++)
        {
            yield return _levels[(_head + i) % Length];
        }
    }

    public bool IsSilent => _levels.All(l => l < 0.02f);
}

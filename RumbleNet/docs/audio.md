# Audio

Mumble audio is always **48 kHz**, and the pipeline works in **10 ms frames of 480 samples**.

## Modes

| `AudioMode` | Input | Output | Use for |
|---|---|---|---|
| `Disabled` (default) | none | none | Text-only tools and dashboards |
| `Headless` | `Audio.SendPcm(float[] or short[])` | `Audio.AudioFrameReceived` on a 10 ms clock | Bots, recording, servers, tests |
| `Devices` | microphone | speakers, via WASAPI, ALSA/PulseAudio or CoreAudio | Voice chat apps |

```csharp
options.Audio.Mode = AudioMode.Devices;
options.Audio.InputDeviceId = RumbleNative.GetAudioDevices().First(d => d.IsInput).Id; // null = system default
client.Audio.SetMode(AudioMode.Headless);   // switch at runtime; the pipeline is rebuilt
```

## Transmitting

| Option | Default | Meaning |
|---|---|---|
| `TransmitMode` | `VoiceActivity` | `Continuous`, `VoiceActivity` or `PushToTalk`. `Audio.PushToTalk = true/false` sets the key state. |
| `Bitrate` | 48 000 | Opus bits per second (8 000–510 000) |
| `FramesPerPacket` | 2 | 1, 2, 4 or 6 × 10 ms per packet. Fewer frames per packet means lower latency; more means less overhead. |
| `Complexity` | 8 | Opus encoder complexity, 0–10 |
| `InbandFec`, `ExpectedPacketLoss` | on, 5 % | Opus forward error correction |
| `VoiceActivityThresholdDb`, `VoiceActivityHoldFrames` | −45 dB, 25 | VAD trigger level and hangover (250 ms) |
| `NoiseGateDb` | off | Built-in noise gate |
| `DcFilter` | on | Removes microphone DC offset |

These options are applied when the client connects, or when you call `Audio.ApplyOptions()`. `TransmitMode`, `PushToTalk`, `VoiceTarget` and `MasterVolume` take effect immediately.

`Audio.VoiceTarget` selects where voice goes:

- `0`: normal talking
- `1–30`: a target registered with `RegisterVoiceTarget` (whisper or shout)
- `31`: server loopback (you hear yourself; useful for testing)

`EndTransmission()` sends the terminator packet immediately.

Meters: `Audio.InputLevelDb`, `Audio.IsTransmitting`.

## Receiving

```csharp
client.Audio.AudioFrameReceived += (in AudioFrame f) =>
{
    // Runs on the audio thread. Don't block, and don't keep f.Samples after returning.
    if (!f.IsConcealed) meter[f.Session] = f.LevelDb;
};
client.Audio.SetUserVolume(user, 1.5f);   // local only
client.Audio.SetUserMuted(user, true);    // local only
client.Audio.MasterVolume = 0.8f;
```

Each speaker has its own **adaptive jitter buffer**. Its target delay tracks three standard deviations of the RFC 3550 inter-arrival jitter, bounded to 20–200 ms. Missing packets are concealed with Opus PLC, and a stream ends after a terminator packet or 150 ms of silence.

## Capture filters

Filters run in place on each 10 ms frame, before encoding:

```csharp
client.Audio.AddCaptureFilter(new GainFilter(+6));
client.Audio.AddCaptureFilter(new NoiseGateFilter(-50));
client.Audio.AddCaptureFilter(new LimiterFilter(-1));
client.Audio.AddCaptureFilter(new DelegateAudioFilter(s => AudioMath.ApplyGain(s, 0.5f)));

sealed class Robot : IAudioFilter
{
    private int _phase;
    public void Process(Span<float> s) { for (var i = 0; i < s.Length; i++) s[i] *= MathF.Sin(_phase++ * 0.05f) > 0 ? 1 : 0.2f; }
}
```

`AudioMath` provides SIMD-accelerated `Rms`, `ApplyGain`, `Peak`, dB conversions and PCM16 conversion.

## Positional audio

```csharp
options.PositionalTransmit = true;                                   // send your position with your voice
client.Audio.EnablePositionalAudio(new PositionalAudioSettings(MinDistance: 1, MaxDistance: 25));
client.Audio.SetListener(position, forward, up);                     // call every game frame
```

Coordinates are in meters, left-handed: +X right, +Y up, +Z forward. The same convention as Unity. The mixer applies equal-power stereo panning, linear distance attenuation down to `MinVolume`, and `RearAttenuation` for sources behind the listener. It computes the gains for all speakers in one auto-vectorized pass, which takes about 5 µs for 256 sources.

## Helpers

- `ToneGenerator.Sine` and `ToneGenerator.Melody` generate test signals.
- `WaveFile.ReadMono48k` and `WaveFile.WriteMono48k` read and write WAV files, resampling and downmixing on read.
- `AudioPlayer.PlayAsync(client, samples)` and `PlayWaveAsync(client, path)` stream audio in real time.

## Opus codec

```csharp
using var enc = new OpusEncoder(channels: 1, bitrate: 32_000);
using var dec = new OpusDecoder();
int bytes = enc.Encode(pcm20ms, packet);
int samples = dec.Decode(packet.AsSpan(0, bytes), output);
dec.Conceal(output.AsSpan(0, 960));        // packet lost
```

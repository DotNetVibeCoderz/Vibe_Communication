using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoipNet.Audio;
using VoipNet.Samples.Theme;
using VoipNet.Softphone.Services;

namespace VoipNet.Softphone.ViewModels;

/// <summary>The call on screen: its state, quality and the controls the user has over it.</summary>
public sealed partial class CallViewModel : ObservableObject
{
    private readonly LevelHistory _inbound = new(140);
    private readonly LevelHistory _outbound = new(140);
    private CallAudioBridge? _bridge;
    private CallRecorder? _recorder;
    private CallVideo? _video;

    public CallViewModel(VoipCall call, string? displayName)
    {
        Call = call;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Friendly(call.RemoteUri) : displayName;
        Address = call.RemoteUri;
        State = call.State;
        call.AudioReceived += OnAudio;
    }

    public VoipCall Call { get; }

    public string DisplayName { get; }

    public string Address { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsRinging), nameof(IsConnected), nameof(IsEnded), nameof(IsIncoming), nameof(CanControl))]
    public partial CallState State { get; set; }

    [ObservableProperty]
    public partial string Duration { get; set; } = "00:00";

    [ObservableProperty]
    public partial string Quality { get; set; } = "—";

    [ObservableProperty]
    public partial string CodecLabel { get; set; } = "negotiating";

    [ObservableProperty]
    public partial double[] InboundLevels { get; set; } = [];

    [ObservableProperty]
    public partial double[] OutboundLevels { get; set; } = [];

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsOnHold { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial bool ShowKeypad { get; set; }

    [ObservableProperty]
    public partial string TransferTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SentDigits { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? RecordingPath { get; set; }

    [ObservableProperty]
    public partial bool IsCameraOn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideo), nameof(HasRemotePicture))]
    public partial Bitmap? RemotePicture { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowVideo))]
    public partial Bitmap? LocalPicture { get; set; }

    /// <summary>Whether this machine can do video at all, and the call agreed to carry it.</summary>
    public bool VideoAvailable => CallVideo.IsSupported && Call.VideoCodec is { Length: > 0 };

    /// <summary>Whether to give the picture the middle of the panel.</summary>
    public bool ShowVideo => RemotePicture is not null || LocalPicture is not null;

    /// <summary>Whether the far end's picture has arrived, as opposed to only ours going out.</summary>
    public bool HasRemotePicture => RemotePicture is not null;

    public bool IsRinging => State is CallState.Calling or CallState.Ringing or CallState.EarlyMedia;

    public bool IsIncoming => State == CallState.Incoming;

    public bool IsConnected => State is CallState.Connected or CallState.OnHold or CallState.RemoteHold;

    public bool IsEnded => State == CallState.Terminated;

    public bool CanControl => IsConnected;

    public string StateLabel => State switch
    {
        CallState.Calling => "CALLING",
        CallState.Ringing => "RINGING",
        CallState.EarlyMedia => "EARLY MEDIA",
        CallState.Incoming => "INCOMING",
        CallState.Connected => "CONNECTED",
        CallState.OnHold => "ON HOLD",
        CallState.RemoteHold => "HELD BY OTHER PARTY",
        _ => "ENDED",
    };

    /// <summary>Connects the local microphone and speakers once media is flowing.</summary>
    public void AttachAudioDevices(bool enabled)
    {
        if (enabled && _bridge is null && IsConnected)
        {
            _bridge = CallAudioBridge.Attach(Call);
        }
    }

    public void Refresh()
    {
        State = Call.State;
        InboundLevels = _inbound.Snapshot();
        OutboundLevels = _outbound.Snapshot();
        Duration = Call.Duration.ToString(Call.Duration.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        OnPropertyChanged(nameof(VideoAvailable));
        if (Call.Codec is { } codec)
        {
            CodecLabel = $"{codec} · {Call.SampleRate / 1000.0:0.#} kHz";
        }

        if (IsConnected)
        {
            try
            {
                var stats = Call.GetStatistics();
                Quality = stats.PacketsReceived == 0
                    ? "waiting for audio"
                    : $"MOS {stats.Mos:0.0} · loss {stats.LossPercent:0.0}% · jitter {stats.JitterMs:0} ms{(stats.SecureRtp ? " · SRTP" : string.Empty)}";
            }
            catch (VoipException)
            {
                Quality = "—";
            }
        }
        else if (IsEnded && Call.FinalStatistics is { } final)
        {
            Quality = $"final MOS {final.Mos:0.0} · loss {final.LossPercent:0.0}%";
        }
    }

    [RelayCommand]
    private Task Answer() => Call.AnswerAsync();

    [RelayCommand]
    private void Decline() => Call.Reject(603);

    [RelayCommand]
    private void Hangup()
    {
        if (!IsEnded)
        {
            Call.Hangup();
        }
    }

    [RelayCommand]
    private void ToggleMute()
    {
        IsMuted = !IsMuted;
        Call.SetMute(IsMuted);
    }

    [RelayCommand]
    private void ToggleHold()
    {
        IsOnHold = !IsOnHold;
        Call.SetHold(IsOnHold);
    }

    [RelayCommand]
    private void ToggleKeypad() => ShowKeypad = !ShowKeypad;

    [RelayCommand]
    private void SendDigit(string digit)
    {
        if (!CanControl)
        {
            return;
        }

        SentDigits += digit;
        Call.SendDtmf(digit);
    }

    [RelayCommand]
    private void Transfer()
    {
        if (CanControl && !string.IsNullOrWhiteSpace(TransferTarget))
        {
            Call.Transfer(TransferTarget.Trim());
        }
    }

    /// <summary>Turns the camera on, which starts sending it, or off, which puts its light out.</summary>
    [RelayCommand]
    private void ToggleCamera()
    {
        if (_video is null)
        {
            if (!VideoAvailable)
            {
                return;
            }

            _video = new CallVideo(Call);
            _video.Changed += OnPictureChanged;
            _video.Start();
            IsCameraOn = true;
        }
        else
        {
            _video.Changed -= OnPictureChanged;
            _video.Dispose();
            _video = null;
            IsCameraOn = false;
            LocalPicture = null;
        }
    }

    private void OnPictureChanged()
    {
        if (_video is null)
        {
            return;
        }

        RemotePicture = _video.Remote;
        LocalPicture = _video.Local;
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (_recorder is null)
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Voip.NET");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, $"call-{DateTime.Now:yyyyMMdd-HHmmss}.mp3");
            _recorder = CallRecorder.Start(Call, file, RecordingFormat.Mp3);
            RecordingPath = _recorder.Path;
            IsRecording = true;
        }
        else
        {
            _recorder.Dispose();
            _recorder = null;
            IsRecording = false;
        }
    }

    public void Release()
    {
        Call.AudioReceived -= OnAudio;
        if (_video is not null)
        {
            _video.Changed -= OnPictureChanged;
            _video.Dispose();
            _video = null;
        }

        _bridge?.Dispose();
        _recorder?.Dispose();
    }

    private void OnAudio(VoipCall call, AudioDirection direction, int sampleRate, ReadOnlySpan<short> samples)
    {
        var level = Pcm.Rms(samples);
        (direction == AudioDirection.Inbound ? _inbound : _outbound).Push(level);
    }

    private static string Friendly(string uri)
    {
        var value = uri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? uri[4..] : uri;
        var at = value.IndexOf('@');
        return at > 0 ? value[..at] : value;
    }
}

/// <summary>A finished call in the history list.</summary>
public sealed record RecentCall(string Name, string Address, bool Outgoing, string Duration, string When, bool Missed)
{
    public string Direction => Outgoing ? "OUT" : Missed ? "MISSED" : "IN";
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.AI;
using VoipNet.AI.Llm;
using VoipNet.Softphone.Services;

namespace VoipNet.Softphone.ViewModels;

/// <summary>State and behaviour of the softphone window.</summary>
public sealed partial class SoftphoneViewModel : ObservableObject, IAsyncDisposable
{
    private VoipClient? _client;
    private DemoBots? _bots;
    private readonly Avalonia.Threading.DispatcherTimer _timer;

    public SoftphoneViewModel()
    {
        _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _timer.Tick += (_, _) => ActiveCall?.Refresh();
    }

    public ObservableCollection<RecentCall> Recent { get; } = [];

    public string EngineVersion => $"engine {VoipClient.EngineVersion}";

    // ---- Account ------------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool DemoMode { get; set; } = true;

    [ObservableProperty]
    public partial string Domain { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Username { get; set; } = "1001";

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = "Softphone";

    [ObservableProperty]
    public partial bool UseTcp { get; set; }

    [ObservableProperty]
    public partial bool UseSrtp { get; set; }

    [ObservableProperty]
    public partial bool UseAudioDevices { get; set; } = true;

    [ObservableProperty]
    public partial bool IsSettingsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline))]
    public partial string Status { get; set; } = "OFFLINE";

    [ObservableProperty]
    public partial string Identity { get; set; } = "not connected";

    public bool IsOnline => Status is "READY" or "REGISTERED";

    // ---- Dialling -----------------------------------------------------------------------------

    [ObservableProperty]
    public partial string DialTarget { get; set; } = "echo";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActiveCall), nameof(IsIdle))]
    public partial CallViewModel? ActiveCall { get; set; }

    public bool HasActiveCall => ActiveCall is not null;

    public bool IsIdle => ActiveCall is null;

    [ObservableProperty]
    public partial string? Error { get; set; }

    // ---- AI notes -----------------------------------------------------------------------------

    [ObservableProperty]
    public partial string AiEndpoint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiKey { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AiModel { get; set; } = "gpt-5-mini";

    [ObservableProperty]
    public partial string Notes { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Summary { get; set; } = "Write notes during the call, then let AI turn them into a summary and follow-ups.";

    [ObservableProperty]
    public partial bool IsSummarizing { get; set; }

    public async Task StartAsync()
    {
        await ConnectAsync();
    }

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private async Task ConnectAsync()
    {
        Error = null;
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }

        try
        {
            if (DemoMode)
            {
                _bots ??= await DemoBots.StartAsync();
            }

            var options = new VoipClientOptions
            {
                BindAddress = DemoMode ? "127.0.0.1" : "0.0.0.0",
                SipPort = 0,
                Domain = DemoMode ? string.Empty : Domain,
                Username = Username,
                Password = Password,
                DisplayName = DisplayName,
                Transport = UseTcp ? SipTransport.Tcp : SipTransport.Udp,
                Srtp = UseSrtp ? SrtpMode.Optional : SrtpMode.Disabled,
                // Offer video only where there is a codec to encode it with.
                Video = Services.CallVideo.IsSupported,
                EventSynchronizationContext = SynchronizationContext.Current,
            };

            _client = new VoipClient(options);
            _client.IncomingCall += OnIncomingCall;
            await _client.StartAsync();

            if (!DemoMode && !string.IsNullOrWhiteSpace(Domain))
            {
                Status = "REGISTERING";
                var result = await _client.RegisterAsync();
                Status = result.State == RegistrationState.Registered ? "REGISTERED" : "FAILED";
                Identity = result.State == RegistrationState.Registered
                    ? $"{Username}@{Domain}"
                    : $"{result.StatusCode} {result.Reason}";
            }
            else
            {
                Status = "READY";
                Identity = DemoMode ? $"demo · dial echo or music" : _client.LocalAddress;
            }

            IsSettingsOpen = false;
        }
        catch (Exception ex) when (ex is VoipException or InvalidOperationException or TimeoutException)
        {
            Status = "OFFLINE";
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void Key(string digit)
    {
        if (ActiveCall is { CanControl: true } call)
        {
            call.SendDigitCommand.Execute(digit);
        }
        else
        {
            DialTarget = (DialTarget == "echo" ? string.Empty : DialTarget) + digit;
        }
    }

    [RelayCommand]
    private void Backspace()
    {
        if (DialTarget.Length > 0)
        {
            DialTarget = DialTarget[..^1];
        }
    }

    [RelayCommand]
    private void Dial()
    {
        if (_client is null || ActiveCall is not null || string.IsNullOrWhiteSpace(DialTarget))
        {
            return;
        }

        Error = null;
        var target = DialTarget.Trim();
        if (DemoMode && _bots is not null && !target.Contains(':', StringComparison.Ordinal) && !target.Contains('@', StringComparison.Ordinal))
        {
            target = _bots.UriFor(target);
        }

        try
        {
            Track(new CallViewModel(_client.Call(target), null));
        }
        catch (VoipException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SummarizeAsync()
    {
        if (string.IsNullOrWhiteSpace(Notes))
        {
            Summary = "Add a few notes first — who called, what they needed, what you promised.";
            return;
        }

        if (string.IsNullOrWhiteSpace(AiEndpoint) || string.IsNullOrWhiteSpace(AiKey))
        {
            Summary = "Add an OpenAI-compatible endpoint and key in Settings to summarise with AI.";
            return;
        }

        IsSummarizing = true;
        try
        {
            var options = AiEndpoint.Contains("azure.com", StringComparison.OrdinalIgnoreCase)
                ? OpenAiChatOptions.ForAzure(AiEndpoint, AiKey, AiModel)
                : new OpenAiChatOptions { BaseUri = new Uri(AiEndpoint.TrimEnd('/') + "/"), ApiKey = AiKey, Model = AiModel };
            using var chat = new OpenAiChatClient(options);
            var context = ActiveCall is { } call ? $"Call with {call.DisplayName} ({call.Address}), duration {call.Duration}." : "Call notes.";
            var response = await chat.GetResponseAsync(
                [new ChatMessage(ChatRole.User, $"{context}\n\nNotes:\n{Notes}")],
                new ChatOptions
                {
                    Instructions = "Summarise these call-centre notes in the notes' language: three short bullet points, then 'Follow-up:' with concrete next steps.",
                    MaxOutputTokens = 1500,
                });
            Summary = response.Text.Trim();
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or TaskCanceledException)
        {
            Summary = $"The summary could not be created: {ex.Message}";
        }
        finally
        {
            IsSummarizing = false;
        }
    }

    [RelayCommand]
    private void DismissCall()
    {
        if (ActiveCall is { IsEnded: true } ended)
        {
            ended.Release();
            ActiveCall = null;
            _timer.Stop();
        }
    }

    private void OnIncomingCall(object? sender, IncomingCallEventArgs e)
    {
        if (ActiveCall is not null)
        {
            e.Call.Reject(486);
            Recent.Insert(0, new RecentCall(e.DisplayName ?? e.From, e.From, false, "busy", DateTime.Now.ToString("HH:mm"), Missed: true));
            return;
        }

        Track(new CallViewModel(e.Call, e.DisplayName));
    }

    private void Track(CallViewModel vm)
    {
        ActiveCall = vm;
        _timer.Start();
        vm.Call.StateChanged += (_, e) =>
        {
            vm.State = e.State;
            if (e.State == CallState.Connected)
            {
                vm.AttachAudioDevices(UseAudioDevices);
            }

            if (e.State == CallState.Terminated)
            {
                vm.Refresh();
                Recent.Insert(0, new RecentCall(
                    vm.DisplayName,
                    vm.Address,
                    vm.Call.IsOutgoing,
                    vm.Call.ConnectedAt is null ? $"{e.StatusCode}" : vm.Duration,
                    DateTime.Now.ToString("HH:mm"),
                    Missed: !vm.Call.IsOutgoing && vm.Call.ConnectedAt is null));
                vm.Release();
            }
        };
    }

    public async ValueTask DisposeAsync()
    {
        _timer.Stop();
        ActiveCall?.Release();
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_bots is not null)
        {
            await _bots.DisposeAsync();
        }
    }
}

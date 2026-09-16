using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.AI.Speech;
using VoipNet.Audio;

namespace VoipNet.Enterprise.Ivr;

/// <summary>How an IVR run finished.</summary>
public enum IvrOutcome
{
    /// <summary>The caller hung up, or the call dropped.</summary>
    CallerLeft,

    /// <summary>The IVR hung up after saying goodbye.</summary>
    Completed,

    /// <summary>The call was transferred.</summary>
    Transferred,

    /// <summary>The caller was put into a queue.</summary>
    Queued,

    /// <summary>A custom handler (for example an AI agent) took over and returned.</summary>
    HandedOff,
}

/// <summary>Result of one IVR run.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Context">Values collected and menus visited.</param>
public sealed record IvrResult(IvrOutcome Outcome, IvrContext Context);

/// <summary>
/// Plays an <see cref="IvrFlow"/> to a caller: speaks prompts, collects DTMF and follows the
/// actions. Prompts are synthesised when a text-to-speech provider is supplied, otherwise the
/// menus must reference WAV files.
/// </summary>
/// <param name="textToSpeech">Optional synthesiser for spoken prompts.</param>
/// <param name="logger">Optional logger.</param>
public sealed class IvrRunner(ITextToSpeech? textToSpeech = null, ILogger<IvrRunner>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<IvrRunner>.Instance;

    /// <summary>Runs a flow on a connected call.</summary>
    /// <param name="call">The call.</param>
    /// <param name="flow">Flow to run.</param>
    /// <param name="cancellationToken">Stops the IVR.</param>
    public async Task<IvrResult> RunAsync(VoipCall call, IvrFlow flow, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(flow);

        var context = new IvrContext();
        var digits = Channel.CreateUnbounded<char>();
        void OnDtmf(object? sender, DtmfEventArgs e) => digits.Writer.TryWrite(e.Digit);
        call.DtmfReceived += OnDtmf;

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void OnState(object? sender, CallStateEventArgs e)
        {
            if (e.State == CallState.Terminated)
            {
                lifetime.Cancel();
            }
        }

        call.StateChanged += OnState;

        try
        {
            if (!string.IsNullOrWhiteSpace(flow.Welcome))
            {
                await SpeakAsync(call, flow.Welcome, lifetime.Token).ConfigureAwait(false);
            }

            var menuId = flow.StartMenuId;
            while (!lifetime.IsCancellationRequested && call.IsActive)
            {
                if (!flow.Menus.TryGetValue(menuId, out var menu))
                {
                    _logger.LogWarning("Menu {MenuId} is missing; ending the IVR", menuId);
                    return new IvrResult(IvrOutcome.Completed, context);
                }

                context.Path.Add(menu.Id);
                var choice = await AskAsync(call, menu, digits, lifetime.Token).ConfigureAwait(false);
                var action = choice?.Action ?? menu.OnFailure;

                switch (action)
                {
                    case IvrAction.Goto go:
                        menuId = go.MenuId;
                        break;

                    case IvrAction.Say say:
                        await SpeakAsync(call, say.Text, lifetime.Token).ConfigureAwait(false);
                        break;

                    case IvrAction.Transfer transfer:
                        if (!string.IsNullOrWhiteSpace(transfer.Announcement))
                        {
                            await SpeakAsync(call, transfer.Announcement, lifetime.Token).ConfigureAwait(false);
                        }

                        call.Transfer(transfer.Target);
                        return new IvrResult(IvrOutcome.Transferred, context);

                    case IvrAction.Enqueue enqueue:
                        if (!string.IsNullOrWhiteSpace(enqueue.Announcement))
                        {
                            await SpeakAsync(call, enqueue.Announcement, lifetime.Token).ConfigureAwait(false);
                        }

                        context.RequestedQueue = enqueue.QueueName;
                        return new IvrResult(IvrOutcome.Queued, context);

                    case IvrAction.Handoff handoff:
                        _logger.LogInformation("IVR handing call {CallId} to {Handler}", call.Id, handoff.Name);
                        call.DtmfReceived -= OnDtmf;
                        await handoff.Handler(call, context, lifetime.Token).ConfigureAwait(false);
                        return new IvrResult(IvrOutcome.HandedOff, context);

                    case IvrAction.Collect collect:
                        var value = await CollectAsync(call, collect, digits, lifetime.Token).ConfigureAwait(false);
                        context.Values[collect.Key] = value;
                        menuId = collect.NextMenuId;
                        break;

                    case IvrAction.Hangup hangup:
                        if (!string.IsNullOrWhiteSpace(hangup.Announcement))
                        {
                            await SpeakAsync(call, hangup.Announcement, lifetime.Token).ConfigureAwait(false);
                            await DrainAsync(call, lifetime.Token).ConfigureAwait(false);
                        }

                        call.Hangup();
                        return new IvrResult(IvrOutcome.Completed, context);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The caller hung up.
        }
        finally
        {
            call.DtmfReceived -= OnDtmf;
            call.StateChanged -= OnState;
            digits.Writer.TryComplete();
        }

        return new IvrResult(IvrOutcome.CallerLeft, context);
    }

    private async Task<IvrChoice?> AskAsync(VoipCall call, IvrMenu menu, Channel<char> digits, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < menu.MaxAttempts && call.IsActive; attempt++)
        {
            while (digits.Reader.TryRead(out _))
            {
                // Discard keys pressed before the prompt started.
            }

            if (menu.PromptAudioFile is { Length: > 0 } file)
            {
                await PlayFileAsync(call, file, cancellationToken).ConfigureAwait(false);
            }
            else if (!string.IsNullOrWhiteSpace(menu.Prompt))
            {
                await SpeakAsync(call, menu.Prompt, cancellationToken).ConfigureAwait(false);
            }

            var digit = await ReadDigitAsync(digits, menu.Timeout, cancellationToken).ConfigureAwait(false);
            if (digit is null)
            {
                continue;
            }

            if (menu.Choices.TryGetValue(digit.Value, out var choice))
            {
                return choice;
            }

            await SpeakAsync(call, menu.InvalidPrompt, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string> CollectAsync(VoipCall call, IvrAction.Collect collect, Channel<char> digits, CancellationToken cancellationToken)
    {
        await SpeakAsync(call, collect.Prompt, cancellationToken).ConfigureAwait(false);
        var value = new System.Text.StringBuilder();
        while (value.Length < collect.MaxDigits && call.IsActive)
        {
            var digit = await ReadDigitAsync(digits, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
            if (digit is null || digit == collect.Terminator)
            {
                break;
            }

            value.Append(digit.Value);
        }

        return value.ToString();
    }

    private static async Task<char?> ReadDigitAsync(Channel<char> digits, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            return await digits.Reader.ReadAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private async Task SpeakAsync(VoipCall call, string text, CancellationToken cancellationToken)
    {
        if (textToSpeech is null)
        {
            _logger.LogWarning("No text-to-speech provider is configured, so the prompt was skipped: {Prompt}", text);
            return;
        }

        await textToSpeech.SpeakAsync(call, text, null, cancellationToken).ConfigureAwait(false);
        await DrainAsync(call, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PlayFileAsync(VoipCall call, string path, CancellationToken cancellationToken)
    {
        var (samples, sampleRate, channels) = WavReader.Read(path);
        if (channels == 2)
        {
            // Downmix to mono: telephony carries one channel.
            var mono = new short[samples.Length / 2];
            for (var i = 0; i < mono.Length; i++)
            {
                mono[i] = (short)((samples[i * 2] + samples[(i * 2) + 1]) / 2);
            }

            samples = mono;
        }

        call.SendAudio(samples, sampleRate);
        await DrainAsync(call, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits until the queued audio has actually been sent, so prompts do not overlap.</summary>
    private static async Task DrainAsync(VoipCall call, CancellationToken cancellationToken)
    {
        while (call.IsActive && call.QueuedAudioMs > 60 && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
    }
}

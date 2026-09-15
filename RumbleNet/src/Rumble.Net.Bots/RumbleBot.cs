using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Rumble.Net.Events;
using Rumble.Net.Models;

namespace Rumble.Net.Bots;

/// <summary>
/// Base class for Mumble bots. Methods marked with <see cref="BotCommandAttribute"/> are registered
/// automatically; override the <c>On…Async</c> hooks to react to server activity.
/// </summary>
public abstract class RumbleBot : IAsyncDisposable
{
    private CancellationTokenSource? _cts;

    /// <summary>Creates the bot. Bots identify as bots and default to headless audio.</summary>
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "Bot types are preserved by their own usage.")]
    protected RumbleBot(RumbleClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.IsBot = true;
        if (options.Audio.Mode == AudioMode.Disabled)
        {
            options.Audio.Mode = AudioMode.Headless;
        }

        Client = new RumbleClient(options);
        Logger = options.LoggerFactory?.CreateLogger(GetType()) ?? NullLogger.Instance;
        Commands = new BotCommandRouter();
        Commands.MapFromObject(GetType(), this);
    }

    /// <summary>The bot's client.</summary>
    public RumbleClient Client { get; }

    /// <summary>Command router.</summary>
    public BotCommandRouter Commands { get; }

    /// <summary>Logger.</summary>
    protected ILogger Logger { get; }

    /// <summary>Token cancelled when the bot stops.</summary>
    protected CancellationToken StoppingToken => _cts?.Token ?? CancellationToken.None;

    /// <summary>Connects and starts handling events.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = new CancellationTokenSource();
        Client.TextMessageReceived += OnTextMessage;
        Client.UserJoined += OnUserJoined;
        Client.UserLeft += OnUserLeft;
        Client.UserMoved += OnUserMoved;
        await Client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await OnStartedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the bot and disconnects.</summary>
    public async Task StopAsync()
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        Client.TextMessageReceived -= OnTextMessage;
        Client.UserJoined -= OnUserJoined;
        Client.UserLeft -= OnUserLeft;
        Client.UserMoved -= OnUserMoved;
        await OnStoppingAsync().ConfigureAwait(false);
        await Client.DisconnectAsync().ConfigureAwait(false);
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>Runs until <paramref name="cancellationToken"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Called after connecting.</summary>
    protected virtual Task OnStartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called before disconnecting.</summary>
    protected virtual Task OnStoppingAsync() => Task.CompletedTask;

    /// <summary>Called for messages that are not commands (and not sent by the bot).</summary>
    protected virtual Task OnMessageAsync(TextMessage message, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called when a user joins the server.</summary>
    protected virtual Task OnUserJoinedAsync(User user, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called when a user leaves the server.</summary>
    protected virtual Task OnUserLeftAsync(UserLeftEventArgs args, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Called when a user changes channel.</summary>
    protected virtual Task OnUserMovedAsync(UserMovedEventArgs args, CancellationToken cancellationToken) => Task.CompletedTask;

    private void OnTextMessage(object? sender, TextMessage message)
    {
        if (message.Sender?.IsSelf == true)
        {
            return;
        }

        Run(async ct =>
        {
            if (!await Commands.DispatchAsync(Client, message, ct).ConfigureAwait(false))
            {
                await OnMessageAsync(message, ct).ConfigureAwait(false);
            }
        });
    }

    private void OnUserJoined(object? sender, User user)
    {
        if (Client.IsConnected && !user.IsSelf)
        {
            Run(ct => OnUserJoinedAsync(user, ct));
        }
    }

    private void OnUserLeft(object? sender, UserLeftEventArgs args) => Run(ct => OnUserLeftAsync(args, ct));

    private void OnUserMoved(object? sender, UserMovedEventArgs args) => Run(ct => OnUserMovedAsync(args, ct));

    private void Run(Func<CancellationToken, Task> work)
    {
        var token = StoppingToken;
        _ = Task.Run(async () =>
        {
            try
            {
                await work(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Bot handler failed");
            }
        }, token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Client.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Runs a <see cref="RumbleBot"/> as an <see cref="IHostedService"/>.</summary>
public sealed class RumbleBotHostedService<TBot>(TBot bot) : BackgroundService
    where TBot : RumbleBot
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => bot.RunAsync(stoppingToken);
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoipNet.AI.DependencyInjection;
using VoipNet.AI.Realtime;

namespace VoipNet.RealtimeAgent;

/// <summary>Starts the SIP endpoint and gives every incoming call its own AI agent.</summary>
public sealed class AgentService(
    VoipClient client,
    IServiceProvider services,
    IConfiguration configuration,
    ILogger<AgentService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        client.IncomingCall += (_, e) => _ = HandleAsync(e, stoppingToken);
        client.RegistrationChanged += (_, e) => logger.LogInformation("Registration: {State} ({Code} {Reason})", e.State, e.StatusCode, e.Reason);
        await client.StartAsync(stoppingToken);

        var mode = configuration["Agent:Mode"] ?? "pipeline";
        logger.LogInformation("AI agent ({Mode}) answering on {Address} — made by Gravicode Studios, led by Kang Fadhil", mode, client.LocalAddress);
        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task HandleAsync(IncomingCallEventArgs e, CancellationToken stoppingToken)
    {
        var call = e.Call;
        logger.LogInformation("Call from {Caller}", e.DisplayName ?? e.From);
        try
        {
            await call.AnswerAsync(stoppingToken);

            if (string.Equals(configuration["Agent:Mode"], "realtime", StringComparison.OrdinalIgnoreCase))
            {
                var realtime = services.GetRequiredService<RealtimeVoiceAgent>();
                realtime.CallerSaid += (_, text) => logger.LogInformation("Caller: {Text}", text);
                realtime.AgentSaid += (_, text) => logger.LogInformation("Agent:  {Text}", text);
                await realtime.RunAsync(call, stoppingToken);
            }
            else
            {
                await using var agent = services.GetRequiredService<VoiceAgentFactory>().Create(o => o.ConversationKey = e.From);
                agent.CallerSaid += (_, text) => logger.LogInformation("Caller: {Text}", text);
                agent.AgentSaid += (_, text) => logger.LogInformation("Agent:  {Text}", text);
                agent.Interrupted += (_, _) => logger.LogInformation("(caller interrupted)");
                await agent.RunAsync(call, stoppingToken);
            }

            if (call.FinalStatistics is { } stats)
            {
                logger.LogInformation("Call ended after {Duration:mm\\:ss}, MOS {Mos:0.00}", call.Duration, stats.Mos);
            }
        }
        catch (Exception ex) when (ex is VoipException or OperationCanceledException)
        {
            logger.LogWarning("Call ended: {Message}", ex.Message);
        }
    }
}

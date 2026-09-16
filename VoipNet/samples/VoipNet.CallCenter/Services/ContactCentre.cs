using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using VoipNet.AI.Llm;
using VoipNet.Enterprise.CallCenter;
using VoipNet.Enterprise.Recording;

namespace VoipNet.CallCenter.Services;

/// <summary>Everything the wallboard shows, captured at one moment.</summary>
public sealed record WallboardSnapshot(
    IReadOnlyList<QueueSnapshot> Queues,
    IReadOnlyList<WaitingCaller> Waiting,
    IReadOnlyList<AgentView> Agents,
    IReadOnlyList<LiveCall> Calls,
    IReadOnlyList<string> Events,
    bool TrafficRunning,
    double CallsPerMinute,
    DateTimeOffset At);

public sealed record WaitingCaller(string Queue, string Caller, int Position, TimeSpan Waiting, TimeSpan Target);

public sealed record AgentView(string Id, string Name, AgentState State, TimeSpan InState, string? Caller, int Handled, TimeSpan TalkTime, IReadOnlyList<string> Skills);

public sealed record LiveCall(ulong Id, string Caller, string Queue, TimeSpan Duration, string Codec, double Mos, double LossPercent, double JitterMs);

/// <summary>
/// A self-contained contact centre running on the loopback interface: a PBX endpoint with two queues,
/// five agent softphones that answer and talk, and a traffic generator that plays the customers.
/// All of it is real SIP and RTP through the Voip.NET engine.
/// </summary>
public sealed class ContactCentre : IHostedService, IAsyncDisposable
{
    private readonly ILogger<ContactCentre> _logger;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ConcurrentQueue<string> _events = new();
    private readonly ConcurrentDictionary<ulong, (string Queue, string Caller)> _liveCalls = new();
    private VoipClient? _pbx;
    private VoipClient? _agentPhones;
    private VoipClient? _customers;
    private CancellationTokenSource? _traffic;

    public ContactCentre(ILogger<ContactCentre> logger, IConfiguration configuration, IWebHostEnvironment environment)
    {
        _logger = logger;
        _configuration = configuration;
        _environment = environment;
    }

    public CallCenterService? Centre { get; private set; }

    public RecordingService? Recordings { get; private set; }

    public double CallsPerMinute { get; set; } = 10;

    public string RecordingsFolder => Path.Combine(_environment.ContentRootPath, "recordings");

    /// <summary>Raised whenever something changes; the dashboard throttles its own refreshes.</summary>
    public event Action? Changed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _pbx = await StartClientAsync("pbx", 51000);
        _agentPhones = await StartClientAsync("agents", 52000);
        _customers = await StartClientAsync("customers", 53000);

        Recordings = new RecordingService(_pbx, new RecordingOptions
        {
            Directory = RecordingsFolder,
            Format = VoipNet.Audio.RecordingFormat.Mp3,
            RecordAllCalls = false,
        });

        Centre = new CallCenterService(_pbx);
        Centre.AddQueue(new CallQueueOptions { Name = "support", RequiredSkill = "support", AnnouncePosition = false, ServiceLevelTarget = TimeSpan.FromSeconds(10), WrapupTime = TimeSpan.FromSeconds(4) });
        Centre.AddQueue(new CallQueueOptions { Name = "sales", RequiredSkill = "sales", AnnouncePosition = false, ServiceLevelTarget = TimeSpan.FromSeconds(15), WrapupTime = TimeSpan.FromSeconds(3) });

        (string Id, string Name, string[] Skills)[] team =
        [
            ("2001", "Sari Wulandari", ["support"]),
            ("2002", "Dimas Pratama", ["support"]),
            ("2003", "Rina Kusuma", ["support", "sales"]),
            ("2004", "Agus Setiawan", ["sales"]),
            ("2005", "Maya Putri", ["sales", "support"]),
        ];
        foreach (var member in team)
        {
            var agent = new Agent { Id = member.Id, Name = member.Name, Uri = $"sip:{member.Id}@{_agentPhones.LocalAddress}" };
            foreach (var skill in member.Skills)
            {
                agent.Skills.Add(skill);
            }

            Centre.AddAgent(agent);
            Centre.SetAgentState(member.Id, AgentState.Available);
        }

        Centre.AgentStateChanged += (_, agent) => Notify();
        Centre.CallQueued += (_, queued) => Event($"{Label(queued.Call)} joined {queued.QueueName}");
        Centre.CallDequeued += (_, e) =>
        {
            var who = Label(e.Call.Call);
            Event(e.Result.Outcome switch
            {
                QueueOutcome.Answered => $"{e.Result.Agent?.Name} answered {who} after {e.Result.Waited.TotalSeconds:0} s",
                QueueOutcome.Abandoned => $"{who} hung up after waiting {e.Result.Waited.TotalSeconds:0} s",
                _ => $"{who} {e.Result.Outcome.ToString().ToLowerInvariant()}",
            });
        };

        _pbx.IncomingCall += (_, e) => _ = HandleInboundAsync(e);
        _agentPhones.IncomingCall += (_, e) => _ = AgentPickUpAsync(e.Call);

        Event("Contact centre online: 2 queues, 5 agents");
        if (_configuration.GetValue("Traffic:AutoStart", true))
        {
            StartTraffic();
        }
    }

    private async Task<VoipClient> StartClientAsync(string name, int rtpBase)
    {
        var client = new VoipClient(new VoipClientOptions
        {
            BindAddress = "127.0.0.1",
            SipPort = 0,
            Username = name,
            DisplayName = name,
            RtpPortMin = rtpBase,
            RtpPortMax = rtpBase + 999,
        });
        await client.StartAsync();
        return client;
    }

    private async Task HandleInboundAsync(IncomingCallEventArgs e)
    {
        try
        {
            var queue = e.To.Contains("sales", StringComparison.OrdinalIgnoreCase) ? "sales" : "support";
            _liveCalls[e.Call.Id] = (queue, Label(e.Call));
            await e.Call.AnswerAsync();
            Recordings?.Start(e.Call, new Dictionary<string, string> { ["queue"] = queue });
            e.Call.StateChanged += (sender, s) =>
            {
                if (s.State == CallState.Terminated)
                {
                    _liveCalls.TryRemove(e.Call.Id, out _);
                    Notify();
                }
            };
            await Centre!.EnqueueAsync(e.Call, queue, context: new Dictionary<string, string> { ["caller"] = e.DisplayName ?? string.Empty });
        }
        catch (Exception ex) when (ex is VoipException or ObjectDisposedException or TimeoutException)
        {
            _logger.LogDebug(ex, "Inbound call ended early");
        }
    }

    /// <summary>The agent softphones: ring for a moment, pick up, talk, and let the customer end the call.</summary>
    private async Task AgentPickUpAsync(VoipCall call)
    {
        try
        {
            await Task.Delay(Random.Shared.Next(800, 2600));
            if (call.State != CallState.Incoming)
            {
                return;
            }

            await call.AnswerAsync();
            while (call.IsActive)
            {
                call.SendAudio(Speech(Random.Shared.Next(1200, 2600), Random.Shared.Next(150, 230)), 16000);
                await Task.Delay(Random.Shared.Next(3500, 6000));
            }
        }
        catch (Exception ex) when (ex is VoipException or ObjectDisposedException or TimeoutException)
        {
            _logger.LogDebug(ex, "Agent leg ended early");
        }
    }

    public void StartTraffic()
    {
        if (_traffic is not null)
        {
            return;
        }

        _traffic = new CancellationTokenSource();
        _ = GenerateTrafficAsync(_traffic.Token);
        Event($"Traffic generator started at {CallsPerMinute:0} calls per minute");
    }

    public void StopTraffic()
    {
        _traffic?.Cancel();
        _traffic = null;
        Event("Traffic generator stopped");
    }

    private async Task GenerateTrafficAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // Exponential inter-arrival times give Poisson traffic, like real callers.
            var meanSeconds = 60.0 / Math.Max(CallsPerMinute, 0.5);
            var wait = -Math.Log(1 - Random.Shared.NextDouble()) * meanSeconds;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(wait, 0.3, 30)), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = OneCustomerAsync();
        }
    }

    private async Task OneCustomerAsync()
    {
        if (_customers is null || _pbx is null)
        {
            return;
        }

        var queue = Random.Shared.NextDouble() < 0.65 ? "support" : "sales";
        var patience = TimeSpan.FromSeconds(Random.Shared.Next(15, 60));
        var talk = TimeSpan.FromSeconds(Random.Shared.Next(10, 35));
        try
        {
            var call = _customers.Call($"sip:{queue}@{_pbx.LocalAddress}");
            await call.Connected.WaitAsync(TimeSpan.FromSeconds(15));

            // Wait in the queue until an agent speaks, or give up.
            var started = DateTimeOffset.UtcNow;
            var heardAgent = false;
            call.AudioReceived += (_, direction, _, samples) =>
            {
                if (direction == AudioDirection.Inbound && VoipNet.Audio.Pcm.Rms(samples) > 0.05)
                {
                    heardAgent = true;
                }
            };

            while (call.IsActive && !heardAgent && DateTimeOffset.UtcNow - started < patience)
            {
                await Task.Delay(250);
            }

            if (!heardAgent)
            {
                call.Hangup();
                return;
            }

            var end = DateTimeOffset.UtcNow + talk;
            while (call.IsActive && DateTimeOffset.UtcNow < end)
            {
                call.SendAudio(Speech(Random.Shared.Next(1500, 3000), Random.Shared.Next(170, 260)), 16000);
                await Task.Delay(Random.Shared.Next(4000, 7000));
            }

            if (call.IsActive)
            {
                call.Hangup();
            }
        }
        catch (Exception ex) when (ex is VoipException or ObjectDisposedException or TimeoutException)
        {
            _logger.LogDebug(ex, "Customer call ended early");
        }
    }


    public void SetAgentState(string agentId, AgentState state)
    {
        Centre?.SetAgentState(agentId, state);
        Event($"Supervisor set agent {agentId} to {state}");
    }

    public WallboardSnapshot Snapshot()
    {
        var centre = Centre;
        if (centre is null || _pbx is null)
        {
            return new WallboardSnapshot([], [], [], [], [], false, CallsPerMinute, DateTimeOffset.UtcNow);
        }

        var queues = centre.Queues.Select(q => centre.Metrics.Snapshot(q.Name)).ToList();
        var waiting = centre.Queues
            .SelectMany(q => centre.Waiting(q.Name).Select(w => new WaitingCaller(q.Name, Label(w.Call), w.Position, w.Waiting, q.ServiceLevelTarget)))
            .OrderByDescending(w => w.Waiting)
            .ToList();
        var agents = centre.Agents
            .OrderBy(a => a.Id)
            .Select(a => new AgentView(a.Id, a.Name, a.State, DateTimeOffset.UtcNow - a.StateSince, a.CurrentCaller is null ? null : Label(a.CurrentCaller), a.HandledCalls, a.TalkTime, [.. a.Skills]))
            .ToList();

        var calls = new List<LiveCall>();
        foreach (var call in _pbx.Calls.Where(c => c.State == CallState.Connected && !c.IsOutgoing))
        {
            try
            {
                var stats = call.GetStatistics();
                var queue = _liveCalls.TryGetValue(call.Id, out var info) ? info.Queue : "—";
                calls.Add(new LiveCall(call.Id, Label(call), queue, call.Duration, call.Codec ?? "—", stats.Mos, stats.LossPercent, stats.JitterMs));
            }
            catch (VoipException)
            {
                // The call ended between listing and reading statistics.
            }
        }

        return new WallboardSnapshot(queues, waiting, agents, calls, [.. _events.Reverse()], _traffic is not null, CallsPerMinute, DateTimeOffset.UtcNow);
    }

    /// <summary>Asks the configured model to read the wallboard like a shift supervisor would.</summary>
    public async Task<string> AskForInsightsAsync(CancellationToken cancellationToken)
    {
        var endpoint = _configuration["AI:Endpoint"] ?? Environment.GetEnvironmentVariable("VOIPNET_AI_ENDPOINT");
        var key = _configuration["AI:ApiKey"] ?? Environment.GetEnvironmentVariable("VOIPNET_AI_KEY");
        var model = _configuration["AI:Model"] ?? Environment.GetEnvironmentVariable("VOIPNET_AI_MODEL") ?? "gpt-5-mini";
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
        {
            return "Configure AI:Endpoint and AI:ApiKey (or VOIPNET_AI_ENDPOINT and VOIPNET_AI_KEY) to get AI staffing advice.";
        }

        var options = endpoint.Contains("azure.com", StringComparison.OrdinalIgnoreCase)
            ? OpenAiChatOptions.ForAzure(endpoint, key, model)
            : new OpenAiChatOptions { BaseUri = new Uri(endpoint.TrimEnd('/') + "/"), ApiKey = key, Model = model };
        options.ReasoningEffort = "low";
        using var chat = new OpenAiChatClient(options);

        var snapshot = Snapshot();
        var data = JsonSerializer.Serialize(new
        {
            queues = snapshot.Queues.Select(q => new { q.Queue, q.Offered, q.Answered, q.Abandoned, averageWaitSeconds = Math.Round(q.AverageWait.TotalSeconds, 1), serviceLevel = Math.Round(q.ServiceLevel, 2) }),
            waiting = snapshot.Waiting.Select(w => new { w.Queue, waitingSeconds = Math.Round(w.Waiting.TotalSeconds) }),
            agents = snapshot.Agents.Select(a => new { a.Name, state = a.State.ToString(), a.Handled, skills = a.Skills }),
            callsPerMinute = snapshot.CallsPerMinute,
        });

        var response = await chat.GetResponseAsync(
            [new ChatMessage(ChatRole.User, data)],
            new ChatOptions
            {
                Instructions = "You are a contact-centre shift supervisor. Read this live wallboard JSON and give three short, specific recommendations in Indonesian (staffing, skills, queue settings). Plain text, one line each, no preamble.",
                MaxOutputTokens = 2500,
            },
            cancellationToken);
        return response.Text.Trim();
    }

    private void Event(string text)
    {
        _events.Enqueue($"{DateTime.Now:HH:mm:ss}  {text}");
        while (_events.Count > 60)
        {
            _events.TryDequeue(out _);
        }

        Notify();
    }

    private void Notify() => Changed?.Invoke();

    private static string Label(VoipCall call) => $"Caller {call.Id:000}";

    private static string Short(string uri)
    {
        var value = uri.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ? uri[4..] : uri;
        var at = value.IndexOf('@');
        return at > 0 ? value[..at] : value;
    }

    private static short[] Speech(int milliseconds, double pitch)
    {
        var samples = new short[16 * milliseconds];
        for (var i = 0; i < samples.Length; i++)
        {
            var syllable = Math.Max(0, Math.Sin(Math.PI * i / 4000.0));
            samples[i] = (short)(6000 * syllable * Math.Sin(2 * Math.PI * (pitch + (30 * Math.Sin(i / 800.0))) * i / 16000));
        }

        return samples;
    }

    public async Task StopAsync(CancellationToken cancellationToken) => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        _traffic?.Cancel();
        Recordings?.Dispose();
        if (Centre is not null)
        {
            await Centre.DisposeAsync();
        }

        foreach (var client in new[] { _customers, _agentPhones, _pbx })
        {
            if (client is not null)
            {
                await client.DisposeAsync();
            }
        }

        _pbx = _agentPhones = _customers = null;
    }
}

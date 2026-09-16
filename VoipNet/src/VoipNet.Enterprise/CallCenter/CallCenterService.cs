using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using VoipNet.AI.Speech;
using VoipNet.Audio;

namespace VoipNet.Enterprise.CallCenter;

/// <summary>
/// Queues callers, rings agents and bridges the two legs in a conference, which keeps both calls
/// under the application's control for recording, monitoring and supervisor barge-in.
/// </summary>
/// <param name="client">Client used to call agents.</param>
/// <param name="textToSpeech">Optional synthesiser for queue announcements.</param>
/// <param name="logger">Optional logger.</param>
public sealed class CallCenterService(VoipClient client, ITextToSpeech? textToSpeech = null, ILogger<CallCenterService>? logger = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Agent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CallQueueOptions> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<QueuedCall>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, VoipConference> _bridges = new();
    private readonly ILogger _logger = logger ?? NullLogger<CallCenterService>.Instance;
    private readonly Lock _gate = new();
    private int _roundRobin;
    private bool _disposed;

    /// <summary>Metrics for dashboards and reports.</summary>
    public CallCenterMetrics Metrics { get; } = new();

    /// <summary>Agents known to the service.</summary>
    public IReadOnlyCollection<Agent> Agents => _agents.Values.ToArray();

    /// <summary>Queues known to the service.</summary>
    public IReadOnlyCollection<CallQueueOptions> Queues => _queues.Values.ToArray();

    /// <summary>Raised when an agent changes state.</summary>
    public event EventHandler<Agent>? AgentStateChanged;

    /// <summary>Raised when a caller joins a queue.</summary>
    public event EventHandler<QueuedCall>? CallQueued;

    /// <summary>Raised when a caller leaves a queue, for any reason.</summary>
    public event EventHandler<(QueuedCall Call, QueueResult Result)>? CallDequeued;

    /// <summary>Adds a queue.</summary>
    /// <param name="options">Queue settings.</param>
    public void AddQueue(CallQueueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _queues[options.Name] = options;
        _waiting.TryAdd(options.Name, []);
    }

    /// <summary>Registers an agent. New agents start <see cref="AgentState.Offline"/>.</summary>
    /// <param name="agent">The agent.</param>
    public Agent AddAgent(Agent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        _agents[agent.Id] = agent;
        return agent;
    }

    /// <summary>Changes an agent's state, for example when they sign in or take a break.</summary>
    /// <param name="agentId">Agent identifier.</param>
    /// <param name="state">New state.</param>
    public void SetAgentState(string agentId, AgentState state)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
        {
            return;
        }

        agent.State = state;
        agent.StateSince = DateTimeOffset.UtcNow;
        AgentStateChanged?.Invoke(this, agent);
        _logger.LogInformation("Agent {Agent} is now {State}", agent.Name, state);
    }

    /// <summary>Callers waiting in a queue, in answer order.</summary>
    /// <param name="queueName">Queue to inspect.</param>
    public IReadOnlyList<QueuedCall> Waiting(string queueName) =>
        _waiting.TryGetValue(queueName, out var list) ? Snapshot(list) : [];

    /// <summary>
    /// Puts a caller in a queue and waits until an agent answers, the caller gives up, or the
    /// maximum wait passes. Announcements and music on hold play while waiting.
    /// </summary>
    /// <param name="call">The caller.</param>
    /// <param name="queueName">Queue to join.</param>
    /// <param name="priority">Higher numbers are answered first.</param>
    /// <param name="context">Values from the IVR to pass to the agent.</param>
    /// <param name="cancellationToken">Cancels waiting.</param>
    public async Task<QueueResult> EnqueueAsync(
        VoipCall call,
        string queueName,
        int priority = 0,
        IReadOnlyDictionary<string, string>? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (!_queues.TryGetValue(queueName, out var options))
        {
            throw new ArgumentException($"Queue '{queueName}' does not exist.", nameof(queueName));
        }

        var entry = new QueuedCall
        {
            Call = call,
            QueueName = queueName,
            Priority = priority,
            Context = context ?? new Dictionary<string, string>(),
        };

        var list = _waiting.GetOrAdd(queueName, _ => []);
        lock (_gate)
        {
            list.Add(entry);
            Reorder(list);
        }

        Metrics.RecordOffered(queueName);
        CallQueued?.Invoke(this, entry);
        _logger.LogInformation("Caller {Caller} joined queue {Queue} at position {Position}", call.RemoteUri, queueName, entry.Position);

        var result = await WaitForAgentAsync(entry, options, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            list.Remove(entry);
            Reorder(list);
        }

        Metrics.RecordOutcome(queueName, result, options.ServiceLevelTarget);
        CallDequeued?.Invoke(this, (entry, result));
        return result;
    }

    private async Task<QueueResult> WaitForAgentAsync(QueuedCall entry, CallQueueOptions options, CancellationToken cancellationToken)
    {
        var lastAnnounce = DateTimeOffset.UtcNow;
        var musicPosition = 0;
        short[]? music = null;
        var musicRate = 8000;
        if (options.MusicOnHoldFile is { Length: > 0 } file && File.Exists(file))
        {
            (music, musicRate, _) = WavReader.Read(file);
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!entry.Call.IsActive)
            {
                return new QueueResult(QueueOutcome.Abandoned, null, entry.Waiting);
            }

            if (entry.Waiting > options.MaxWait)
            {
                if (options.OverflowTarget is { Length: > 0 } overflow)
                {
                    entry.Call.Transfer(overflow);
                    return new QueueResult(QueueOutcome.Overflowed, null, entry.Waiting);
                }

                return new QueueResult(QueueOutcome.Overflowed, null, entry.Waiting);
            }

            if (entry.Position == 1 && TryReserveAgent(options) is { } agent)
            {
                var connected = await OfferAsync(entry, agent, options, cancellationToken).ConfigureAwait(false);
                if (connected)
                {
                    return new QueueResult(QueueOutcome.Answered, agent, entry.Waiting);
                }

                continue;
            }

            if (options.AnnouncePosition && textToSpeech is not null && DateTimeOffset.UtcNow - lastAnnounce > options.AnnounceEvery)
            {
                lastAnnounce = DateTimeOffset.UtcNow;
                var text = entry.Position <= 1
                    ? "Anda adalah penelepon berikutnya. Mohon tunggu sebentar."
                    : $"Anda berada di antrean nomor {entry.Position}. Mohon tunggu.";
                await textToSpeech.SpeakAsync(entry.Call, text, null, cancellationToken).ConfigureAwait(false);
            }
            else if (music is { Length: > 0 } && entry.Call.QueuedAudioMs < 400)
            {
                // Feed music on hold in one second slices so the queue stays responsive.
                var slice = Math.Min(musicRate, music.Length - musicPosition);
                entry.Call.SendAudio(music.AsSpan(musicPosition, slice), musicRate);
                musicPosition = (musicPosition + slice) % music.Length;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }

        return new QueueResult(QueueOutcome.Cancelled, null, entry.Waiting);
    }

    private Agent? TryReserveAgent(CallQueueOptions options)
    {
        lock (_gate)
        {
            var candidates = _agents.Values
                .Where(a => a.IsAvailable && a.HasSkill(options.RequiredSkill))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var chosen = options.Strategy switch
            {
                RoutingStrategy.RoundRobin => candidates[Math.Abs(_roundRobin++) % candidates.Count],
                RoutingStrategy.FewestCalls => candidates.OrderBy(a => a.HandledCalls).First(),
                RoutingStrategy.SkillBased => candidates
                    .OrderByDescending(a => options.RequiredSkill is not null && a.Skills.Contains(options.RequiredSkill))
                    .ThenBy(a => a.LastCallEnded)
                    .First(),
                _ => candidates.OrderBy(a => a.LastCallEnded).First(),
            };

            chosen.State = AgentState.Ringing;
            chosen.StateSince = DateTimeOffset.UtcNow;
            return chosen;
        }
    }

    private async Task<bool> OfferAsync(QueuedCall entry, Agent agent, CallQueueOptions options, CancellationToken cancellationToken)
    {
        AgentStateChanged?.Invoke(this, agent);
        VoipCall? agentCall = null;
        try
        {
            using var ring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ring.CancelAfter(options.RingTimeout);
            agentCall = await client.CallAsync(agent.Uri, ring.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or VoipException)
        {
            _logger.LogInformation("Agent {Agent} did not answer; trying someone else", agent.Name);
            SetAgentState(agent.Id, AgentState.Available);
            agentCall?.Hangup();
            return false;
        }

        if (!entry.Call.IsActive)
        {
            agentCall.Hangup();
            SetAgentState(agent.Id, AgentState.Available);
            return false;
        }

        // Bridge the caller and the agent through a conference so both legs stay under our control.
        var bridge = client.CreateConference();
        bridge.Add(entry.Call);
        bridge.Add(agentCall);
        _bridges[entry.Call.Id] = bridge;

        agent.State = AgentState.OnCall;
        agent.StateSince = DateTimeOffset.UtcNow;
        agent.CurrentCall = agentCall;
        agent.CurrentCaller = entry.Call;
        agent.HandledCalls++;
        AgentStateChanged?.Invoke(this, agent);
        _logger.LogInformation("Agent {Agent} answered caller {Caller}", agent.Name, entry.Call.RemoteUri);

        _ = MonitorBridgeAsync(entry, agent, agentCall, bridge, options);
        return true;
    }

    private async Task MonitorBridgeAsync(QueuedCall entry, Agent agent, VoipCall agentCall, VoipConference bridge, CallQueueOptions options)
    {
        var start = DateTimeOffset.UtcNow;
        try
        {
            var callerEnded = entry.Call.Completion;
            var agentEnded = agentCall.Completion;
            await Task.WhenAny(callerEnded, agentEnded).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VoipException or TaskCanceledException)
        {
            // One of the legs ended abruptly.
        }
        finally
        {
            var talk = DateTimeOffset.UtcNow - start;
            Metrics.RecordTalkTime(entry.QueueName, talk);
            agent.TalkTime += talk;
            agent.LastCallEnded = DateTimeOffset.UtcNow;
            agent.CurrentCall = null;
            agent.CurrentCaller = null;

            if (entry.Call.IsActive)
            {
                entry.Call.Hangup();
            }

            if (agentCall.IsActive)
            {
                agentCall.Hangup();
            }

            _bridges.TryRemove(entry.Call.Id, out _);
            bridge.Dispose();

            SetAgentState(agent.Id, AgentState.Wrapup);
            if (options.WrapupTime > TimeSpan.Zero)
            {
                await Task.Delay(options.WrapupTime).ConfigureAwait(false);
            }

            if (agent.State == AgentState.Wrapup)
            {
                SetAgentState(agent.Id, AgentState.Available);
            }
        }
    }

    /// <summary>
    /// Lets a supervisor listen to a live call. In <paramref name="whisper"/> mode the supervisor
    /// is heard by both parties; otherwise the leg is made send-only (a hold from the bridge's side),
    /// so the supervisor hears the conversation but contributes nothing to the mix.
    /// </summary>
    /// <param name="callerCallId">Identifier of the caller's call.</param>
    /// <param name="supervisorCall">The supervisor's call, already connected.</param>
    /// <param name="whisper">True to let the supervisor be heard.</param>
    public bool Monitor(ulong callerCallId, VoipCall supervisorCall, bool whisper = false)
    {
        ArgumentNullException.ThrowIfNull(supervisorCall);
        if (!_bridges.TryGetValue(callerCallId, out var bridge))
        {
            return false;
        }

        bridge.Add(supervisorCall);
        if (!whisper)
        {
            // Muting would silence what the supervisor hears; send-only keeps the mix flowing to them
            // while their own audio is ignored by the bridge.
            supervisorCall.SetHold(true);
        }

        return true;
    }

    private static void Reorder(List<QueuedCall> list)
    {
        var ordered = list.OrderByDescending(e => e.Priority).ThenBy(e => e.EnqueuedAt).ToList();
        list.Clear();
        list.AddRange(ordered);
        for (var i = 0; i < list.Count; i++)
        {
            list[i].Position = i + 1;
        }
    }

    private List<QueuedCall> Snapshot(List<QueuedCall> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }

    /// <summary>Ends every bridge and releases the service.</summary>
    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        foreach (var bridge in _bridges.Values)
        {
            bridge.Dispose();
        }

        _bridges.Clear();
        return ValueTask.CompletedTask;
    }
}

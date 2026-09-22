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
/// <param name="store">Optional shared state, so agent states and callbacks survive a restart and can
/// be shared between nodes.</param>
public sealed class CallCenterService(
    VoipClient client,
    ITextToSpeech? textToSpeech = null,
    ILogger<CallCenterService>? logger = null,
    ICallCenterStore? store = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Agent> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CallQueueOptions> _queues = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<QueuedCall>> _waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<ulong, VoipConference> _bridges = new();
    private readonly ConcurrentDictionary<string, List<CallbackRequest>> _callbacks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _callbackPump;
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

    /// <summary>Raised when a caller asks to be called back instead of waiting.</summary>
    public event EventHandler<CallbackRequest>? CallbackScheduled;

    /// <summary>Raised when a callback is made, or given up on.</summary>
    public event EventHandler<CallbackRequest>? CallbackCompleted;

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
        Publish(agent);
        _logger.LogInformation("Agent {Agent} is now {State}", agent.Name, state);
    }

    /// <summary>Callers waiting in a queue, in answer order.</summary>
    /// <param name="queueName">Queue to inspect.</param>
    public IReadOnlyList<QueuedCall> Waiting(string queueName) =>
        _waiting.TryGetValue(queueName, out var list) ? Snapshot(list) : [];

    /// <summary>
    /// Loads the callbacks the store still owes and takes them on. Call this at startup: a caller who
    /// was promised a ring back should get one even if the node that promised it was restarted.
    /// </summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return;
        }

        foreach (var queueName in _queues.Keys)
        {
            var pending = await store.LoadCallbacksAsync(queueName, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                continue;
            }

            var list = _callbacks.GetOrAdd(queueName, _ => []);
            lock (_gate)
            {
                foreach (var request in pending.Where(p => list.All(existing => existing.Id != p.Id)))
                {
                    list.Add(request);
                }
            }

            _logger.LogInformation("Restored {Count} callbacks for queue {Queue}", pending.Count, queueName);
        }

        StartCallbackPump();
    }

    /// <summary>
    /// Writes a finished queue call to the history. The talk time is filled in when the bridge ends,
    /// so the row is written then for answered calls and straight away for the rest.
    /// </summary>
    private void Record(QueuedCall entry, QueueResult result, TimeSpan talked = default)
    {
        if (store is null || (result.Outcome == QueueOutcome.Answered && talked == TimeSpan.Zero))
        {
            return;
        }

        var record = new CallRecord(
            $"{entry.Call.Id}-{entry.EnqueuedAt.ToUnixTimeMilliseconds()}",
            entry.QueueName,
            entry.Call.RemoteUri,
            result.Agent?.Id,
            result.Outcome,
            entry.EnqueuedAt,
            result.Waited,
            talked,
            NodeName);

        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveCallAsync(record).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not store the history of call {Call}", record.Id);
            }
        });
    }

    /// <summary>Writes an agent's state to the shared store, when there is one.</summary>
    private void Publish(Agent agent)
    {
        if (store is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await store.SaveAgentAsync(new StoredAgent(agent.Id, agent.Name, agent.Uri, agent.State, agent.StateSince, NodeName)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                // A store that is briefly unavailable must not stop a call centre from running.
                _logger.LogWarning(ex, "Could not publish the state of agent {Agent}", agent.Id);
            }
        });
    }

    /// <summary>Name this node writes into shared rows.</summary>
    public string NodeName { get; set; } = Environment.MachineName;

    /// <summary>Agents across every node, from the shared store. Falls back to this node's own agents.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<StoredAgent>> AllAgentsAsync(CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return Agents.Select(a => new StoredAgent(a.Id, a.Name, a.Uri, a.State, a.StateSince, NodeName)).ToArray();
        }

        return await store.LoadAgentsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Report rows for a stretch of time, read from the shared store. Without a store there is no
    /// history to report on, and an empty report says so rather than inventing one.
    /// </summary>
    /// <param name="from">Start of the period.</param>
    /// <param name="to">End of the period.</param>
    /// <param name="interval">Reporting interval; half an hour is the usual choice.</param>
    /// <param name="queueName">Queue to report on, or null for every queue.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<IReadOnlyList<ReportRow>> ReportAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        TimeSpan interval,
        string? queueName = null,
        CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            return [];
        }

        var calls = await store.LoadCallsAsync(from, to, queueName, cancellationToken).ConfigureAwait(false);
        var target = queueName is not null && _queues.TryGetValue(queueName, out var options)
            ? options.ServiceLevelTarget
            : TimeSpan.FromSeconds(20);
        return WorkforceReport.Summarize(calls, interval, target);
    }

    /// <summary>Callbacks still to be made for a queue, in the order they will be made.</summary>
    /// <param name="queueName">Queue to inspect.</param>
    public IReadOnlyList<CallbackRequest> PendingCallbacks(string queueName)
    {
        if (!_callbacks.TryGetValue(queueName, out var list))
        {
            return [];
        }

        lock (_gate)
        {
            return list.Where(c => c.IsPending).OrderByDescending(c => c.Priority).ThenBy(c => c.RequestedAt).ToArray();
        }
    }

    /// <summary>
    /// How long a caller joining now is likely to wait: the average handling time spread across the
    /// agents who can take this queue, multiplied by the callers ahead of them. Falls back to three
    /// minutes of handling time until the queue has answered enough calls to know better.
    /// </summary>
    /// <param name="queueName">Queue to estimate for.</param>
    /// <param name="position">Position to estimate for; 0 means "joining at the back".</param>
    public TimeSpan EstimatedWait(string queueName, int position = 0)
    {
        if (!_queues.TryGetValue(queueName, out var options))
        {
            return TimeSpan.Zero;
        }

        var snapshot = Metrics.Snapshot(queueName);
        var handling = snapshot.AverageTalk > TimeSpan.Zero ? snapshot.AverageTalk : TimeSpan.FromMinutes(3);
        handling += options.WrapupTime;

        var staffed = _agents.Values.Count(a => a.HasSkill(options.RequiredSkill) && a.State is AgentState.Available or AgentState.OnCall or AgentState.Wrapup or AgentState.Ringing);
        if (staffed == 0)
        {
            // Nobody is signed in: an estimate would be a guess dressed up as a promise.
            return TimeSpan.MaxValue;
        }

        if (position <= 0)
        {
            position = Waiting(queueName).Count + PendingCallbacks(queueName).Count + 1;
        }

        var free = _agents.Values.Count(a => a.IsAvailable && a.HasSkill(options.RequiredSkill));
        var ahead = Math.Max(position - free, 0);
        return ahead == 0 ? TimeSpan.Zero : handling * ahead / staffed;
    }

    /// <summary>
    /// Takes a caller out of the queue but keeps their place: the service rings them back when their
    /// turn comes and an agent is free. The caller's line is theirs to hang up once this returns.
    /// </summary>
    /// <param name="entry">The waiting caller, as handed to <c>CallQueued</c>.</param>
    /// <param name="destination">Where to ring; defaults to the caller's own address.</param>
    /// <param name="notBefore">Earliest time to ring, for a caller who asked for later.</param>
    public CallbackRequest RequestCallback(QueuedCall entry, string? destination = null, DateTimeOffset? notBefore = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var request = new CallbackRequest
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            QueueName = entry.QueueName,
            Destination = destination ?? entry.Call.RemoteUri,
            Priority = entry.Priority,
            NotBefore = notBefore ?? DateTimeOffset.UtcNow,
            AlreadyWaited = entry.Waiting,
            Context = entry.Context,
        };

        var list = _callbacks.GetOrAdd(entry.QueueName, _ => []);
        lock (_gate)
        {
            list.Add(request);
        }

        entry.Callback = request;
        if (store is not null)
        {
            // Saved before the caller hangs up, so a crash cannot lose a promise already made.
            try
            {
                store.SaveCallbackAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not store the callback for {Destination}", request.Destination);
            }
        }

        StartCallbackPump();
        CallbackScheduled?.Invoke(this, request);
        _logger.LogInformation("Caller {Caller} asked for a callback on {Destination} in queue {Queue}", entry.Call.RemoteUri, request.Destination, entry.QueueName);
        return request;
    }

    /// <summary>Drops a callback that is no longer wanted.</summary>
    /// <param name="id">Identifier from <see cref="RequestCallback"/>.</param>
    public bool CancelCallback(string id)
    {
        lock (_gate)
        {
            foreach (var request in _callbacks.Values.SelectMany(list => list))
            {
                if (request.Id == id && request.IsPending)
                {
                    request.Outcome = CallbackOutcome.Cancelled;
                    CallbackCompleted?.Invoke(this, request);
                    return true;
                }
            }
        }

        return false;
    }

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
        Record(entry, result);
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
            if (entry.Callback is not null)
            {
                // The caller keeps their place and hangs up; the pump rings them back.
                return new QueueResult(QueueOutcome.CallbackScheduled, null, entry.Waiting);
            }

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

    /// <summary>Starts the loop that rings callers back, once there is something to ring.</summary>
    private void StartCallbackPump()
    {
        if (_callbackPump is not null || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            _callbackPump ??= Task.Run(() => PumpCallbacksAsync(_shutdown.Token));
        }
    }

    private async Task PumpCallbacksAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                foreach (var (queueName, options) in _queues.ToArray())
                {
                    var due = NextCallback(queueName);
                    if (due is null)
                    {
                        continue;
                    }

                    var agent = TryReserveAgent(options);
                    if (agent is null)
                    {
                        continue;
                    }

                    // With several nodes sharing a queue, exactly one of them may ring this caller.
                    if (store is not null && !await store.TryClaimCallbackAsync(due.Id, NodeName, cancellationToken).ConfigureAwait(false))
                    {
                        SetAgentState(agent.Id, AgentState.Available);
                        due.Outcome = CallbackOutcome.Cancelled;
                        continue;
                    }

                    await MakeCallbackAsync(due, agent, options, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is VoipException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "A callback attempt failed; the caller stays in the list");
            }
        }
    }

    /// <summary>
    /// The callback to make next, if any. A callback waits its turn: it only goes ahead of a caller
    /// still holding the line when it has been waiting longer than that caller.
    /// </summary>
    private CallbackRequest? NextCallback(string queueName)
    {
        var pending = PendingCallbacks(queueName)
            .Where(c => c.NotBefore <= DateTimeOffset.UtcNow)
            .Where(c => c.LastAttempt is null || DateTimeOffset.UtcNow - c.LastAttempt > _queues[queueName].Callbacks.RetryAfter)
            .ToArray();
        if (pending.Length == 0)
        {
            return null;
        }

        var candidate = pending[0];
        var waited = candidate.AlreadyWaited + (DateTimeOffset.UtcNow - candidate.RequestedAt);
        var longestLiveWait = Waiting(queueName).Select(w => w.Waiting).DefaultIfEmpty(TimeSpan.Zero).Max();
        return waited >= longestLiveWait ? candidate : null;
    }

    private async Task MakeCallbackAsync(CallbackRequest request, Agent agent, CallQueueOptions options, CancellationToken cancellationToken)
    {
        request.Attempts++;
        request.LastAttempt = DateTimeOffset.UtcNow;
        AgentStateChanged?.Invoke(this, agent);
        _logger.LogInformation("Calling {Destination} back for queue {Queue} (attempt {Attempt})", request.Destination, request.QueueName, request.Attempts);

        VoipCall? call = null;
        try
        {
            using var ring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ring.CancelAfter(options.Callbacks.RingTimeout);
            call = await client.CallAsync(request.Destination, ring.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or VoipException)
        {
            call?.Hangup();
            SetAgentState(agent.Id, AgentState.Available);
            if (request.Attempts >= options.Callbacks.MaxAttempts)
            {
                request.Outcome = CallbackOutcome.NoAnswer;
                await RecordAsync(request).ConfigureAwait(false);
                CallbackCompleted?.Invoke(this, request);
                _logger.LogInformation("Gave up calling {Destination} back after {Attempts} attempts", request.Destination, request.Attempts);
            }
            else
            {
                // Still owed: let go of it so any node can try again after the retry interval.
                await RecordAsync(request).ConfigureAwait(false);
            }

            return;
        }

        // The caller picked up: say why they are being rung before an agent joins.
        if (textToSpeech is not null && options.Callbacks.Announcement is { Length: > 0 } announcement)
        {
            try
            {
                await textToSpeech.SpeakAsync(call, announcement, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is VoipException or HttpRequestException)
            {
                _logger.LogWarning(ex, "Could not play the callback announcement");
            }
        }

        var entry = new QueuedCall
        {
            Call = call,
            QueueName = request.QueueName,
            Priority = request.Priority,
            Context = request.Context,
        };

        Metrics.RecordOffered(request.QueueName);
        if (await OfferAsync(entry, agent, options, cancellationToken).ConfigureAwait(false))
        {
            request.Outcome = CallbackOutcome.Connected;
            Metrics.RecordOutcome(request.QueueName, new QueueResult(QueueOutcome.Answered, agent, request.AlreadyWaited), options.ServiceLevelTarget);
        }
        else
        {
            request.Outcome = CallbackOutcome.AgentLost;
            if (call.IsActive)
            {
                call.Hangup();
            }
        }

        await RecordAsync(request).ConfigureAwait(false);
        CallbackCompleted?.Invoke(this, request);
    }

    /// <summary>Writes a callback's progress to the shared store, when there is one.</summary>
    private async Task RecordAsync(CallbackRequest request)
    {
        if (store is null)
        {
            return;
        }

        try
        {
            await store.CompleteCallbackAsync(request.Id, request.Outcome, request.Attempts).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not record the outcome of callback {Id}", request.Id);
        }
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
            Record(entry, new QueueResult(QueueOutcome.Answered, agent, entry.Waiting), talk);
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
        _shutdown.Cancel();
        foreach (var request in _callbacks.Values.SelectMany(list => list).Where(c => c.IsPending))
        {
            request.Outcome = CallbackOutcome.Cancelled;
        }

        foreach (var bridge in _bridges.Values)
        {
            bridge.Dispose();
        }

        _bridges.Clear();
        _shutdown.Dispose();
        return ValueTask.CompletedTask;
    }
}

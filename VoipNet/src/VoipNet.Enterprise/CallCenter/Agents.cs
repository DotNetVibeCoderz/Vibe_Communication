namespace VoipNet.Enterprise.CallCenter;

/// <summary>What an agent is doing right now.</summary>
public enum AgentState
{
    /// <summary>Not signed in.</summary>
    Offline,

    /// <summary>Signed in and ready for calls.</summary>
    Available,

    /// <summary>Being called about a queued caller.</summary>
    Ringing,

    /// <summary>Talking to a caller.</summary>
    OnCall,

    /// <summary>Finishing notes after a call.</summary>
    Wrapup,

    /// <summary>Signed in but unavailable (break, meeting, training).</summary>
    Paused,
}

/// <summary>Someone who answers queued calls.</summary>
public sealed class Agent
{
    /// <summary>Unique identifier, for example an employee number.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Where to reach the agent, for example <c>sip:2001@pbx.local</c>.</summary>
    public required string Uri { get; init; }

    /// <summary>Skills used by skill-based routing, such as <c>billing</c> or <c>english</c>.</summary>
    public HashSet<string> Skills { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current state.</summary>
    public AgentState State { get; internal set; } = AgentState.Offline;

    /// <summary>When the state last changed.</summary>
    public DateTimeOffset StateSince { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>The call the agent is on, when any.</summary>
    public VoipCall? CurrentCall { get; internal set; }

    /// <summary>The caller the agent is talking to, when any.</summary>
    public VoipCall? CurrentCaller { get; internal set; }

    /// <summary>Calls answered in this session.</summary>
    public int HandledCalls { get; internal set; }

    /// <summary>Total talk time in this session.</summary>
    public TimeSpan TalkTime { get; internal set; }

    /// <summary>Last time the agent finished a call, used by longest-idle routing.</summary>
    public DateTimeOffset LastCallEnded { get; internal set; } = DateTimeOffset.MinValue;

    /// <summary>True when the agent can take a call now.</summary>
    public bool IsAvailable => State == AgentState.Available;

    /// <summary>Checks whether the agent has a skill (an empty requirement always matches).</summary>
    /// <param name="skill">Skill to check.</param>
    public bool HasSkill(string? skill) => string.IsNullOrEmpty(skill) || Skills.Contains(skill);
}

/// <summary>How waiting calls are matched to agents.</summary>
public enum RoutingStrategy
{
    /// <summary>Each call goes to the next agent in turn.</summary>
    RoundRobin,

    /// <summary>The agent who has been idle longest answers.</summary>
    LongestIdle,

    /// <summary>The agent with the fewest calls this session answers.</summary>
    FewestCalls,

    /// <summary>Agents with the queue's skill first, then longest idle.</summary>
    SkillBased,
}

/// <summary>Settings for one queue.</summary>
public sealed class CallQueueOptions
{
    /// <summary>Queue name, used when a call is enqueued.</summary>
    public required string Name { get; init; }

    /// <summary>How calls are matched to agents.</summary>
    public RoutingStrategy Strategy { get; init; } = RoutingStrategy.LongestIdle;

    /// <summary>Skill an agent needs to take calls from this queue.</summary>
    public string? RequiredSkill { get; init; }

    /// <summary>How long a caller may wait before <see cref="OverflowTarget"/> is used.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Where callers go when they wait too long, for example a voicemail box.</summary>
    public string? OverflowTarget { get; init; }

    /// <summary>How long an agent's phone rings before the call is offered to someone else.</summary>
    public TimeSpan RingTimeout { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>Time an agent stays unavailable after a call to finish their notes.</summary>
    public TimeSpan WrapupTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Answer target used for the service level figure.</summary>
    public TimeSpan ServiceLevelTarget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Tell callers their position while they wait.</summary>
    public bool AnnouncePosition { get; init; } = true;

    /// <summary>How often the position announcement repeats.</summary>
    public TimeSpan AnnounceEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A 16-bit WAV file played on loop while callers wait.</summary>
    public string? MusicOnHoldFile { get; init; }
}

/// <summary>A caller waiting in a queue.</summary>
public sealed class QueuedCall
{
    /// <summary>The waiting call.</summary>
    public required VoipCall Call { get; init; }

    /// <summary>Queue the caller is in.</summary>
    public required string QueueName { get; init; }

    /// <summary>Higher numbers are answered first.</summary>
    public int Priority { get; init; }

    /// <summary>When the caller joined the queue.</summary>
    public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>How long the caller has been waiting.</summary>
    public TimeSpan Waiting => DateTimeOffset.UtcNow - EnqueuedAt;

    /// <summary>Position in the queue, counting from one.</summary>
    public int Position { get; internal set; }

    /// <summary>Anything the IVR collected, carried to the agent's screen.</summary>
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();
}

/// <summary>How a queued call ended.</summary>
public enum QueueOutcome
{
    /// <summary>An agent answered and the calls were bridged.</summary>
    Answered,

    /// <summary>The caller hung up while waiting.</summary>
    Abandoned,

    /// <summary>Nobody answered in time and the caller went to the overflow target.</summary>
    Overflowed,

    /// <summary>The queue was shut down.</summary>
    Cancelled,
}

/// <summary>Result of waiting in a queue.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Agent">The agent who answered, when any.</param>
/// <param name="Waited">How long the caller waited.</param>
public sealed record QueueResult(QueueOutcome Outcome, Agent? Agent, TimeSpan Waited);

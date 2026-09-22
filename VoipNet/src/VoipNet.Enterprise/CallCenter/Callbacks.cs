namespace VoipNet.Enterprise.CallCenter;

/// <summary>A caller who hung up but kept their place in the queue.</summary>
public sealed class CallbackRequest
{
    /// <summary>Identifier, for dashboards and cancellation.</summary>
    public required string Id { get; init; }

    /// <summary>Queue the caller was waiting in.</summary>
    public required string QueueName { get; init; }

    /// <summary>Where to call back, usually the caller's own number.</summary>
    public required string Destination { get; init; }

    /// <summary>Priority carried over from the queue entry.</summary>
    public int Priority { get; init; }

    /// <summary>When the callback was asked for.</summary>
    public DateTimeOffset RequestedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Not before this time, for a caller who asked to be rung later.</summary>
    public DateTimeOffset NotBefore { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>How long the caller had already waited before giving up the line.</summary>
    public TimeSpan AlreadyWaited { get; init; }

    /// <summary>Whatever the IVR collected, carried to the agent's screen.</summary>
    public IReadOnlyDictionary<string, string> Context { get; init; } = new Dictionary<string, string>();

    /// <summary>Attempts made so far.</summary>
    public int Attempts { get; internal set; }

    /// <summary>When the last attempt was made.</summary>
    public DateTimeOffset? LastAttempt { get; internal set; }

    /// <summary>How it ended, once it has.</summary>
    public CallbackOutcome Outcome { get; internal set; } = CallbackOutcome.Waiting;

    /// <summary>True while the callback is still to be made.</summary>
    public bool IsPending => Outcome == CallbackOutcome.Waiting;
}

/// <summary>How a callback ended.</summary>
public enum CallbackOutcome
{
    /// <summary>Still queued for a free agent.</summary>
    Waiting,

    /// <summary>The caller was reached and an agent took the call.</summary>
    Connected,

    /// <summary>Nobody picked up at the caller's number, after every attempt.</summary>
    NoAnswer,

    /// <summary>The agent hung up before the caller answered.</summary>
    AgentLost,

    /// <summary>Cancelled by the application, or the service shut down.</summary>
    Cancelled,
}

/// <summary>How callbacks are made for one queue.</summary>
public sealed class CallbackOptions
{
    /// <summary>How long the caller's phone rings before the attempt is given up.</summary>
    public TimeSpan RingTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How many times a caller who does not answer is tried again.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long to wait before trying a caller again.</summary>
    public TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Played to the caller when they answer, before the agent is bridged in.</summary>
    public string? Announcement { get; set; } = "Ini panggilan balik dari layanan pelanggan. Mohon tunggu sebentar.";
}

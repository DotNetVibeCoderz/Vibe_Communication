namespace VoipNet.Enterprise.Ivr;

/// <summary>What the IVR does when a caller picks an option.</summary>
public abstract record IvrAction
{
    /// <summary>Moves to another menu.</summary>
    /// <param name="MenuId">Menu to show next.</param>
    public sealed record Goto(string MenuId) : IvrAction;

    /// <summary>Speaks a line and stays on the same menu.</summary>
    /// <param name="Text">What to say.</param>
    public sealed record Say(string Text) : IvrAction;

    /// <summary>Transfers the call.</summary>
    /// <param name="Target">Destination URI or extension.</param>
    /// <param name="Announcement">Optional line spoken before transferring.</param>
    public sealed record Transfer(string Target, string? Announcement = null) : IvrAction;

    /// <summary>Puts the caller into a queue.</summary>
    /// <param name="QueueName">Queue to join.</param>
    /// <param name="Announcement">Optional line spoken before queueing.</param>
    public sealed record Enqueue(string QueueName, string? Announcement = null) : IvrAction;

    /// <summary>Hands the call to an AI agent or any custom handler.</summary>
    /// <param name="Name">Handler name, for logs.</param>
    /// <param name="Handler">The handler. It owns the call until it returns.</param>
    public sealed record Handoff(string Name, Func<VoipCall, IvrContext, CancellationToken, Task> Handler) : IvrAction;

    /// <summary>Asks the caller to key in a value, then moves on.</summary>
    /// <param name="Key">Name the value is stored under in <see cref="IvrContext.Values"/>.</param>
    /// <param name="Prompt">What to ask.</param>
    /// <param name="MaxDigits">Longest accepted value.</param>
    /// <param name="NextMenuId">Menu to show afterwards.</param>
    /// <param name="Terminator">Digit that ends the entry early.</param>
    public sealed record Collect(string Key, string Prompt, int MaxDigits, string NextMenuId, char Terminator = '#') : IvrAction;

    /// <summary>Says goodbye and hangs up.</summary>
    /// <param name="Announcement">Optional farewell.</param>
    public sealed record Hangup(string? Announcement = null) : IvrAction;
}

/// <summary>One option in a menu.</summary>
/// <param name="Digit">Key the caller presses.</param>
/// <param name="Description">What the option does, used for prompts and documentation.</param>
/// <param name="Action">What happens when it is chosen.</param>
public sealed record IvrChoice(char Digit, string Description, IvrAction Action);

/// <summary>A menu: a prompt plus the options that follow it.</summary>
public sealed class IvrMenu
{
    /// <summary>Identifier used by <see cref="IvrAction.Goto"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Spoken prompt. Ignored when <see cref="PromptAudioFile"/> is set.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>A 16-bit WAV file played instead of synthesising <see cref="Prompt"/>.</summary>
    public string? PromptAudioFile { get; init; }

    /// <summary>Options, keyed by the digit that selects them.</summary>
    public Dictionary<char, IvrChoice> Choices { get; init; } = [];

    /// <summary>How long to wait for a key press.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>How many times the prompt is repeated before <see cref="OnFailure"/> runs.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Spoken when the caller presses a key with no option.</summary>
    public string InvalidPrompt { get; init; } = "Maaf, pilihan tidak tersedia.";

    /// <summary>Runs when the caller neither chooses nor answers after <see cref="MaxAttempts"/>.</summary>
    public IvrAction OnFailure { get; init; } = new IvrAction.Hangup("Terima kasih telah menghubungi kami.");
}

/// <summary>A complete IVR: menus plus the one that starts the call.</summary>
public sealed class IvrFlow
{
    /// <summary>Menu the call starts on.</summary>
    public required string StartMenuId { get; init; }

    /// <summary>Every menu in the flow, keyed by id.</summary>
    public required IReadOnlyDictionary<string, IvrMenu> Menus { get; init; }

    /// <summary>Spoken once when the call connects, before the first menu.</summary>
    public string Welcome { get; init; } = string.Empty;

    /// <summary>Starts building a flow.</summary>
    public static IvrFlowBuilder Create() => new();
}

/// <summary>State carried through one run of an IVR.</summary>
public sealed class IvrContext
{
    /// <summary>Values collected from the caller, keyed by the name given to <see cref="IvrAction.Collect"/>.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Menus visited, in order.</summary>
    public List<string> Path { get; } = [];

    /// <summary>When the IVR started.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Queue the caller should join, set by <see cref="IvrAction.Enqueue"/>.</summary>
    public string? RequestedQueue { get; internal set; }
}

/// <summary>Fluent builder for <see cref="IvrFlow"/>.</summary>
public sealed class IvrFlowBuilder
{
    private readonly Dictionary<string, IvrMenu> _menus = [];
    private string? _start;
    private string _welcome = string.Empty;

    /// <summary>Sets the line spoken when the call connects.</summary>
    /// <param name="text">What to say.</param>
    public IvrFlowBuilder Welcome(string text)
    {
        _welcome = text;
        return this;
    }

    /// <summary>Adds a menu. The first menu added becomes the start menu.</summary>
    /// <param name="id">Menu identifier.</param>
    /// <param name="prompt">Spoken prompt.</param>
    /// <param name="configure">Adds the options.</param>
    public IvrFlowBuilder Menu(string id, string prompt, Action<IvrMenuBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(configure);
        var builder = new IvrMenuBuilder(id, prompt);
        configure(builder);
        _menus[id] = builder.Build();
        _start ??= id;
        return this;
    }

    /// <summary>Overrides which menu the call starts on.</summary>
    /// <param name="id">Menu identifier.</param>
    public IvrFlowBuilder StartAt(string id)
    {
        _start = id;
        return this;
    }

    /// <summary>Builds the flow and checks that every target menu exists.</summary>
    public IvrFlow Build()
    {
        if (_start is null || _menus.Count == 0)
        {
            throw new InvalidOperationException("An IVR flow needs at least one menu.");
        }

        foreach (var menu in _menus.Values)
        {
            foreach (var target in menu.Choices.Values.Select(c => c.Action).Concat([menu.OnFailure]))
            {
                var missing = target switch
                {
                    IvrAction.Goto go when !_menus.ContainsKey(go.MenuId) => go.MenuId,
                    IvrAction.Collect collect when !_menus.ContainsKey(collect.NextMenuId) => collect.NextMenuId,
                    _ => null,
                };
                if (missing is not null)
                {
                    throw new InvalidOperationException($"Menu '{menu.Id}' points at '{missing}', which does not exist.");
                }
            }
        }

        return new IvrFlow { StartMenuId = _start, Menus = _menus, Welcome = _welcome };
    }
}

/// <summary>Fluent builder for one <see cref="IvrMenu"/>.</summary>
/// <param name="id">Menu identifier.</param>
/// <param name="prompt">Spoken prompt.</param>
public sealed class IvrMenuBuilder(string id, string prompt)
{
    private readonly Dictionary<char, IvrChoice> _choices = [];
    private string? _audioFile;
    private TimeSpan _timeout = TimeSpan.FromSeconds(6);
    private int _maxAttempts = 3;
    private string _invalid = "Maaf, pilihan tidak tersedia.";
    private IvrAction _onFailure = new IvrAction.Hangup("Terima kasih telah menghubungi kami.");

    /// <summary>Adds an option.</summary>
    /// <param name="digit">Key the caller presses.</param>
    /// <param name="description">What the option does.</param>
    /// <param name="action">What happens when it is chosen.</param>
    public IvrMenuBuilder Option(char digit, string description, IvrAction action)
    {
        _choices[digit] = new IvrChoice(digit, description, action);
        return this;
    }

    /// <summary>Plays a WAV file instead of synthesising the prompt.</summary>
    /// <param name="path">Path to a 16-bit WAV file.</param>
    public IvrMenuBuilder PromptFromFile(string path)
    {
        _audioFile = path;
        return this;
    }

    /// <summary>Sets how long to wait for a key press.</summary>
    /// <param name="timeout">The wait.</param>
    public IvrMenuBuilder WaitFor(TimeSpan timeout)
    {
        _timeout = timeout;
        return this;
    }

    /// <summary>Sets how many times the prompt repeats before giving up.</summary>
    /// <param name="attempts">Number of attempts.</param>
    public IvrMenuBuilder Attempts(int attempts)
    {
        _maxAttempts = Math.Max(attempts, 1);
        return this;
    }

    /// <summary>Sets the line spoken after an unknown key.</summary>
    /// <param name="text">What to say.</param>
    public IvrMenuBuilder InvalidPrompt(string text)
    {
        _invalid = text;
        return this;
    }

    /// <summary>Sets what happens when the caller never chooses.</summary>
    /// <param name="action">Fallback action.</param>
    public IvrMenuBuilder OnFailure(IvrAction action)
    {
        _onFailure = action;
        return this;
    }

    internal IvrMenu Build() => new()
    {
        Id = id,
        Prompt = prompt,
        PromptAudioFile = _audioFile,
        Choices = _choices,
        Timeout = _timeout,
        MaxAttempts = _maxAttempts,
        InvalidPrompt = _invalid,
        OnFailure = _onFailure,
    };
}

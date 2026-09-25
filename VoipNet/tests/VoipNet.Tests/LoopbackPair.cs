namespace VoipNet.Tests;

/// <summary>Two started clients with one connected call between them.</summary>
public sealed class LoopbackPair : IAsyncDisposable
{
    private LoopbackPair(VoipClient caller, VoipClient callee, VoipCall callerLeg, VoipCall calleeLeg)
    {
        Caller = caller;
        Callee = callee;
        CallerLeg = callerLeg;
        CalleeLeg = calleeLeg;
    }

    public VoipClient Caller { get; }

    public VoipClient Callee { get; }

    /// <summary>The call as the caller sees it.</summary>
    public VoipCall CallerLeg { get; }

    /// <summary>The call as the callee sees it.</summary>
    public VoipCall CalleeLeg { get; }

    /// <param name="callerName">SIP user name of the calling side.</param>
    /// <param name="calleeName">SIP user name of the answering side.</param>
    /// <param name="configure">Applied to both sides.</param>
    /// <param name="configureCaller">Applied to the caller only, for tests where the two differ.</param>
    public static async Task<LoopbackPair> ConnectAsync(
        string callerName = "caller",
        string calleeName = "callee",
        Action<VoipClientOptions>? configure = null,
        Action<VoipClientOptions>? configureCaller = null)
    {
        var callerOptions = TestHelpers.LoopbackOptions(callerName);
        var calleeOptions = TestHelpers.LoopbackOptions(calleeName);
        configure?.Invoke(callerOptions);
        configure?.Invoke(calleeOptions);
        configureCaller?.Invoke(callerOptions);
        var caller = new VoipClient(callerOptions);
        var callee = new VoipClient(calleeOptions);
        await caller.StartAsync();
        await callee.StartAsync();

        VoipCall? incoming = null;
        callee.IncomingCall += (_, e) => incoming = e.Call;
        var callTask = caller.CallAsync($"sip:{calleeName}@{callee.LocalAddress}");
        var calleeLeg = await TestHelpers.WaitAsync(() => incoming, TimeSpan.FromSeconds(10), "incoming call");
        await calleeLeg.AnswerAsync();
        var callerLeg = await callTask.WaitAsync(TimeSpan.FromSeconds(10));
        return new LoopbackPair(caller, callee, callerLeg, calleeLeg);
    }

    public async ValueTask DisposeAsync()
    {
        await Caller.DisposeAsync();
        await Callee.DisposeAsync();
    }
}

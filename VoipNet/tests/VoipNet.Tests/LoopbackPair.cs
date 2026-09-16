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

    public static async Task<LoopbackPair> ConnectAsync(string callerName = "caller", string calleeName = "callee")
    {
        var caller = new VoipClient(TestHelpers.LoopbackOptions(callerName));
        var callee = new VoipClient(TestHelpers.LoopbackOptions(calleeName));
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

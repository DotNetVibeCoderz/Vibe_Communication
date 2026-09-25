using Microsoft.Extensions.AI;
using VoipNet.Enterprise.CallCenter;
using VoipNet.Enterprise.Crm;
using VoipNet.Enterprise.Ivr;
using VoipNet.Enterprise.Recording;
using Xunit;

namespace VoipNet.Tests;

public sealed class EnterpriseTests
{
    [Fact]
    public void IvrBuilderRejectsDanglingMenus()
    {
        var error = Assert.Throws<InvalidOperationException>(() => IvrFlow.Create()
            .Menu("main", "Tekan 1", m => m.Option('1', "Sales", new IvrAction.Goto("sales")))
            .Build());
        Assert.Contains("sales", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IvrFollowsDigitsCollectsValuesAndQueues()
    {
        await using var pair = await LoopbackPair.ConnectAsync("customer", "ivr");
        var flow = IvrFlow.Create()
            .Menu("main", "Tekan 1 untuk penjualan, 2 untuk bantuan.", m => m
                .Option('1', "Penjualan", new IvrAction.Transfer("sip:sales@example.com"))
                .Option('2', "Bantuan", new IvrAction.Collect("account", "Masukkan nomor pelanggan diakhiri pagar.", 8, "support"))
                .WaitFor(TimeSpan.FromSeconds(5)))
            .Menu("support", "Terima kasih.", m => m
                .Option('9', "Antre", new IvrAction.Enqueue("support"))
                .WaitFor(TimeSpan.FromSeconds(5)))
            .Build();

        var runner = new IvrRunner(new ToneTextToSpeech());
        var run = runner.RunAsync(pair.CalleeLeg, flow);

        // Press keys once each prompt has played: the runner ignores keys pressed before a menu starts,
        // and fixed delays were too short on slow CI machines.
        await PromptPlayedAsync(pair.CalleeLeg);
        pair.CallerLeg.SendDtmf("2", 80);
        await PromptPlayedAsync(pair.CalleeLeg);
        pair.CallerLeg.SendDtmf("4321#", 80);

        // The last prompt is short; keep pressing 9 until the menu that follows takes it. Digits queued after
        // "#" never reach the account number, and keys pressed before the menu starts are discarded.
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (!run.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(700);
            pair.CallerLeg.SendDtmf("9", 80);
        }

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(IvrOutcome.Queued, result.Outcome);
        Assert.Equal("4321", result.Context.Values["account"]);
        Assert.Equal("support", result.Context.RequestedQueue);
        Assert.Equal(["main", "support"], result.Context.Path);
    }

    /// <summary>Waits until the leg starts sending a prompt and has sent all of it.</summary>
    private static async Task PromptPlayedAsync(VoipCall leg)
    {
        await TestHelpers.WaitUntilAsync(() => leg.QueuedAudioMs > 0, TimeSpan.FromSeconds(15), "prompt audio");
        await TestHelpers.WaitUntilAsync(() => leg.QueuedAudioMs == 0, TimeSpan.FromSeconds(15), "prompt to finish");
        await Task.Delay(150);
    }

    [Fact]
    public async Task QueuedCallerIsBridgedToAnAvailableAgent()
    {
        await using var pbx = new VoipClient(TestHelpers.LoopbackOptions("pbx"));
        await using var agentPhone = new VoipClient(TestHelpers.LoopbackOptions("agent"));
        await using var customer = new VoipClient(TestHelpers.LoopbackOptions("customer"));
        await pbx.StartAsync();
        await agentPhone.StartAsync();
        await customer.StartAsync();

        // The agent's softphone answers automatically.
        agentPhone.IncomingCall += (_, e) => _ = e.Call.AnswerAsync();
        VoipCall? inbound = null;
        pbx.IncomingCall += (_, e) => { inbound = e.Call; _ = e.Call.AnswerAsync(); };

        await using var center = new CallCenterService(pbx);
        center.AddQueue(new CallQueueOptions { Name = "support", AnnouncePosition = false, WrapupTime = TimeSpan.FromMilliseconds(200) });
        center.AddAgent(new Agent { Id = "a1", Name = "Sari", Uri = $"sip:agent@{agentPhone.LocalAddress}", Skills = { "support" } });
        center.SetAgentState("a1", AgentState.Available);

        var customerCall = await customer.CallAsync($"sip:pbx@{pbx.LocalAddress}");
        var pbxLeg = await TestHelpers.WaitAsync(() => inbound, TimeSpan.FromSeconds(10), "pbx leg");
        await pbxLeg.Connected.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await center.EnqueueAsync(pbxLeg, "support").WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(QueueOutcome.Answered, result.Outcome);
        Assert.Equal("Sari", result.Agent?.Name);
        Assert.Equal(AgentState.OnCall, center.Agents.Single().State);

        // Audio from the customer reaches the agent through the bridge.
        var agentCall = agentPhone.Calls.Single(c => c.IsActive);
        var heard = 0;
        agentCall.AudioReceived += (_, direction, _, samples) =>
        {
            if (direction == AudioDirection.Inbound && VoipNet.Audio.Pcm.Rms(samples) > 0.05)
            {
                Interlocked.Increment(ref heard);
            }
        };
        customerCall.SendAudio(TestHelpers.Tone(16000, 1500), 16000);
        await TestHelpers.WaitUntilAsync(() => heard > 10, TimeSpan.FromSeconds(8), "agent hears the customer");

        // A supervisor listens in: they hear the customer, but the agent never hears them.
        await using var supervisorPhone = new VoipClient(TestHelpers.LoopbackOptions("supervisor"));
        await supervisorPhone.StartAsync();
        VoipCall? supervisorSide = null;
        supervisorPhone.IncomingCall += (_, e) => { supervisorSide = e.Call; _ = e.Call.AnswerAsync(); };
        var pbxToSupervisor = await pbx.CallAsync($"sip:supervisor@{supervisorPhone.LocalAddress}");
        Assert.True(center.Monitor(pbxLeg.Id, pbxToSupervisor, whisper: false));
        var supervisorLeg = await TestHelpers.WaitAsync(() => supervisorSide, TimeSpan.FromSeconds(5), "supervisor leg");

        var supervisorHeard = 0;
        supervisorLeg.AudioReceived += (_, direction, _, samples) =>
        {
            if (direction == AudioDirection.Inbound && VoipNet.Audio.Pcm.Rms(samples) > 0.05)
            {
                Interlocked.Increment(ref supervisorHeard);
            }
        };
        await Task.Delay(1500); // let the customer's earlier audio drain
        customerCall.SendAudio(TestHelpers.Tone(16000, 1500), 16000);
        await TestHelpers.WaitUntilAsync(() => supervisorHeard > 10, TimeSpan.FromSeconds(8), "supervisor hears the customer");

        // Wait until the customer's tone has fully played out at the agent (slow runners buffer more),
        // so anything the agent hears afterwards can only come from the supervisor.
        await TestHelpers.WaitUntilAsync(() => customerCall.QueuedAudioMs == 0, TimeSpan.FromSeconds(10), "customer audio sent");
        var quietDeadline = DateTime.UtcNow.AddSeconds(10);
        var lastHeard = heard;
        var quietSince = DateTime.UtcNow;
        while (DateTime.UtcNow - quietSince < TimeSpan.FromMilliseconds(700) && DateTime.UtcNow < quietDeadline)
        {
            await Task.Delay(50);
            if (heard != lastHeard)
            {
                lastHeard = heard;
                quietSince = DateTime.UtcNow;
            }
        }

        Interlocked.Exchange(ref heard, 0);
        supervisorLeg.SendAudio(TestHelpers.Tone(16000, 1200, 700), 16000);
        await Task.Delay(1600);
        Assert.True(heard < 3, $"the agent heard the listen-only supervisor in {heard} frames");

        await customerCall.HangupAsync();
        await TestHelpers.WaitUntilAsync(() => center.Agents.Single().State == AgentState.Available, TimeSpan.FromSeconds(10), "agent back to available");

        var metrics = center.Metrics.Snapshot("support");
        Assert.Equal(1, metrics.Offered);
        Assert.Equal(1, metrics.Answered);
        Assert.Equal(1.0, metrics.ServiceLevel);
        Assert.Equal(1, center.Agents.Single().HandledCalls);
    }

    [Fact]
    public async Task CallerWhoHangsUpIsCountedAsAbandoned()
    {
        await using var pair = await LoopbackPair.ConnectAsync("customer", "pbx");
        await using var center = new CallCenterService(pair.Callee);
        center.AddQueue(new CallQueueOptions { Name = "sales", AnnouncePosition = false });

        var wait = center.EnqueueAsync(pair.CalleeLeg, "sales");
        await TestHelpers.WaitUntilAsync(() => center.Waiting("sales").Count == 1, TimeSpan.FromSeconds(5), "queued");
        await pair.CallerLeg.HangupAsync();

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueOutcome.Abandoned, result.Outcome);
        Assert.Equal(1, center.Metrics.Snapshot("sales").Abandoned);
    }

    [Fact]
    public async Task ACallerWhoAsksForACallbackIsRungBackAndBridged()
    {
        // The customer's phone answers whatever comes in, which is what the callback rings.
        await using var customer = new VoipClient(TestHelpers.LoopbackOptions("customer"));
        await using var agentPhone = new VoipClient(TestHelpers.LoopbackOptions("agent"));
        await using var pbx = new VoipClient(TestHelpers.LoopbackOptions("pbx"));
        await customer.StartAsync();
        await agentPhone.StartAsync();
        await pbx.StartAsync();

        VoipCall? calledBack = null;
        customer.IncomingCall += (_, e) => { calledBack = e.Call; _ = e.Call.AnswerAsync(); };
        agentPhone.IncomingCall += (_, e) => _ = e.Call.AnswerAsync();
        VoipCall? inbound = null;
        pbx.IncomingCall += (_, e) => { inbound = e.Call; _ = e.Call.AnswerAsync(); };

        await using var center = new CallCenterService(pbx);
        center.AddQueue(new CallQueueOptions
        {
            Name = "support",
            AnnouncePosition = false,
            WrapupTime = TimeSpan.FromMilliseconds(200),
            Callbacks = new CallbackOptions { Announcement = null, RingTimeout = TimeSpan.FromSeconds(10) },
        });
        center.AddAgent(new Agent { Id = "a1", Name = "Sari", Uri = $"sip:agent@{agentPhone.LocalAddress}" });

        var customerCall = await customer.CallAsync($"sip:pbx@{pbx.LocalAddress}");
        var pbxLeg = await TestHelpers.WaitAsync(() => inbound, TimeSpan.FromSeconds(10), "pbx leg");
        await pbxLeg.Connected.WaitAsync(TimeSpan.FromSeconds(5));

        // Nobody is signed in yet, so there is no honest estimate to give.
        Assert.Equal(TimeSpan.MaxValue, center.EstimatedWait("support"));

        var wait = center.EnqueueAsync(pbxLeg, "support");
        var queued = await TestHelpers.WaitAsync(
            () => center.Waiting("support").FirstOrDefault(),
            TimeSpan.FromSeconds(5),
            "queued caller");

        CallbackRequest? completed = null;
        center.CallbackCompleted += (_, request) => completed = request;
        var callback = center.RequestCallback(queued, $"sip:customer@{customer.LocalAddress}");
        Assert.Single(center.PendingCallbacks("support"));

        // The caller hangs up; their place is kept.
        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(QueueOutcome.CallbackScheduled, result.Outcome);
        await customerCall.HangupAsync();

        // An agent signs in, and the service rings the customer back.
        center.SetAgentState("a1", AgentState.Available);
        await TestHelpers.WaitUntilAsync(() => completed is not null, TimeSpan.FromSeconds(20), "callback made");

        Assert.Equal(CallbackOutcome.Connected, completed!.Outcome);
        Assert.Equal(callback.Id, completed.Id);
        Assert.NotNull(calledBack);
        Assert.True(calledBack!.IsActive, "the customer is on the call the service placed");
        Assert.Equal(AgentState.OnCall, center.Agents.Single().State);
        Assert.Empty(center.PendingCallbacks("support"));
    }

    [Fact]
    public async Task EstimatedWaitGrowsWithTheQueueAndShrinksWithAgents()
    {
        await using var pbx = new VoipClient(TestHelpers.LoopbackOptions("pbx"));
        await pbx.StartAsync();
        await using var center = new CallCenterService(pbx);
        center.AddQueue(new CallQueueOptions { Name = "sales", AnnouncePosition = false, WrapupTime = TimeSpan.Zero });

        center.AddAgent(new Agent { Id = "a1", Name = "Sari", Uri = "sip:a1@localhost" });
        center.SetAgentState("a1", AgentState.OnCall);
        // No history yet, so the estimate uses the three minute fallback: one caller ahead, one agent.
        Assert.Equal(TimeSpan.FromMinutes(3), center.EstimatedWait("sales", position: 1));
        Assert.Equal(TimeSpan.FromMinutes(9), center.EstimatedWait("sales", position: 3));

        center.AddAgent(new Agent { Id = "a2", Name = "Budi", Uri = "sip:a2@localhost" });
        center.SetAgentState("a2", AgentState.OnCall);
        Assert.Equal(TimeSpan.FromMinutes(4.5), center.EstimatedWait("sales", position: 3));

        // An agent who is free takes the next caller straight away.
        center.SetAgentState("a2", AgentState.Available);
        Assert.Equal(TimeSpan.Zero, center.EstimatedWait("sales", position: 1));
    }

    [Fact]
    public async Task RecordingServiceIndexesCalls()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"voipnet-recs-{Guid.NewGuid():N}");
        await using var pair = await LoopbackPair.ConnectAsync();
        using var service = new RecordingService(pair.Callee, new RecordingOptions { Directory = dir, Format = VoipNet.Audio.RecordingFormat.Wav });

        service.Start(pair.CalleeLeg, new Dictionary<string, string> { ["queue"] = "support" });
        // One direction only, so the recorder pairs the caller against half a second of silence
        // before it writes anything: wait for the audio to have arrived rather than for a clock.
        pair.CallerLeg.SendAudio(TestHelpers.Tone(16000, 2000), 16000);
        await TestHelpers.ReceivedAudioAsync(pair.CalleeLeg, 1400);
        var info = service.Stop(pair.CalleeLeg);

        Assert.NotNull(info);
        var listed = Assert.Single(service.List());
        Assert.Equal("support", listed.Tags["queue"]);
        Assert.True(listed.Duration > TimeSpan.FromMilliseconds(300));
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task CrmToolsCallTheConnector()
    {
        var crm = new InMemoryCrmConnector().Add(new CustomerRecord("C-1", "Budi", "+62 812-3456-7890", null, "Gold", null));
        var tools = CrmToolset.Create(crm).OfType<AIFunction>().ToDictionary(t => t.Name);

        var lookup = await tools["crm_lookup_customer"].InvokeAsync(new AIFunctionArguments { ["phone"] = "sip:081234567890@pbx" });
        Assert.Contains("Budi", lookup?.ToString(), StringComparison.Ordinal);

        await tools["crm_create_ticket"].InvokeAsync(new AIFunctionArguments { ["customerId"] = "C-1", ["subject"] = "Internet lambat", ["description"] = "Sejak pagi" });
        var tickets = await crm.RecentTicketsAsync("C-1");
        Assert.Equal("Internet lambat", Assert.Single(tickets).Subject);

        await tools["crm_add_note"].InvokeAsync(new AIFunctionArguments { ["customerId"] = "C-1", ["note"] = "Sudah dibantu" });
        Assert.Equal("Sudah dibantu", Assert.Single(crm.NotesFor("C-1")));
    }
}

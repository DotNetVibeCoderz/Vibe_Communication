using Microsoft.Data.Sqlite;
using VoipNet.Enterprise.CallCenter;
using Xunit;

namespace VoipNet.Tests;

/// <summary>The shared call centre state, against a real SQLite database.</summary>
public sealed class StoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"voipnet-store-{Guid.NewGuid():N}.db");

    private SqlCallCenterStore Store(string node) =>
        new(() => new SqliteConnection($"Data Source={_path}"), node);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public async Task AgentStateIsSharedBetweenNodes()
    {
        var first = Store("pbx-1");
        var second = Store("pbx-2");

        await first.SaveAgentAsync(new StoredAgent("a1", "Sari", "sip:a1@pbx", AgentState.Available, DateTimeOffset.UtcNow, "pbx-1"));
        await second.SaveAgentAsync(new StoredAgent("a2", "Budi", "sip:a2@pbx", AgentState.OnCall, DateTimeOffset.UtcNow, "pbx-2"));

        var agents = await second.LoadAgentsAsync();
        Assert.Equal(["a1", "a2"], agents.Select(a => a.Id));
        Assert.Equal(AgentState.Available, agents[0].State);
        Assert.Equal("pbx-2", agents[1].Node);

        // The same agent signing in again replaces the old row rather than adding one.
        await first.SaveAgentAsync(new StoredAgent("a1", "Sari", "sip:a1@pbx", AgentState.Paused, DateTimeOffset.UtcNow, "pbx-1"));
        agents = await first.LoadAgentsAsync();
        Assert.Equal(2, agents.Count);
        Assert.Equal(AgentState.Paused, agents.Single(a => a.Id == "a1").State);
    }

    [Fact]
    public async Task OnlyOneNodeCanClaimACallback()
    {
        var first = Store("pbx-1");
        var second = Store("pbx-2");
        var request = new CallbackRequest
        {
            Id = "cb1",
            QueueName = "support",
            Destination = "sip:customer@example.com",
            Priority = 2,
            AlreadyWaited = TimeSpan.FromMinutes(4),
        };
        await first.SaveCallbackAsync(request);

        var pending = await second.LoadCallbacksAsync("support");
        var restored = Assert.Single(pending);
        Assert.Equal("sip:customer@example.com", restored.Destination);
        Assert.Equal(2, restored.Priority);
        Assert.Equal(TimeSpan.FromMinutes(4), restored.AlreadyWaited);
        Assert.Empty(await second.LoadCallbacksAsync("sales"));

        Assert.True(await first.TryClaimCallbackAsync("cb1", "pbx-1"));
        Assert.False(await second.TryClaimCallbackAsync("cb1", "pbx-2"));

        // A caller who did not pick up goes back in the pool for whichever node is free next.
        await first.CompleteCallbackAsync("cb1", CallbackOutcome.Waiting, attempts: 1);
        Assert.True(await second.TryClaimCallbackAsync("cb1", "pbx-2"));

        await second.CompleteCallbackAsync("cb1", CallbackOutcome.Connected, attempts: 2);
        Assert.Empty(await first.LoadCallbacksAsync("support"));
    }

    [Fact]
    public async Task ACallCentreResumesTheCallbacksItOwes()
    {
        await using var pbx = new VoipClient(TestHelpers.LoopbackOptions("pbx"));
        await pbx.StartAsync();
        var store = Store("pbx-1");

        // A node that has gone away left a promise behind.
        await store.SaveCallbackAsync(new CallbackRequest
        {
            Id = "cb-restart",
            QueueName = "support",
            Destination = "sip:customer@example.com",
            AlreadyWaited = TimeSpan.FromMinutes(2),
        });

        await using var center = new CallCenterService(pbx, store: store);
        center.AddQueue(new CallQueueOptions { Name = "support", AnnouncePosition = false });
        Assert.Empty(center.PendingCallbacks("support"));

        await center.RestoreAsync();

        var restored = Assert.Single(center.PendingCallbacks("support"));
        Assert.Equal("cb-restart", restored.Id);
        Assert.Equal("sip:customer@example.com", restored.Destination);
    }

    [Fact]
    public async Task AgentSignInsReachTheStore()
    {
        await using var pbx = new VoipClient(TestHelpers.LoopbackOptions("pbx"));
        await pbx.StartAsync();
        var store = Store("pbx-1");
        await using var center = new CallCenterService(pbx, store: store) { NodeName = "pbx-1" };
        center.AddQueue(new CallQueueOptions { Name = "support", AnnouncePosition = false });
        center.AddAgent(new Agent { Id = "a1", Name = "Sari", Uri = "sip:a1@pbx" });

        center.SetAgentState("a1", AgentState.Available);

        var agents = await TestHelpers.WaitAsync(
            async () => (await store.LoadAgentsAsync()) is { Count: > 0 } list ? list : null,
            TimeSpan.FromSeconds(5),
            "agent in the store");
        Assert.Equal("Sari", agents[0].Name);
        Assert.Equal(AgentState.Available, agents[0].State);
        Assert.Equal("pbx-1", agents[0].Node);
    }
}

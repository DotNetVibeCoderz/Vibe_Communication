namespace VoipNet.Gallery.Infrastructure;

/// <summary>
/// A small telephone lab: named endpoints on the loopback interface that call each other. Pages ask
/// for the parties they need and the lab tears everything down between demos.
/// </summary>
public sealed class Lab : IAsyncDisposable
{
    private static int _portBase = 47000;
    private readonly List<VoipClient> _clients = [];

    /// <summary>Starts an endpoint called <paramref name="name"/>.</summary>
    /// <param name="name">User part of its address.</param>
    /// <param name="configure">Adjusts the options before starting.</param>
    public async Task<VoipClient> StartAsync(string name, Action<VoipClientOptions>? configure = null)
    {
        var min = Interlocked.Add(ref _portBase, 100);
        if (min > 64000)
        {
            Interlocked.Exchange(ref _portBase, 47000);
        }

        var options = new VoipClientOptions
        {
            BindAddress = "127.0.0.1",
            SipPort = 0,
            Username = name,
            DisplayName = char.ToUpperInvariant(name[0]) + name[1..],
            RtpPortMin = min,
            RtpPortMax = min + 99,
            JitterMinMs = 20,
            EventSynchronizationContext = SynchronizationContext.Current,
        };
        configure?.Invoke(options);
        var client = new VoipClient(options);
        await client.StartAsync();
        _clients.Add(client);
        return client;
    }

    /// <summary>Makes <paramref name="callee"/> answer every call after a short ring.</summary>
    public static void AutoAnswer(VoipClient callee, int ringMs = 400) =>
        callee.IncomingCall += (_, e) => Safe(async () =>
        {
            await Task.Delay(ringMs);
            if (e.Call.State == CallState.Incoming)
            {
                await e.Call.AnswerAsync();
            }
        });

    /// <summary>
    /// Runs event-handler work that may outlive its demo: when the user switches pages the lab is
    /// torn down, and a call that no longer exists is simply ignored.
    /// </summary>
    public static async void Safe(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is VoipException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
            System.Diagnostics.Debug.WriteLine($"Demo handler stopped: {ex.Message}");
        }
    }

    /// <summary>SIP URI of an endpoint in the lab.</summary>
    public static string Uri(VoipClient client, string user) => $"sip:{user}@{client.LocalAddress}";

    /// <summary>Stops every endpoint.</summary>
    public async Task ResetAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        _clients.Clear();
    }

    public ValueTask DisposeAsync() => new(ResetAsync());
}

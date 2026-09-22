using System.Data.Common;

namespace VoipNet.Enterprise.CallCenter;

/// <summary>An agent as the shared store knows them, which may be another node's agent.</summary>
/// <param name="Id">Agent identifier.</param>
/// <param name="Name">Display name.</param>
/// <param name="Uri">Where the agent is reached.</param>
/// <param name="State">What the agent is doing.</param>
/// <param name="Since">When the state last changed.</param>
/// <param name="Node">The node that last reported this agent.</param>
public sealed record StoredAgent(string Id, string Name, string Uri, AgentState State, DateTimeOffset Since, string Node);

/// <summary>
/// Shared state for a call centre that runs on more than one node: who is signed in where, and which
/// callbacks are still owed. Live calls stay on the node that answered them — only the facts every
/// node has to agree on are stored.
/// </summary>
public interface ICallCenterStore
{
    /// <summary>Records an agent's state, replacing whatever was there.</summary>
    /// <param name="agent">The agent.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveAgentAsync(StoredAgent agent, CancellationToken cancellationToken = default);

    /// <summary>Every agent the store knows, from all nodes.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<StoredAgent>> LoadAgentsAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores a callback so it survives a restart and other nodes can see it.</summary>
    /// <param name="request">The callback.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveCallbackAsync(CallbackRequest request, CancellationToken cancellationToken = default);

    /// <summary>Callbacks still owed for a queue, oldest first.</summary>
    /// <param name="queueName">Queue to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<CallbackRequest>> LoadCallbacksAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes ownership of a callback so that only one node rings the customer. Returns false when
    /// another node claimed it first, or when it is no longer pending.
    /// </summary>
    /// <param name="id">Callback identifier.</param>
    /// <param name="node">The node claiming it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<bool> TryClaimCallbackAsync(string id, string node, CancellationToken cancellationToken = default);

    /// <summary>Records how a callback ended.</summary>
    /// <param name="id">Callback identifier.</param>
    /// <param name="outcome">How it ended.</param>
    /// <param name="attempts">Attempts made.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task CompleteCallbackAsync(string id, CallbackOutcome outcome, int attempts, CancellationToken cancellationToken = default);
}

/// <summary>
/// A store in ordinary SQL, over any ADO.NET provider: SQLite for a single node with a restart to
/// survive, SQL Server or PostgreSQL when several nodes share a queue. The tables are created on first
/// use, so a deployment needs no migration step.
/// </summary>
/// <remarks>
/// Claiming a callback is the one operation that has to be atomic, and it is: a single conditional
/// UPDATE that only succeeds for the first node to run it. Everything else is last-writer-wins, which
/// is what agent state is anyway.
/// </remarks>
public sealed class SqlCallCenterStore : ICallCenterStore
{
    private readonly Func<DbConnection> _connect;
    private readonly string _node;
    private readonly SemaphoreSlim _ready = new(1, 1);
    private bool _created;

    /// <summary>Creates a store.</summary>
    /// <param name="connect">Opens a new connection; the store closes each one it opens.</param>
    /// <param name="node">Name of this node, stored with the rows it writes.</param>
    public SqlCallCenterStore(Func<DbConnection> connect, string? node = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        _connect = connect;
        _node = node ?? Environment.MachineName;
    }

    /// <summary>The name this node writes into the rows it owns.</summary>
    public string Node => _node;

    /// <inheritdoc/>
    public async Task SaveAgentAsync(StoredAgent agent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM voipnet_agents WHERE id = @id",
            cancellationToken,
            ("@id", agent.Id)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "INSERT INTO voipnet_agents (id, name, uri, state, since, node) VALUES (@id, @name, @uri, @state, @since, @node)",
            cancellationToken,
            ("@id", agent.Id),
            ("@name", agent.Name),
            ("@uri", agent.Uri),
            ("@state", agent.State.ToString()),
            ("@since", agent.Since.ToUnixTimeMilliseconds()),
            ("@node", agent.Node)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<StoredAgent>> LoadAgentsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, uri, state, since, node FROM voipnet_agents ORDER BY id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var agents = new List<StoredAgent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            agents.Add(new StoredAgent(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                Enum.TryParse<AgentState>(reader.GetString(3), out var state) ? state : AgentState.Offline,
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.GetString(5)));
        }

        return agents;
    }

    /// <inheritdoc/>
    public async Task SaveCallbackAsync(CallbackRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            "DELETE FROM voipnet_callbacks WHERE id = @id",
            cancellationToken,
            ("@id", request.Id)).ConfigureAwait(false);
        await ExecuteAsync(
            connection,
            """
            INSERT INTO voipnet_callbacks (id, queue, destination, priority, requested_at, not_before, already_waited, attempts, outcome, owner)
            VALUES (@id, @queue, @destination, @priority, @requested, @notBefore, @waited, @attempts, @outcome, NULL)
            """,
            cancellationToken,
            ("@id", request.Id),
            ("@queue", request.QueueName),
            ("@destination", request.Destination),
            ("@priority", request.Priority),
            ("@requested", request.RequestedAt.ToUnixTimeMilliseconds()),
            ("@notBefore", request.NotBefore.ToUnixTimeMilliseconds()),
            ("@waited", (long)request.AlreadyWaited.TotalMilliseconds),
            ("@attempts", request.Attempts),
            ("@outcome", request.Outcome.ToString())).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<CallbackRequest>> LoadCallbacksAsync(string queueName, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, queue, destination, priority, requested_at, not_before, already_waited, attempts
            FROM voipnet_callbacks WHERE queue = @queue AND outcome = 'Waiting' ORDER BY priority DESC, requested_at
            """;
        Add(command, "@queue", queueName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var callbacks = new List<CallbackRequest>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            callbacks.Add(new CallbackRequest
            {
                Id = reader.GetString(0),
                QueueName = reader.GetString(1),
                Destination = reader.GetString(2),
                Priority = (int)reader.GetInt64(3),
                NotBefore = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
                AlreadyWaited = TimeSpan.FromMilliseconds(reader.GetInt64(6)),
                Attempts = (int)reader.GetInt64(7),
            });
        }

        return callbacks;
    }

    /// <inheritdoc/>
    public async Task<bool> TryClaimCallbackAsync(string id, string node, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Only the first node to run this sees a row change, which is what makes the claim exclusive.
        var claimed = await ExecuteAsync(
            connection,
            "UPDATE voipnet_callbacks SET owner = @node WHERE id = @id AND owner IS NULL AND outcome = 'Waiting'",
            cancellationToken,
            ("@node", node),
            ("@id", id)).ConfigureAwait(false);
        return claimed == 1;
    }

    /// <inheritdoc/>
    public async Task CompleteCallbackAsync(string id, CallbackOutcome outcome, int attempts, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // A callback that is not connected yet goes back in the pool for whoever is free next.
        var owner = outcome == CallbackOutcome.Waiting ? null : _node;
        await ExecuteAsync(
            connection,
            "UPDATE voipnet_callbacks SET outcome = @outcome, attempts = @attempts, owner = @owner WHERE id = @id",
            cancellationToken,
            ("@outcome", outcome.ToString()),
            ("@attempts", attempts),
            ("@owner", owner),
            ("@id", id)).ConfigureAwait(false);
    }

    private async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = _connect();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (_created)
        {
            return connection;
        }

        await _ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_created)
            {
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE IF NOT EXISTS voipnet_agents (
                        id TEXT PRIMARY KEY, name TEXT NOT NULL, uri TEXT NOT NULL,
                        state TEXT NOT NULL, since BIGINT NOT NULL, node TEXT NOT NULL)
                    """,
                    cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(
                    connection,
                    """
                    CREATE TABLE IF NOT EXISTS voipnet_callbacks (
                        id TEXT PRIMARY KEY, queue TEXT NOT NULL, destination TEXT NOT NULL,
                        priority INT NOT NULL, requested_at BIGINT NOT NULL, not_before BIGINT NOT NULL,
                        already_waited BIGINT NOT NULL, attempts INT NOT NULL, outcome TEXT NOT NULL, owner TEXT NULL)
                    """,
                    cancellationToken).ConfigureAwait(false);
                _created = true;
            }
        }
        finally
        {
            _ready.Release();
        }

        return connection;
    }

    private static async Task<int> ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace VoipNet.AI.Agents;

/// <summary>Keeps conversations so a returning caller can continue where they left off.</summary>
public interface IConversationStore
{
    /// <summary>Loads the stored turns for a key, oldest first.</summary>
    /// <param name="key">Conversation key, usually the caller's number.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<IReadOnlyList<ConversationTurn>> LoadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Replaces the stored turns for a key.</summary>
    /// <param name="key">Conversation key.</param>
    /// <param name="turns">Turns to store.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task SaveAsync(string key, IReadOnlyList<ConversationTurn> turns, CancellationToken cancellationToken = default);
}

/// <summary>Keeps conversations in memory for the lifetime of the process.</summary>
public sealed class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<ConversationTurn>> _conversations = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public Task<IReadOnlyList<ConversationTurn>> LoadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_conversations.GetValueOrDefault(key, []));

    /// <inheritdoc/>
    public Task SaveAsync(string key, IReadOnlyList<ConversationTurn> turns, CancellationToken cancellationToken = default)
    {
        _conversations[key] = turns;
        return Task.CompletedTask;
    }
}

/// <summary>Stores each conversation as a JSON file, which survives restarts without a database.</summary>
/// <param name="directory">Directory that holds the files. It is created when missing.</param>
public sealed class JsonFileConversationStore(string directory) : IConversationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ConversationTurn>> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(path);
            var turns = await JsonSerializer.DeserializeAsync<List<ConversationTurn>>(stream, Json, cancellationToken).ConfigureAwait(false);
            return turns ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string key, IReadOnlyList<ConversationTurn> turns, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.Create(PathFor(key));
            await JsonSerializer.SerializeAsync(stream, turns, Json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string key)
    {
        var safe = string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(directory, $"{safe}.json");
    }
}

/// <summary>Conversions between stored turns and chat messages.</summary>
public static class ConversationTurnExtensions
{
    /// <summary>Converts stored turns into chat messages.</summary>
    /// <param name="turns">Turns to convert.</param>
    public static IEnumerable<ChatMessage> ToChatMessages(this IEnumerable<ConversationTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(turns);
        foreach (var turn in turns)
        {
            var role = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? ChatRole.Assistant : ChatRole.User;
            yield return new ChatMessage(role, turn.Text);
        }
    }
}

using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Rumble.Net.Events;

namespace Rumble.Net.Bots;

/// <summary>Options for <see cref="AiChatResponder"/>.</summary>
public sealed class AiChatOptions
{
    /// <summary>System prompt describing the assistant.</summary>
    public string SystemPrompt { get; set; } =
        "You are a friendly assistant in a Mumble voice chat server. Answer briefly in plain text (no Markdown).";

    /// <summary>Messages kept per conversation (channel or private chat).</summary>
    public int MaxHistoryMessages { get; set; } = 20;

    /// <summary>In channels, only respond when the message mentions the bot name. Private messages always get a reply.</summary>
    public bool RequireMentionInChannels { get; set; } = true;

    /// <summary>Maximum reply length (characters).</summary>
    public int MaxReplyLength { get; set; } = 2000;

    /// <summary>Extra model options (model id, temperature…).</summary>
    public ChatOptions? ChatOptions { get; set; }
}

/// <summary>
/// Answers chat messages with any <see cref="IChatClient"/> (OpenAI, Azure, Anthropic, Ollama… via
/// their Microsoft.Extensions.AI adapters), keeping a short per-conversation history.
/// </summary>
public sealed class AiChatResponder(IChatClient chatClient, AiChatOptions? options = null)
{
    private readonly AiChatOptions _options = options ?? new AiChatOptions();
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _histories = new();

    /// <summary>Generates a reply, or null when the message should be ignored.</summary>
    public async Task<string?> RespondAsync(RumbleClient client, TextMessage message, CancellationToken cancellationToken = default)
    {
        var botName = client.Self?.Name ?? string.Empty;
        var text = message.PlainText;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!message.IsPrivate && _options.RequireMentionInChannels && !text.Contains(botName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var key = message.IsPrivate ? $"pm:{message.Sender?.Session}" : $"ch:{message.Channels.FirstOrDefault()?.Id}";
        var history = _histories.GetOrAdd(key, _ => []);
        List<ChatMessage> request;
        lock (history)
        {
            history.Add(new ChatMessage(ChatRole.User, $"{message.Sender?.Name ?? "someone"}: {text}"));
            while (history.Count > _options.MaxHistoryMessages)
            {
                history.RemoveAt(0);
            }

            request = [new ChatMessage(ChatRole.System, _options.SystemPrompt), .. history];
        }

        var response = await chatClient.GetResponseAsync(request, _options.ChatOptions, cancellationToken).ConfigureAwait(false);
        var reply = response.Text?.Trim();
        if (string.IsNullOrEmpty(reply))
        {
            return null;
        }

        if (reply.Length > _options.MaxReplyLength)
        {
            reply = reply[.._options.MaxReplyLength] + "…";
        }

        lock (history)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, reply));
        }

        return reply;
    }

    /// <summary>Clears all conversation histories.</summary>
    public void Reset() => _histories.Clear();
}

/// <summary>A ready-made bot that answers chat with an AI model.</summary>
public class AiChatBot(RumbleClientOptions options, IChatClient chatClient, AiChatOptions? aiOptions = null) : RumbleBot(options)
{
    private readonly AiChatResponder _responder = new(chatClient, aiOptions);

    /// <inheritdoc />
    protected override async Task OnMessageAsync(TextMessage message, CancellationToken cancellationToken)
    {
        var reply = await _responder.RespondAsync(Client, message, cancellationToken).ConfigureAwait(false);
        if (reply is null)
        {
            return;
        }

        var html = System.Net.WebUtility.HtmlEncode(reply).Replace("\n", "<br/>");
        if (message.IsPrivate && message.Sender is not null)
        {
            Client.SendPrivateMessage(message.Sender, html);
        }
        else if (message.Channels.Count > 0)
        {
            Client.SendChannelMessage(message.Channels[0], html);
        }
    }

    /// <summary>Clears the conversation memory.</summary>
    [BotCommand("forget", Description = "Clears the AI conversation memory")]
    public void Forget(BotCommandContext context)
    {
        _responder.Reset();
        context.Reply("Memory cleared.");
    }
}

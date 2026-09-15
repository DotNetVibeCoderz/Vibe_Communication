using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Rumble.Net;
using Rumble.Net.Bots;
using RumbleGallery.Infrastructure;

namespace RumbleGallery.Samples;

[Sample("Bots", "AI chat bot with IChatClient", "Answer chat with any Microsoft.Extensions.AI model provider.",
    Description = "AiChatBot works with any IChatClient — OpenAI, Azure OpenAI, Anthropic, Ollama and more through their adapters. This sample uses a tiny offline IChatClient so it runs without an API key; swap in a real client in one line.",
    Order = 85)]
public static class AiChatBotSample
{
    public static async Task RunAsync(SampleContext ctx)
    {
        // In a real app: IChatClient chat = new OpenAIClient(key).GetChatClient("model").AsIChatClient();
        IChatClient chat = new OfflineChatClient();

        await using var bot = new AiChatBot(
            ctx.CreateOptions("Assistant"),
            chat,
            new AiChatOptions { SystemPrompt = "You help players on a Mumble server.", MaxHistoryMessages = 10 });
        await bot.StartAsync(ctx.Token);

        await using var user = new RumbleClient(ctx.CreateOptions("Curious"));
        user.PrivateMessageReceived += (_, m) => ctx.Log($"{m.Sender?.Name}: {m.PlainText}");
        await user.ConnectAsync(ctx.Token);
        var assistant = user.Server.FindUser("Assistant")!;

        foreach (var question in new[] { "Hi! What can you do?", "How do I talk to only one person?", "Thanks!" })
        {
            ctx.Note($"Curious → {question}");
            user.SendPrivateMessage(assistant, question);
            await Task.Delay(500, ctx.Token);
        }

        await bot.StopAsync();
    }

    /// <summary>A rule-based stand-in for a language model.</summary>
    private sealed class OfflineChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var history = messages.ToList();
            var last = history.Last(m => m.Role == ChatRole.User).Text.ToLowerInvariant();
            var turns = history.Count(m => m.Role == ChatRole.User);
            var answer = last switch
            {
                _ when last.Contains("what can you do") => "I answer questions about this server. Ask me about channels, whispering or push-to-talk.",
                _ when last.Contains("one person") || last.Contains("whisper") => "Register a voice target with RegisterVoiceTarget and set Audio.VoiceTarget to its id to whisper to one person.",
                _ when last.Contains("thank") => $"You're welcome! We talked {turns} times — I remember the conversation.",
                _ => "I'm an offline demo model. Plug in a real IChatClient for smarter answers.",
            };
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

using Telepati.Bot;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Server.Realtime;

/// <summary>
/// Wraps "send a message" with the bot turn that may follow it. All three transports route
/// sends through here, so Kang Bacot answers identically whether the message arrived over
/// SignalR, gRPC or REST — and no transport has to know the bot exists.
/// </summary>
public class ChatOrchestrator(
    IMessageService messages,
    IChatService chats,
    IBotService bot,
    IServiceScopeFactory scopeFactory,
    ILogger<ChatOrchestrator> logger)
{
    public async Task<ApiResult<MessageDto>> SendAsync(Guid senderId, SendMessageRequest request, CancellationToken ct = default)
    {
        var result = await messages.SendAsync(senderId, request, ct);
        if (!result.Success || result.Data is null) return result;

        if (await bot.ShouldRespondAsync(request.ChatId, request.Content, ct))
        {
            // The model call can take many seconds; the sender's request must not wait on it.
            _ = RespondInBackgroundAsync(senderId, request.ChatId, result.Data);
        }

        return result;
    }

    private async Task RespondInBackgroundAsync(Guid senderId, Guid chatId, MessageDto original)
    {
        // A detached task cannot borrow the request's scoped services, so it opens its own.
        using var scope = scopeFactory.CreateScope();
        var provider = scope.ServiceProvider;

        try
        {
            var scopedBot = provider.GetRequiredService<IBotService>();
            var scopedChats = provider.GetRequiredService<IChatService>();
            var scopedMessages = provider.GetRequiredService<IMessageService>();
            var users = provider.GetRequiredService<IUserService>();

            var botUserId = await scopedBot.GetBotUserIdAsync();
            if (botUserId is null) return;

            var chat = await scopedChats.GetChatAsync(senderId, chatId);
            var isGroup = chat is not null && (ChatType)chat.Type is ChatType.Group or ChatType.Channel;

            var sender = await users.GetAsync(senderId);

            var reply = await scopedBot.HandleAsync(new BotRequest(
                chatId,
                senderId,
                sender?.DisplayName ?? "Teman",
                original.Content ?? string.Empty,
                isGroup,
                original.Attachments.Select(a => a.Url).ToList()));

            if (!reply.Handled || string.IsNullOrWhiteSpace(reply.Content)) return;

            await scopedMessages.SendAsync(botUserId.Value, new SendMessageRequest
            {
                ChatId = chatId,
                Type = (int)MessageType.Text,
                Content = reply.Content,
                ReplyToMessageId = isGroup ? original.Id : null
            });
        }
        catch (Exception e)
        {
            logger.LogError(e, "Bot reply failed for chat {ChatId}", chatId);
        }
    }

    public Task<IReadOnlyList<Guid>> GetRecipientsAsync(Guid chatId, CancellationToken ct = default) =>
        chats.GetMemberIdsAsync(chatId, ct);
}

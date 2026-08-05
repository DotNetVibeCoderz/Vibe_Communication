using Telepati.Client.Core;
using Telepati.Shared.Contracts;

namespace Telepati.UI.Services;

/// <summary>
/// The messenger's in-memory view of the conversation. It subscribes to the transport's realtime
/// events once and re-publishes a single <see cref="Changed"/> signal, so components never have
/// to know which transport is live or wire up their own handlers.
/// </summary>
public class ChatState : IAsyncDisposable
{
    private readonly ITelepatiClient _client;
    private readonly Dictionary<Guid, DateTimeOffset> _typingUntil = [];

    public ChatState(ITelepatiClient client)
    {
        _client = client;

        _client.MessageReceived += OnMessageReceived;
        _client.MessageEdited += OnMessageEdited;
        _client.MessageDeleted += OnMessageDeleted;
        _client.TypingChanged += OnTypingChanged;
        _client.PresenceChanged += OnPresenceChanged;
        _client.ChatUpdated += OnChatUpdated;
        _client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public event Action? Changed;
    public event Action<MessageDto>? MessageArrived;

    public List<ChatDto> Chats { get; } = [];
    public List<MessageDto> Messages { get; } = [];
    public ChatDto? ActiveChat { get; private set; }
    public UserDto? Me => _client.CurrentUser;
    public bool IsConnected => _client.IsConnected;
    public string TransportName => _client.TransportName;
    public bool IsLoading { get; private set; }

    /// <summary>Names currently typing in the open chat; entries expire on their own.</summary>
    public List<string> TypingNames { get; } = [];

    public async Task LoadChatsAsync(CancellationToken ct = default)
    {
        IsLoading = true;
        Changed?.Invoke();

        try
        {
            var page = await _client.GetChatsAsync(1, 50, false, ct);
            Chats.Clear();
            Chats.AddRange(page.Items);
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    public async Task OpenChatAsync(ChatDto chat, CancellationToken ct = default)
    {
        ActiveChat = chat;
        Messages.Clear();
        TypingNames.Clear();
        IsLoading = true;
        Changed?.Invoke();

        try
        {
            var page = await _client.GetMessagesAsync(chat.Id, 1, 50, null, ct);
            Messages.AddRange(page.Items);

            if (Messages.Count > 0)
            {
                await _client.MarkReadAsync(chat.Id, Messages[^1].Id, ct);
                ClearUnread(chat.Id);
            }
        }
        finally
        {
            IsLoading = false;
            Changed?.Invoke();
        }
    }

    public async Task<bool> LoadOlderAsync(CancellationToken ct = default)
    {
        if (ActiveChat is null || Messages.Count == 0) return false;

        var page = await _client.GetMessagesAsync(ActiveChat.Id, 1, 50, Messages[0].Id, ct);
        if (page.Items.Count == 0) return false;

        Messages.InsertRange(0, page.Items);
        Changed?.Invoke();
        return true;
    }

    public async Task<MessageDto?> SendAsync(string content, Guid? replyTo = null,
        IReadOnlyList<Guid>? attachmentIds = null, IReadOnlyList<Guid>? mentions = null, CancellationToken ct = default)
    {
        if (ActiveChat is null || string.IsNullOrWhiteSpace(content) && attachmentIds is null or { Count: 0 }) return null;

        var sent = await _client.SendMessageAsync(new SendMessageRequest
        {
            ChatId = ActiveChat.Id,
            Type = 0,
            Content = content,
            ReplyToMessageId = replyTo,
            AttachmentIds = attachmentIds ?? [],
            MentionedUserIds = mentions ?? [],
            ClientMessageId = Guid.CreateVersion7().ToString()
        }, ct);

        // The realtime push also carries this message back; Append de-duplicates by id.
        if (sent is not null) Append(sent);
        return sent;
    }

    public async Task ReactAsync(Guid messageId, string emoji, CancellationToken ct = default)
    {
        await _client.ReactAsync(messageId, emoji, ct);
    }

    public async Task DeleteAsync(Guid messageId, CancellationToken ct = default)
    {
        var result = await _client.DeleteMessageAsync(messageId, ct);
        if (result.Success)
        {
            Messages.RemoveAll(m => m.Id == messageId);
            Changed?.Invoke();
        }
    }

    public Task SetTypingAsync(bool isTyping, CancellationToken ct = default) =>
        ActiveChat is null ? Task.CompletedTask : _client.SetTypingAsync(ActiveChat.Id, isTyping, ct);

    // -- realtime handlers ----------------------------------------------------

    private void OnMessageReceived(MessageDto message)
    {
        if (ActiveChat?.Id == message.ChatId)
        {
            Append(message);
            MessageArrived?.Invoke(message);
            _ = _client.MarkReadAsync(message.ChatId, message.Id);
        }
        else
        {
            // Not the open chat: bump its unread badge and float it to the top.
            var chat = Chats.FirstOrDefault(c => c.Id == message.ChatId);
            if (chat is not null)
            {
                var index = Chats.IndexOf(chat);
                Chats[index] = chat with
                {
                    UnreadCount = chat.UnreadCount + 1,
                    LastMessage = message,
                    LastMessageAt = message.CreatedAt
                };
                Resort();
            }
            MessageArrived?.Invoke(message);
        }

        UpdateChatPreview(message);
        Changed?.Invoke();
    }

    private void OnMessageEdited(MessageDto message)
    {
        var index = Messages.FindIndex(m => m.Id == message.Id);
        if (index >= 0) Messages[index] = message;
        Changed?.Invoke();
    }

    private void OnMessageDeleted(Guid chatId, Guid messageId)
    {
        Messages.RemoveAll(m => m.Id == messageId);
        Changed?.Invoke();
    }

    private void OnTypingChanged(TypingNotification notification)
    {
        if (ActiveChat?.Id != notification.ChatId) return;

        if (notification.IsTyping)
        {
            if (!TypingNames.Contains(notification.UserName)) TypingNames.Add(notification.UserName);

            // Clients do not always send the "stopped" signal, so every indicator carries
            // its own expiry rather than trusting the peer to clear it.
            _typingUntil[notification.UserId] = DateTimeOffset.UtcNow.AddSeconds(6);
            _ = ExpireTypingAsync(notification.UserId, notification.UserName);
        }
        else
        {
            TypingNames.Remove(notification.UserName);
            _typingUntil.Remove(notification.UserId);
        }

        Changed?.Invoke();
    }

    private async Task ExpireTypingAsync(Guid userId, string userName)
    {
        await Task.Delay(TimeSpan.FromSeconds(6.5));

        if (_typingUntil.TryGetValue(userId, out var until) && until <= DateTimeOffset.UtcNow)
        {
            _typingUntil.Remove(userId);
            TypingNames.Remove(userName);
            Changed?.Invoke();
        }
    }

    private void OnPresenceChanged(PresenceNotification notification) => Changed?.Invoke();

    private void OnChatUpdated(ChatDto chat)
    {
        var index = Chats.FindIndex(c => c.Id == chat.Id);
        if (index >= 0) Chats[index] = chat;
        else Chats.Insert(0, chat);

        Changed?.Invoke();
    }

    private void OnConnectionStateChanged(bool connected) => Changed?.Invoke();

    // -- helpers --------------------------------------------------------------

    private void Append(MessageDto message)
    {
        if (Messages.Any(m => m.Id == message.Id)) return;

        Messages.Add(message);
        Messages.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
    }

    private void UpdateChatPreview(MessageDto message)
    {
        var index = Chats.FindIndex(c => c.Id == message.ChatId);
        if (index < 0) return;

        Chats[index] = Chats[index] with { LastMessage = message, LastMessageAt = message.CreatedAt };
        Resort();
    }

    private void ClearUnread(Guid chatId)
    {
        var index = Chats.FindIndex(c => c.Id == chatId);
        if (index >= 0) Chats[index] = Chats[index] with { UnreadCount = 0 };
    }

    private void Resort()
    {
        var sorted = Chats
            .OrderByDescending(c => c.IsPinned)
            .ThenByDescending(c => c.LastMessageAt ?? DateTimeOffset.MinValue)
            .ToList();

        Chats.Clear();
        Chats.AddRange(sorted);
    }

    public ValueTask DisposeAsync()
    {
        _client.MessageReceived -= OnMessageReceived;
        _client.MessageEdited -= OnMessageEdited;
        _client.MessageDeleted -= OnMessageDeleted;
        _client.TypingChanged -= OnTypingChanged;
        _client.PresenceChanged -= OnPresenceChanged;
        _client.ChatUpdated -= OnChatUpdated;
        _client.ConnectionStateChanged -= OnConnectionStateChanged;

        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

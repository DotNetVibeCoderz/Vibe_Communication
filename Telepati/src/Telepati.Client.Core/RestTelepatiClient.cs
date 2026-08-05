using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Client.Core;

/// <summary>
/// REST transport. HTTP has no server push, so realtime is emulated by polling on the interval
/// from <see cref="ClientOptions.PollIntervalSeconds"/>. It is the most compatible option
/// (plain HTTPS, proxy-friendly) and the least immediate — which is exactly the trade-off the
/// transport setting exists to let a user make.
/// </summary>
public class RestTelepatiClient : ITelepatiClient
{
    private readonly HttpClient _http;
    private readonly ClientOptions _options;
    private readonly TokenStore _tokens;
    private readonly ILogger<RestTelepatiClient> _logger;

    private CancellationTokenSource? _pollingCts;
    private readonly Dictionary<Guid, DateTimeOffset> _lastSeenPerChat = [];

    public RestTelepatiClient(HttpClient http, ClientOptions options, TokenStore tokens, ILogger<RestTelepatiClient> logger)
    {
        _http = http;
        _options = options;
        _tokens = tokens;
        _logger = logger;
        _http.BaseAddress ??= new Uri(options.ServerUrl);
    }

    public string TransportName => "REST";
    public bool IsConnected { get; private set; }
    public UserDto? CurrentUser => _tokens.User;

    public event Action<MessageDto>? MessageReceived;
    public event Action<MessageDto>? MessageEdited;
    public event Action<Guid, Guid>? MessageDeleted;
    public event Action<TypingNotification>? TypingChanged;
    public event Action<PresenceNotification>? PresenceChanged;
    public event Action<CallSignalDto>? CallSignalReceived;
    public event Action<ChatDto>? ChatUpdated;
    public event Action<bool>? ConnectionStateChanged;

    // -- auth -----------------------------------------------------------------

    public async Task<LoginResponse> LoginAsync(string usernameOrEmail, string password, string? twoFactorCode = null, CancellationToken ct = default)
    {
        var request = new LoginRequest(usernameOrEmail, password, twoFactorCode, Environment.MachineName, "rest", Environment.OSVersion.Platform.ToString());
        var response = await _http.PostAsJsonAsync("/api/auth/login", request, ct);
        var result = await ReadAsync<LoginResponse>(response, ct)
                     ?? new LoginResponse(false, null, null, null, null, false, "Tidak ada respons dari server.");

        if (result.Success) _tokens.Apply(result);
        return result;
    }

    public async Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync("/api/auth/register", request, ct);
        var result = await ReadAsync<LoginResponse>(response, ct)
                     ?? new LoginResponse(false, null, null, null, null, false, "Tidak ada respons dari server.");

        if (result.Success) _tokens.Apply(result);
        return result;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        try
        {
            await AuthorizeAsync(ct);
            await _http.PostAsync("/api/auth/logout", null, ct);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "Logout call failed; clearing local tokens anyway.");
        }
        finally
        {
            _tokens.Clear();
            await DisconnectAsync(ct);
        }
    }

    // -- "realtime" -----------------------------------------------------------

    public Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return Task.CompletedTask;

        _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = PollLoopAsync(_pollingCts.Token);

        IsConnected = true;
        ConnectionStateChanged?.Invoke(true);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _pollingCts = null;

        if (IsConnected)
        {
            IsConnected = false;
            ConnectionStateChanged?.Invoke(false);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Walks the chat list and raises <see cref="MessageReceived"/> for anything newer than the
    /// last timestamp seen for that chat. Only the newest page is fetched, so a quiet client
    /// costs one request per interval.
    /// </summary>
    private async Task PollLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, _options.PollIntervalSeconds));

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct);

                var chats = await GetChatsAsync(1, 50, false, ct);
                foreach (var chat in chats.Items)
                {
                    if (chat.LastMessageAt is null) continue;

                    if (_lastSeenPerChat.TryGetValue(chat.Id, out var seen) && chat.LastMessageAt <= seen) continue;

                    var isFirstSight = !_lastSeenPerChat.ContainsKey(chat.Id);
                    _lastSeenPerChat[chat.Id] = chat.LastMessageAt.Value;

                    // The first pass only records the watermark; replaying history as "new"
                    // would flood the UI the moment a client connects.
                    if (isFirstSight) continue;

                    var page = await GetMessagesAsync(chat.Id, 1, 20, null, ct);
                    foreach (var message in page.Items.Where(m => m.CreatedAt > seen))
                    {
                        MessageReceived?.Invoke(message);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "REST polling iteration failed; will retry.");
            }
        }
    }

    // -- data -----------------------------------------------------------------

    public async Task<PagedResult<ChatDto>> GetChatsAsync(int page = 1, int pageSize = 30, bool includeArchived = false, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        return await _http.GetFromJsonAsync<PagedResult<ChatDto>>(
            $"/api/chats?page={page}&pageSize={pageSize}&includeArchived={includeArchived}", ct)
               ?? new PagedResult<ChatDto>([], 0, page, pageSize);
    }

    public async Task<ChatDto?> GetChatAsync(Guid chatId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        return await _http.GetFromJsonAsync<ChatDto>($"/api/chats/{chatId}", ct);
    }

    public async Task<ChatDto?> CreateChatAsync(CreateChatRequest request, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/chats", request, ct);
        return await ReadAsync<ChatDto>(response, ct);
    }

    public async Task<ChatDto?> OpenDirectChatAsync(Guid otherUserId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsync($"/api/chats/direct/{otherUserId}", null, ct);
        return await ReadAsync<ChatDto>(response, ct);
    }

    public async Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        return await _http.GetFromJsonAsync<List<ChatMemberDto>>($"/api/chats/{chatId}/members", ct) ?? [];
    }

    public async Task<PagedResult<MessageDto>> GetMessagesAsync(Guid chatId, int page = 1, int pageSize = 50, Guid? beforeMessageId = null, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var url = $"/api/messages/{chatId}?page={page}&pageSize={pageSize}";
        if (beforeMessageId is not null) url += $"&before={beforeMessageId}";

        return await _http.GetFromJsonAsync<PagedResult<MessageDto>>(url, ct)
               ?? new PagedResult<MessageDto>([], 0, page, pageSize);
    }

    public async Task<MessageDto?> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/messages", request, ct);
        return await ReadAsync<MessageDto>(response, ct);
    }

    public async Task<MessageDto?> EditMessageAsync(Guid messageId, string content, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PutAsJsonAsync("/api/messages", new EditMessageRequest(messageId, content), ct);
        return await ReadAsync<MessageDto>(response, ct);
    }

    public async Task<ApiResult> DeleteMessageAsync(Guid messageId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.DeleteAsync($"/api/messages/{messageId}", ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Fail("Gagal menghapus pesan.");
    }

    public async Task<ApiResult> ReactAsync(Guid messageId, string emoji, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/messages/react", new ReactionRequest(messageId, emoji), ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Fail("Gagal mengirim reaksi.");
    }

    public async Task<ApiResult> MarkReadAsync(Guid chatId, Guid lastReadMessageId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/messages/read", new ReadReceiptRequest(chatId, lastReadMessageId), ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Fail("Gagal menandai dibaca.");
    }

    public async Task<ApiResult> SetTypingAsync(Guid chatId, bool isTyping, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/messages/typing", new { chatId, isTyping }, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Ok();
    }

    public async Task<ApiResult> SetPresenceAsync(int presence, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/users/me/presence", new { presence }, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Ok();
    }

    public async Task<IReadOnlyList<ContactDto>> GetContactsAsync(CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        return await _http.GetFromJsonAsync<List<ContactDto>>("/api/contacts", ct) ?? [];
    }

    public async Task<IReadOnlyList<UserDto>> SearchUsersAsync(ContactSearchRequest request, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/users/search", request, ct);
        return await ReadAsync<List<UserDto>>(response, ct) ?? [];
    }

    public async Task<ApiResult> AddContactAsync(Guid userId, string? alias = null, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync($"/api/contacts/{userId}", new { alias }, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Fail("Gagal menambah kontak.");
    }

    public async Task<IReadOnlyList<StatusPostDto>> GetStatusFeedAsync(CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        return await _http.GetFromJsonAsync<List<StatusPostDto>>("/api/status", ct) ?? [];
    }

    public async Task<ApiResult> ViewStatusAsync(Guid statusId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsync($"/api/status/{statusId}/view", null, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Ok();
    }

    public async Task<ApiResult<CallSignalDto>> StartCallAsync(Guid chatId, int type, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/calls/start", new StartCallRequest(chatId, type), ct);
        var signal = await ReadAsync<CallSignalDto>(response, ct);
        return signal is null ? ApiResult<CallSignalDto>.Fail("Gagal memulai panggilan.") : ApiResult<CallSignalDto>.Ok(signal);
    }

    public async Task<ApiResult> SendCallSignalAsync(CallSignalDto signal, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync("/api/calls/signal", signal, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Ok();
    }

    public async Task<ApiResult> AnswerCallAsync(Guid callId, bool accepted, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsJsonAsync($"/api/calls/{callId}/answer", new { value = accepted }, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Ok();
    }

    public async Task<ApiResult> EndCallAsync(Guid callId, CancellationToken ct = default)
    {
        await AuthorizeAsync(ct);
        var response = await _http.PostAsync($"/api/calls/{callId}/end", null, ct);
        return await ReadAsync<ApiResult>(response, ct) ?? ApiResult.Ok();
    }

    public Task<ThemeDto?> GetActiveThemeAsync(CancellationToken ct = default) =>
        _http.GetFromJsonAsync<ThemeDto>("/api/theme/active", ct);

    // -- helpers --------------------------------------------------------------

    /// <summary>Refreshes the access token when it is about to expire, then sets the header.</summary>
    private async Task AuthorizeAsync(CancellationToken ct)
    {
        if (_tokens.NeedsRefresh && _tokens.RefreshToken is not null)
        {
            try
            {
                var response = await _http.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(_tokens.RefreshToken), ct);
                var refreshed = await ReadAsync<LoginResponse>(response, ct);
                if (refreshed is { Success: true }) _tokens.Apply(refreshed);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Token refresh failed.");
            }
        }

        _http.DefaultRequestHeaders.Authorization = _tokens.AccessToken is null
            ? null
            : new AuthenticationHeaderValue("Bearer", _tokens.AccessToken);
    }

    private static async Task<T?> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return default;

        try
        {
            return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            // Error responses use a different shape; the caller's null fallback handles it.
            return default;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}

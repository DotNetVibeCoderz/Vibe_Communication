using Microsoft.EntityFrameworkCore;
using Telepati.Domain;
using Telepati.Infrastructure.Caching;
using Telepati.Infrastructure.Data;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IChatService
{
    Task<PagedResult<ChatDto>> GetChatsAsync(Guid userId, int page, int pageSize, bool includeArchived, CancellationToken ct = default);
    Task<ChatDto?> GetChatAsync(Guid userId, Guid chatId, CancellationToken ct = default);
    Task<ApiResult<ChatDto>> CreateChatAsync(Guid userId, CreateChatRequest request, CancellationToken ct = default);
    /// <summary>Returns the existing direct chat between two users, creating it on first contact.</summary>
    Task<ChatDto> GetOrCreateDirectChatAsync(Guid userId, Guid otherUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default);
    Task<ApiResult> AddMembersAsync(Guid actorId, Guid chatId, IReadOnlyList<Guid> userIds, CancellationToken ct = default);
    Task<ApiResult> RemoveMemberAsync(Guid actorId, Guid chatId, Guid userId, CancellationToken ct = default);
    Task<ApiResult> UpdateMemberRoleAsync(Guid actorId, UpdateMemberRoleRequest request, CancellationToken ct = default);
    Task<ApiResult> LeaveAsync(Guid userId, Guid chatId, CancellationToken ct = default);
    Task<ApiResult> UpdateChatAsync(Guid actorId, Guid chatId, string? title, string? description, string? avatarUrl, bool? onlyAdminsCanPost, CancellationToken ct = default);
    Task<ApiResult> SetMuteAsync(Guid userId, Guid chatId, bool muted, CancellationToken ct = default);
    Task<ApiResult> SetPinAsync(Guid userId, Guid chatId, bool pinned, CancellationToken ct = default);
    Task<ApiResult> SetArchiveAsync(Guid userId, Guid chatId, bool archived, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetMemberIdsAsync(Guid chatId, CancellationToken ct = default);
    Task<bool> IsMemberAsync(Guid chatId, Guid userId, CancellationToken ct = default);
    Task<PagedResult<ChatDto>> SearchPublicChannelsAsync(string query, int page, int pageSize, CancellationToken ct = default);
    Task<ApiResult> SubscribeChannelAsync(Guid userId, Guid channelId, CancellationToken ct = default);
}

public class ChatService(
    TelepatiDbContext db,
    ICacheService cache,
    IStorageService storage,
    ISettingsService settings,
    IActivityLogger activity,
    IRealtimeNotifier notifier) : IChatService
{
    public async Task<PagedResult<ChatDto>> GetChatsAsync(Guid userId, int page, int pageSize, bool includeArchived, CancellationToken ct = default)
    {
        var query = db.ChatMembers.AsNoTracking()
            .Where(m => m.UserId == userId && m.LeftAt == null);

        if (!includeArchived) query = query.Where(m => !m.IsArchived);

        var total = await query.CountAsync(ct);

        var memberships = await query
            .Include(m => m.Chat)
            // Pinned conversations float to the top, then by recency of the last message.
            .OrderByDescending(m => m.IsPinned)
            .ThenByDescending(m => m.Chat!.LastMessageAt ?? m.Chat.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        // The chat row already points at its newest message, so the previews are fetched by id.
        // Grouping in SQL and then calling Include is not translatable — EF rejects an Include
        // that follows a projection.
        var lastMessageIds = memberships
            .Select(m => m.Chat?.LastMessageId)
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .ToList();

        var lastMessages = await db.Messages.AsNoTracking()
            .Where(m => lastMessageIds.Contains(m.Id))
            .Include(m => m.Sender)
            .Include(m => m.Attachments)
            .Include(m => m.Reactions)
            .Include(m => m.Mentions)
            // Three collections on one root would multiply into a cartesian product.
            .AsSplitQuery()
            .ToListAsync(ct);

        var results = new List<ChatDto>(memberships.Count);
        foreach (var membership in memberships)
        {
            var chat = membership.Chat!;
            var last = lastMessages.FirstOrDefault(m => m.Id == chat.LastMessageId);
            var lastDto = last is null ? null : await last.ToDtoAsync(storage, ct: ct);
            results.Add(chat.ToDto(membership, await ResolveTitleAsync(chat, userId, ct), lastDto));
        }

        return new PagedResult<ChatDto>(results, total, page, pageSize);
    }

    public async Task<ChatDto?> GetChatAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var membership = await db.ChatMembers.AsNoTracking()
            .Include(m => m.Chat)
            .FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId, ct);

        if (membership?.Chat is null) return null;
        return membership.Chat.ToDto(membership, await ResolveTitleAsync(membership.Chat, userId, ct));
    }

    public async Task<ApiResult<ChatDto>> CreateChatAsync(Guid userId, CreateChatRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        var type = (ChatType)request.Type;

        if (type == ChatType.Direct)
        {
            var other = request.MemberIds.FirstOrDefault();
            if (other == Guid.Empty) return ApiResult<ChatDto>.Fail("Direct chat butuh satu lawan bicara.");
            return ApiResult<ChatDto>.Ok(await GetOrCreateDirectChatAsync(userId, other, ct));
        }

        if (type == ChatType.Channel && !options.Features.EnableChannels)
            return ApiResult<ChatDto>.Fail("Fitur channel dimatikan.");

        if (request.MemberIds.Count + 1 > options.Limits.MaxGroupMembers)
            return ApiResult<ChatDto>.Fail($"Anggota melebihi batas {options.Limits.MaxGroupMembers}.");

        if (!string.IsNullOrWhiteSpace(request.Handle) &&
            await db.Chats.AnyAsync(c => c.Handle == request.Handle, ct))
            return ApiResult<ChatDto>.Fail("Handle sudah dipakai.");

        var chat = new Chat
        {
            Type = type,
            Title = request.Title,
            Description = request.Description,
            Handle = string.IsNullOrWhiteSpace(request.Handle) ? null : request.Handle.Trim().ToLowerInvariant(),
            CreatedById = userId,
            IsPublic = request.IsPublic,
            // The global switch decides the default; an explicit request can only turn it on.
            IsEncrypted = request.IsEncrypted || options.Security.EnableEndToEndEncryption,
            OnlyAdminsCanPost = type == ChatType.Channel,
            MemberCount = 1
        };
        db.Chats.Add(chat);

        db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = userId, Role = ChatMemberRole.Owner });

        foreach (var memberId in request.MemberIds.Distinct().Where(id => id != userId))
        {
            db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = memberId });
            chat.MemberCount++;
        }

        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(chat.Id, ct);

        await activity.LogAsync(ActivityKind.GroupCreated, userId, $"Membuat {type} '{chat.Title}'.", entityType: nameof(Chat), entityId: chat.Id, ct: ct);

        var dto = chat.ToDto(null, chat.Title);
        await notifier.ChatUpdatedAsync(dto, await GetMemberIdsAsync(chat.Id, ct), ct);
        return ApiResult<ChatDto>.Ok(dto);
    }

    public async Task<ChatDto> GetOrCreateDirectChatAsync(Guid userId, Guid otherUserId, CancellationToken ct = default)
    {
        // A direct chat is identified by having exactly these two members and no other.
        var existing = await db.Chats.AsNoTracking()
            .Where(c => c.Type == ChatType.Direct)
            .Where(c => c.Members.Any(m => m.UserId == userId) && c.Members.Any(m => m.UserId == otherUserId))
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            var membership = await db.ChatMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ChatId == existing.Id && m.UserId == userId, ct);
            return existing.ToDto(membership, await ResolveTitleAsync(existing, userId, ct));
        }

        var options = await settings.GetOptionsAsync(ct);
        var chat = new Chat
        {
            Type = ChatType.Direct,
            CreatedById = userId,
            MemberCount = 2,
            IsEncrypted = options.Security.EnableEndToEndEncryption
        };

        db.Chats.Add(chat);
        db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = userId });
        db.ChatMembers.Add(new ChatMember { ChatId = chat.Id, UserId = otherUserId });
        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(chat.Id, ct);

        return chat.ToDto(null, await ResolveTitleAsync(chat, userId, ct));
    }

    public async Task<IReadOnlyList<ChatMemberDto>> GetMembersAsync(Guid chatId, CancellationToken ct = default) =>
        await cache.GetOrCreateAsync(CacheKeys.ChatMembers(chatId), async () =>
            (IReadOnlyList<ChatMemberDto>)await db.ChatMembers.AsNoTracking()
                .Where(m => m.ChatId == chatId && m.LeftAt == null)
                .Include(m => m.User)
                .OrderByDescending(m => m.Role)
                .ThenBy(m => m.User!.DisplayName)
                .Select(m => new ChatMemberDto(
                    m.UserId, m.User!.DisplayName, m.User.Username, m.User.AvatarUrl,
                    (int)m.Role, m.JoinedAt, (int)m.User.Presence))
                .ToListAsync(ct),
            TimeSpan.FromMinutes(2), ct);

    public async Task<ApiResult> AddMembersAsync(Guid actorId, Guid chatId, IReadOnlyList<Guid> userIds, CancellationToken ct = default)
    {
        var guard = await RequireRoleAsync(actorId, chatId, ChatMemberRole.Moderator, ct);
        if (!guard.Success) return guard;

        var options = await settings.GetOptionsAsync(ct);
        var chat = await db.Chats.FirstAsync(c => c.Id == chatId, ct);

        var existing = await db.ChatMembers.Where(m => m.ChatId == chatId).ToListAsync(ct);
        var added = 0;

        foreach (var userId in userIds.Distinct())
        {
            var member = existing.FirstOrDefault(m => m.UserId == userId);
            if (member is { LeftAt: null }) continue;

            if (chat.MemberCount + added >= options.Limits.MaxGroupMembers)
                return ApiResult.Fail($"Anggota melebihi batas {options.Limits.MaxGroupMembers}.");

            if (member is not null)
            {
                // Re-joining reuses the original row so history and read state survive.
                member.LeftAt = null;
                member.JoinedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                db.ChatMembers.Add(new ChatMember { ChatId = chatId, UserId = userId });
            }
            added++;
        }

        chat.MemberCount += added;
        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(chatId, ct);
        await activity.LogAsync(ActivityKind.GroupJoined, actorId, $"Menambah {added} anggota.", entityType: nameof(Chat), entityId: chatId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> RemoveMemberAsync(Guid actorId, Guid chatId, Guid userId, CancellationToken ct = default)
    {
        var guard = await RequireRoleAsync(actorId, chatId, ChatMemberRole.Moderator, ct);
        if (!guard.Success) return guard;

        var target = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId, ct);
        if (target is null || target.LeftAt is not null) return ApiResult.Fail("Anggota tidak ditemukan.");
        if (target.Role == ChatMemberRole.Owner) return ApiResult.Fail("Owner tidak bisa dikeluarkan.");

        target.LeftAt = DateTimeOffset.UtcNow;
        var chat = await db.Chats.FirstAsync(c => c.Id == chatId, ct);
        chat.MemberCount = Math.Max(0, chat.MemberCount - 1);

        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(chatId, ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> UpdateMemberRoleAsync(Guid actorId, UpdateMemberRoleRequest request, CancellationToken ct = default)
    {
        var guard = await RequireRoleAsync(actorId, request.ChatId, ChatMemberRole.Admin, ct);
        if (!guard.Success) return guard;

        var target = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == request.ChatId && m.UserId == request.UserId, ct);
        if (target is null) return ApiResult.Fail("Anggota tidak ditemukan.");
        if (target.Role == ChatMemberRole.Owner) return ApiResult.Fail("Role owner tidak bisa diubah.");

        var newRole = (ChatMemberRole)request.Role;
        if (newRole == ChatMemberRole.Owner) return ApiResult.Fail("Owner hanya bisa dipindahkan lewat transfer kepemilikan.");

        target.Role = newRole;
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ChatMembers(request.ChatId), ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> LeaveAsync(Guid userId, Guid chatId, CancellationToken ct = default)
    {
        var membership = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId, ct);
        if (membership is null) return ApiResult.Fail("Bukan anggota chat ini.");

        var chat = await db.Chats.FirstAsync(c => c.Id == chatId, ct);

        if (membership.Role == ChatMemberRole.Owner)
        {
            // Hand ownership to the longest-serving admin, or the longest-serving member.
            var successor = await db.ChatMembers
                .Where(m => m.ChatId == chatId && m.UserId != userId && m.LeftAt == null)
                .OrderByDescending(m => m.Role)
                .ThenBy(m => m.JoinedAt)
                .FirstOrDefaultAsync(ct);

            if (successor is null)
            {
                chat.IsDeleted = true;
                chat.DeletedAt = DateTimeOffset.UtcNow;
            }
            else
            {
                successor.Role = ChatMemberRole.Owner;
            }
        }

        membership.LeftAt = DateTimeOffset.UtcNow;
        chat.MemberCount = Math.Max(0, chat.MemberCount - 1);

        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(chatId, ct);
        await activity.LogAsync(ActivityKind.GroupLeft, userId, $"Keluar dari chat.", entityType: nameof(Chat), entityId: chatId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> UpdateChatAsync(Guid actorId, Guid chatId, string? title, string? description,
        string? avatarUrl, bool? onlyAdminsCanPost, CancellationToken ct = default)
    {
        var guard = await RequireRoleAsync(actorId, chatId, ChatMemberRole.Admin, ct);
        if (!guard.Success) return guard;

        var chat = await db.Chats.FirstAsync(c => c.Id == chatId, ct);
        if (title is not null) chat.Title = title;
        if (description is not null) chat.Description = description;
        if (avatarUrl is not null) chat.AvatarUrl = avatarUrl;
        if (onlyAdminsCanPost is not null) chat.OnlyAdminsCanPost = onlyAdminsCanPost.Value;

        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(chatId, ct);
        await notifier.ChatUpdatedAsync(chat.ToDto(null, chat.Title), await GetMemberIdsAsync(chatId, ct), ct);
        return ApiResult.Ok();
    }

    public Task<ApiResult> SetMuteAsync(Guid userId, Guid chatId, bool muted, CancellationToken ct = default) =>
        UpdateMembershipAsync(userId, chatId, m => m.IsMuted = muted, ct);

    public Task<ApiResult> SetPinAsync(Guid userId, Guid chatId, bool pinned, CancellationToken ct = default) =>
        UpdateMembershipAsync(userId, chatId, m => m.IsPinned = pinned, ct);

    public Task<ApiResult> SetArchiveAsync(Guid userId, Guid chatId, bool archived, CancellationToken ct = default) =>
        UpdateMembershipAsync(userId, chatId, m => m.IsArchived = archived, ct);

    public async Task<IReadOnlyList<Guid>> GetMemberIdsAsync(Guid chatId, CancellationToken ct = default) =>
        await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chatId && m.LeftAt == null)
            .Select(m => m.UserId)
            .ToListAsync(ct);

    public Task<bool> IsMemberAsync(Guid chatId, Guid userId, CancellationToken ct = default) =>
        db.ChatMembers.AsNoTracking().AnyAsync(m => m.ChatId == chatId && m.UserId == userId && m.LeftAt == null, ct);

    public async Task<PagedResult<ChatDto>> SearchPublicChannelsAsync(string query, int page, int pageSize, CancellationToken ct = default)
    {
        var q = db.Chats.AsNoTracking()
            .Where(c => c.Type == ChatType.Channel && c.IsPublic);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim().ToLowerInvariant();
            q = q.Where(c => c.Title!.ToLower().Contains(term) || c.Handle!.Contains(term));
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(c => c.MemberCount)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ChatDto>(items.Select(c => c.ToDto(null, c.Title)).ToList(), total, page, pageSize);
    }

    public async Task<ApiResult> SubscribeChannelAsync(Guid userId, Guid channelId, CancellationToken ct = default)
    {
        var chat = await db.Chats.FirstOrDefaultAsync(c => c.Id == channelId && c.Type == ChatType.Channel, ct);
        if (chat is null) return ApiResult.Fail("Channel tidak ditemukan.");
        if (!chat.IsPublic) return ApiResult.Fail("Channel ini tidak terbuka untuk umum.");

        var existing = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == channelId && m.UserId == userId, ct);
        if (existing is { LeftAt: null }) return ApiResult.Ok();

        if (existing is not null)
        {
            existing.LeftAt = null;
        }
        else
        {
            db.ChatMembers.Add(new ChatMember { ChatId = channelId, UserId = userId });
        }

        chat.MemberCount++;
        await db.SaveChangesAsync(ct);
        await InvalidateChatCachesAsync(channelId, ct);
        return ApiResult.Ok();
    }

    // -- helpers --------------------------------------------------------------

    /// <summary>
    /// Direct chats carry no title of their own — each side sees the other's name, so the
    /// label has to be resolved per viewer rather than stored on the row.
    /// </summary>
    private async Task<string?> ResolveTitleAsync(Chat chat, Guid viewerId, CancellationToken ct)
    {
        if (chat.Type != ChatType.Direct) return chat.Title;

        var other = await db.ChatMembers.AsNoTracking()
            .Where(m => m.ChatId == chat.Id && m.UserId != viewerId)
            .Include(m => m.User)
            .Select(m => m.User!.DisplayName)
            .FirstOrDefaultAsync(ct);

        return other ?? "Percakapan";
    }

    private async Task<ApiResult> RequireRoleAsync(Guid userId, Guid chatId, ChatMemberRole minimum, CancellationToken ct)
    {
        var membership = await db.ChatMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId && m.LeftAt == null, ct);

        if (membership is null) return ApiResult.Fail("Bukan anggota chat ini.");
        return membership.Role >= minimum ? ApiResult.Ok() : ApiResult.Fail("Hak akses tidak cukup.");
    }

    private async Task<ApiResult> UpdateMembershipAsync(Guid userId, Guid chatId, Action<ChatMember> mutate, CancellationToken ct)
    {
        var membership = await db.ChatMembers.FirstOrDefaultAsync(m => m.ChatId == chatId && m.UserId == userId, ct);
        if (membership is null) return ApiResult.Fail("Bukan anggota chat ini.");

        mutate(membership);
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync(CacheKeys.ChatList(userId), ct);
        return ApiResult.Ok();
    }

    private async Task InvalidateChatCachesAsync(Guid chatId, CancellationToken ct)
    {
        await cache.RemoveAsync(CacheKeys.Chat(chatId), ct);
        await cache.RemoveAsync(CacheKeys.ChatMembers(chatId), ct);

        foreach (var memberId in await GetMemberIdsAsync(chatId, ct))
        {
            await cache.RemoveAsync(CacheKeys.ChatList(memberId), ct);
        }
    }
}

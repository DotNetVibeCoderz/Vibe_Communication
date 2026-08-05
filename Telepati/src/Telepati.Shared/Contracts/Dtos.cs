namespace Telepati.Shared.Contracts;

// Wire shapes shared by REST, SignalR and gRPC. They are deliberately flat and provider
// agnostic so a client can switch transport without touching any view model.

public record LoginRequest(string UsernameOrEmail, string Password, string? TwoFactorCode, string DeviceName, string DeviceType, string? Platform);

public record LoginResponse(bool Success, string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, UserDto? User, bool TwoFactorRequired, string? Error);

public record RegisterRequest(string Username, string Email, string? PhoneNumber, string DisplayName, string Password);

public record RefreshRequest(string RefreshToken);

public record UserDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? PhoneNumber { get; init; }
    public string? AvatarUrl { get; init; }
    public string? About { get; init; }
    public int Role { get; init; }
    public int Presence { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public bool IsBot { get; init; }
    public bool TwoFactorEnabled { get; init; }
    /// <summary>Only populated by nearby search; kilometres from the requesting user.</summary>
    public double? DistanceKm { get; init; }
}

public record ChatDto
{
    public Guid Id { get; init; }
    public int Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Handle { get; init; }
    public bool IsEncrypted { get; init; }
    public bool IsPublic { get; init; }
    public bool OnlyAdminsCanPost { get; init; }
    public int MemberCount { get; init; }
    public int UnreadCount { get; init; }
    public bool IsMuted { get; init; }
    public bool IsPinned { get; init; }
    public bool IsArchived { get; init; }
    public int MyRole { get; init; }
    public MessageDto? LastMessage { get; init; }
    public DateTimeOffset? LastMessageAt { get; init; }
}

public record MessageDto
{
    public Guid Id { get; init; }
    public Guid ChatId { get; init; }
    public Guid SenderId { get; init; }
    public string SenderName { get; init; } = string.Empty;
    public string? SenderAvatarUrl { get; init; }
    public int Type { get; init; }
    public string? Content { get; init; }
    public bool IsEncrypted { get; init; }
    public Guid? ReplyToMessageId { get; init; }
    public string? ReplyToPreview { get; init; }
    public Guid? ForwardedFromUserId { get; init; }
    public string? ForwardedFromName { get; init; }
    public bool IsPinned { get; init; }
    public bool IsEdited { get; init; }
    public bool IsBotMessage { get; init; }
    public int DeliveryState { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public IReadOnlyList<AttachmentDto> Attachments { get; init; } = [];
    public IReadOnlyList<ReactionDto> Reactions { get; init; } = [];
    public IReadOnlyList<Guid> MentionedUserIds { get; init; } = [];
}

public record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, string Url, string? ThumbnailUrl, int? Width, int? Height, int? DurationSeconds);

public record ReactionDto(string Emoji, int Count, IReadOnlyList<Guid> UserIds);

public record SendMessageRequest
{
    public Guid ChatId { get; init; }
    public int Type { get; init; }
    public string? Content { get; init; }
    public Guid? ReplyToMessageId { get; init; }
    public Guid? ForwardedFromMessageId { get; init; }
    public IReadOnlyList<Guid> AttachmentIds { get; init; } = [];
    public IReadOnlyList<Guid> MentionedUserIds { get; init; } = [];
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// <summary>Client-generated id echoed back so optimistic UI can reconcile the sent message.</summary>
    public string? ClientMessageId { get; init; }
}

public record EditMessageRequest(Guid MessageId, string Content);

public record ReactionRequest(Guid MessageId, string Emoji);

public record ReadReceiptRequest(Guid ChatId, Guid LastReadMessageId);

public record TypingNotification(Guid ChatId, Guid UserId, string UserName, bool IsTyping);

public record PresenceNotification(Guid UserId, int Presence, DateTimeOffset? LastSeenAt);

public record CreateChatRequest(int Type, string? Title, string? Description, IReadOnlyList<Guid> MemberIds, bool IsEncrypted, bool IsPublic, string? Handle);

public record ChatMemberDto(Guid UserId, string DisplayName, string Username, string? AvatarUrl, int Role, DateTimeOffset JoinedAt, int Presence);

public record UpdateMemberRoleRequest(Guid ChatId, Guid UserId, int Role);

public record ContactDto(Guid Id, UserDto User, string? Alias, bool IsFavorite, bool IsMuted);

public record ContactSearchRequest
{
    public string? Query { get; init; }
    /// <summary>email | phone | username | nearby</summary>
    public string Mode { get; init; } = "username";
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// <summary>Nearby radius, clamped to the 5–100 km range the client exposes.</summary>
    public double RadiusKm { get; init; } = 10;
    public int Take { get; init; } = 25;
}

public record StatusPostDto(Guid Id, Guid UserId, string UserName, string? UserAvatarUrl, int MediaType, string? Caption, string? MediaUrl, string? BackgroundColor, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, int ViewCount, bool ViewedByMe);

public record CreateStatusRequest(int MediaType, string? Caption, Guid? AttachmentId, string? BackgroundColor);

public record CallSignalDto(Guid CallId, Guid ChatId, Guid FromUserId, Guid? ToUserId, int Type, string SignalKind, string? Payload);

public record StartCallRequest(Guid ChatId, int Type);

public record BroadcastRequest(string Name, string Content, IReadOnlyList<Guid> RecipientIds, DateTimeOffset? ScheduledAt);

public record SessionDto(Guid Id, string DeviceName, string DeviceType, string? Platform, string? IpAddress, DateTimeOffset LastActiveAt, DateTimeOffset CreatedAt, bool IsCurrent);

public record UploadResultDto(Guid AttachmentId, string FileName, string ContentType, long SizeBytes, string Url, string? ThumbnailUrl);

public record ThemeDto(Guid Id, string Name, string? Description, string PrimaryColor, string SecondaryColor, string AccentColor, string BackgroundColor, string SurfaceColor, string TextColor, string? IconSet, bool IsDark, bool IsActive, bool IsSeasonal, DateTimeOffset? ActiveFrom, DateTimeOffset? ActiveTo);

public record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize)
{
    public bool HasMore => Page * PageSize < Total;
}

public record ApiResult(bool Success, string? Error = null)
{
    public static ApiResult Ok() => new(true);
    public static ApiResult Fail(string error) => new(false, error);
}

public record ApiResult<T>(bool Success, T? Data, string? Error = null)
{
    public static ApiResult<T> Ok(T data) => new(true, data);
    public static ApiResult<T> Fail(string error) => new(false, default, error);
}

// --- Admin -----------------------------------------------------------------

public record DashboardStatsDto
{
    public int TotalUsers { get; init; }
    public int ActiveUsers24h { get; init; }
    public int OnlineUsers { get; init; }
    public int TotalChats { get; init; }
    public int TotalGroups { get; init; }
    public int TotalChannels { get; init; }
    public long TotalMessages { get; init; }
    public long Messages24h { get; init; }
    public long StorageBytes { get; init; }
    public int OpenReports { get; init; }
    public int ActiveCalls { get; init; }
    public int BotInvocations24h { get; init; }
    public IReadOnlyList<TimeSeriesPoint> MessageTrend { get; init; } = [];
    public IReadOnlyList<TimeSeriesPoint> UserTrend { get; init; } = [];
}

public record TimeSeriesPoint(DateTimeOffset At, double Value, string? Label = null);

public record GroupInsightsDto(Guid ChatId, string Title, int MemberCount, long MessageCount, long Messages7d, IReadOnlyList<MemberActivityDto> TopContributors, IReadOnlyList<TimeSeriesPoint> ActivityTrend);

public record MemberActivityDto(Guid UserId, string DisplayName, long MessageCount);

public record DeliveryReportDto(Guid MessageId, int Sent, int Delivered, int Read, int Failed, IReadOnlyList<DeliveryDetailDto> Details);

public record DeliveryDetailDto(Guid UserId, string DisplayName, int State, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt, string? FailureReason);

public record ActivityLogDto(Guid Id, Guid? UserId, string? UserName, int Kind, string? Description, string? IpAddress, DateTimeOffset CreatedAt);

public record SettingDto(string Key, string? Value, string Category, string? Description, string ValueType, bool IsSecret);

public record BackupResultDto(bool Success, string? FileName, string? Url, long SizeBytes, string? Error);

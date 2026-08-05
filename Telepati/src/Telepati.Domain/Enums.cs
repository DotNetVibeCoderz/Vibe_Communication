namespace Telepati.Domain;

public enum ChatType
{
    /// <summary>One-to-one conversation between exactly two users.</summary>
    Direct = 0,
    /// <summary>Many-to-many conversation with roles and member control.</summary>
    Group = 1,
    /// <summary>One-to-many broadcast; only admins post, subscribers read.</summary>
    Channel = 2,
    /// <summary>Private conversation between a user and a bot.</summary>
    Bot = 3,
    /// <summary>Notes-to-self chat, automatically created for every user.</summary>
    SavedMessages = 4
}

public enum ChatMemberRole
{
    Member = 0,
    Moderator = 1,
    Admin = 2,
    Owner = 3
}

public enum MessageType
{
    Text = 0,
    Image = 1,
    Video = 2,
    Audio = 3,
    Voice = 4,
    Document = 5,
    Sticker = 6,
    Location = 7,
    Contact = 8,
    Poll = 9,
    /// <summary>Join/leave/rename/pin events rendered inline in the thread.</summary>
    System = 10,
    Call = 11
}

public enum MessageDeliveryState
{
    Pending = 0,
    Sent = 1,
    Delivered = 2,
    Read = 3,
    Failed = 4
}

public enum UserPresence
{
    Offline = 0,
    Online = 1,
    Away = 2,
    Busy = 3,
    Invisible = 4
}

public enum UserRole
{
    User = 0,
    Moderator = 1,
    Admin = 2,
    SuperAdmin = 3
}

public enum StatusMediaType
{
    Text = 0,
    Image = 1,
    Video = 2
}

public enum CallType
{
    Voice = 0,
    Video = 1
}

public enum CallState
{
    Ringing = 0,
    Accepted = 1,
    Rejected = 2,
    Missed = 3,
    Ended = 4,
    Failed = 5
}

public enum ContactRequestState
{
    Pending = 0,
    Accepted = 1,
    Rejected = 2
}

public enum ReportState
{
    Open = 0,
    UnderReview = 1,
    Resolved = 2,
    Dismissed = 3
}

public enum ReportReason
{
    Spam = 0,
    Abuse = 1,
    Harassment = 2,
    Impersonation = 3,
    IllegalContent = 4,
    Other = 5
}

public enum BroadcastState
{
    Draft = 0,
    Sending = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>How a client app talks to the server. Selectable per client at runtime.</summary>
public enum TransportKind
{
    SignalR = 0,
    Grpc = 1,
    Rest = 2
}

public enum DatabaseProvider
{
    Sqlite = 0,
    SqlServer = 1,
    MySql = 2,
    PostgreSql = 3
}

public enum CacheProvider
{
    Memory = 0,
    Redis = 1
}

public enum StorageProvider
{
    FileSystem = 0,
    AzureBlob = 1,
    S3 = 2,
    MinIO = 3
}

public enum AiProvider
{
    OpenAI = 0,
    Anthropic = 1,
    Gemini = 2,
    Ollama = 3
}

public enum ActivityKind
{
    Login = 0,
    Logout = 1,
    LoginFailed = 2,
    MessageSent = 3,
    MessageDeleted = 4,
    GroupCreated = 5,
    GroupJoined = 6,
    GroupLeft = 7,
    ContactAdded = 8,
    UserBlocked = 9,
    UserReported = 10,
    SettingChanged = 11,
    FileUploaded = 12,
    CallStarted = 13,
    BotInvoked = 14,
    BackupCreated = 15
}

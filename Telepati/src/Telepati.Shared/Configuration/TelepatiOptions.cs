namespace Telepati.Shared.Configuration;

/// <summary>
/// Root of every knob in the product. Bound from the <c>Telepati</c> section of appsettings and
/// then overlaid with rows from the <c>AppSettings</c> table, so the admin app can change any of
/// this at runtime without a redeploy. Keys are addressed as <c>Telepati:Section:Property</c>.
/// </summary>
public class TelepatiOptions
{
    public const string SectionName = "Telepati";

    public BrandingOptions Branding { get; set; } = new();
    public DatabaseOptions Database { get; set; } = new();
    public CacheOptions Cache { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public SecurityOptions Security { get; set; } = new();
    public FeatureOptions Features { get; set; } = new();
    public BotOptions Bot { get; set; } = new();
    public ThemeOptions Theme { get; set; } = new();
    public LimitsOptions Limits { get; set; } = new();
}

public class BrandingOptions
{
    public string AppName { get; set; } = "Telepati";
    public string Tagline { get; set; } = "Ngobrol seru, tanpa jeda.";
    public string Company { get; set; } = "Gravicode Studios";
    public string Leader { get; set; } = "Kang Fadhil";
    public string SupportEmail { get; set; } = "support@telepati.app";
    public string LogoUrl { get; set; } = "/img/telepati-logo.svg";
}

public class DatabaseOptions
{
    /// <summary>Sqlite | SqlServer | MySql | PostgreSql</summary>
    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=telepati.db";
    public bool AutoMigrate { get; set; } = true;
    public bool SeedSampleData { get; set; } = true;
    public bool EnableSensitiveDataLogging { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 30;
    public ShardingOptions Sharding { get; set; } = new();
}

/// <summary>
/// Horizontal split of the message tables. Shard selection is deterministic on chat id, so all
/// messages of one conversation always land on the same node and reads never fan out.
/// </summary>
public class ShardingOptions
{
    public bool Enabled { get; set; }
    /// <summary>Connection strings, ordered; index is the shard number.</summary>
    public List<string> Shards { get; set; } = [];
    /// <summary>ChatId | UserId</summary>
    public string Strategy { get; set; } = "ChatId";
}

public class CacheOptions
{
    /// <summary>Memory | Redis</summary>
    public string Provider { get; set; } = "Memory";
    public string ConnectionString { get; set; } = "localhost:6379";
    public string InstanceName { get; set; } = "telepati:";
    public int DefaultTtlSeconds { get; set; } = 300;
    public int PresenceTtlSeconds { get; set; } = 60;
}

public class StorageOptions
{
    /// <summary>FileSystem | AzureBlob | S3 | MinIO</summary>
    public string Provider { get; set; } = "FileSystem";
    public string RootPath { get; set; } = "storage";
    public string PublicBaseUrl { get; set; } = "/files";
    public string ContainerName { get; set; } = "telepati";
    public string ConnectionString { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Region { get; set; } = "us-east-1";
    public bool UseSsl { get; set; } = true;
    public int SignedUrlTtlMinutes { get; set; } = 60;
}

public class SecurityOptions
{
    public string JwtIssuer { get; set; } = "Telepati";
    public string JwtAudience { get; set; } = "TelepatiClients";
    /// <summary>Must be at least 32 characters. Override per environment.</summary>
    public string JwtSecret { get; set; } = "telepati-development-secret-key-change-me!";
    public int AccessTokenMinutes { get; set; } = 60;
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Turns E2E encryption on for newly created chats. Existing chats keep their flag.</summary>
    public bool EnableEndToEndEncryption { get; set; }
    /// <summary>When false the 2FA endpoints reject enrolment, whatever the user profile says.</summary>
    public bool EnableTwoFactor { get; set; } = true;
    public bool RequireTwoFactorForAdmins { get; set; }
    public int MaxActiveSessions { get; set; } = 10;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public List<string> AllowedCorsOrigins { get; set; } = [];
}

public class FeatureOptions
{
    public bool EnableVoiceCall { get; set; } = true;
    public bool EnableVideoCall { get; set; } = true;
    public bool EnableStatus { get; set; } = true;
    public bool EnableChannels { get; set; } = true;
    public bool EnableBroadcast { get; set; } = true;
    public bool EnableBots { get; set; } = true;
    public bool EnableNearbySearch { get; set; } = true;
    public bool EnableStickers { get; set; } = true;
    public bool EnableGrpcTransport { get; set; } = true;
    public bool EnableRestTransport { get; set; } = true;
    public bool EnableSignalRTransport { get; set; } = true;
    /// <summary>STUN/TURN servers handed to clients for WebRTC.</summary>
    public List<string> IceServers { get; set; } = ["stun:stun.l.google.com:19302"];
    public double MinNearbyRadiusKm { get; set; } = 5;
    public double MaxNearbyRadiusKm { get; set; } = 100;
}

public class BotOptions
{
    public bool Enabled { get; set; } = true;
    public string Handle { get; set; } = "bacot";
    public string DisplayName { get; set; } = "Kang Bacot";
    public string AvatarUrl { get; set; } = "/img/bacot.svg";

    /// <summary>OpenAI | Anthropic | Gemini | Ollama</summary>
    public string Provider { get; set; } = "Ollama";
    public string Model { get; set; } = "llama3.2";
    public string ApiKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = "http://localhost:11434";
    public float Temperature { get; set; } = 0.8f;
    public float TopP { get; set; } = 0.95f;
    public int MaxTokens { get; set; } = 2048;

    public string SystemPrompt { get; set; } =
        "Kamu adalah Kang Bacot, teman ngobrol yang santai, ramah, dan kadang jenaka. " +
        "Kamu menjawab dalam Bahasa Indonesia yang natural kecuali diminta bahasa lain. " +
        "Kamu boleh memakai tool yang tersedia untuk mencari informasi, membaca file, " +
        "menghitung, atau menjalankan skrip ketika itu benar-benar membantu. " +
        "Jawaban ditulis dalam Markdown yang rapi.";

    /// <summary>Fold older turns into a summary once usage passes this share of the window.</summary>
    public double AutoCompactThreshold { get; set; } = 0.75;
    public int ContextWindowTokens { get; set; } = 32000;
    public int MaxTurnsKeptVerbatim { get; set; } = 20;

    /// <summary>Sandbox root for every file the bot writes or any script it runs.</summary>
    public string WorkspacePath { get; set; } = "workspace";
    public bool EnableCodeExecution { get; set; } = true;
    public bool AllowPackageInstall { get; set; }
    public int ExecutionTimeoutSeconds { get; set; } = 120;
    public List<string> AllowedExecutors { get; set; } = ["powershell", "python", "dotnet", "cmd", "node"];

    public string TavilyApiKey { get; set; } = string.Empty;
    public bool EnableWebSearch { get; set; } = true;
    public int MaxFunctionCallsPerTurn { get; set; } = 8;
}

public class ThemeOptions
{
    /// <summary>light | dark | system</summary>
    public string DefaultMode { get; set; } = "system";
    public string DefaultThemeName { get; set; } = "Telepati Classic";
    public bool EnableSeasonalThemes { get; set; } = true;
    public bool AllowUserThemeOverride { get; set; } = true;
    public bool EnableMicroInteractions { get; set; } = true;
}

public class LimitsOptions
{
    public long MaxUploadBytes { get; set; } = 100L * 1024 * 1024;
    public int MaxGroupMembers { get; set; } = 500;
    public int MaxBroadcastRecipients { get; set; } = 1000;
    public int MaxPinnedMessages { get; set; } = 5;
    public int MessagePageSize { get; set; } = 50;
    public int MaxMessageLength { get; set; } = 8000;
    public int StatusRetentionHours { get; set; } = 24;
}

/// <summary>Client-side half of the configuration: which server and which transport to use.</summary>
public class ClientOptions
{
    public const string SectionName = "TelepatiClient";

    public string ServerUrl { get; set; } = "https://localhost:7180";
    /// <summary>SignalR | Grpc | Rest — the user can change this from the client's settings page.</summary>
    public string Transport { get; set; } = "SignalR";
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectDelaySeconds { get; set; } = 3;
    /// <summary>REST fallback polling interval; ignored by the SignalR and gRPC transports.</summary>
    public int PollIntervalSeconds { get; set; } = 5;
    public bool ShareLocation { get; set; }
    public bool AllowContactImport { get; set; }
    public bool EnableNotificationSound { get; set; } = true;
    public string ThemeMode { get; set; } = "system";

    public LocalDatabaseOptions LocalDatabase { get; set; } = new();
}

/// <summary>
/// On-device cache for the desktop and mobile apps. Data already pulled from the API is kept
/// here so a cold start paints from disk; anything not cached still goes straight to the API.
/// The web app persists in the browser instead — see <c>telepati-store.js</c>.
/// </summary>
public class LocalDatabaseOptions
{
    /// <summary>Sqlite | LiteDb | None</summary>
    public string Provider { get; set; } = "Sqlite";

    /// <summary>Folder for the database file. Empty means the platform's local app-data folder.</summary>
    public string Path { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Cached lists older than this are refreshed from the API before being shown.</summary>
    public int FreshnessSeconds { get; set; } = 60;

    /// <summary>Newest messages per conversation that are never pruned.</summary>
    public int KeepMessagesPerChat { get; set; } = 300;

    /// <summary>Anything older than this is pruned, except the rows above.</summary>
    public int RetentionDays { get; set; } = 30;
}

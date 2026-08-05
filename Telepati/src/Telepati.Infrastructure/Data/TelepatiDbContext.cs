using Microsoft.EntityFrameworkCore;
using Telepati.Domain;

namespace Telepati.Infrastructure.Data;

public class TelepatiDbContext(DbContextOptions<TelepatiDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<ContactRequest> ContactRequests => Set<ContactRequest>();
    public DbSet<BlockedUser> BlockedUsers => Set<BlockedUser>();
    public DbSet<UserReport> UserReports => Set<UserReport>();

    public DbSet<Chat> Chats => Set<Chat>();
    public DbSet<ChatMember> ChatMembers => Set<ChatMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageAttachment> MessageAttachments => Set<MessageAttachment>();
    public DbSet<MessageReaction> MessageReactions => Set<MessageReaction>();
    public DbSet<MessageReceipt> MessageReceipts => Set<MessageReceipt>();
    public DbSet<MessageMention> MessageMentions => Set<MessageMention>();

    public DbSet<StatusPost> StatusPosts => Set<StatusPost>();
    public DbSet<StatusView> StatusViews => Set<StatusView>();
    public DbSet<CallSession> CallSessions => Set<CallSession>();
    public DbSet<Broadcast> Broadcasts => Set<Broadcast>();
    public DbSet<BroadcastRecipient> BroadcastRecipients => Set<BroadcastRecipient>();

    public DbSet<StickerPack> StickerPacks => Set<StickerPack>();
    public DbSet<Sticker> Stickers => Set<Sticker>();

    public DbSet<BotSession> BotSessions => Set<BotSession>();
    public DbSet<BotMessage> BotMessages => Set<BotMessage>();

    public DbSet<SkillRepository> SkillRepositories => Set<SkillRepository>();
    public DbSet<SkillDefinition> Skills => Set<SkillDefinition>();
    public DbSet<McpServerDefinition> McpServers => Set<McpServerDefinition>();

    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ThemeDefinition> Themes => Set<ThemeDefinition>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.PhoneNumber);
            // Nearby search scans this pair; a composite index keeps the bounding-box filter cheap.
            e.HasIndex(x => new { x.LastLatitude, x.LastLongitude });
            e.HasQueryFilter(x => !x.IsDeleted);
        });

        b.Entity<UserSession>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.IsRevoked });
            e.HasOne(x => x.User).WithMany(x => x.Sessions)
                .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Contact>(e =>
        {
            e.HasIndex(x => new { x.OwnerId, x.ContactUserId }).IsUnique();
            e.HasOne(x => x.Owner).WithMany(x => x.Contacts)
                .HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.ContactUser).WithMany()
                .HasForeignKey(x => x.ContactUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<ContactRequest>(e =>
        {
            e.HasIndex(x => new { x.TargetId, x.State });
            e.HasOne(x => x.Requester).WithMany().HasForeignKey(x => x.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BlockedUser>(e =>
        {
            e.HasIndex(x => new { x.OwnerId, x.BlockedUserId }).IsUnique();
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Blocked).WithMany().HasForeignKey(x => x.BlockedUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<UserReport>(e =>
        {
            e.HasIndex(x => x.State);
            e.HasOne(x => x.Reporter).WithMany().HasForeignKey(x => x.ReporterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReportedUser).WithMany().HasForeignKey(x => x.ReportedUserId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Chat>(e =>
        {
            e.HasIndex(x => x.Handle).IsUnique();
            e.HasIndex(x => new { x.Type, x.LastMessageAt });
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne(x => x.CreatedBy).WithMany().HasForeignKey(x => x.CreatedById).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ChatMember>(e =>
        {
            e.HasIndex(x => new { x.ChatId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.IsArchived });
            // The chat list filters on UserId + LeftAt and sorts by IsPinned; putting all three
            // in one index lets the engine serve that page without a sort step.
            e.HasIndex(x => new { x.UserId, x.LeftAt, x.IsPinned }).HasDatabaseName("IX_ChatMembers_Inbox");
            e.HasOne(x => x.Chat).WithMany(x => x.Members).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.User).WithMany(x => x.Memberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Message>(e =>
        {
            // The hot path is "latest page of one chat". IsDeleted is part of the key because
            // the global query filter adds it to every single read — without it the engine has
            // to fetch each row just to discover it is soft-deleted.
            e.HasIndex(x => new { x.ChatId, x.IsDeleted, x.CreatedAt }).HasDatabaseName("IX_Messages_Chat_Live");
            e.HasIndex(x => new { x.ChatId, x.CreatedAt });
            e.HasIndex(x => new { x.ChatId, x.IsPinned });
            e.HasIndex(x => x.SenderId);
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne(x => x.Chat).WithMany(x => x.Messages).HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ReplyToMessage).WithMany().HasForeignKey(x => x.ReplyToMessageId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<MessageAttachment>(e =>
            e.HasOne(x => x.Message).WithMany(x => x.Attachments).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade));

        b.Entity<MessageReaction>(e =>
        {
            e.HasIndex(x => new { x.MessageId, x.UserId, x.Emoji }).IsUnique();
            e.HasOne(x => x.Message).WithMany(x => x.Reactions).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MessageReceipt>(e =>
        {
            e.HasIndex(x => new { x.MessageId, x.UserId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.State });
            e.HasOne(x => x.Message).WithMany(x => x.Receipts).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<MessageMention>(e =>
        {
            e.HasIndex(x => new { x.MentionedUserId, x.IsNotified });
            e.HasOne(x => x.Message).WithMany(x => x.Mentions).HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StatusPost>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.ExpiresAt });
            e.HasQueryFilter(x => !x.IsDeleted);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<StatusView>(e =>
        {
            e.HasIndex(x => new { x.StatusPostId, x.ViewerId }).IsUnique();
            e.HasOne(x => x.StatusPost).WithMany(x => x.Views).HasForeignKey(x => x.StatusPostId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CallSession>(e =>
        {
            e.HasIndex(x => new { x.ChatId, x.CreatedAt });
            e.HasOne(x => x.Chat).WithMany().HasForeignKey(x => x.ChatId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Broadcast>(e =>
        {
            e.HasIndex(x => new { x.SenderId, x.State });
            e.HasOne(x => x.Sender).WithMany().HasForeignKey(x => x.SenderId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<BroadcastRecipient>(e =>
        {
            e.HasIndex(x => new { x.BroadcastId, x.UserId }).IsUnique();
            e.HasOne(x => x.Broadcast).WithMany(x => x.Recipients).HasForeignKey(x => x.BroadcastId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Sticker>(e =>
            e.HasOne(x => x.Pack).WithMany(x => x.Stickers).HasForeignKey(x => x.StickerPackId).OnDelete(DeleteBehavior.Cascade));

        b.Entity<BotSession>(e =>
        {
            // A bot session is unique per (chat, user) pair: group sessions carry ChatId,
            // direct sessions carry UserId, and the two never share history.
            e.HasIndex(x => new { x.ChatId, x.UserId, x.BotHandle }).IsUnique();
        });

        b.Entity<BotMessage>(e =>
        {
            e.HasIndex(x => new { x.BotSessionId, x.IsActive, x.CreatedAt });
            e.HasOne(x => x.Session).WithMany(x => x.Messages).HasForeignKey(x => x.BotSessionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ActivityLog>(e =>
        {
            e.HasIndex(x => new { x.CreatedAt, x.Kind });
            e.HasIndex(x => x.UserId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<SkillRepository>(e => e.HasIndex(x => x.Slug).IsUnique());

        b.Entity<SkillDefinition>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => x.IsEnabled);
            e.HasOne(x => x.Repository).WithMany()
                .HasForeignKey(x => x.SkillRepositoryId).OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<McpServerDefinition>(e =>
        {
            e.HasIndex(x => x.Slug).IsUnique();
            e.HasIndex(x => new { x.IsEnabled, x.Category });
        });

        b.Entity<AppSetting>(e => e.HasIndex(x => x.Key).IsUnique());

        b.Entity<ThemeDefinition>(e => e.HasIndex(x => x.Name).IsUnique());

        ApplyProviderQuirks(b);
    }

    /// <summary>
    /// MySQL and older SQL Server collations cap an indexed string key, and SQLite has no
    /// native <c>DateTimeOffset</c> ordering. Both are normalised here so one model definition
    /// works across all four supported providers.
    /// </summary>
    private void ApplyProviderQuirks(ModelBuilder b)
    {
        var provider = Database.ProviderName ?? string.Empty;

        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var property in b.Model.GetEntityTypes()
                         .SelectMany(t => t.GetProperties())
                         .Where(p => p.ClrType == typeof(DateTimeOffset) || p.ClrType == typeof(DateTimeOffset?)))
            {
                property.SetValueConverter(SqliteConverters.DateTimeOffsetConverter);
            }
        }

        if (provider.Contains("MySql", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var property in b.Model.GetEntityTypes()
                         .SelectMany(t => t.GetProperties())
                         .Where(p => p.ClrType == typeof(string) && p.GetMaxLength() is null or > 768))
            {
                property.SetMaxLength(768);
            }
        }
    }

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void StampTimestamps()
    {
        foreach (var entry in ChangeTracker.Entries<BaseEntity>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }
    }
}

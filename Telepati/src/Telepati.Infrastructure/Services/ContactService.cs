using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IContactService
{
    Task<IReadOnlyList<ContactDto>> GetContactsAsync(Guid userId, CancellationToken ct = default);
    Task<ApiResult> AddAsync(Guid userId, Guid contactUserId, string? alias, CancellationToken ct = default);
    Task<ApiResult> RemoveAsync(Guid userId, Guid contactUserId, CancellationToken ct = default);
    Task<ApiResult> SetFavoriteAsync(Guid userId, Guid contactUserId, bool favorite, CancellationToken ct = default);
    Task<ApiResult> BlockAsync(Guid userId, Guid targetId, string? reason, CancellationToken ct = default);
    Task<ApiResult> UnblockAsync(Guid userId, Guid targetId, CancellationToken ct = default);
    Task<IReadOnlyList<UserDto>> GetBlockedAsync(Guid userId, CancellationToken ct = default);
    Task<ApiResult> ReportAsync(Guid userId, Guid targetId, ReportReason reason, string? details, Guid? messageId, CancellationToken ct = default);

    /// <summary>Payload encoded into the user's shareable QR code.</summary>
    Task<ApiResult<ContactCardDto>> GetContactCardAsync(Guid userId, CancellationToken ct = default);
    Task<ApiResult<UserDto>> ResolveContactCardAsync(Guid requesterId, string payload, CancellationToken ct = default);
    /// <summary>Matches an imported phone book against registered accounts.</summary>
    Task<IReadOnlyList<UserDto>> MatchPhoneNumbersAsync(Guid userId, IReadOnlyList<string> phoneNumbers, CancellationToken ct = default);
}

public record ContactCardDto(Guid UserId, string Username, string DisplayName, string? AvatarUrl, string Payload, string QrCodePngBase64);

public class ContactService(TelepatiDbContext db, IActivityLogger activity) : IContactService
{
    private const string CardScheme = "telepati://contact/";

    public async Task<IReadOnlyList<ContactDto>> GetContactsAsync(Guid userId, CancellationToken ct = default)
    {
        var contacts = await db.Contacts.AsNoTracking()
            .Where(c => c.OwnerId == userId)
            .Include(c => c.ContactUser)
            .OrderByDescending(c => c.IsFavorite)
            .ThenBy(c => c.Alias ?? c.ContactUser!.DisplayName)
            .ToListAsync(ct);

        return contacts
            .Where(c => c.ContactUser is not null)
            .Select(c => new ContactDto(c.Id, c.ContactUser!.ToDto(), c.Alias, c.IsFavorite, c.IsMuted))
            .ToList();
    }

    public async Task<ApiResult> AddAsync(Guid userId, Guid contactUserId, string? alias, CancellationToken ct = default)
    {
        if (userId == contactUserId) return ApiResult.Fail("Tidak bisa menambah diri sendiri.");
        if (!await db.Users.AnyAsync(u => u.Id == contactUserId, ct)) return ApiResult.Fail("User tidak ditemukan.");
        if (await db.Contacts.AnyAsync(c => c.OwnerId == userId && c.ContactUserId == contactUserId, ct))
            return ApiResult.Fail("Kontak sudah ada.");

        db.Contacts.Add(new Contact { OwnerId = userId, ContactUserId = contactUserId, Alias = alias });
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(ActivityKind.ContactAdded, userId, "Menambah kontak.", entityType: nameof(Contact), entityId: contactUserId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> RemoveAsync(Guid userId, Guid contactUserId, CancellationToken ct = default)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.OwnerId == userId && c.ContactUserId == contactUserId, ct);
        if (contact is null) return ApiResult.Fail("Kontak tidak ditemukan.");

        db.Contacts.Remove(contact);
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> SetFavoriteAsync(Guid userId, Guid contactUserId, bool favorite, CancellationToken ct = default)
    {
        var contact = await db.Contacts.FirstOrDefaultAsync(c => c.OwnerId == userId && c.ContactUserId == contactUserId, ct);
        if (contact is null) return ApiResult.Fail("Kontak tidak ditemukan.");

        contact.IsFavorite = favorite;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> BlockAsync(Guid userId, Guid targetId, string? reason, CancellationToken ct = default)
    {
        if (userId == targetId) return ApiResult.Fail("Tidak bisa memblokir diri sendiri.");
        if (await db.BlockedUsers.AnyAsync(b => b.OwnerId == userId && b.BlockedUserId == targetId, ct))
            return ApiResult.Ok();

        db.BlockedUsers.Add(new BlockedUser { OwnerId = userId, BlockedUserId = targetId, Reason = reason });
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(ActivityKind.UserBlocked, userId, reason, entityType: nameof(User), entityId: targetId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> UnblockAsync(Guid userId, Guid targetId, CancellationToken ct = default)
    {
        var block = await db.BlockedUsers.FirstOrDefaultAsync(b => b.OwnerId == userId && b.BlockedUserId == targetId, ct);
        if (block is null) return ApiResult.Ok();

        db.BlockedUsers.Remove(block);
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<IReadOnlyList<UserDto>> GetBlockedAsync(Guid userId, CancellationToken ct = default)
    {
        var blocked = await db.BlockedUsers.AsNoTracking()
            .Where(b => b.OwnerId == userId)
            .Include(b => b.Blocked)
            .ToListAsync(ct);

        return blocked.Where(b => b.Blocked is not null).Select(b => b.Blocked!.ToDto()).ToList();
    }

    public async Task<ApiResult> ReportAsync(Guid userId, Guid targetId, ReportReason reason, string? details, Guid? messageId, CancellationToken ct = default)
    {
        if (userId == targetId) return ApiResult.Fail("Tidak bisa melaporkan diri sendiri.");

        db.UserReports.Add(new UserReport
        {
            ReporterId = userId,
            ReportedUserId = targetId,
            Reason = reason,
            Details = details,
            MessageId = messageId
        });

        await db.SaveChangesAsync(ct);
        await activity.LogAsync(ActivityKind.UserReported, userId, $"{reason}: {details}", entityType: nameof(User), entityId: targetId, ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult<ContactCardDto>> GetContactCardAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult<ContactCardDto>.Fail("User tidak ditemukan.");

        // The card carries only public identifiers, so a shared QR leaks nothing sensitive.
        var payload = CardScheme + System.Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = user.Id,
            u = user.Username,
            n = user.DisplayName
        }));

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(10);

        return ApiResult<ContactCardDto>.Ok(new ContactCardDto(
            user.Id, user.Username, user.DisplayName, user.AvatarUrl, payload, System.Convert.ToBase64String(png)));
    }

    public async Task<ApiResult<UserDto>> ResolveContactCardAsync(Guid requesterId, string payload, CancellationToken ct = default)
    {
        if (!payload.StartsWith(CardScheme, StringComparison.OrdinalIgnoreCase))
            return ApiResult<UserDto>.Fail("QR bukan kartu kontak Telepati.");

        Guid targetId;
        try
        {
            var json = System.Convert.FromBase64String(payload[CardScheme.Length..]);
            using var document = JsonDocument.Parse(json);
            targetId = document.RootElement.GetProperty("id").GetGuid();
        }
        catch
        {
            return ApiResult<UserDto>.Fail("QR tidak bisa dibaca.");
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId, ct);
        if (user is null) return ApiResult<UserDto>.Fail("User tidak ditemukan.");

        // Blocks still apply to a scanned card.
        var blocked = await db.BlockedUsers.AsNoTracking().AnyAsync(b =>
            (b.OwnerId == requesterId && b.BlockedUserId == targetId) ||
            (b.OwnerId == targetId && b.BlockedUserId == requesterId), ct);

        return blocked
            ? ApiResult<UserDto>.Fail("User tidak tersedia.")
            : ApiResult<UserDto>.Ok(user.ToDto());
    }

    public async Task<IReadOnlyList<UserDto>> MatchPhoneNumbersAsync(Guid userId, IReadOnlyList<string> phoneNumbers, CancellationToken ct = default)
    {
        if (phoneNumbers.Count == 0) return [];

        var normalized = phoneNumbers
            .Select(Normalize)
            .Where(p => p.Length >= 8)
            .Distinct()
            .Take(2000)
            .ToList();

        var candidates = await db.Users.AsNoTracking()
            .Where(u => u.Id != userId && u.IsActive && u.PhoneNumber != null)
            .Select(u => new { u.Id, u.PhoneNumber })
            .ToListAsync(ct);

        // Matching happens in memory because normalisation (spaces, dashes, leading 0 vs +62)
        // cannot be expressed as an index-friendly SQL predicate.
        var matchedIds = candidates
            .Where(c => normalized.Contains(Normalize(c.PhoneNumber!)))
            .Select(c => c.Id)
            .ToList();

        var users = await db.Users.AsNoTracking().Where(u => matchedIds.Contains(u.Id)).ToListAsync(ct);
        return users.Select(u => u.ToDto()).ToList();
    }

    private static string Normalize(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        // Indonesian numbers arrive as 08xx, 628xx or +628xx; compare on the last 9 digits.
        return digits.Length > 9 ? digits[^9..] : digits;
    }
}

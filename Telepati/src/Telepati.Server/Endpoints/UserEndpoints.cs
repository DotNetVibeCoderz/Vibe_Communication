using System.Security.Claims;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Infrastructure.Storage;
using Telepati.Shared.Contracts;

namespace Telepati.Server.Endpoints;

public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/users").WithTags("Users").RequireAuthorization();

        group.MapGet("/me", async (ClaimsPrincipal user, IUserService users, CancellationToken ct) =>
        {
            var dto = await users.GetAsync(user.GetUserId(), ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithSummary("Profil saya");

        group.MapGet("/{userId:guid}", async (Guid userId, IUserService users, CancellationToken ct) =>
        {
            var dto = await users.GetAsync(userId, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithSummary("Profil pengguna lain");

        group.MapGet("/by-username/{username}", async (string username, IUserService users, CancellationToken ct) =>
        {
            var dto = await users.GetByUsernameAsync(username, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        })
        .WithSummary("Cari pengguna berdasarkan username");

        group.MapPut("/me", async (UpdateProfileRequest request, ClaimsPrincipal user, IUserService users, CancellationToken ct) =>
        {
            var result = await users.UpdateProfileAsync(user.GetUserId(), request.DisplayName, request.About,
                request.AvatarUrl, request.PreferredLanguage, request.PreferredTheme, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Perbarui profil");

        group.MapPost("/me/password", async (ChangePasswordRequest request, ClaimsPrincipal user, IUserService users, CancellationToken ct) =>
            Results.Ok(await users.ChangePasswordAsync(user.GetUserId(), request.CurrentPassword, request.NewPassword, ct)))
        .WithSummary("Ganti password");

        group.MapPost("/me/presence", async (PresenceRequestDto request, ClaimsPrincipal user, IUserService users, CancellationToken ct) =>
            Results.Ok(await users.SetPresenceAsync(user.GetUserId(), (UserPresence)request.Presence, ct)))
        .WithSummary("Ubah status kehadiran");

        group.MapPost("/me/location", async (LocationRequest request, ClaimsPrincipal user, IUserService users, CancellationToken ct) =>
            Results.Ok(await users.UpdateLocationAsync(user.GetUserId(), request.Latitude, request.Longitude, request.ShareForDiscovery, ct)))
        .WithSummary("Kirim lokasi untuk fitur pencarian sekitar");

        group.MapPost("/search", async (ContactSearchRequest request, ClaimsPrincipal user, IUserService users, CancellationToken ct) =>
            Results.Ok(await users.SearchAsync(user.GetUserId(), request, ct)))
        .WithSummary("Cari pengguna via email / telepon / username / sekitar");

        return app;
    }
}

public static class ContactEndpoints
{
    public static IEndpointRouteBuilder MapContactEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/contacts").WithTags("Contacts").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.GetContactsAsync(user.GetUserId(), ct)))
        .WithSummary("Daftar kontak");

        group.MapPost("/{contactUserId:guid}", async (Guid contactUserId, AliasRequest? request,
            ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.AddAsync(user.GetUserId(), contactUserId, request?.Alias, ct)))
        .WithSummary("Tambah kontak");

        group.MapDelete("/{contactUserId:guid}", async (Guid contactUserId, ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.RemoveAsync(user.GetUserId(), contactUserId, ct)))
        .WithSummary("Hapus kontak");

        group.MapPost("/{contactUserId:guid}/favorite", async (Guid contactUserId, ToggleRequest request,
            ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.SetFavoriteAsync(user.GetUserId(), contactUserId, request.Value, ct)))
        .WithSummary("Tandai kontak favorit");

        group.MapGet("/qr", async (ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
        {
            var result = await contacts.GetContactCardAsync(user.GetUserId(), ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Kartu kontak saya dalam bentuk QR");

        group.MapPost("/qr/resolve", async (QrPayloadRequest request, ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
        {
            var result = await contacts.ResolveContactCardAsync(user.GetUserId(), request.Payload, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Baca kartu kontak dari hasil scan QR");

        group.MapPost("/import-phones", async (ImportPhonesRequest request, ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.MatchPhoneNumbersAsync(user.GetUserId(), request.PhoneNumbers, ct)))
        .WithSummary("Cocokkan kontak telepon dengan pengguna Telepati");

        group.MapPost("/{targetId:guid}/block", async (Guid targetId, BlockRequest? request,
            ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.BlockAsync(user.GetUserId(), targetId, request?.Reason, ct)))
        .WithSummary("Blokir pengguna");

        group.MapDelete("/{targetId:guid}/block", async (Guid targetId, ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.UnblockAsync(user.GetUserId(), targetId, ct)))
        .WithSummary("Buka blokir");

        group.MapGet("/blocked", async (ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.GetBlockedAsync(user.GetUserId(), ct)))
        .WithSummary("Daftar pengguna yang diblokir");

        group.MapPost("/{targetId:guid}/report", async (Guid targetId, ReportRequest request,
            ClaimsPrincipal user, IContactService contacts, CancellationToken ct) =>
            Results.Ok(await contacts.ReportAsync(user.GetUserId(), targetId, (ReportReason)request.Reason, request.Details, request.MessageId, ct)))
        .WithSummary("Laporkan pengguna");

        return app;
    }
}

public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files").WithTags("Files").RequireAuthorization();

        group.MapPost("/upload", async (IFormFile file, ClaimsPrincipal user, IAttachmentService attachments, string? folder, CancellationToken ct) =>
        {
            if (file.Length == 0) return Results.BadRequest(ApiResult.Fail("File kosong."));

            await using var stream = file.OpenReadStream();
            var result = await attachments.UploadAsync(user.GetUserId(), stream, file.FileName,
                file.ContentType ?? "application/octet-stream", folder ?? StorageFolders.Attachments, ct);

            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .DisableAntiforgery()
        .WithSummary("Unggah file dan dapatkan attachment id");

        group.MapGet("/{attachmentId:guid}", async (Guid attachmentId, IAttachmentService attachments, CancellationToken ct) =>
        {
            var file = await attachments.DownloadAsync(attachmentId, ct);
            return file is null
                ? Results.NotFound()
                : Results.File(file.Value.Content, file.Value.ContentType, file.Value.FileName);
        })
        .WithSummary("Unduh lampiran");

        group.MapDelete("/{attachmentId:guid}", async (Guid attachmentId, ClaimsPrincipal user, IAttachmentService attachments, CancellationToken ct) =>
            Results.Ok(await attachments.DeleteAsync(user.GetUserId(), attachmentId, ct)))
        .WithSummary("Hapus lampiran");

        return app;
    }
}

public record UpdateProfileRequest(string? DisplayName, string? About, string? AvatarUrl, string? PreferredLanguage, string? PreferredTheme);
public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public record PresenceRequestDto(int Presence);
public record LocationRequest(double Latitude, double Longitude, bool ShareForDiscovery);
public record AliasRequest(string? Alias);
public record QrPayloadRequest(string Payload);
public record ImportPhonesRequest(IReadOnlyList<string> PhoneNumbers);
public record BlockRequest(string? Reason);
public record ReportRequest(int Reason, string? Details, Guid? MessageId);

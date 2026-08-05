using System.Security.Claims;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Server.Endpoints;

/// <summary>
/// Administration surface, also the integration point for external systems — it is plain REST
/// with Swagger, so a third-party tool can drive it without touching SignalR or gRPC.
/// </summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("AdminOnly");

        group.MapGet("/dashboard", async (IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.GetDashboardAsync(ct)))
        .WithSummary("Statistik realtime untuk dashboard");

        group.MapGet("/users", async (string? search, int? page, int? pageSize, IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.GetUsersAsync(search, QueryDefaults.Page(page), QueryDefaults.Size(pageSize, 25), ct)))
        .WithSummary("Daftar pengguna");

        group.MapPost("/users/{userId:guid}/active", async (Guid userId, ToggleRequest request,
            ClaimsPrincipal user, IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.SetUserActiveAsync(user.GetUserId(), userId, request.Value, ct)))
        .WithSummary("Aktifkan / nonaktifkan pengguna");

        group.MapPost("/users/{userId:guid}/role", async (Guid userId, RoleRequest request,
            ClaimsPrincipal user, IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.SetUserRoleAsync(user.GetUserId(), userId, (UserRole)request.Role, ct)))
        .WithSummary("Ubah role pengguna");

        group.MapGet("/chats", async (int? type, string? search, int? page, int? pageSize, IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.GetChatsAsync(type is null ? null : (ChatType)type, search, QueryDefaults.Page(page), QueryDefaults.Size(pageSize, 25), ct)))
        .WithSummary("Daftar percakapan");

        group.MapGet("/chats/{chatId:guid}/insights", async (Guid chatId, IAdminService admin, CancellationToken ct) =>
        {
            var insights = await admin.GetGroupInsightsAsync(chatId, ct);
            return insights is null ? Results.NotFound() : Results.Ok(insights);
        })
        .WithSummary("Statistik aktivitas grup");

        group.MapGet("/reports", async (int? state, int? page, int? pageSize, IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.GetReportsAsync(state is null ? null : (ReportState)state, QueryDefaults.Page(page), QueryDefaults.Size(pageSize, 25), ct)))
        .WithSummary("Daftar laporan pengguna");

        group.MapPost("/reports/{reportId:guid}/resolve", async (Guid reportId, ResolveReportRequest request,
            ClaimsPrincipal user, IAdminService admin, CancellationToken ct) =>
            Results.Ok(await admin.ResolveReportAsync(user.GetUserId(), reportId, (ReportState)request.State, request.Note, ct)))
        .WithSummary("Tindak lanjuti laporan");

        group.MapGet("/activity", async (Guid? userId, int? kind, DateTimeOffset? from, DateTimeOffset? to,
            int? page, int? pageSize, IActivityLogger logger, CancellationToken ct) =>
            Results.Ok(await logger.QueryAsync(userId, kind is null ? null : (ActivityKind)kind, from, to,
                QueryDefaults.Page(page), QueryDefaults.Size(pageSize, 50), ct)))
        .WithSummary("Log aktivitas pengguna");

        // --- settings --------------------------------------------------------

        group.MapGet("/settings", async (ISettingsService settings, CancellationToken ct) =>
            Results.Ok(await settings.GetAllAsync(ct)))
        .WithSummary("Semua pengaturan aplikasi");

        group.MapPut("/settings", async (IReadOnlyList<SettingDto> settingsToSave, ISettingsService settings, CancellationToken ct) =>
        {
            await settings.SetManyAsync(settingsToSave, ct);
            return Results.Ok(ApiResult.Ok());
        })
        .WithSummary("Simpan perubahan pengaturan");

        group.MapDelete("/settings/{key}", async (string key, ISettingsService settings, CancellationToken ct) =>
        {
            await settings.ResetAsync(key, ct);
            return Results.Ok(ApiResult.Ok());
        })
        .WithSummary("Kembalikan satu pengaturan ke nilai appsettings");

        // --- themes ----------------------------------------------------------

        group.MapGet("/themes", async (IThemeService themes, CancellationToken ct) =>
            Results.Ok(await themes.GetAllAsync(ct)))
        .WithSummary("Daftar tema");

        group.MapPost("/themes", async (ThemeDto theme, IThemeService themes, CancellationToken ct) =>
        {
            var result = await themes.CreateAsync(theme, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Tambah tema (termasuk tema hari besar)");

        group.MapPut("/themes", async (ThemeDto theme, IThemeService themes, CancellationToken ct) =>
            Results.Ok(await themes.UpdateAsync(theme, ct)))
        .WithSummary("Ubah tema");

        group.MapPost("/themes/{themeId:guid}/activate", async (Guid themeId, IThemeService themes, CancellationToken ct) =>
            Results.Ok(await themes.ActivateAsync(themeId, ct)))
        .WithSummary("Aktifkan tema");

        group.MapDelete("/themes/{themeId:guid}", async (Guid themeId, IThemeService themes, CancellationToken ct) =>
            Results.Ok(await themes.DeleteAsync(themeId, ct)))
        .WithSummary("Hapus tema");

        // --- maintenance -----------------------------------------------------

        group.MapPost("/backup", async (ClaimsPrincipal user, IBackupService backup, CancellationToken ct) =>
            Results.Ok(await backup.CreateBackupAsync(user.GetUserId(), ct)))
        .WithSummary("Buat backup database ke file .sql");

        group.MapPost("/maintenance/purge-status", async (IStatusService status, CancellationToken ct) =>
            Results.Ok(new { removed = await status.PurgeExpiredAsync(ct) }))
        .WithSummary("Hapus status yang sudah kedaluwarsa");

        group.MapPost("/maintenance/purge-orphan-files", async (IAttachmentService attachments, CancellationToken ct) =>
            Results.Ok(new { removed = await attachments.PurgeOrphansAsync(TimeSpan.FromDays(1), ct) }))
        .WithSummary("Hapus file terunggah yang tidak pernah terkirim");

        group.MapPost("/maintenance/purge-logs", async (int? days, IActivityLogger logger, CancellationToken ct) =>
            Results.Ok(new { removed = await logger.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-Math.Max(1, days ?? 90)), ct) }))
        .WithSummary("Hapus log aktivitas lama");

        return app;
    }
}

/// <summary>Endpoints every signed-in client needs, regardless of role.</summary>
public static class PublicEndpoints
{
    public static IEndpointRouteBuilder MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/theme/active", async (IThemeService themes, CancellationToken ct) =>
            Results.Ok(await themes.GetActiveAsync(ct)))
        .WithTags("Theme")
        .AllowAnonymous()
        .CacheOutput("static-config")
        .WithSummary("Tema yang sedang aktif (termasuk tema musiman)");

        app.MapGet("/api/config/client", async (ISettingsService settings, CancellationToken ct) =>
        {
            var options = await settings.GetOptionsAsync(ct);
            // Only the switches a client legitimately needs; secrets never leave the server.
            return Results.Ok(new
            {
                appName = options.Branding.AppName,
                tagline = options.Branding.Tagline,
                company = options.Branding.Company,
                leader = options.Branding.Leader,
                features = options.Features,
                theme = options.Theme,
                limits = options.Limits,
                botHandle = options.Bot.Handle,
                botDisplayName = options.Bot.DisplayName,
                encryptionEnabled = options.Security.EnableEndToEndEncryption,
                twoFactorEnabled = options.Security.EnableTwoFactor
            });
        })
        .WithTags("Config")
        .AllowAnonymous()
        .CacheOutput("static-config")
        .WithSummary("Konfigurasi publik untuk aplikasi klien");

        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", at = DateTimeOffset.UtcNow }))
            .WithTags("Health")
            .AllowAnonymous()
            .WithSummary("Health check");

        return app;
    }
}

public static class SocialEndpoints
{
    public static IEndpointRouteBuilder MapSocialEndpoints(this IEndpointRouteBuilder app)
    {
        var status = app.MapGroup("/api/status").WithTags("Status").RequireAuthorization();

        status.MapGet("/", async (ClaimsPrincipal user, IStatusService statuses, CancellationToken ct) =>
            Results.Ok(await statuses.GetFeedAsync(user.GetUserId(), ct)))
        .WithSummary("Status dari kontak");

        status.MapGet("/mine", async (ClaimsPrincipal user, IStatusService statuses, CancellationToken ct) =>
            Results.Ok(await statuses.GetMineAsync(user.GetUserId(), ct)))
        .WithSummary("Status saya");

        status.MapPost("/", async (CreateStatusRequest request, ClaimsPrincipal user, IStatusService statuses, CancellationToken ct) =>
        {
            var result = await statuses.CreateAsync(user.GetUserId(), request, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Pasang status baru");

        status.MapPost("/{statusId:guid}/view", async (Guid statusId, ClaimsPrincipal user, IStatusService statuses, CancellationToken ct) =>
            Results.Ok(await statuses.ViewAsync(user.GetUserId(), statusId, ct)))
        .WithSummary("Tandai status sudah dilihat");

        status.MapDelete("/{statusId:guid}", async (Guid statusId, ClaimsPrincipal user, IStatusService statuses, CancellationToken ct) =>
            Results.Ok(await statuses.DeleteAsync(user.GetUserId(), statusId, ct)))
        .WithSummary("Hapus status");

        var calls = app.MapGroup("/api/calls").WithTags("Calls").RequireAuthorization();

        calls.MapGet("/ice-servers", async (ICallService callService, CancellationToken ct) =>
            Results.Ok(await callService.GetIceServersAsync(ct)))
        .WithSummary("Daftar STUN/TURN untuk WebRTC");

        calls.MapPost("/start", async (StartCallRequest request, ClaimsPrincipal user, ICallService callService, CancellationToken ct) =>
        {
            var result = await callService.StartAsync(user.GetUserId(), request, ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .WithSummary("Mulai panggilan suara / video");

        calls.MapPost("/signal", async (CallSignalDto signal, ClaimsPrincipal user, ICallService callService, CancellationToken ct) =>
            Results.Ok(await callService.RelaySignalAsync(user.GetUserId(), signal, ct)))
        .WithSummary("Relay sinyal WebRTC (offer/answer/ICE)");

        calls.MapPost("/{callId:guid}/answer", async (Guid callId, ToggleRequest request,
            ClaimsPrincipal user, ICallService callService, CancellationToken ct) =>
            Results.Ok(await callService.AnswerAsync(user.GetUserId(), callId, request.Value, ct)))
        .WithSummary("Terima / tolak panggilan");

        calls.MapPost("/{callId:guid}/end", async (Guid callId, ClaimsPrincipal user, ICallService callService, CancellationToken ct) =>
            Results.Ok(await callService.EndAsync(user.GetUserId(), callId, ct)))
        .WithSummary("Akhiri panggilan");

        var broadcasts = app.MapGroup("/api/broadcasts").WithTags("Broadcasts").RequireAuthorization();

        broadcasts.MapGet("/", async (ClaimsPrincipal user, IBroadcastService service, CancellationToken ct) =>
            Results.Ok(await service.GetMineAsync(user.GetUserId(), ct)))
        .WithSummary("Daftar broadcast saya");

        broadcasts.MapPost("/", async (BroadcastRequest request, ClaimsPrincipal user, IBroadcastService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(user.GetUserId(), request, ct);
            return result.Success ? Results.Ok(new { broadcastId = result.Data }) : Results.BadRequest(result);
        })
        .WithSummary("Susun broadcast");

        broadcasts.MapPost("/{broadcastId:guid}/send", async (Guid broadcastId, ClaimsPrincipal user, IBroadcastService service, CancellationToken ct) =>
            Results.Ok(await service.SendAsync(user.GetUserId(), broadcastId, ct)))
        .WithSummary("Kirim broadcast ke semua penerima");

        broadcasts.MapGet("/{broadcastId:guid}/report", async (Guid broadcastId, ClaimsPrincipal user, IBroadcastService service, CancellationToken ct) =>
        {
            var report = await service.GetReportAsync(user.GetUserId(), broadcastId, ct);
            return report is null ? Results.NotFound() : Results.Ok(report);
        })
        .WithSummary("Laporan pengiriman broadcast");

        return app;
    }
}

public record RoleRequest(int Role);
public record ResolveReportRequest(int State, string? Note);

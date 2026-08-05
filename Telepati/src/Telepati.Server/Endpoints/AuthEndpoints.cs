using System.Security.Claims;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Server.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (RegisterRequest request, IAuthService auth, CancellationToken ct) =>
        {
            var result = await auth.RegisterAsync(request, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        })
        .AllowAnonymous()
        .WithSummary("Daftar akun baru");

        group.MapPost("/login", async (LoginRequest request, IAuthService auth, HttpContext http, CancellationToken ct) =>
        {
            var result = await auth.LoginAsync(
                request,
                http.Connection.RemoteIpAddress?.ToString(),
                http.Request.Headers.UserAgent.ToString(),
                ct);

            // A pending 2FA challenge is a valid outcome, not an error — the client shows a prompt.
            return result.Success || result.TwoFactorRequired ? Results.Ok(result) : Results.BadRequest(result);
        })
        .AllowAnonymous()
        .WithSummary("Login dan dapatkan token");

        group.MapPost("/refresh", async (RefreshRequest request, IAuthService auth, CancellationToken ct) =>
        {
            var result = await auth.RefreshAsync(request.RefreshToken, ct);
            return result.Success ? Results.Ok(result) : Results.Unauthorized();
        })
        .AllowAnonymous()
        .WithSummary("Perbarui access token");

        group.MapPost("/logout", async (ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
        {
            var sessionId = user.GetSessionId();
            if (sessionId is null) return Results.BadRequest(ApiResult.Fail("Sesi tidak dikenali."));

            return Results.Ok(await auth.LogoutAsync(sessionId.Value, ct));
        })
        .RequireAuthorization()
        .WithSummary("Logout dari perangkat ini");

        group.MapGet("/sessions", async (ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.GetSessionsAsync(user.GetUserId(), user.GetSessionId(), ct)))
        .RequireAuthorization()
        .WithSummary("Daftar perangkat yang sedang login");

        group.MapDelete("/sessions/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.RevokeSessionAsync(user.GetUserId(), sessionId, ct)))
        .RequireAuthorization()
        .WithSummary("Keluarkan satu perangkat");

        group.MapPost("/sessions/revoke-others", async (ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
        {
            var sessionId = user.GetSessionId();
            if (sessionId is null) return Results.BadRequest(ApiResult.Fail("Sesi tidak dikenali."));

            return Results.Ok(await auth.RevokeAllOtherSessionsAsync(user.GetUserId(), sessionId.Value, ct));
        })
        .RequireAuthorization()
        .WithSummary("Keluarkan semua perangkat lain");

        // --- two-factor ------------------------------------------------------

        group.MapPost("/2fa/setup", async (ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
        {
            var result = await auth.BeginTwoFactorSetupAsync(user.GetUserId(), ct);
            return result.Success ? Results.Ok(result.Data) : Results.BadRequest(result);
        })
        .RequireAuthorization()
        .WithSummary("Mulai pendaftaran 2FA (QR + secret)");

        group.MapPost("/2fa/confirm", async (TwoFactorCodeRequest request, ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.ConfirmTwoFactorAsync(user.GetUserId(), request.Code, ct)))
        .RequireAuthorization()
        .WithSummary("Konfirmasi dan aktifkan 2FA");

        group.MapPost("/2fa/disable", async (TwoFactorCodeRequest request, ClaimsPrincipal user, IAuthService auth, CancellationToken ct) =>
            Results.Ok(await auth.DisableTwoFactorAsync(user.GetUserId(), request.Code, ct)))
        .RequireAuthorization()
        .WithSummary("Matikan 2FA");

        return app;
    }
}

public record TwoFactorCodeRequest(string Code);

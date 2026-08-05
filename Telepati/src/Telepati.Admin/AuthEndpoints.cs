using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Admin;

/// <summary>
/// Sign-in and sign-out for the console.
///
/// These are plain form endpoints rather than Blazor event handlers because issuing an
/// authentication cookie needs the real <c>HttpContext</c>, which an interactive circuit no
/// longer has — the response headers are long gone by the time a button is clicked.
/// </summary>
public static class AuthEndpoints
{
    public const string SignInPath = "/auth/signin";
    public const string SignOutPath = "/auth/signout";

    public static IEndpointRouteBuilder MapAdminAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(SignInPath, async (
            HttpContext http,
            IAuthService auth,
            [Microsoft.AspNetCore.Mvc.FromForm] string identifier,
            [Microsoft.AspNetCore.Mvc.FromForm] string password,
            [Microsoft.AspNetCore.Mvc.FromForm] string? code) =>
        {
            var result = await auth.LoginAsync(new LoginRequest(
                    identifier, password,
                    string.IsNullOrWhiteSpace(code) ? null : code,
                    "Konsol Admin", "admin", "web"),
                http.Connection.RemoteIpAddress?.ToString(),
                http.Request.Headers.UserAgent.ToString());

            if (result.TwoFactorRequired)
            {
                return Results.Redirect("/login?twofactor=1&identifier=" + Uri.EscapeDataString(identifier));
            }

            if (!result.Success || result.User is null)
            {
                return Results.Redirect("/login?error=" + Uri.EscapeDataString(result.Error ?? "Login gagal."));
            }

            // Authenticating is not enough — the console is only for elevated roles.
            if (result.User.Role < (int)UserRole.Admin)
            {
                return Results.Redirect("/login?error=" + Uri.EscapeDataString("Akun ini tidak punya akses ke konsol admin."));
            }

            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, result.User.Id.ToString()),
                new Claim(ClaimTypes.Name, result.User.Username),
                new Claim(ClaimTypes.Role, ((UserRole)result.User.Role).ToString())
            ], CookieAuthenticationDefaults.AuthenticationScheme);

            await http.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(identity),
                new AuthenticationProperties { IsPersistent = true });

            return Results.Redirect("/");
        })
        .DisableAntiforgery();

        app.MapPost(SignOutPath, async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        })
        .DisableAntiforgery();

        return app;
    }
}

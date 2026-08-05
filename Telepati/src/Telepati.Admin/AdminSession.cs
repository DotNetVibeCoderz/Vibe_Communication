using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Telepati.Domain;
using Telepati.Infrastructure.Services;
using Telepati.Shared.Contracts;

namespace Telepati.Admin;

/// <summary>
/// Who is signed in to the admin console.
///
/// The identity comes from an authentication cookie rather than from circuit state. An earlier
/// version held it in a scoped field, which meant every full-page navigation — and every F5 —
/// started a fresh circuit and silently logged the admin out.
/// </summary>
public class AdminSession(AuthenticationStateProvider authentication, IUserService users)
{
    private UserDto? _user;
    private bool _loaded;

    public UserDto? User => _user;

    public bool IsAuthenticated => _user is not null;

    public bool IsSuperAdmin => _user?.Role >= (int)UserRole.SuperAdmin;

    public Guid UserId => _user?.Id ?? Guid.Empty;

    /// <summary>
    /// Reads the signed-in admin from the cookie's claims. Safe to call repeatedly — the layout
    /// calls it on every render, and only the first one does any work.
    /// </summary>
    public async Task<bool> EnsureLoadedAsync()
    {
        if (_loaded) return IsAuthenticated;
        _loaded = true;

        var state = await authentication.GetAuthenticationStateAsync();
        var principal = state.User;

        if (principal.Identity?.IsAuthenticated != true) return false;

        var id = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(id, out var userId)) return false;

        var user = await users.GetAsync(userId);

        // The cookie can outlive a demotion or a deactivation, so the role is re-checked
        // against the database rather than trusted from the claim.
        if (user is null || user.Role < (int)UserRole.Admin) return false;

        _user = user;
        return true;
    }
}

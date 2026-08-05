using System.Security.Claims;
using Telepati.Domain;
using Telepati.Infrastructure.Services;

namespace Telepati.Server;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                    ?? principal.FindFirstValue("sub")
                    ?? principal.FindFirstValue("nameid");

        return Guid.TryParse(value, out var id)
            ? id
            : throw new UnauthorizedAccessException("Token tidak memuat identitas pengguna.");
    }

    public static Guid? GetSessionId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(TelepatiClaims.SessionId), out var id) ? id : null;

    public static UserRole GetRole(this ClaimsPrincipal principal) =>
        Enum.TryParse<UserRole>(principal.FindFirstValue(ClaimTypes.Role), out var role) ? role : UserRole.User;

    public static bool IsAdmin(this ClaimsPrincipal principal) => principal.GetRole() >= UserRole.Admin;
}

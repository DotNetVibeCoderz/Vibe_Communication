using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OtpNet;
using QRCoder;
using Telepati.Domain;
using Telepati.Infrastructure.Data;
using Telepati.Shared.Configuration;
using Telepati.Shared.Contracts;

namespace Telepati.Infrastructure.Services;

public interface IAuthService
{
    Task<LoginResponse> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default);
    Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default);
    Task<LoginResponse> RefreshAsync(string refreshToken, CancellationToken ct = default);
    Task<ApiResult> LogoutAsync(Guid sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<SessionDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default);
    Task<ApiResult> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
    Task<ApiResult> RevokeAllOtherSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default);

    Task<ApiResult<TwoFactorSetupDto>> BeginTwoFactorSetupAsync(Guid userId, CancellationToken ct = default);
    Task<ApiResult> ConfirmTwoFactorAsync(Guid userId, string code, CancellationToken ct = default);
    Task<ApiResult> DisableTwoFactorAsync(Guid userId, string code, CancellationToken ct = default);
}

public record TwoFactorSetupDto(string Secret, string OtpAuthUri, string QrCodePngBase64);

public class AuthService(
    TelepatiDbContext db,
    ISettingsService settings,
    IActivityLogger activity) : IAuthService
{
    public async Task<LoginResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);

        var username = request.Username.Trim().ToLowerInvariant();
        var email = request.Email.Trim().ToLowerInvariant();

        if (await db.Users.AnyAsync(u => u.Username == username, ct))
            return Failed("Username sudah dipakai.");
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
            return Failed("Email sudah terdaftar.");
        if (request.Password.Length < 8)
            return Failed("Password minimal 8 karakter.");

        var user = new User
        {
            Username = username,
            Email = email,
            PhoneNumber = request.PhoneNumber,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? request.Username : request.DisplayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Role = UserRole.User,
            IsActive = true
        };

        db.Users.Add(user);

        // Every account gets its own notes chat so "saved messages" needs no special-casing later.
        var savedMessages = new Chat { Type = ChatType.SavedMessages, Title = "Pesan Tersimpan", CreatedById = user.Id, MemberCount = 1 };
        db.Chats.Add(savedMessages);
        db.ChatMembers.Add(new ChatMember { ChatId = savedMessages.Id, UserId = user.Id, Role = ChatMemberRole.Owner });

        await db.SaveChangesAsync(ct);

        var session = await CreateSessionAsync(user, "Registration", "web", null, null, null, options, ct);
        return BuildResponse(user, session, options);
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest request, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        var identifier = request.UsernameOrEmail.Trim().ToLowerInvariant();

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.Username == identifier || u.Email == identifier || u.PhoneNumber == request.UsernameOrEmail, ct);

        if (user is null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
        {
            await activity.LogAsync(ActivityKind.LoginFailed, user?.Id, $"Login gagal untuk '{request.UsernameOrEmail}'.", ipAddress, userAgent, ct: ct);
            return Failed("Username atau password salah.");
        }

        if (!user.IsActive) return Failed("Akun dinonaktifkan. Hubungi admin.");

        // 2FA can be switched off globally; a user's own flag only matters while the feature is on.
        var twoFactorActive = options.Security.EnableTwoFactor && user.TwoFactorEnabled;
        if (twoFactorActive)
        {
            if (string.IsNullOrWhiteSpace(request.TwoFactorCode))
            {
                return new LoginResponse(false, null, null, null, null, true, null);
            }
            if (!VerifyTotp(user.TwoFactorSecret, request.TwoFactorCode))
            {
                return Failed("Kode 2FA tidak valid.");
            }
        }

        await EnforceSessionLimitAsync(user.Id, options.Security.MaxActiveSessions, ct);

        var session = await CreateSessionAsync(user, request.DeviceName, request.DeviceType, request.Platform, ipAddress, userAgent, options, ct);

        user.Presence = UserPresence.Online;
        user.LastSeenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await activity.LogAsync(ActivityKind.Login, user.Id, $"Login dari {request.DeviceName} ({request.DeviceType}).", ipAddress, userAgent, ct: ct);
        return BuildResponse(user, session, options);
    }

    public async Task<LoginResponse> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        var hash = Hash(refreshToken);

        var session = await db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.RefreshTokenHash == hash && !s.IsRevoked, ct);

        if (session is null || session.ExpiresAt < DateTimeOffset.UtcNow || session.User is null)
        {
            return Failed("Sesi tidak valid atau sudah kedaluwarsa.");
        }

        // Rotate on every refresh so a stolen token is usable at most once.
        var newToken = GenerateToken();
        session.RefreshTokenHash = Hash(newToken);
        session.ExpiresAt = DateTimeOffset.UtcNow.AddDays(options.Security.RefreshTokenDays);
        session.LastActiveAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var access = CreateAccessToken(session.User, session.Id, options.Security);
        return new LoginResponse(true, access.Token, newToken, access.ExpiresAt, session.User.ToDto(), false, null);
    }

    public async Task<ApiResult> LogoutAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null) return ApiResult.Fail("Sesi tidak ditemukan.");

        session.IsRevoked = true;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(ActivityKind.Logout, session.UserId, "Logout.", ct: ct);
        return ApiResult.Ok();
    }

    public async Task<IReadOnlyList<SessionDto>> GetSessionsAsync(Guid userId, Guid? currentSessionId, CancellationToken ct = default) =>
        await db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .OrderByDescending(s => s.LastActiveAt)
            .Select(s => new SessionDto(s.Id, s.DeviceName, s.DeviceType, s.Platform, s.IpAddress, s.LastActiveAt, s.CreatedAt, s.Id == currentSessionId))
            .ToListAsync(ct);

    public async Task<ApiResult> RevokeSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (session is null) return ApiResult.Fail("Sesi tidak ditemukan.");

        session.IsRevoked = true;
        await db.SaveChangesAsync(ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> RevokeAllOtherSessionsAsync(Guid userId, Guid currentSessionId, CancellationToken ct = default)
    {
        await db.UserSessions
            .Where(s => s.UserId == userId && s.Id != currentSessionId && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRevoked, true), ct);
        return ApiResult.Ok();
    }

    // -- two-factor -----------------------------------------------------------

    public async Task<ApiResult<TwoFactorSetupDto>> BeginTwoFactorSetupAsync(Guid userId, CancellationToken ct = default)
    {
        var options = await settings.GetOptionsAsync(ct);
        if (!options.Security.EnableTwoFactor)
            return ApiResult<TwoFactorSetupDto>.Fail("Fitur 2FA dimatikan oleh admin.");

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult<TwoFactorSetupDto>.Fail("User tidak ditemukan.");

        var secretBytes = RandomNumberGenerator.GetBytes(20);
        var secret = Base32Encoding.ToString(secretBytes);

        // Stored but not yet enabled — the user must prove they can generate a code first.
        user.TwoFactorSecret = secret;
        await db.SaveChangesAsync(ct);

        var issuer = Uri.EscapeDataString(options.Branding.AppName);
        var account = Uri.EscapeDataString(user.Email);
        var uri = $"otpauth://totp/{issuer}:{account}?secret={secret}&issuer={issuer}&digits=6&period=30";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);

        return ApiResult<TwoFactorSetupDto>.Ok(new TwoFactorSetupDto(secret, uri, System.Convert.ToBase64String(png)));
    }

    public async Task<ApiResult> ConfirmTwoFactorAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");
        if (!VerifyTotp(user.TwoFactorSecret, code)) return ApiResult.Fail("Kode tidak valid.");

        user.TwoFactorEnabled = true;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(ActivityKind.SettingChanged, userId, "2FA diaktifkan.", ct: ct);
        return ApiResult.Ok();
    }

    public async Task<ApiResult> DisableTwoFactorAsync(Guid userId, string code, CancellationToken ct = default)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return ApiResult.Fail("User tidak ditemukan.");
        if (!VerifyTotp(user.TwoFactorSecret, code)) return ApiResult.Fail("Kode tidak valid.");

        user.TwoFactorEnabled = false;
        user.TwoFactorSecret = null;
        await db.SaveChangesAsync(ct);
        await activity.LogAsync(ActivityKind.SettingChanged, userId, "2FA dimatikan.", ct: ct);
        return ApiResult.Ok();
    }

    // -- helpers --------------------------------------------------------------

    private static bool VerifyTotp(string? secret, string code)
    {
        if (string.IsNullOrWhiteSpace(secret)) return false;

        var totp = new Totp(Base32Encoding.ToBytes(secret));
        // A one-step window each way absorbs clock drift between phone and server.
        return totp.VerifyTotp(code.Trim(), out _, new VerificationWindow(previous: 1, future: 1));
    }

    private async Task EnforceSessionLimitAsync(Guid userId, int maxSessions, CancellationToken ct)
    {
        var active = await db.UserSessions
            .Where(s => s.UserId == userId && !s.IsRevoked)
            .OrderByDescending(s => s.LastActiveAt)
            .ToListAsync(ct);

        foreach (var stale in active.Skip(Math.Max(0, maxSessions - 1)))
        {
            stale.IsRevoked = true;
        }
    }

    private async Task<IssuedSession> CreateSessionAsync(
        User user, string deviceName, string deviceType, string? platform,
        string? ip, string? userAgent, TelepatiOptions options, CancellationToken ct)
    {
        var refreshToken = GenerateToken();
        var session = new UserSession
        {
            UserId = user.Id,
            DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Perangkat" : deviceName,
            DeviceType = deviceType,
            Platform = platform,
            IpAddress = ip,
            UserAgent = userAgent,
            RefreshTokenHash = Hash(refreshToken),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(options.Security.RefreshTokenDays)
        };

        db.UserSessions.Add(session);
        await db.SaveChangesAsync(ct);

        // Only the hash is persisted; the plaintext is returned once and never stored.
        return new IssuedSession(session, refreshToken);
    }

    private LoginResponse BuildResponse(User user, IssuedSession issued, TelepatiOptions options)
    {
        var access = CreateAccessToken(user, issued.Session.Id, options.Security);
        return new LoginResponse(true, access.Token, issued.RefreshToken, access.ExpiresAt, user.ToDto(), false, null);
    }

    private static (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid sessionId, SecurityOptions security)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(security.AccessTokenMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(security.JwtSecret.PadRight(32, '!')));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = security.JwtIssuer,
            Audience = security.JwtAudience,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                [ClaimTypes.NameIdentifier] = user.Id.ToString(),
                [ClaimTypes.Name] = user.Username,
                [ClaimTypes.Role] = user.Role.ToString(),
                [TelepatiClaims.SessionId] = sessionId.ToString(),
                [TelepatiClaims.DisplayName] = user.DisplayName
            }
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    private static string GenerateToken() => System.Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static string Hash(string value) =>
        System.Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static LoginResponse Failed(string error) => new(false, null, null, null, null, false, error);
}

public static class TelepatiClaims
{
    public const string SessionId = "sid";
    public const string DisplayName = "name";
}

/// <summary>A freshly created session together with the one-time plaintext refresh token.</summary>
internal readonly record struct IssuedSession(UserSession Session, string RefreshToken);

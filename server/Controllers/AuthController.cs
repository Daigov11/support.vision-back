using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Authentication;
using VisionSupport.Server.Data;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using VisionSupport.Server.Services;

namespace VisionSupport.Server.Controllers;

/// <summary>
/// Login por email/contraseña para técnicos y administradores del panel web. No hay registro
/// público: las cuentas las crea un Admin ya autenticado vía POST /api/users, o se crea la
/// primera (Admin) al arrancar el servidor (ver Services/AdminBootstrapper.cs).
/// Esto NO afecta a Android: los dispositivos siguen autenticándose por deviceCode + pairingKey.
/// </summary>
[ApiController]
[Route("api/auth")]
[Authorize]
public class AuthController : ControllerBase
{
    private const string InvalidCredentialsMessage = "Email o contraseña incorrectos.";
    private const string InvalidRefreshTokenMessage = "Refresh token inválido o expirado.";

    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;
    private readonly TokenService _tokens;

    public AuthController(AppDbContext db, IPasswordHasher<User> passwordHasher, TokenService tokens)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _tokens = tokens;
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthTokensResponse>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email);
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

        // Mismo mensaje exista o no la cuenta, esté activa o no: no revelar nada sobre el email.
        if (user is null || !user.IsActive)
        {
            return Unauthorized(new { message = InvalidCredentialsMessage });
        }

        var verification = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return Unauthorized(new { message = InvalidCredentialsMessage });
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);
        }

        var now = DateTimeOffset.UtcNow;
        user.LastLoginAt = now;
        user.UpdatedAt = now;

        var response = IssueTokens(user);
        await _db.SaveChangesAsync(ct);

        return Ok(response);
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthTokensResponse>> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(request.RefreshToken);
        var existing = await _db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (existing is null)
        {
            return Unauthorized(new { message = InvalidRefreshTokenMessage });
        }

        if (existing.RevokedAt is not null)
        {
            // Un token ya rotado no debería volver a presentarse: indicio de robo/reuso.
            // Revocamos toda la familia del usuario para forzar re-login en todas partes.
            await RevokeAllRefreshTokensAsync(existing.UserId, ct);
            return Unauthorized(new { message = InvalidRefreshTokenMessage });
        }

        if (existing.ExpiresAt <= DateTimeOffset.UtcNow || existing.User is null || !existing.User.IsActive)
        {
            return Unauthorized(new { message = InvalidRefreshTokenMessage });
        }

        existing.RevokedAt = DateTimeOffset.UtcNow;

        var response = IssueTokens(existing.User!);
        await _db.SaveChangesAsync(ct);

        return Ok(response);
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request, CancellationToken ct)
    {
        var hash = TokenService.HashRefreshToken(request.RefreshToken);
        var existing = await _db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, ct);

        // Idempotente y sin filtrar si el token existía: siempre 204.
        if (existing is not null && existing.RevokedAt is null)
        {
            existing.RevokedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    [HttpGet("me")]
    public async Task<ActionResult<MeResponse>> Me(CancellationToken ct)
    {
        var userId = User.GetUserId();
        var user = await _db.Users.FindAsync([userId], ct);
        if (user is null || !user.IsActive)
        {
            return Unauthorized();
        }

        return Ok(new MeResponse(user.Id, user.Email, user.DisplayName, user.Role.ToString(), user.LastLoginAt));
    }

    private AuthTokensResponse IssueTokens(User user)
    {
        var (accessToken, accessExpiresAt) = _tokens.CreateAccessToken(user);
        var refreshPlainText = _tokens.GenerateRefreshTokenPlainText();
        var refreshExpiresAt = DateTimeOffset.UtcNow.Add(_tokens.RefreshTokenLifetime);

        _db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.HashRefreshToken(refreshPlainText),
            ExpiresAt = refreshExpiresAt,
        });

        return new AuthTokensResponse(accessToken, accessExpiresAt, refreshPlainText, refreshExpiresAt, UserDto.FromModel(user));
    }

    private async Task RevokeAllRefreshTokensAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

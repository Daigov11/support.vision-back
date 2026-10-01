using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using VisionSupport.Server.Models;
using VisionSupport.Server.Options;

namespace VisionSupport.Server.Services;

/// <summary>
/// Emite access tokens JWT (cortos) y refresh tokens opacos (largos, aleatorios, guardados
/// solo como hash). Nunca registra el valor de un token en logs.
/// </summary>
public class TokenService
{
    private readonly JwtOptions _options;

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
    }

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Role, user.Role.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }

    /// <summary>Valor aleatorio de 512 bits; el cliente lo trata como opaco.</summary>
    public string GenerateRefreshTokenPlainText() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

    /// <summary>SHA-256 en hex: lo único que se persiste en RefreshToken.TokenHash.</summary>
    public static string HashRefreshToken(string plainTextToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainTextToken)));
}

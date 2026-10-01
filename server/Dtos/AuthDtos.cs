namespace VisionSupport.Server.Dtos;

public record LoginRequest(string Email, string Password);

public record RefreshRequest(string RefreshToken);

public record LogoutRequest(string RefreshToken);

/// <summary>
/// Respuesta de login/refresh. El refresh token viaja en el body (no como cookie) para este
/// MVP — ver web/README.md para la decisión de dónde lo guarda el panel y sus contrapartidas.
/// </summary>
public record AuthTokensResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);

public record MeResponse(Guid Id, string Email, string DisplayName, string Role, DateTimeOffset? LastLoginAt);

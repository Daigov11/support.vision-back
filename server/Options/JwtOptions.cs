namespace VisionSupport.Server.Options;

/// <summary>
/// Configuración de JWT para la autenticación de técnicos/administradores (panel web). NO se
/// usa para Android: los dispositivos se autentican por deviceCode + pairingKey (ver
/// RemoteHub.AnnounceDevicePresence), un mecanismo completamente separado.
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Clave simétrica para firmar/validar access tokens (HS256). Mínimo 32 bytes.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "vision-support";
    public string Audience { get; set; } = "vision-support-clients";

    /// <summary>Corta a propósito: si un access token se filtra, deja de servir pronto.</summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>Vida del refresh token; cada uso lo rota (ver AuthController.Refresh).</summary>
    public int RefreshTokenDays { get; set; } = 14;
}

namespace VisionSupport.Server.Models;

/// <summary>
/// Un refresh token emitido a un usuario. Solo se guarda el HASH (SHA-256) del valor real —
/// el valor en texto plano solo existe en la respuesta de login/refresh y en el cliente.
/// Rotativo: cada uso de /api/auth/refresh revoca este registro y crea uno nuevo (ver
/// AuthController); si un token ya revocado se vuelve a presentar, se trata como posible robo
/// y se revoca toda la familia de tokens del usuario.
/// </summary>
public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

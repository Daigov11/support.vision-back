namespace VisionSupport.Server.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Normalizado (trim + minúsculas) antes de guardarse; ver controladores de Auth/Users.</summary>
    public string Email { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Support;

    /// <summary>Hash de <see cref="Microsoft.AspNetCore.Identity.PasswordHasher{TUser}"/>. Nunca texto plano.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

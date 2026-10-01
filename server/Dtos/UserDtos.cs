using VisionSupport.Server.Models;

namespace VisionSupport.Server.Dtos;

/// <summary>Nunca incluye PasswordHash.</summary>
public record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastLoginAt)
{
    public static UserDto FromModel(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.Role.ToString(),
        user.IsActive,
        user.CreatedAt,
        user.UpdatedAt,
        user.LastLoginAt);
}

public record CreateUserRequest(string Email, string DisplayName, string Role, string Password);

/// <summary>Todos los campos opcionales: solo se actualiza lo que venga no nulo.</summary>
public record UpdateUserRequest(string? DisplayName, string? Role, bool? IsActive, string? NewPassword);

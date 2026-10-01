using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;

namespace VisionSupport.Server.Controllers;

/// <summary>
/// Gestión de cuentas. Solo Admin: no hay registro público, un Support no puede crear ni
/// modificar usuarios (ni a sí mismo).
/// </summary>
[ApiController]
[Route("api/users")]
[Authorize(Roles = nameof(UserRole.Admin))]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IPasswordHasher<User> _passwordHasher;

    public UsersController(AppDbContext db, IPasswordHasher<User> passwordHasher)
    {
        _db = db;
        _passwordHasher = passwordHasher;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers(CancellationToken ct)
    {
        var users = await _db.Users
            .OrderBy(u => u.DisplayName)
            .Select(u => UserDto.FromModel(u))
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost]
    public async Task<ActionResult<UserDto>> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.DisplayName) ||
            string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest("Email, DisplayName y Password son obligatorios.");
        }

        if (request.Password.Length < 8)
        {
            return BadRequest("La contraseña debe tener al menos 8 caracteres.");
        }

        if (!TryParseRole(request.Role, out var role, out var roleError))
        {
            return BadRequest(roleError);
        }

        var email = NormalizeEmail(request.Email);
        if (await _db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Conflict("Ya existe un usuario con ese email.");
        }

        var user = new User
        {
            Email = email,
            DisplayName = request.DisplayName.Trim(),
            Role = role,
            IsActive = true,
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetUsers), new { id = user.Id }, UserDto.FromModel(user));
    }

    /// <summary>Actualiza nombre, rol, activo y/o contraseña — solo los campos enviados (no nulos).</summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<UserDto>> UpdateUser(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return NotFound();
        }

        if (request.DisplayName is not null)
        {
            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return BadRequest("DisplayName no puede quedar vacío.");
            }
            user.DisplayName = request.DisplayName.Trim();
        }

        if (request.Role is not null)
        {
            if (!TryParseRole(request.Role, out var role, out var roleError))
            {
                return BadRequest(roleError);
            }
            user.Role = role;
        }

        var now = DateTimeOffset.UtcNow;

        if (request.IsActive is { } isActive)
        {
            user.IsActive = isActive;
            if (!isActive)
            {
                // Que no pueda seguir renovando su sesión tras ser desactivado.
                await RevokeAllRefreshTokensAsync(user.Id, now, ct);
            }
        }

        if (!string.IsNullOrEmpty(request.NewPassword))
        {
            if (request.NewPassword.Length < 8)
            {
                return BadRequest("La contraseña debe tener al menos 8 caracteres.");
            }
            user.PasswordHash = _passwordHasher.HashPassword(user, request.NewPassword);
            // Restablecer la contraseña también cierra las sesiones ya abiertas de ese usuario.
            await RevokeAllRefreshTokensAsync(user.Id, now, ct);
        }

        user.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(UserDto.FromModel(user));
    }

    private async Task RevokeAllRefreshTokensAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        await _db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static bool TryParseRole(string roleText, out UserRole role, out string? error)
    {
        if (Enum.TryParse(roleText, ignoreCase: true, out role) && Enum.IsDefined(role))
        {
            error = null;
            return true;
        }
        error = $"Rol inválido. Valores permitidos: {string.Join(", ", Enum.GetNames<UserRole>())}.";
        return false;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

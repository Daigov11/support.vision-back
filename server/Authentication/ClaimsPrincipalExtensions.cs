using System.Security.Claims;

namespace VisionSupport.Server.Authentication;

/// <summary>
/// Helpers para leer la identidad del usuario autenticado directamente de los claims del JWT
/// (ver Services/TokenService.cs), sin necesitar una consulta a la base de datos: el token ya
/// es la fuente de verdad mientras es válido (máx. <c>Jwt:AccessTokenMinutes</c>).
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(value) || !Guid.TryParse(value, out var id))
        {
            throw new InvalidOperationException("El token no contiene un identificador de usuario válido.");
        }
        return id;
    }

    public static string GetDisplayName(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

    public static string GetEmail(this ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.Email) ?? string.Empty;
}

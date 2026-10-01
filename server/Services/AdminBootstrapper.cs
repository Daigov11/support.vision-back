using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Models;

namespace VisionSupport.Server.Services;

/// <summary>
/// Crea el primer Admin al arrancar, SOLO si la tabla Users está completamente vacía (no hay
/// registro público: a partir del segundo usuario, todos los altas pasan por
/// POST /api/users, solo accesible a un Admin ya autenticado).
/// </summary>
public static class AdminBootstrapper
{
    public const string EmailEnvVar = "INITIAL_ADMIN_EMAIL";
    public const string PasswordEnvVar = "INITIAL_ADMIN_PASSWORD";

    public static async Task RunAsync(
        AppDbContext db,
        IPasswordHasher<User> passwordHasher,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return; // Ya hay al menos un usuario: el bootstrap solo aplica una vez, en frío.
        }

        var email = configuration[EmailEnvVar];
        var password = configuration[PasswordEnvVar];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            var message = $"No hay usuarios y faltan {EmailEnvVar}/{PasswordEnvVar}: no se puede " +
                "crear el primer Admin. No existe registro público, así que sin esto nadie podrá " +
                "iniciar sesión.";

            if (environment.IsProduction())
            {
                // Falla fuerte y claro en producción: preferible a arrancar sin forma de entrar.
                throw new InvalidOperationException(message);
            }

            logger.LogWarning(
                "{Message} Define ambas variables y reinicia (ver docs/local-development.md).",
                message);
            return;
        }

        if (password.Length < 8)
        {
            throw new InvalidOperationException(
                $"{PasswordEnvVar} debe tener al menos 8 caracteres.");
        }

        var normalizedEmail = email.Trim().ToLowerInvariant();
        var admin = new User
        {
            Email = normalizedEmail,
            DisplayName = "Administrador",
            Role = UserRole.Admin,
            IsActive = true,
        };
        admin.PasswordHash = passwordHasher.HashPassword(admin, password);

        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        // Nunca registrar la contraseña; el email tampoco es secreto pero basta con confirmar
        // que se creó, sin repetirlo, para no invitar a copiar/pegar credenciales en logs.
        logger.LogInformation("Admin inicial creado ({Email}).", normalizedEmail);
    }
}

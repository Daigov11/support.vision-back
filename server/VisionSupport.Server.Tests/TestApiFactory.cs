using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using VisionSupport.Server.Data;
using VisionSupport.Server.Models;
using VisionSupport.Server.Options;
using VisionSupport.Server.RateLimiting;
using VisionSupport.Server.Services;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Levanta un servidor HTTP real de prueba (ASP.NET Core TestServer) con exactamente el mismo
/// pipeline de autenticación/autorización que Program.cs (JwtBearer, [Authorize(Roles=...)],
/// controladores reales), pero con SQLite en memoria en vez de Postgres y sin la migración de
/// Program.cs (que es específica de Npgsql). Necesario porque instanciar un controlador
/// directamente (como en RemoteHubTests) NO evalúa los atributos [Authorize]/[Authorize(Roles=)]
/// — eso solo lo hace el middleware de autorización real, de ahí este arnés de integración.
/// </summary>
public sealed class TestApiFactory : IAsyncLifetime
{
    public const string TestSigningKey = "integration-test-signing-key-at-least-32-bytes-long";
    public const string TestIssuer = "vision-support-tests";
    public const string TestAudience = "vision-support-tests-clients";

    /// <summary>
    /// Bajo a propósito para que la prueba de rate limit no necesite cientos de peticiones, pero
    /// por encima de la ráfaga concurrente de DevicesEnrollApiTests.Enroll_ConcurrentRequestsWithSameCode
    /// para no confundir "se agotó la ventana" con "perdió la carrera por el código".
    /// </summary>
    public const int TestPairingEnrollRateLimit = 10;

    private SqliteConnection? _connection;
    private IHost? _host;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var hostBuilder = new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();

                webHost.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Jwt:SigningKey"] = TestSigningKey,
                        ["Jwt:Issuer"] = TestIssuer,
                        ["Jwt:Audience"] = TestAudience,
                        ["Jwt:AccessTokenMinutes"] = "15",
                        ["Jwt:RefreshTokenDays"] = "14",
                    });
                });

                webHost.ConfigureServices(services =>
                {
                    services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

                    services.Configure<JwtOptions>(o =>
                    {
                        o.SigningKey = TestSigningKey;
                        o.Issuer = TestIssuer;
                        o.Audience = TestAudience;
                        o.AccessTokenMinutes = 15;
                        o.RefreshTokenDays = 14;
                    });
                    services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
                    services.AddSingleton<TokenService>();

                    services.AddControllers()
                        .AddApplicationPart(typeof(VisionSupport.Server.Controllers.AuthController).Assembly);

                    services
                        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                        .AddJwtBearer(options =>
                        {
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidateIssuer = true,
                                ValidIssuer = TestIssuer,
                                ValidateAudience = true,
                                ValidAudience = TestAudience,
                                ValidateIssuerSigningKey = true,
                                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey)),
                                ValidateLifetime = true,
                                ClockSkew = TimeSpan.FromSeconds(30),
                            };
                        });
                    services.AddAuthorization();

                    // Misma política que Program.cs, con un límite bajo para que las pruebas de
                    // "rate limit" no necesiten disparar cientos de peticiones.
                    services.AddRateLimiter(options =>
                    {
                        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                        options.AddPolicy(RateLimitPolicyNames.PairingEnroll, httpContext =>
                            RateLimitPartition.GetFixedWindowLimiter(
                                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                                factory: _ => new FixedWindowRateLimiterOptions
                                {
                                    PermitLimit = TestPairingEnrollRateLimit,
                                    Window = TimeSpan.FromMinutes(1),
                                    QueueLimit = 0,
                                }));
                    });
                });

                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseRateLimiter();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        _host = await hostBuilder.StartAsync();

        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.EnsureCreatedAsync();
        }

        Client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }
    }

    /// <summary>Inserta un usuario directamente en la base (evita depender de /api/users en tests de otras rutas).</summary>
    public async Task<User> SeedUserAsync(string email, string password, UserRole role = UserRole.Support, bool isActive = true)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        var user = new User
        {
            Email = email.Trim().ToLowerInvariant(),
            DisplayName = "Usuario de prueba",
            Role = role,
            IsActive = isActive,
        };
        user.PasswordHash = hasher.HashPassword(user, password);

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Inserta un PairingCode directamente en la base, con estado controlable (expirado, usado,
    /// revocado) sin depender de POST /api/pairing-codes ni de esperar 10 minutos reales.
    /// Devuelve el código en texto plano junto con la entidad, para poder llamar
    /// POST /api/devices/enroll en los tests.
    /// </summary>
    public async Task<(PairingCode Entity, string PlainTextCode)> SeedPairingCodeAsync(
        Guid createdByUserId,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? usedAt = null,
        DateTimeOffset? revokedAt = null,
        string? label = null)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var plainTextCode = PairingCodeService.GenerateCode();
        var entity = new PairingCode
        {
            CodeHash = PairingCodeService.Hash(plainTextCode),
            CreatedByUserId = createdByUserId,
            Label = label,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.Add(PairingCodeService.Lifetime),
            UsedAt = usedAt,
            RevokedAt = revokedAt,
        };
        db.PairingCodes.Add(entity);
        await db.SaveChangesAsync();
        return (entity, plainTextCode);
    }

    /// <summary>Inserta un Device directamente en la base (sin pasar por /api/devices/enroll).</summary>
    public async Task<Device> SeedDeviceAsync(string deviceCode, string name = "Dispositivo de prueba")
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var device = new Device
        {
            DeviceCode = deviceCode,
            Name = name,
            PairingKey = Guid.NewGuid().ToString("N"),
            Status = DeviceStatus.Offline,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    /// <summary>Inserta el DeviceHealthSnapshot de un dispositivo directamente (sin pasar por el Hub).</summary>
    public async Task<DeviceHealthSnapshot> SeedDeviceHealthSnapshotAsync(Guid deviceId, int batteryPercent = 80)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var snapshot = new DeviceHealthSnapshot
        {
            DeviceId = deviceId,
            BatteryPercent = batteryPercent,
            IsCharging = false,
            NetworkConnected = true,
            NetworkType = "Wifi",
            WifiSignalLevel = 3,
            StorageTotalBytes = 128_000_000_000,
            StorageUsedBytes = 64_000_000_000,
            StorageAvailableBytes = 64_000_000_000,
            Manufacturer = "Google",
            Model = "Pixel de prueba",
            AndroidVersion = "Android 15 (API 35)",
            AppVersion = "0.1.0",
        };
        db.DeviceHealthSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        return snapshot;
    }

    /// <summary>Inserta un DeviceDiagnosticEvent directamente, con CreatedAt controlable para probar el orden.</summary>
    public async Task<DeviceDiagnosticEvent> SeedDiagnosticEventAsync(
        Guid deviceId,
        DeviceDiagnosticEventType type,
        string message,
        DateTimeOffset? createdAt = null,
        string? sourcePackage = null,
        string? sourceAppVersion = null,
        string? code = null,
        DateTimeOffset? occurredAt = null)
    {
        using var scope = _host!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var evt = new DeviceDiagnosticEvent
        {
            DeviceId = deviceId,
            Type = type,
            Message = message,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            SourcePackage = sourcePackage,
            SourceAppVersion = sourceAppVersion,
            Code = code,
            OccurredAt = occurredAt,
        };
        db.DeviceDiagnosticEvents.Add(evt);
        await db.SaveChangesAsync();
        return evt;
    }
}

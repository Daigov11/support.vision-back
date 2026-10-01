using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using VisionSupport.Server.Data;
using VisionSupport.Server.Hubs;
using VisionSupport.Server.Models;
using VisionSupport.Server.Options;
using VisionSupport.Server.RateLimiting;
using VisionSupport.Server.Services;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Falta la cadena de conexión 'ConnectionStrings:Default'. Defínela en appsettings.Development.json o " +
        "mediante la variable de entorno ConnectionStrings__Default (ver .env.example).");

var corsAllowedOrigin = builder.Configuration["Cors:AllowedOrigin"] ?? "http://localhost:5173";

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// Credenciales TURN efímeras para Coturn (ver Services/TurnCredentialService.cs y
// infra/.env.example: TURN_SHARED_SECRET, TURN_REALM, TURN_PUBLIC_HOST, TURN_PORT).
builder.Services.Configure<TurnOptions>(builder.Configuration.GetSection(TurnOptions.SectionName));
builder.Services.AddSingleton<TurnCredentialService>();

// Autenticación de técnicos/administradores por email+contraseña con JWT (ver
// Options/JwtOptions.cs, Services/TokenService.cs). Totalmente separada de Android, que se
// autentica por deviceCode+pairingKey (ver RemoteHub.AnnounceDevicePresence) y nunca usa JWT.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddSingleton<TokenService>();

// Retención de eventos de diagnóstico (ver Options/DiagnosticsOptions.cs y RemoteHub).
builder.Services.Configure<DiagnosticsOptions>(builder.Configuration.GetSection(DiagnosticsOptions.SectionName));

builder.Services.AddControllers();
builder.Services.AddSignalR();

// Limita POST /api/devices/enroll por IP: dificulta adivinar códigos de emparejamiento por
// fuerza bruta (10 min de vida, alta entropía, pero igual conviene no dejar intentos ilimitados).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicyNames.PairingEnroll, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Access token de /api/auth/login, sin el prefijo \"Bearer \" (Swagger lo añade solo).",
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            },
            Array.Empty<string>()
        },
    });
});

var jwtSection = builder.Configuration.GetSection(JwtOptions.SectionName);
var jwtSigningKey = jwtSection["SigningKey"]
    ?? throw new InvalidOperationException(
        "Falta Jwt:SigningKey (variable de entorno JWT_SIGNING_KEY). Ver docs/local-development.md.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSection["Issuer"] ?? "vision-support",
            ValidateAudience = true,
            ValidAudience = jwtSection["Audience"] ?? "vision-support-clients",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
        options.Events = new JwtBearerEvents
        {
            // El navegador no puede mandar cabeceras propias en el handshake de WebSocket: el
            // panel manda el access token como "?access_token=" (ver web/src/hooks/useRemoteHub.ts).
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs/remote"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

const string CorsPolicyName = "WebPanel";
builder.Services.AddCors(options =>
{
    options.AddPolicy(CorsPolicyName, policy =>
    {
        policy.WithOrigins(corsAllowedOrigin)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Migraciones y bootstrap del primer Admin corren en TODO entorno (no solo Development): sin
// esto, AdminBootstrapper nunca llegaría a ejecutarse en producción y su comprobación de
// "fallar claramente si faltan INITIAL_ADMIN_EMAIL/PASSWORD" jamás se activaría.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    await AdminBootstrapper.RunAsync(
        db,
        scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>(),
        app.Configuration,
        app.Environment,
        app.Logger);
}

app.UseCors(CorsPolicyName);

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<RemoteHub>("/hubs/remote");

app.Run();

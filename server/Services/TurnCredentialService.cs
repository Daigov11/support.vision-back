using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Options;

namespace VisionSupport.Server.Services;

/// <summary>
/// Genera credenciales TURN efímeras usando el mecanismo "TURN REST API" que entiende Coturn
/// con <c>use-auth-secret</c> (RFC de facto usado por coturn, Twilio, Xirsys, etc., NO un RFC
/// formal de IETF): <c>username = "{expiración-unix}:{etiqueta}"</c>,
/// <c>credential = Base64(HMAC-SHA1(secretoCompartido, username))</c>. Coturn valida el HMAC y
/// el timestamp de expiración por su cuenta; el backend nunca contacta a Coturn directamente.
///
/// IMPORTANTE: ni <see cref="TurnOptions.SharedSecret"/> ni la credencial calculada deben
/// registrarse en logs — por eso esta clase no usa <c>ILogger</c> en absoluto.
/// </summary>
public class TurnCredentialService
{
    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(5);

    private readonly TurnOptions _options;

    public TurnCredentialService(IOptions<TurnOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>Falso si falta SharedSecret o PublicHost: no se pueden emitir credenciales útiles.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.SharedSecret) && !string.IsNullOrWhiteSpace(_options.PublicHost);

    /// <param name="label">
    /// Identificador no sensible incluido en el username para trazabilidad en los logs de
    /// Coturn (aquí usamos el id de la sesión). Nunca debe ser un secreto.
    /// </param>
    public TurnCredentialsDto GenerateCredentials(string label)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "TURN no está configurado en este servidor (faltan Turn:SharedSecret o Turn:PublicHost).");
        }

        var ttl = _options.CredentialTtlSeconds > 0 ? TimeSpan.FromSeconds(_options.CredentialTtlSeconds) : DefaultTtl;
        if (ttl > MaxTtl)
        {
            ttl = MaxTtl;
        }

        var expiresAt = DateTimeOffset.UtcNow.Add(ttl);
        var username = $"{expiresAt.ToUnixTimeSeconds()}:{label}";
        var credential = ComputeCredential(username);

        var urls = new List<string>
        {
            $"stun:{_options.PublicHost}:{_options.Port}",
            $"turn:{_options.PublicHost}:{_options.Port}?transport=udp",
            $"turn:{_options.PublicHost}:{_options.Port}?transport=tcp",
        };
        if (_options.TlsPort is { } tlsPort)
        {
            urls.Add($"turns:{_options.PublicHost}:{tlsPort}?transport=tcp");
        }

        var iceServer = new IceServerDto(urls.ToArray(), username, credential);
        return new TurnCredentialsDto([iceServer], expiresAt);
    }

    private string ComputeCredential(string username)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(_options.SharedSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        return Convert.ToBase64String(hash);
    }
}

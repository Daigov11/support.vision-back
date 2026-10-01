namespace VisionSupport.Server.Options;

/// <summary>
/// Configuración para generar credenciales TURN efímeras compatibles con el mecanismo
/// "TURN REST API" (<c>use-auth-secret</c>) de Coturn. Se puebla desde la sección "Turn" de
/// configuración, normalmente vía variables de entorno Turn__* en Docker Compose
/// (ver infra/.env.example: TURN_SHARED_SECRET, TURN_REALM, TURN_PUBLIC_HOST, TURN_PORT).
/// </summary>
public class TurnOptions
{
    public const string SectionName = "Turn";

    /// <summary>
    /// Secreto compartido con el servidor Coturn (--static-auth-secret). Nunca debe registrarse
    /// en logs ni exponerse en ninguna respuesta; solo se usa para calcular el HMAC de cada
    /// credencial efímera.
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;

    public string Realm { get; set; } = "visionsupport.local";

    /// <summary>Host/IP que los clientes usarán en las URLs turn:/turns: (LAN local o IP pública de VPS).</summary>
    public string PublicHost { get; set; } = string.Empty;

    public int Port { get; set; } = 3478;

    /// <summary>Si se define, se añade una URL turns: (TURN sobre TLS). Null = sin TLS configurado (dev local).</summary>
    public int? TlsPort { get; set; }

    /// <summary>
    /// Duración de la credencial. Se recorta a un máximo absoluto de 10 minutos
    /// (ver <see cref="Services.TurnCredentialService"/>) sin importar este valor.
    /// </summary>
    public int CredentialTtlSeconds { get; set; } = 300;
}

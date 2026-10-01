namespace VisionSupport.Server.Models;

/// <summary>
/// Auditoría de cada evento relevante de un <see cref="PairingCode"/>: creación, uso, revocación
/// e intentos fallidos de POST /api/devices/enroll (código inválido/vencido/usado/revocado).
/// <see cref="PairingCodeId"/> es null cuando el intento no coincide con el hash de ningún
/// código conocido (adivinanza). Nunca contiene el código en texto plano.
/// </summary>
public class PairingCodeEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? PairingCodeId { get; set; }
    public PairingCode? PairingCode { get; set; }

    public PairingCodeEventType Type { get; set; }

    public string? RemoteIp { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

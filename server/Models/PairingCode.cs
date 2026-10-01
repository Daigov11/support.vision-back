namespace VisionSupport.Server.Models;

/// <summary>
/// Código de emparejamiento de un solo uso que un Admin genera desde el panel para autorizar a
/// un Android concreto a obtener su <see cref="Device.PairingKey"/> (ver
/// DevicesController.Enroll). Solo se guarda el HASH (SHA-256) del código — el valor en texto
/// plano solo existe en la respuesta de creación y en el Android que lo recibe.
/// </summary>
public class PairingCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string CodeHash { get; set; } = string.Empty;

    public Guid CreatedByUserId { get; set; }
    public User? CreatedByUser { get; set; }

    /// <summary>Nombre opcional que el Admin asigna de antemano al dispositivo que espera emparejar.</summary>
    public string? Label { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Dispositivo creado/asociado por este código, una vez usado con éxito.</summary>
    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }
}

namespace VisionSupport.Server.Models;

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identificador estable que la app Android usa para emparejarse (no es secreto).</summary>
    public string DeviceCode { get; set; } = string.Empty;

    /// <summary>
    /// Secreto generado por el backend al registrar el dispositivo (ver DevicesController.Register).
    /// Android debe presentarlo en AnnounceDevicePresence para demostrar que es el dueño de
    /// DeviceCode. Nunca se devuelve en GET /api/devices, solo en la respuesta del registro.
    /// TEMPORAL: en producción esto se sustituirá por credenciales de dispositivo reales (mTLS/JWT).
    /// </summary>
    public string PairingKey { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public DeviceStatus Status { get; set; } = DeviceStatus.Offline;

    /// <summary>Id de conexión SignalR activo mientras el dispositivo está online.</summary>
    public string? ConnectionId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<RemoteSession> Sessions { get; set; } = new List<RemoteSession>();
}

namespace VisionSupport.Server.Models;

/// <summary>
/// Registro de auditoría de cada mensaje de señalización que atraviesa el RemoteHub.
/// No contiene vídeo ni audio: solo metadatos de señalización WebRTC (SDP, ICE, estados).
/// </summary>
public class SessionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid RemoteSessionId { get; set; }
    public RemoteSession? RemoteSession { get; set; }

    public SessionEventType Type { get; set; }

    /// <summary>Payload JSON del mensaje (SDP, candidato ICE, motivo, etc.).</summary>
    public string? Payload { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

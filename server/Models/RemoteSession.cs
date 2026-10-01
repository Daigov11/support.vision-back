namespace VisionSupport.Server.Models;

public class RemoteSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public Guid TechnicianUserId { get; set; }
    public User? TechnicianUser { get; set; }

    /// <summary>
    /// Id de conexión SignalR del técnico mientras participa activamente en esta sesión
    /// (se fija en RemoteHub.JoinSession tras verificar que es el dueño de la sesión).
    /// Junto con Device.ConnectionId, delimita quién puede enviar/recibir offer/answer/ICE.
    /// </summary>
    public string? TechnicianConnectionId { get; set; }

    public SessionStatus Status { get; set; } = SessionStatus.Requested;

    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? EndReason { get; set; }

    public ICollection<SessionEvent> Events { get; set; } = new List<SessionEvent>();
}

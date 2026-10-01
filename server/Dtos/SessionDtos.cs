using VisionSupport.Server.Models;

namespace VisionSupport.Server.Dtos;

public record SessionDto(
    Guid Id,
    Guid DeviceId,
    string DeviceName,
    Guid TechnicianUserId,
    string TechnicianName,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? EndReason)
{
    public static SessionDto FromModel(RemoteSession session) =>
        FromModel(session, session.TechnicianUser?.DisplayName ?? string.Empty);

    /// <summary>
    /// Variante sin navegación cargada: usada justo tras crear la sesión, cuando el nombre del
    /// técnico ya se conoce (viene del JWT) y no hace falta una consulta extra a Users.
    /// </summary>
    public static SessionDto FromModel(RemoteSession session, string technicianName) => new(
        session.Id,
        session.DeviceId,
        session.Device?.Name ?? string.Empty,
        session.TechnicianUserId,
        technicianName,
        session.Status.ToString(),
        session.RequestedAt,
        session.StartedAt,
        session.EndedAt,
        session.EndReason);
}

public record RequestSessionRequest(Guid DeviceId);

public record EndSessionRequest(string? Reason);

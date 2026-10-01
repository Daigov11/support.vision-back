using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Authentication;
using VisionSupport.Server.Data;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Hubs;
using VisionSupport.Server.Models;

namespace VisionSupport.Server.Controllers;

/// <summary>Requiere JWT (Admin o Support): "ve dispositivos y solicita sesiones" aplica a ambos roles.</summary>
[ApiController]
[Route("api/sessions")]
[Authorize]
public class SessionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IHubContext<RemoteHub> _hub;

    public SessionsController(AppDbContext db, IHubContext<RemoteHub> hub)
    {
        _db = db;
        _hub = hub;
    }

    /// <summary>El técnico solicita iniciar una sesión de soporte con un dispositivo.</summary>
    [HttpPost("request")]
    public async Task<ActionResult<SessionDto>> RequestSession([FromBody] RequestSessionRequest request, CancellationToken ct)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.Id == request.DeviceId, ct);
        if (device is null)
        {
            return NotFound($"Dispositivo '{request.DeviceId}' no encontrado.");
        }

        var technicianId = User.GetUserId();
        var technicianName = User.GetDisplayName();

        var session = new RemoteSession
        {
            DeviceId = device.Id,
            TechnicianUserId = technicianId,
            Status = SessionStatus.Requested
        };
        _db.RemoteSessions.Add(session);

        _db.SessionEvents.Add(new SessionEvent
        {
            RemoteSessionId = session.Id,
            Type = SessionEventType.SessionRequested
        });

        await _db.SaveChangesAsync(ct);

        session.Device = device;

        await _hub.Clients.Group(HubGroups.Device(device.Id)).SendAsync(
            "SessionRequested",
            new SessionRequestedMessage(session.Id, device.Id, technicianId, technicianName, session.RequestedAt),
            ct);

        return CreatedAtAction(nameof(RequestSession), new { id = session.Id }, SessionDto.FromModel(session, technicianName));
    }

    /// <summary>Finaliza una sesión activa o pendiente (usado por técnico o dispositivo).</summary>
    [HttpPost("{id:guid}/end")]
    public async Task<ActionResult<SessionDto>> EndSession(Guid id, [FromBody] EndSessionRequest? request, CancellationToken ct)
    {
        var session = await _db.RemoteSessions
            .Include(s => s.Device)
            .Include(s => s.TechnicianUser)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (session is null)
        {
            return NotFound($"Sesión '{id}' no encontrada.");
        }

        session.Status = SessionStatus.Ended;
        session.EndedAt = DateTimeOffset.UtcNow;
        session.EndReason = request?.Reason;
        session.TechnicianConnectionId = null;

        if (session.Device is not null && session.Device.Status == DeviceStatus.InSession)
        {
            session.Device.Status = session.Device.ConnectionId is null ? DeviceStatus.Offline : DeviceStatus.Online;
        }

        _db.SessionEvents.Add(new SessionEvent
        {
            RemoteSessionId = session.Id,
            Type = SessionEventType.SessionEnded,
            Payload = request?.Reason
        });

        await _db.SaveChangesAsync(ct);

        await _hub.Clients.Group(HubGroups.Session(session.Id)).SendAsync(
            "SessionEnded", new SessionEndedMessage(session.Id, request?.Reason), ct);
        await _hub.Clients.Group(HubGroups.Panel).SendAsync(
            "SessionEnded", new SessionEndedMessage(session.Id, request?.Reason), ct);

        return Ok(SessionDto.FromModel(session));
    }
}

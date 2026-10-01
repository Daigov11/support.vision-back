using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using VisionSupport.Server.RateLimiting;
using VisionSupport.Server.Services;

namespace VisionSupport.Server.Controllers;

/// <summary>
/// GET requiere JWT (Admin o Support: ambos "ven dispositivos"). Enroll queda anónimo a
/// propósito: lo llama Android, que nunca tiene ni necesita un access token de técnico — pero,
/// a diferencia del registro abierto anterior, exige un código de emparejamiento de un solo uso
/// generado por un Admin (ver PairingCodesController). No existe alta automática de dispositivos.
/// </summary>
[ApiController]
[Route("api/devices")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly AppDbContext _db;

    public DevicesController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<DeviceDto>>> GetDevices(CancellationToken ct)
    {
        var devices = await _db.Devices
            .OrderBy(d => d.Name)
            .Select(d => DeviceDto.FromModel(d))
            .ToListAsync(ct);

        return Ok(devices);
    }

    /// <summary>
    /// Último snapshot de diagnóstico del dispositivo (ver RemoteHub.ReportDiagnosticsSnapshot).
    /// Visible para cualquier técnico/admin autenticado, igual que GET /api/devices — no hay
    /// restricción adicional por dispositivo: la escritura sí está estrictamente acotada a la
    /// propia conexión SignalR del dispositivo (ver RemoteHub.RequireDeviceConnectionAsync), que
    /// es donde vive el requisito real de "no leer/escribir datos de otro dispositivo".
    /// 204 si el dispositivo existe pero todavía no reportó ningún diagnóstico (no es un error).
    /// </summary>
    [HttpGet("{id:guid}/diagnostics")]
    public async Task<ActionResult<DeviceHealthSnapshotDto>> GetDiagnostics(Guid id, CancellationToken ct)
    {
        if (!await _db.Devices.AnyAsync(d => d.Id == id, ct))
        {
            return NotFound();
        }

        var snapshot = await _db.DeviceHealthSnapshots.FirstOrDefaultAsync(s => s.DeviceId == id, ct);
        return snapshot is null ? NoContent() : Ok(DeviceHealthSnapshotDto.FromModel(snapshot));
    }

    /// <summary>
    /// Histórico de eventos de diagnóstico (cambios de red, batería crítica, errores del
    /// agente), más recientes primero. Sujeto a la misma retención que aplica al escribir (ver
    /// RemoteHub.PruneOldDiagnosticEventsAsync): como máximo los últimos 200 por dispositivo.
    /// </summary>
    [HttpGet("{id:guid}/diagnostics/events")]
    public async Task<ActionResult<IEnumerable<DeviceDiagnosticEventDto>>> GetDiagnosticEvents(Guid id, CancellationToken ct)
    {
        if (!await _db.Devices.AnyAsync(d => d.Id == id, ct))
        {
            return NotFound();
        }

        // Orden en memoria, no vía ORDER BY en SQL: mismo motivo que en PairingCodesController
        // (SQLite, usado en las pruebas de integración, no admite ordenar por DateTimeOffset).
        var events = await _db.DeviceDiagnosticEvents.Where(e => e.DeviceId == id).ToListAsync(ct);
        return Ok(events.OrderByDescending(e => e.CreatedAt).Select(DeviceDiagnosticEventDto.FromModel));
    }

    /// <summary>
    /// Único punto de alta de dispositivos. Requiere un código de emparejamiento válido, no
    /// vencido, no usado y no revocado (ver PairingCodesController); lo invalida atómicamente al
    /// usarlo, de un solo uso incluso ante intentos concurrentes con el mismo código. Si
    /// DeviceCode ya existe (reemparejamiento de un dispositivo de desarrollo), rota su
    /// PairingKey en vez de crear un dispositivo duplicado. Limitado por IP
    /// (ver Program.cs, política "pairing-enroll") para dificultar adivinar códigos por fuerza
    /// bruta. La respuesta incluye PairingKey solo aquí: Android debe guardarlo y presentarlo
    /// luego en RemoteHub.AnnounceDevicePresence.
    /// </summary>
    [HttpPost("enroll")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicyNames.PairingEnroll)]
    public async Task<ActionResult<EnrollDeviceResponse>> Enroll([FromBody] EnrollDeviceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) ||
            string.IsNullOrWhiteSpace(request.DeviceCode) ||
            string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new EnrollErrorResponse("invalid", "Código, deviceCode y nombre son obligatorios."));
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress?.ToString();
        var codeHash = PairingCodeService.Hash(request.Code);

        var pairingCode = await _db.PairingCodes.AsNoTracking().FirstOrDefaultAsync(p => p.CodeHash == codeHash, ct);
        if (pairingCode is null)
        {
            await LogAttemptAsync(null, PairingCodeEventType.AttemptInvalidCode, remoteIp, ct);
            return BadRequest(new EnrollErrorResponse("invalid", "Código inválido."));
        }
        if (pairingCode.RevokedAt is not null)
        {
            await LogAttemptAsync(pairingCode.Id, PairingCodeEventType.AttemptRevokedCode, remoteIp, ct);
            return BadRequest(new EnrollErrorResponse("revoked", "Código revocado."));
        }
        if (pairingCode.UsedAt is not null)
        {
            await LogAttemptAsync(pairingCode.Id, PairingCodeEventType.AttemptUsedCode, remoteIp, ct);
            return BadRequest(new EnrollErrorResponse("used", "Código ya utilizado."));
        }

        var now = DateTimeOffset.UtcNow;
        if (pairingCode.ExpiresAt <= now)
        {
            await LogAttemptAsync(pairingCode.Id, PairingCodeEventType.AttemptExpiredCode, remoteIp, ct);
            return BadRequest(new EnrollErrorResponse("expired", "Código vencido."));
        }

        // Reclamo atómico de un solo uso: ante dos peticiones concurrentes con el mismo código,
        // esta actualización condicional (UPDATE ... WHERE UsedAt IS NULL AND RevokedAt IS NULL)
        // solo puede afectar una fila la primera vez — la segunda ve 0 filas afectadas.
        var claimed = await _db.PairingCodes
            .Where(p => p.Id == pairingCode.Id && p.UsedAt == null && p.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.UsedAt, now), ct);
        if (claimed == 0)
        {
            await LogAttemptAsync(pairingCode.Id, PairingCodeEventType.AttemptUsedCode, remoteIp, ct);
            return BadRequest(new EnrollErrorResponse("used", "Código ya utilizado."));
        }

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceCode == request.DeviceCode, ct);
        var pairingKey = GeneratePairingKey();
        var deviceName = string.IsNullOrWhiteSpace(pairingCode.Label) ? request.Name.Trim() : pairingCode.Label!;

        if (device is null)
        {
            device = new Device
            {
                DeviceCode = request.DeviceCode,
                Name = deviceName,
                Status = DeviceStatus.Offline,
                PairingKey = pairingKey,
            };
            _db.Devices.Add(device);
        }
        else
        {
            // Reemparejamiento (p. ej. dispositivo de desarrollo reinstalado): rota el secreto,
            // no crea un dispositivo duplicado.
            device.PairingKey = pairingKey;
            device.Name = deviceName;
        }
        await _db.SaveChangesAsync(ct);

        await _db.PairingCodes
            .Where(p => p.Id == pairingCode.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(p => p.DeviceId, device.Id), ct);
        await LogAttemptAsync(pairingCode.Id, PairingCodeEventType.Used, remoteIp, ct);

        return Ok(new EnrollDeviceResponse(device.Id, device.DeviceCode, device.Name, pairingKey));
    }

    private async Task LogAttemptAsync(Guid? pairingCodeId, PairingCodeEventType type, string? remoteIp, CancellationToken ct)
    {
        _db.PairingCodeEvents.Add(new PairingCodeEvent { PairingCodeId = pairingCodeId, Type = type, RemoteIp = remoteIp });
        await _db.SaveChangesAsync(ct);
    }

    private static string GeneratePairingKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

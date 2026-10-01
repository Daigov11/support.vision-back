using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Authentication;
using VisionSupport.Server.Data;
using VisionSupport.Server.Dtos;
using VisionSupport.Server.Models;
using VisionSupport.Server.Services;

namespace VisionSupport.Server.Controllers;

/// <summary>
/// Gestión de códigos de emparejamiento de un solo uso. Solo Admin: es la única forma de que un
/// Android nuevo obtenga su PairingKey (ver DevicesController.Enroll) — no hay registro
/// automático/abierto.
/// </summary>
[ApiController]
[Route("api/pairing-codes")]
[Authorize(Roles = nameof(UserRole.Admin))]
public class PairingCodesController : ControllerBase
{
    private readonly AppDbContext _db;

    public PairingCodesController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PairingCodeDto>>> GetPairingCodes(CancellationToken ct)
    {
        // Orden en memoria (no vía ORDER BY en SQL): SQLite, usado en las pruebas de integración,
        // no admite ordenar por DateTimeOffset en el servidor; Postgres sí, pero mantenemos un
        // único camino de código para ambos. La lista es de tamaño acotado (panel de Admin), así
        // que el costo de ordenar en memoria es irrelevante.
        var codes = await _db.PairingCodes
            .Include(p => p.CreatedByUser)
            .Include(p => p.Device)
            .ToListAsync(ct);

        return Ok(codes
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => PairingCodeDto.FromModel(c, c.CreatedByUser?.DisplayName ?? "—", c.Device?.Name)));
    }

    /// <summary>El valor de <c>Code</c> en la respuesta solo se entrega aquí, una vez.</summary>
    [HttpPost]
    public async Task<ActionResult<CreatePairingCodeResponse>> CreatePairingCode(
        [FromBody] CreatePairingCodeRequest request, CancellationToken ct)
    {
        var plainCode = PairingCodeService.GenerateCode();
        var now = DateTimeOffset.UtcNow;

        var entity = new PairingCode
        {
            CodeHash = PairingCodeService.Hash(plainCode),
            CreatedByUserId = User.GetUserId(),
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            CreatedAt = now,
            ExpiresAt = now.Add(PairingCodeService.Lifetime),
        };
        _db.PairingCodes.Add(entity);
        _db.PairingCodeEvents.Add(new PairingCodeEvent
        {
            PairingCodeId = entity.Id,
            Type = PairingCodeEventType.Created,
            RemoteIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
        });
        await _db.SaveChangesAsync(ct);

        var response = new CreatePairingCodeResponse(
            entity.Id, PairingCodeService.FormatForDisplay(plainCode), entity.ExpiresAt, entity.Label);
        return CreatedAtAction(nameof(GetPairingCodes), null, response);
    }

    [HttpPost("{id:guid}/revoke")]
    public async Task<ActionResult<PairingCodeDto>> RevokePairingCode(Guid id, CancellationToken ct)
    {
        var code = await _db.PairingCodes
            .Include(p => p.CreatedByUser)
            .Include(p => p.Device)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (code is null)
        {
            return NotFound();
        }
        if (code.UsedAt is not null)
        {
            return Conflict("El código ya fue usado; no se puede revocar.");
        }

        if (code.RevokedAt is null)
        {
            code.RevokedAt = DateTimeOffset.UtcNow;
            _db.PairingCodeEvents.Add(new PairingCodeEvent
            {
                PairingCodeId = code.Id,
                Type = PairingCodeEventType.Revoked,
                RemoteIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
            });
            await _db.SaveChangesAsync(ct);
        }

        return Ok(PairingCodeDto.FromModel(code, code.CreatedByUser?.DisplayName ?? "—", code.Device?.Name));
    }
}

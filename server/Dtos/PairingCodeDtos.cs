using VisionSupport.Server.Models;

namespace VisionSupport.Server.Dtos;

public record CreatePairingCodeRequest(string? Label);

/// <summary>
/// Respuesta de POST /api/pairing-codes. Única vez que el código en texto plano se expone —
/// GET /api/pairing-codes nunca lo devuelve, solo el estado derivado.
/// </summary>
public record CreatePairingCodeResponse(Guid Id, string Code, DateTimeOffset ExpiresAt, string? Label);

public record PairingCodeDto(
    Guid Id,
    string Status,
    string? Label,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? UsedAt,
    DateTimeOffset? RevokedAt,
    Guid? DeviceId,
    string? DeviceName,
    string CreatedByDisplayName)
{
    public static PairingCodeDto FromModel(PairingCode code, string createdByDisplayName, string? deviceName)
    {
        var status = code.RevokedAt is not null ? "Revoked"
            : code.UsedAt is not null ? "Used"
            : code.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired"
            : "Active";

        return new PairingCodeDto(
            code.Id,
            status,
            code.Label,
            code.CreatedAt,
            code.ExpiresAt,
            code.UsedAt,
            code.RevokedAt,
            code.DeviceId,
            deviceName,
            createdByDisplayName);
    }
}

public record EnrollDeviceRequest(string Code, string DeviceCode, string Name);

/// <summary>
/// Respuesta de éxito de POST /api/devices/enroll. Igual que el registro abierto anterior,
/// PairingKey solo se entrega aquí — Android debe guardarlo y presentarlo luego en
/// RemoteHub.AnnounceDevicePresence.
/// </summary>
public record EnrollDeviceResponse(Guid DeviceId, string DeviceCode, string Name, string PairingKey);

/// <summary>
/// Respuesta de error de POST /api/devices/enroll. ErrorCode es uno de:
/// "invalid" | "expired" | "used" | "revoked", para que Android muestre un mensaje específico.
/// </summary>
public record EnrollErrorResponse(string ErrorCode, string Message);

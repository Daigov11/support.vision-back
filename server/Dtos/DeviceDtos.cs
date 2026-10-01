using VisionSupport.Server.Models;

namespace VisionSupport.Server.Dtos;

public record DeviceDto(
    Guid Id,
    string DeviceCode,
    string Name,
    string Status,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt)
{
    public static DeviceDto FromModel(Device device) => new(
        device.Id,
        device.DeviceCode,
        device.Name,
        device.Status.ToString(),
        device.LastSeenAt,
        device.CreatedAt);
}


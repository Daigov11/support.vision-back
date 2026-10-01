using VisionSupport.Server.Models;

namespace VisionSupport.Server.Dtos;

public record DeviceHealthSnapshotDto(
    Guid DeviceId,
    int? BatteryPercent,
    bool? IsCharging,
    bool NetworkConnected,
    string? NetworkType,
    int? WifiSignalLevel,
    long? StorageTotalBytes,
    long? StorageUsedBytes,
    long? StorageAvailableBytes,
    string Manufacturer,
    string Model,
    string AndroidVersion,
    string AppVersion,
    DateTimeOffset ReceivedAt)
{
    public static DeviceHealthSnapshotDto FromModel(DeviceHealthSnapshot snapshot) => new(
        snapshot.DeviceId,
        snapshot.BatteryPercent,
        snapshot.IsCharging,
        snapshot.NetworkConnected,
        snapshot.NetworkType,
        snapshot.WifiSignalLevel,
        snapshot.StorageTotalBytes,
        snapshot.StorageUsedBytes,
        snapshot.StorageAvailableBytes,
        snapshot.Manufacturer,
        snapshot.Model,
        snapshot.AndroidVersion,
        snapshot.AppVersion,
        snapshot.ReceivedAt);
}

public record DeviceDiagnosticEventDto(
    Guid Id,
    Guid DeviceId,
    string Type,
    string? Message,
    string? Code,
    string? SourcePackage,
    string? SourceAppVersion,
    DateTimeOffset? OccurredAt,
    DateTimeOffset CreatedAt)
{
    public static DeviceDiagnosticEventDto FromModel(DeviceDiagnosticEvent evt) => new(
        evt.Id,
        evt.DeviceId,
        evt.Type.ToString(),
        evt.Message,
        evt.Code,
        evt.SourcePackage,
        evt.SourceAppVersion,
        evt.OccurredAt,
        evt.CreatedAt);
}

namespace VisionSupport.Server.Hubs;

// Mensajes tipados que viajan por el RemoteHub. El backend NO transporta vídeo/audio:
// solo estos metadatos de presencia y señalización WebRTC. Ver docs/signaling-protocol.md.

public record DevicePresenceMessage(Guid DeviceId, string DeviceCode, string Status, DateTimeOffset Timestamp);

public record SessionRequestedMessage(
    Guid SessionId,
    Guid DeviceId,
    Guid TechnicianUserId,
    string TechnicianName,
    DateTimeOffset RequestedAt);

public record SessionRespondedMessage(Guid SessionId, Guid DeviceId, bool Accepted, string? Reason);

public record OfferMessage(Guid SessionId, string Sdp);

public record AnswerMessage(Guid SessionId, string Sdp);

public record IceCandidateMessage(Guid SessionId, string Candidate, string? SdpMid, int? SdpMLineIndex);

public record SessionEndedMessage(Guid SessionId, string? Reason);

// Diagnóstico básico (ver docs/architecture.md y server/README.md): Android reporta esto por el
// RemoteHub, nunca por REST — la única identidad válida es la conexión ya autorizada por
// AnnounceDevicePresence (ver RemoteHub.RequireDeviceConnectionAsync). Nunca contraseñas,
// pairingKey, JWT, contenido de pantalla, contactos, ubicación ni Logcat.
public record DiagnosticsSnapshotMessage(
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
    string AppVersion);

/// <summary>
/// Type es el nombre de DeviceDiagnosticEventType; Message ya viene saneado por Android (por el
/// propio agente, o por el SDK VisionDiagnostics antes de encolarlo — ver
/// android/vision-diagnostics-sdk). Code/SourcePackage/SourceAppVersion/OccurredAt solo aplican
/// a los tipos "Application*" (evento de una app propia integrada); para los tipos propios del
/// agente (NetworkChanged/BatteryCritical/AgentError) el Hub los ignora — ver
/// RemoteHub.ReportDiagnosticEvent. El Hub confía en el SourcePackage que llega aquí porque ya
/// fue verificado río arriba, dentro del propio proceso Android: el `ContentProvider` del agente
/// (android/.../diagnostics/provider/DiagnosticsProvider.kt) resuelve el paquete llamador contra
/// el UID real de Android (nunca un valor que el cliente declare) antes de encolar el evento —
/// este mensaje de Hub solo transporta ese valor ya verificado, no lo re-verifica (no tiene
/// forma de hacerlo desde este canal: para el Hub, todo Android habla por la misma conexión
/// SignalR autorizada por AnnounceDevicePresence).
/// </summary>
public record DiagnosticEventMessage(
    string Type,
    string? Message,
    string? Code = null,
    string? SourcePackage = null,
    string? SourceAppVersion = null,
    DateTimeOffset? OccurredAt = null);

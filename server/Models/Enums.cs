namespace VisionSupport.Server.Models;

public enum UserRole
{
    Support,
    Admin
}

public enum DeviceStatus
{
    Offline,
    Online,
    InSession
}

public enum SessionStatus
{
    Requested,
    Accepted,
    Rejected,
    Active,
    Ended
}

public enum SessionEventType
{
    DevicePresence,
    SessionRequested,
    SessionAccepted,
    SessionRejected,
    Offer,
    Answer,
    IceCandidate,
    SessionEnded
}

/// <summary>Auditoría de POST /api/pairing-codes y POST /api/devices/enroll. Nunca guarda el código en texto plano.</summary>
public enum PairingCodeEventType
{
    Created,
    Used,
    Revoked,
    AttemptInvalidCode,
    AttemptExpiredCode,
    AttemptUsedCode,
    AttemptRevokedCode
}

/// <summary>
/// Tipos de evento inmediato de diagnóstico que Android reporta vía RemoteHub.ReportDiagnosticEvent
/// (ver Models/DeviceDiagnosticEvent.cs). Los tres primeros son siempre del propio agente Vision
/// Support (SourcePackage null); los cuatro `Application*` vienen de una app propia integrada vía
/// el SDK `VisionDiagnostics` (SourcePackage obligatorio, verificado por Android — ver
/// RemoteHub.ReportDiagnosticEvent). Todos son mensajes ya saneados (nunca una excepción cruda ni
/// Logcat).
/// </summary>
public enum DeviceDiagnosticEventType
{
    NetworkChanged,
    BatteryCritical,
    AgentError,
    ApplicationInfo,
    ApplicationWarning,
    ApplicationError,
    ApplicationException
}

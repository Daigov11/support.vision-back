namespace VisionSupport.Server.Models;

/// <summary>
/// Evento puntual de diagnóstico reportado por un dispositivo — ver
/// RemoteHub.ReportDiagnosticEvent. Histórico append-only, a diferencia del estado único de
/// <see cref="DeviceHealthSnapshot"/>. Sujeto a poda de retención configurable (ver
/// Options/DiagnosticsOptions.cs, RemoteHub.PruneOldDiagnosticEventsAsync y
/// docs/architecture.md) para no crecer sin límite.
///
/// Dos orígenes posibles:
/// - Propio del agente Vision Support (<see cref="DeviceDiagnosticEventType.NetworkChanged"/>,
///   <see cref="DeviceDiagnosticEventType.BatteryCritical"/>,
///   <see cref="DeviceDiagnosticEventType.AgentError"/>): <see cref="SourcePackage"/> es null.
/// - Una app propia integrada vía el SDK `VisionDiagnostics`
///   (<see cref="DeviceDiagnosticEventType.ApplicationInfo"/>/`ApplicationWarning`/
///   `ApplicationError`/`ApplicationException`): <see cref="SourcePackage"/> viene del paquete
///   llamador verificado por Android (UID → PackageManager) en el `ContentProvider` del agente
///   — nunca de un valor que el cliente pudiera declarar (ver android/vision-diagnostics-sdk).
/// </summary>
public class DeviceDiagnosticEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public DeviceDiagnosticEventType Type { get; set; }

    /// <summary>Mensaje ya saneado por Android: nunca una excepción cruda, Logcat, ni datos de otras apps.</summary>
    public string? Message { get; set; }

    /// <summary>Código corto opcional definido por la app integrada (p. ej. "PAYMENT_TIMEOUT").</summary>
    public string? Code { get; set; }

    /// <summary>Package name del proceso llamador verificado por Android; null para eventos propios del agente.</summary>
    public string? SourcePackage { get; set; }

    /// <summary>Versión de la app fuente (la suya, no la de Vision Support); null para eventos propios del agente.</summary>
    public string? SourceAppVersion { get; set; }

    /// <summary>Instante en que ocurrió según el reloj del dispositivo (puede preceder a CreatedAt si venía en cola offline).</summary>
    public DateTimeOffset? OccurredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

namespace VisionSupport.Server.Options;

/// <summary>
/// Configuración del módulo de diagnóstico (ver RemoteHub.ReportDiagnosticsSnapshot/
/// ReportDiagnosticEvent y docs/architecture.md). Variable de entorno:
/// Diagnostics__MaxEventsPerDevice.
/// </summary>
public class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>
    /// Retención de DeviceDiagnosticEvent por dispositivo: se podan los más antiguos más allá de
    /// este número en cada escritura nueva (ver RemoteHub.PruneOldDiagnosticEventsAsync). 200 es
    /// un valor inicial razonable para un volumen esperado bajo (solo cambios relevantes, no
    /// telemetría continua); configurable porque una integración con varias apps propias
    /// reportando al mismo dispositivo puede justificar un límite distinto.
    /// </summary>
    public int MaxEventsPerDevice { get; set; } = 200;
}

namespace VisionSupport.Server.Models;

/// <summary>
/// Último estado de diagnóstico reportado por un dispositivo (uno por Device, se sobrescribe en
/// cada snapshot — ver RemoteHub.ReportDiagnosticsSnapshot). El histórico de eventos puntuales
/// vive aparte en <see cref="DeviceDiagnosticEvent"/>. Nunca contiene contraseñas, pairingKey,
/// JWT, contenido de pantalla, contactos, ubicación ni Logcat — solo métricas básicas del
/// dispositivo (batería, red, almacenamiento, identificación de SO/app).
/// </summary>
public class DeviceHealthSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }

    public int? BatteryPercent { get; set; }
    public bool? IsCharging { get; set; }

    public bool NetworkConnected { get; set; }

    /// <summary>"Wifi" | "Cellular" | "Ethernet" | "Other" | null si no hay red activa.</summary>
    public string? NetworkType { get; set; }

    /// <summary>0-4 (WifiManager.calculateSignalLevel); null si no aplica o Android lo restringe sin permiso de ubicación.</summary>
    public int? WifiSignalLevel { get; set; }

    public long? StorageTotalBytes { get; set; }
    public long? StorageUsedBytes { get; set; }
    public long? StorageAvailableBytes { get; set; }

    public string Manufacturer { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string AndroidVersion { get; set; } = string.Empty;

    /// <summary>Versión de la app Vision Support (BuildConfig.VERSION_NAME en Android).</summary>
    public string AppVersion { get; set; } = string.Empty;

    /// <summary>Cuándo llegó este snapshot al backend (no cuándo Android lo generó).</summary>
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

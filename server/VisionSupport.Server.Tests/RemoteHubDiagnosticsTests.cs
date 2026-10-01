using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Hubs;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// Pruebas del Hub (instanciación directa, ver HubTestServer) para
/// ReportDiagnosticsSnapshot/ReportDiagnosticEvent: solo la conexión ya autorizada por
/// AnnounceDevicePresence puede reportar, y siempre para SU PROPIO Device — nunca "de otro
/// dispositivo", porque el Device se resuelve de Context.ConnectionId, no de un parámetro que el
/// cliente pudiera falsificar.
/// </summary>
public class RemoteHubDiagnosticsTests
{
    private const string PairingKey = "0123456789abcdef0123456789abcdef";

    private static async Task<Device> SeedDeviceAsync(AppDbContext db, string deviceCode, string pairingKey = PairingKey)
    {
        var device = new Device
        {
            DeviceCode = deviceCode,
            Name = "Dispositivo de prueba",
            PairingKey = pairingKey,
            Status = DeviceStatus.Offline,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }

    private static DiagnosticsSnapshotMessage SampleSnapshot(int batteryPercent = 80) => new(
        BatteryPercent: batteryPercent,
        IsCharging: false,
        NetworkConnected: true,
        NetworkType: "Wifi",
        WifiSignalLevel: 3,
        StorageTotalBytes: 128_000_000_000,
        StorageUsedBytes: 64_000_000_000,
        StorageAvailableBytes: 64_000_000_000,
        Manufacturer: "Google",
        Model: "Pixel de prueba",
        AndroidVersion: "Android 15 (API 35)",
        AppVersion: "0.1.0");

    [Fact]
    public async Task ReportDiagnosticsSnapshot_FromAuthorizedConnection_PersistsAndNotifiesPanel()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-1");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        await hub.ReportDiagnosticsSnapshot(SampleSnapshot());

        using var assertDb = server.NewDbContext();
        var snapshot = await assertDb.DeviceHealthSnapshots.SingleAsync(s => s.DeviceId == device.Id);
        Assert.Equal(80, snapshot.BatteryPercent);
        Assert.Equal("Wifi", snapshot.NetworkType);
        Assert.Equal("Google", snapshot.Manufacturer);
        Assert.Contains(server.Clients.ForGroup(HubGroups.Panel).Sent, m => m.Method == "DeviceDiagnosticsUpdated");
    }

    [Fact]
    public async Task ReportDiagnosticsSnapshot_CalledTwice_UpsertsSingleRowWithLatestValues()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-2");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        await hub.ReportDiagnosticsSnapshot(SampleSnapshot(batteryPercent: 80));
        await hub.ReportDiagnosticsSnapshot(SampleSnapshot(batteryPercent: 42));

        using var assertDb = server.NewDbContext();
        var snapshots = await assertDb.DeviceHealthSnapshots.Where(s => s.DeviceId == device.Id).ToListAsync();
        Assert.Single(snapshots);
        Assert.Equal(42, snapshots[0].BatteryPercent);
    }

    [Fact]
    public async Task ReportDiagnosticsSnapshot_FromConnectionThatNeverAnnounced_Throws()
    {
        using var server = HubTestServer.Create();
        var hub = server.CreateHub("conn-unauthenticated");

        await Assert.ThrowsAsync<HubException>(() => hub.ReportDiagnosticsSnapshot(SampleSnapshot()));
    }

    [Fact]
    public async Task ReportDiagnosticEvent_FromAuthorizedConnection_PersistsAndNotifiesPanel()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-3");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        await hub.ReportDiagnosticEvent(new DiagnosticEventMessage("NetworkChanged", "Red Wi-Fi conectada"));

        using var assertDb = server.NewDbContext();
        var evt = await assertDb.DeviceDiagnosticEvents.SingleAsync(e => e.DeviceId == device.Id);
        Assert.Equal(DeviceDiagnosticEventType.NetworkChanged, evt.Type);
        Assert.Equal("Red Wi-Fi conectada", evt.Message);
        Assert.Contains(server.Clients.ForGroup(HubGroups.Panel).Sent, m => m.Method == "DeviceDiagnosticEventReceived");
    }

    [Fact]
    public async Task ReportDiagnosticEvent_FromConnectionThatNeverAnnounced_Throws()
    {
        using var server = HubTestServer.Create();
        var hub = server.CreateHub("conn-unauthenticated");

        await Assert.ThrowsAsync<HubException>(
            () => hub.ReportDiagnosticEvent(new DiagnosticEventMessage("AgentError", "algo falló")));
    }

    [Fact]
    public async Task ReportDiagnosticEvent_WithInvalidType_Throws()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-4");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        await Assert.ThrowsAsync<HubException>(
            () => hub.ReportDiagnosticEvent(new DiagnosticEventMessage("TipoInventado", "x")));
    }

    /// <summary>
    /// La prueba de autorización central de este hito: dos dispositivos conectados a la vez, y
    /// el evento reportado por uno de ellos jamás aparece bajo el otro — porque el Device se
    /// resuelve de la conexión que llama, no de un id que el propio mensaje pudiera llevar.
    /// </summary>
    [Fact]
    public async Task ReportDiagnosticEvent_WithTwoDevicesConnected_OnlyAffectsCallersOwnDevice()
    {
        using var server = HubTestServer.Create();
        Device deviceA, deviceB;
        using (var seedDb = server.NewDbContext())
        {
            deviceA = await SeedDeviceAsync(seedDb, "DEV-DIAG-A", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            deviceB = await SeedDeviceAsync(seedDb, "DEV-DIAG-B", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        }

        var hubA = server.CreateHub("conn-A");
        await hubA.AnnounceDevicePresence(deviceA.DeviceCode, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var hubB = server.CreateHub("conn-B");
        await hubB.AnnounceDevicePresence(deviceB.DeviceCode, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

        await hubB.ReportDiagnosticEvent(new DiagnosticEventMessage("BatteryCritical", "Batería al 5%"));

        using var assertDb = server.NewDbContext();
        Assert.Empty(await assertDb.DeviceDiagnosticEvents.Where(e => e.DeviceId == deviceA.Id).ToListAsync());
        var eventForB = await assertDb.DeviceDiagnosticEvents.SingleAsync(e => e.DeviceId == deviceB.Id);
        Assert.Equal("Batería al 5%", eventForB.Message);
    }

    [Fact]
    public async Task ReportDiagnosticEvent_ApplicationError_WithSourcePackage_PersistsSourceFields()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-APP-1");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var occurredAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        await hub.ReportDiagnosticEvent(new DiagnosticEventMessage(
            Type: "ApplicationError",
            Message: "Fallo al procesar el pago",
            Code: "PAYMENT_TIMEOUT",
            SourcePackage: "com.example.tuapp",
            SourceAppVersion: "3.2.1",
            OccurredAt: occurredAt));

        using var assertDb = server.NewDbContext();
        var evt = await assertDb.DeviceDiagnosticEvents.SingleAsync(e => e.DeviceId == device.Id);
        Assert.Equal(DeviceDiagnosticEventType.ApplicationError, evt.Type);
        Assert.Equal("com.example.tuapp", evt.SourcePackage);
        Assert.Equal("3.2.1", evt.SourceAppVersion);
        Assert.Equal("PAYMENT_TIMEOUT", evt.Code);
        Assert.Equal(occurredAt, evt.OccurredAt);
    }

    [Theory]
    [InlineData("ApplicationInfo")]
    [InlineData("ApplicationWarning")]
    [InlineData("ApplicationError")]
    [InlineData("ApplicationException")]
    public async Task ReportDiagnosticEvent_ApplicationType_WithoutSourcePackage_Throws(string type)
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, $"DEV-DIAG-NOPKG-{type}");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        await Assert.ThrowsAsync<HubException>(
            () => hub.ReportDiagnosticEvent(new DiagnosticEventMessage(type, "mensaje", SourcePackage: null)));
    }

    /// <summary>
    /// Defensa en profundidad: aunque Android mandara un SourcePackage para un tipo propio del
    /// agente (no debería ocurrir con el cliente real, pero el Hub no debe confiar en eso), el
    /// backend lo ignora — esos campos solo tienen sentido para eventos "Application*".
    /// </summary>
    [Fact]
    public async Task ReportDiagnosticEvent_AgentOwnType_IgnoresSourcePackageEvenIfProvided()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-AGENT-IGNORE");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        await hub.ReportDiagnosticEvent(new DiagnosticEventMessage(
            Type: "AgentError",
            Message: "error del agente",
            SourcePackage: "com.example.deberia-ser-ignorado"));

        using var assertDb = server.NewDbContext();
        var evt = await assertDb.DeviceDiagnosticEvents.SingleAsync(e => e.DeviceId == device.Id);
        Assert.Null(evt.SourcePackage);
    }

    [Fact]
    public async Task ReportDiagnosticEvent_RetentionLimit_IsConfigurable()
    {
        using var server = HubTestServer.Create();
        server.DiagnosticsOptions.MaxEventsPerDevice = 5;

        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-CONFIGURABLE-RETENTION");
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        for (var i = 0; i < 8; i++)
        {
            await hub.ReportDiagnosticEvent(new DiagnosticEventMessage("AgentError", $"error-{i}"));
        }

        using var assertDb = server.NewDbContext();
        var remaining = await assertDb.DeviceDiagnosticEvents.Where(e => e.DeviceId == device.Id).ToListAsync();
        Assert.Equal(5, remaining.Count);
    }

    [Fact]
    public async Task ReportDiagnosticEvent_BeyondRetentionLimit_KeepsOnlyMostRecentEvents()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb, "DEV-DIAG-RETENTION");

            // Se seedean 204 eventos directamente con CreatedAt controlado (en vez de llamar al
            // hub 204 veces con DateTimeOffset.UtcNow real) para que el orden sea determinista:
            // llamadas reales en bucle muy rápido podrían caer en el mismo instante y volver la
            // prueba dependiente de la resolución del reloj del sistema.
            var baseTime = DateTimeOffset.UtcNow.AddDays(-1);
            for (var i = 0; i < 204; i++)
            {
                seedDb.DeviceDiagnosticEvents.Add(new DeviceDiagnosticEvent
                {
                    DeviceId = device.Id,
                    Type = DeviceDiagnosticEventType.AgentError,
                    Message = $"error-{i}",
                    CreatedAt = baseTime.AddSeconds(i),
                });
            }
            await seedDb.SaveChangesAsync();
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        // El evento 205 (el más nuevo, con CreatedAt = ahora) es el que dispara la poda.
        await hub.ReportDiagnosticEvent(new DiagnosticEventMessage("AgentError", "error-mas-reciente"));

        using var assertDb = server.NewDbContext();
        var remaining = await assertDb.DeviceDiagnosticEvents.Where(e => e.DeviceId == device.Id).ToListAsync();
        Assert.Equal(200, remaining.Count);
        // Se podan los 5 más antiguos (error-0..error-4); sobreviven el resto y el recién llegado.
        Assert.DoesNotContain(remaining, e => e.Message is "error-0" or "error-1" or "error-2" or "error-3" or "error-4");
        Assert.Contains(remaining, e => e.Message == "error-203");
        Assert.Contains(remaining, e => e.Message == "error-mas-reciente");
    }
}

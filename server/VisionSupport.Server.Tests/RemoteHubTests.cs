using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Hubs;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

public class RemoteHubTests
{
    private const string PairingKey = "0123456789abcdef0123456789abcdef";

    private static async Task<Device> SeedDeviceAsync(AppDbContext db, string deviceCode = "DEV-1", string pairingKey = PairingKey)
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

    private static async Task<User> SeedTechnicianAsync(AppDbContext db, string email = "tech@dev.local")
    {
        var user = new User { Email = email, DisplayName = "Técnico", Role = UserRole.Support };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<RemoteSession> SeedSessionAsync(
        AppDbContext db, Guid deviceId, Guid technicianId, SessionStatus status = SessionStatus.Requested)
    {
        var session = new RemoteSession
        {
            DeviceId = deviceId,
            TechnicianUserId = technicianId,
            Status = status,
            StartedAt = status is SessionStatus.Accepted or SessionStatus.Active ? DateTimeOffset.UtcNow : null,
        };
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task AnnounceDevicePresence_WithValidPairingKey_SetsConnectionIdAndJoinsDeviceGroup()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        var hub = server.CreateHub("conn-A");
        await hub.AnnounceDevicePresence(device.DeviceCode, PairingKey);

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal("conn-A", reloaded.ConnectionId);
        Assert.Equal(DeviceStatus.Online, reloaded.Status);
        Assert.True(server.Groups.IsMember("conn-A", HubGroups.Device(device.Id)));
        Assert.Single(server.Clients.ForGroup(HubGroups.Panel).Sent);
    }

    [Fact]
    public async Task AnnounceDevicePresence_WithInvalidPairingKey_ThrowsAndDoesNotChangeDevice()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        var hub = server.CreateHub("conn-A");
        await Assert.ThrowsAsync<HubException>(() => hub.AnnounceDevicePresence(device.DeviceCode, "clave-incorrecta"));

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Null(reloaded.ConnectionId);
        Assert.Equal(DeviceStatus.Offline, reloaded.Status);
    }

    [Fact]
    public async Task AnnounceDevicePresence_WithDifferentLengthPairingKey_Throws()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        var hub = server.CreateHub("conn-A");
        await Assert.ThrowsAsync<HubException>(() => hub.AnnounceDevicePresence(device.DeviceCode, "corta"));
    }

    [Fact]
    public async Task AnnounceDevicePresence_Reconnect_EvictsPreviousConnectionAndNotifiesIt()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        await server.CreateHub("conn-A").AnnounceDevicePresence(device.DeviceCode, PairingKey);
        Assert.True(server.Groups.IsMember("conn-A", HubGroups.Device(device.Id)));

        await server.CreateHub("conn-B").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal("conn-B", reloaded.ConnectionId);

        // La conexión vieja ya no es miembro del grupo del dispositivo...
        Assert.False(server.Groups.IsMember("conn-A", HubGroups.Device(device.Id)));
        Assert.True(server.Groups.IsMember("conn-B", HubGroups.Device(device.Id)));
        Assert.Contains(("conn-A", HubGroups.Device(device.Id)), server.Groups.Removed);

        // ...y recibió un aviso explícito para desconectarse.
        var notice = Assert.Single(server.Clients.ForConnection("conn-A").Sent);
        Assert.Equal("ForceDisconnected", notice.Method);
    }

    [Fact]
    public async Task RespondToSession_FromStaleConnectionAfterReconnect_Throws_NewConnectionSucceeds()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
            technician = await SeedTechnicianAsync(seedDb);
            session = await SeedSessionAsync(seedDb, device.Id, technician.Id);
        }

        // Anuncio original con conn-A, luego reconexión con conn-B (conn-A queda "zombie").
        await server.CreateHub("conn-A").AnnounceDevicePresence(device.DeviceCode, PairingKey);
        await server.CreateHub("conn-B").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var staleHub = server.CreateHub("conn-A");
        var ex = await Assert.ThrowsAsync<HubException>(() => staleHub.RespondToSession(session.Id, true, null));
        Assert.Contains("No autorizado", ex.Message);

        // La sesión sigue pendiente: el intento de la conexión vieja no tuvo ningún efecto.
        using (var checkDb = server.NewDbContext())
        {
            var stillRequested = await checkDb.RemoteSessions.SingleAsync(s => s.Id == session.Id);
            Assert.Equal(SessionStatus.Requested, stillRequested.Status);
        }

        var currentHub = server.CreateHub("conn-B");
        await currentHub.RespondToSession(session.Id, true, null);

        using var assertDb = server.NewDbContext();
        var accepted = await assertDb.RemoteSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(SessionStatus.Accepted, accepted.Status);
    }

    [Fact]
    public async Task SendOffer_FromStaleDeviceConnectionAfterReconnect_Throws()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
            technician = await SeedTechnicianAsync(seedDb);
            session = await SeedSessionAsync(seedDb, device.Id, technician.Id, SessionStatus.Accepted);
        }

        // conn-A "respondió" originalmente (simulamos el estado post-aceptación asignando su
        // ConnectionId directamente) y luego el dispositivo reconecta con conn-B.
        using (var setupDb = server.NewDbContext())
        {
            var d = await setupDb.Devices.SingleAsync(x => x.Id == device.Id);
            d.ConnectionId = "conn-A";
            await setupDb.SaveChangesAsync();
        }
        await server.Groups.AddToGroupAsync("conn-A", HubGroups.Session(session.Id));

        await server.CreateHub("conn-B").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var staleHub = server.CreateHub("conn-A");
        var ex = await Assert.ThrowsAsync<HubException>(
            () => staleHub.SendOffer(new OfferMessage(session.Id, "v=0 stale-offer")));
        Assert.Contains("No autorizado", ex.Message);

        var currentHub = server.CreateHub("conn-B");
        await currentHub.SendOffer(new OfferMessage(session.Id, "v=0 fresh-offer"));
        // Si no lanzó, la conexión nueva sí está autorizada para esta sesión.
    }

    [Fact]
    public async Task SendIceCandidate_FromStaleDeviceConnectionAfterReconnect_Throws()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
            technician = await SeedTechnicianAsync(seedDb);
            session = await SeedSessionAsync(seedDb, device.Id, technician.Id, SessionStatus.Accepted);
        }

        using (var setupDb = server.NewDbContext())
        {
            var d = await setupDb.Devices.SingleAsync(x => x.Id == device.Id);
            d.ConnectionId = "conn-A";
            await setupDb.SaveChangesAsync();
        }

        await server.CreateHub("conn-B").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var staleHub = server.CreateHub("conn-A");
        await Assert.ThrowsAsync<HubException>(
            () => staleHub.SendIceCandidate(new IceCandidateMessage(session.Id, "candidate:1 ...", "0", 0)));
    }

    [Fact]
    public async Task AnnounceDevicePresence_Reconnect_MovesActiveSessionGroupMembershipToNewConnection()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
            technician = await SeedTechnicianAsync(seedDb);
            session = await SeedSessionAsync(seedDb, device.Id, technician.Id, SessionStatus.Accepted);
        }

        var sessionGroup = HubGroups.Session(session.Id);

        await server.CreateHub("conn-A").AnnounceDevicePresence(device.DeviceCode, PairingKey);
        // Como ya había una sesión Accepted para este dispositivo, la conexión actual entra
        // directamente a su sala de señalización (sin esto, el offer/answer/ICE posteriores no
        // le llegarían).
        Assert.True(server.Groups.IsMember("conn-A", sessionGroup));

        await server.CreateHub("conn-B").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        Assert.False(server.Groups.IsMember("conn-A", sessionGroup));
        Assert.True(server.Groups.IsMember("conn-B", sessionGroup));
    }

    [Fact]
    public async Task OnDisconnectedAsync_ConnectionMatchesCurrent_MarksOfflineAndBroadcasts()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        await server.CreateHub("conn-A").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var hub = server.CreateHub("conn-A");
        await hub.OnDisconnectedAsync(null);

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal(DeviceStatus.Offline, reloaded.Status);
        Assert.Null(reloaded.ConnectionId);

        var panelMessages = server.Clients.ForGroup(HubGroups.Panel).Sent;
        Assert.Contains(panelMessages, m => m.Method == "DevicePresenceChanged");
    }

    /// <summary>
    /// Regresión del bug descrito en la tarea: si la conexión nueva ya reemplazó
    /// Device.ConnectionId antes de que se procese la desconexión de la vieja,
    /// OnDisconnectedAsync NO debe marcar el dispositivo Offline.
    /// </summary>
    [Fact]
    public async Task OnDisconnectedAsync_StaleConnectionAfterReconnect_DoesNotMarkOfflineOrBroadcast()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        await server.CreateHub("conn-A").AnnounceDevicePresence(device.DeviceCode, PairingKey);
        await server.CreateHub("conn-B").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        server.Clients.ForGroup(HubGroups.Panel).Sent.Clear();

        // Llega, tarde, el evento de desconexión de la conexión VIEJA.
        var staleHub = server.CreateHub("conn-A");
        await staleHub.OnDisconnectedAsync(null);

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal(DeviceStatus.Online, reloaded.Status);
        Assert.Equal("conn-B", reloaded.ConnectionId);
        Assert.DoesNotContain(
            server.Clients.ForGroup(HubGroups.Panel).Sent,
            m => m.Method == "DevicePresenceChanged");
    }

    [Fact]
    public async Task OnDisconnectedAsync_ForUnrelatedConnectionId_IsNoOp()
    {
        using var server = HubTestServer.Create();
        Device device;
        using (var seedDb = server.NewDbContext())
        {
            device = await SeedDeviceAsync(seedDb);
        }

        await server.CreateHub("conn-A").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var unrelatedHub = server.CreateHub("conn-never-announced");
        await unrelatedHub.OnDisconnectedAsync(null);

        using var assertDb = server.NewDbContext();
        var reloaded = await assertDb.Devices.SingleAsync(d => d.Id == device.Id);
        Assert.Equal(DeviceStatus.Online, reloaded.Status);
        Assert.Equal("conn-A", reloaded.ConnectionId);
    }
}

using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using VisionSupport.Server.Data;
using VisionSupport.Server.Hubs;
using VisionSupport.Server.Models;
using Xunit;

namespace VisionSupport.Server.Tests;

/// <summary>
/// GetTurnCredentials reutiliza exactamente AuthorizeSessionParticipantAsync (el mismo guardia
/// que SendOffer/SendAnswer/SendIceCandidate), así que estas pruebas verifican que esa reutilización
/// efectivamente cubre: sesión inexistente, pendiente, finalizada, y conexión "zombie" tras una
/// reconexión — más el contrato propio del método (formato username/credential, TURN no configurado).
/// </summary>
public class RemoteHubTurnCredentialsTests
{
    private const string PairingKey = "0123456789abcdef0123456789abcdef";

    private static async Task<Device> SeedDeviceAsync(AppDbContext db, string deviceCode = "DEV-TURN-1")
    {
        var device = new Device
        {
            DeviceCode = deviceCode,
            Name = "Dispositivo de prueba",
            PairingKey = PairingKey,
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
        AppDbContext db, Guid deviceId, Guid technicianId, SessionStatus status)
    {
        var session = new RemoteSession
        {
            DeviceId = deviceId,
            TechnicianUserId = technicianId,
            Status = status,
            StartedAt = status is SessionStatus.Accepted or SessionStatus.Active ? DateTimeOffset.UtcNow : null,
            EndedAt = status == SessionStatus.Ended ? DateTimeOffset.UtcNow : null,
        };
        db.RemoteSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    [Fact]
    public async Task GetTurnCredentials_ForAuthorizedDeviceConnection_ReturnsCredentialsForThatSession()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            device = await SeedDeviceAsync(db);
            technician = await SeedTechnicianAsync(db);
            session = await SeedSessionAsync(db, device.Id, technician.Id, SessionStatus.Accepted);
            device.ConnectionId = "conn-device";
            await db.SaveChangesAsync();
        }

        var deviceHub = server.CreateHub("conn-device");
        var result = await deviceHub.GetTurnCredentials(session.Id);

        var iceServer = Assert.Single(result.IceServers);
        Assert.EndsWith($":{session.Id}", iceServer.Username);
        Assert.NotNull(iceServer.Credential);
        Assert.Contains(iceServer.Urls, u => u.StartsWith("turn:"));
    }

    [Fact]
    public async Task GetTurnCredentials_ForAuthorizedTechnicianConnection_ReturnsCredentials()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            device = await SeedDeviceAsync(db);
            technician = await SeedTechnicianAsync(db);
            session = await SeedSessionAsync(db, device.Id, technician.Id, SessionStatus.Accepted);
            session.TechnicianConnectionId = "conn-tech";
            await db.SaveChangesAsync();
        }

        var techHub = server.CreateHub("conn-tech");
        var result = await techHub.GetTurnCredentials(session.Id);

        Assert.NotNull(result.IceServers[0].Credential);
    }

    [Fact]
    public async Task GetTurnCredentials_ForUnrelatedConnection_Throws()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            device = await SeedDeviceAsync(db);
            technician = await SeedTechnicianAsync(db);
            session = await SeedSessionAsync(db, device.Id, technician.Id, SessionStatus.Accepted);
        }

        var strangerHub = server.CreateHub("conn-stranger");
        var ex = await Assert.ThrowsAsync<HubException>(() => strangerHub.GetTurnCredentials(session.Id));
        Assert.Contains("No autorizado", ex.Message);
    }

    [Fact]
    public async Task GetTurnCredentials_ForNonExistentSession_Throws()
    {
        using var server = HubTestServer.Create();
        var hub = server.CreateHub("conn-any");
        await Assert.ThrowsAsync<HubException>(() => hub.GetTurnCredentials(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(SessionStatus.Requested)]
    [InlineData(SessionStatus.Rejected)]
    [InlineData(SessionStatus.Ended)]
    public async Task GetTurnCredentials_ForSessionNotAcceptedOrActive_Throws(SessionStatus status)
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            device = await SeedDeviceAsync(db);
            technician = await SeedTechnicianAsync(db);
            session = await SeedSessionAsync(db, device.Id, technician.Id, status);
            device.ConnectionId = "conn-device";
            await db.SaveChangesAsync();
        }

        var deviceHub = server.CreateHub("conn-device");
        var ex = await Assert.ThrowsAsync<HubException>(() => deviceHub.GetTurnCredentials(session.Id));
        Assert.Contains("no está aceptada ni activa", ex.Message);
    }

    [Fact]
    public async Task GetTurnCredentials_ForZombieDeviceConnectionAfterReconnect_Throws()
    {
        using var server = HubTestServer.Create();
        Device device;
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            device = await SeedDeviceAsync(db);
            technician = await SeedTechnicianAsync(db);
            session = await SeedSessionAsync(db, device.Id, technician.Id, SessionStatus.Accepted);
        }

        using (var db = server.NewDbContext())
        {
            var d = await db.Devices.SingleAsync(x => x.Id == device.Id);
            d.ConnectionId = "conn-old";
            await db.SaveChangesAsync();
        }

        // Reconexión: conn-new reemplaza a conn-old como el dispositivo autorizado.
        await server.CreateHub("conn-new").AnnounceDevicePresence(device.DeviceCode, PairingKey);

        var staleHub = server.CreateHub("conn-old");
        var ex = await Assert.ThrowsAsync<HubException>(() => staleHub.GetTurnCredentials(session.Id));
        Assert.Contains("No autorizado", ex.Message);

        var currentHub = server.CreateHub("conn-new");
        var result = await currentHub.GetTurnCredentials(session.Id);
        Assert.NotNull(result.IceServers[0].Credential);
    }

    [Fact]
    public async Task GetTurnCredentials_WhenTurnNotConfigured_ThrowsClearHubException()
    {
        using var server = HubTestServer.Create();
        server.TurnOptions.SharedSecret = string.Empty;

        Device device;
        User technician;
        RemoteSession session;
        using (var db = server.NewDbContext())
        {
            device = await SeedDeviceAsync(db);
            technician = await SeedTechnicianAsync(db);
            session = await SeedSessionAsync(db, device.Id, technician.Id, SessionStatus.Accepted);
            device.ConnectionId = "conn-device";
            await db.SaveChangesAsync();
        }

        var deviceHub = server.CreateHub("conn-device");
        var ex = await Assert.ThrowsAsync<HubException>(() => deviceHub.GetTurnCredentials(session.Id));
        Assert.Contains("TURN no está configurado", ex.Message);
    }
}
